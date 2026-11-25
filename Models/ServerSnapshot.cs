using System.Text.Json.Serialization;

namespace ServerMonitoringWeb.Models;

public sealed record ServerSnapshot
{
    // Unique inventory identifier.
    public required string ServerId { get; init; }

    // Display name shown on the dashboard.
    public required string ServerName { get; init; }

    // Address used for the latest successful ping or metrics collection.
    public required string IpAddress { get; init; }

    // Configuration fingerprint used to invalidate outdated snapshots.
    public required string ConfigurationRevision { get; init; }

    // Current connectivity state:
    // true  = Online
    // false = Offline
    // null  = Checking
    public bool? IsOnline { get; init; }

    // CPU utilization percentage (0-100).
    // Null means unavailable.
    public double? CpuPercent { get; init; }

    // RAM utilization percentage (0-100).
    // Null means unavailable.
    public double? RamPercent { get; init; }

    // Fixed drives reported by the target server.
    public List<DiskMetrics> Disks { get; init; } = [];

    // Latest successful ping response time in milliseconds.
    public long? ResponseTimeMs { get; init; }

    // Timestamp of the most recent ping check.
    public DateTimeOffset? LastPingUtc { get; init; }

    // Timestamp of the last collection that returned usable resource metrics.
    public DateTimeOffset? LastMetricsUtc { get; init; }

    // Current pair of ping checks, newest first; resets before the next pair.
    public PingLogEntry[] PingLog { get; init; } = [];

    // Non-fatal collection warning shown by the dashboard.
    public string? CollectionWarning { get; init; }

    // User-friendly connectivity status for display.
    public string Status => IsOnline switch
    {
        true => "Online",
        false => "Offline",
        null => "Checking"
    };

    // Combined utilization, weighted by capacity across drives with readings.
    [JsonIgnore]
    public double? DiskUsedPercent
    {
        get
        {
            var percentages =
                Disks
                    .Where(d => d.UsedPercent.HasValue)
                    .Select(d => d.UsedPercent!.Value)
                    .ToArray();

            return percentages.Length > 0
                ? percentages.Average()
                : null;
        }
    }

    // Creates a placeholder snapshot while waiting for real data.
    public static ServerSnapshot Pending(
        ServerDefinition server,
        string? warning = null)
    {
        return new()
        {
            ServerId = server.Id,
            ServerName = server.Name,
            IpAddress = "",
            ConfigurationRevision = server.GetRevision(),
            CollectionWarning = warning
        };
    }
}

// One completed ping observation.
public sealed record PingLogEntry(
    DateTimeOffset CheckedUtc,
    bool IsOnline,
    long? ResponseTimeMs);
