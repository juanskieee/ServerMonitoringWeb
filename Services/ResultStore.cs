using System.Globalization;
using System.Text.Json;
using ServerMonitoringWeb.Models;

namespace ServerMonitoringWeb.Services;

public sealed class ResultStore(DataPaths paths)
{
    private const string HistoryHeader = "Timestamp,ServerId,ServerName,Event";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly DataPaths _paths = paths;

    // One lock protects latest.json and history files from overlapping writes.
    private readonly SemaphoreSlim _lock = new(1, 1);

    public void Initialize()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        Directory.CreateDirectory(_paths.HistoryDirectory);

        if (!File.Exists(_paths.LatestResults))
        {
            File.WriteAllText(
                _paths.LatestResults,
                "{}");
        }
    }

    public List<ServerSnapshot> ReadLatest(
        List<ServerDefinition> servers)
    {
        var results =
            ReadAll();

        var snapshots =
            servers
                .Where(server => server.Enabled)
                .Select(server =>
                {
                    if (results.TryGetValue(
                            server.Id,
                            out var snapshot) &&
                        snapshot.ConfigurationRevision ==
                        server.GetRevision())
                    {
                        return snapshot;
                    }

                    // Existing results become invalid whenever
                    // the server configuration changes.
                    return ServerSnapshot.Pending(
                        server,
                        "Waiting for first scan...");
                })
                .ToList();

        return snapshots;
    }

    private Dictionary<string, ServerSnapshot> ReadAll()
    {
        if (!File.Exists(_paths.LatestResults))
        {
            return new Dictionary<string, ServerSnapshot>(
                StringComparer.OrdinalIgnoreCase);
        }

        // Readers can continue using the current file while a writer
        // atomically replaces it with a new version.
        using var file =
            new FileStream(
                _paths.LatestResults,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

        return JsonSerializer.Deserialize<
                Dictionary<string, ServerSnapshot>>(file)
            ?? new Dictionary<string, ServerSnapshot>(
                StringComparer.OrdinalIgnoreCase);
    }

    public Task<ServerSnapshot> SavePingAsync(
        ServerDefinition server,
        ServerSnapshot ping,
        CancellationToken cancellationToken)
    {
        return UpdateAsync(
            server,
            current =>
            {
                var pingLog =
                    current.PingLog;

                if (ping.LastPingUtc is { } checkedUtc &&
                    ping.IsOnline is { } isOnline)
                {
                    pingLog =
                    [
                        new PingLogEntry(
                        checkedUtc,
                        isOnline,
                        ping.ResponseTimeMs),

                        .. pingLog.Take(1)
                    ];
                }

                return current with
                {
                    IsOnline = ping.IsOnline,

                    // Preserve the last successful address when
                    // a later ping fails.
                    IpAddress =
                        ping.IsOnline == true
                            ? ping.IpAddress
                            : current.IpAddress,

                    ResponseTimeMs = ping.ResponseTimeMs,
                    LastPingUtc = ping.LastPingUtc,
                    PingLog = pingLog
                };
            },
            cancellationToken);
    }

    public Task<ServerSnapshot> SaveMetricsAsync(
        ServerDefinition server,
        ServerSnapshot details,
        CancellationToken cancellationToken)
    {
        return UpdateAsync(
            server,
            current => details with
            {
                // Ping and metrics are collected separately.
                // Preserve connectivity information when metrics update.
                IsOnline = current.IsOnline,
                ResponseTimeMs = current.ResponseTimeMs,
                LastPingUtc = current.LastPingUtc,
                PingLog = current.PingLog
            },
            cancellationToken);
    }

    private async Task<ServerSnapshot> UpdateAsync(
        ServerDefinition server,
        Func<ServerSnapshot, ServerSnapshot> update,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            var results =
                ReadAll();

            var current =
                results.GetValueOrDefault(server.Id);

            if (current is null ||
                current.ConfigurationRevision !=
                server.GetRevision())
            {
                current =
                    ServerSnapshot.Pending(server);
            }

            var updatedSnapshot =
                update(current);

            results[server.Id] =
                updatedSnapshot;

            var json =
                JsonSerializer.Serialize(
                    results,
                    JsonOptions);

            var temporaryFile =
                _paths.LatestResults + ".tmp";

            try
            {
                // Write to a temporary file first so latest.json
                // is replaced only after the new content exists.
                await File.WriteAllTextAsync(
                    temporaryFile,
                    json,
                    cancellationToken);

                // Only known ping-state changes create outage events.
                // Metrics updates preserve IsOnline, so they never append history.
                if (current.IsOnline is { } wasOnline &&
                    updatedSnapshot.IsOnline is { } isOnline &&
                    wasOnline != isOnline &&
                    updatedSnapshot.LastPingUtc is { } checkedUtc)
                {
                    await AppendHistoryAsync(
                        updatedSnapshot,
                        checkedUtc,
                        cancellationToken);
                }

                File.Move(
                    temporaryFile,
                    _paths.LatestResults,
                    overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryFile))
                {
                    File.Delete(temporaryFile);
                }
            }

            return updatedSnapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Called while UpdateAsync holds the storage lock.
    private async Task AppendHistoryAsync(
        ServerSnapshot snapshot,
        DateTimeOffset checkedUtc,
        CancellationToken cancellationToken)
    {
        var timestamp = checkedUtc.UtcDateTime;
        var filePath = Path.Combine(
            _paths.HistoryDirectory,
            $"{timestamp:yyyy-MM-dd}.metrics.csv");

        var isNewFile =
            !File.Exists(filePath) || new FileInfo(filePath).Length == 0;

        if (!isNewFile)
        {
            string? header;
            using (var reader = File.OpenText(filePath))
            {
                header = await reader.ReadLineAsync(cancellationToken);
            }

            if (header != HistoryHeader)
            {
                // Preserve older metrics history without mixing CSV schemas.
                File.Move(
                    filePath,
                    Path.Combine(
                        _paths.HistoryDirectory,
                        $"{timestamp:yyyy-MM-dd}.metrics.legacy-{Guid.NewGuid():N}.csv"));
                isNewFile = true;
            }
        }

        await using var stream =
            new FileStream(
                filePath,
                FileMode.Append,
                FileAccess.Write);

        await using var writer =
            new StreamWriter(stream);

        if (isNewFile)
        {
            await writer.WriteLineAsync(HistoryHeader);
        }

        await writer.WriteLineAsync(
            string.Join(
                ",",
                timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Csv(snapshot.ServerId),
                Csv(snapshot.ServerName),
                snapshot.IsOnline == true ? "Recovered" : "Offline"));
    }

    public async Task DeleteExpiredAsync(
        int retentionDays,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            var cutoffDate =
                DateTime.UtcNow.Date.AddDays(
                    1 - retentionDays);

            foreach (var file in Directory.GetFiles(
                         _paths.HistoryDirectory,
                         "*.csv"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Event files and preserved legacy CSVs start with the UTC date.
                var fileDateText = Path.GetFileName(file).Split('.')[0];

                if (!DateTime.TryParseExact(
                        fileDateText,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var fileDate))
                {
                    continue;
                }

                if (fileDate < cutoffDate)
                {
                    File.Delete(file);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string Csv(string? value)
    {
        value ??= "";

        // Prevent spreadsheet applications from interpreting
        // CSV values as formulas.
        if (value.TrimStart().StartsWith('=') ||
            value.TrimStart().StartsWith('+') ||
            value.TrimStart().StartsWith('-') ||
            value.TrimStart().StartsWith('@'))
        {
            value = "'" + value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
