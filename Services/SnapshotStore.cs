using ServerMonitoringWeb.Models;

namespace ServerMonitoringWeb.Services;

public sealed class SnapshotStore(
    ResultStore store,
    MonitoringConfiguration configuration,
    MonitoringOptions options,
    ILogger<SnapshotStore> logger)
{
    private readonly ResultStore _store = store;
    private readonly MonitoringConfiguration _configuration = configuration;
    private readonly ILogger<SnapshotStore> _logger = logger;

    public string? StorageError { get; private set; }

    public string? FreshnessWarning { get; private set; }

    public string? MetricsWarning { get; private set; }

    public IReadOnlyList<ServerSnapshot> GetAll()
    {
        StorageError = null;
        FreshnessWarning = null;
        MetricsWarning = null;

        try
        {
            // The dashboard only displays enabled servers.
            // Disabled servers are rendered directly from configuration.
            var enabledServers =
                _configuration.Servers
                    .Where(server => server.Enabled)
                    .ToList();

            if (enabledServers.Count == 0)
            {
                return [];
            }

            var snapshots = _store.ReadLatest(enabledServers);
            var now = DateTimeOffset.UtcNow;

            // Allow for a full batch of checks and slow targets, rather than
            // treating the nominal timer interval as a freshness guarantee.
            var batches = Math.Ceiling((double)enabledServers.Count / options.MaxConcurrentChecks);
            var maxAddresses = enabledServers.Max(server => server.IpAddresses.Length);
            var pingLimit = TimeSpan.FromSeconds(Math.Max(60,
                3 * (options.PingIntervalSeconds + batches * options.PingTimeoutMilliseconds / 1000.0)));
            var metricsLimit = TimeSpan.FromSeconds(Math.Max(60,
                3 * (options.DetailsIntervalSeconds + batches * maxAddresses * options.MetricsTimeoutSeconds)));

            var stalePing = snapshots.Any(snapshot =>
                snapshot.LastPingUtc is { } timestamp && now - timestamp > pingLimit);

            var staleMetrics = snapshots.Any(snapshot =>
                (snapshot.CpuPercent is >= 0 and <= 100 ||
                 snapshot.RamPercent is >= 0 and <= 100 ||
                 snapshot.Disks.Any(disk => disk.UsedPercent is >= 0 and <= 100)) &&
                (snapshot.LastMetricsUtc is not { } timestamp || now - timestamp > metricsLimit));

            if (stalePing || staleMetrics)
            {
                FreshnessWarning = stalePing && staleMetrics
                    ? "Saved ping and resource readings may be out of date. Check the monitoring host and storage."
                    : stalePing
                        ? "Saved ping readings may be out of date. Check the monitoring host and storage."
                        : "Saved resource readings may be out of date. Check the monitoring host and metrics collection.";
            }

            // Only safe categories and counts; no raw WMI or inventory details.
            var failed = snapshots.Where(snapshot =>
                snapshot.IsOnline == true &&
                snapshot.CpuPercent is null && snapshot.RamPercent is null &&
                !snapshot.Disks.Any(disk => disk.UsedPercent is not null) &&
                snapshot.CollectionWarning is { } warning &&
                (warning.StartsWith("Local WMI", StringComparison.Ordinal) ||
                 warning.StartsWith("Remote WMI", StringComparison.Ordinal) ||
                 warning.StartsWith("WMI target identification", StringComparison.Ordinal) ||
                 warning.StartsWith("Collector process:", StringComparison.Ordinal) ||
                 warning.StartsWith("CPU:", StringComparison.Ordinal) ||
                 warning.StartsWith("RAM:", StringComparison.Ordinal) ||
                 warning.StartsWith("Disks:", StringComparison.Ordinal))).ToList();

            if (failed.Count > 0)
            {
                var accessDenied = failed.Count(snapshot =>
                    snapshot.CollectionWarning!.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) ||
                    snapshot.CollectionWarning!.Contains("0x80041003", StringComparison.OrdinalIgnoreCase));
                MetricsWarning = $"Resource metrics unavailable for {failed.Count} online server(s). " +
                    (accessDenied > 0
                        ? $"WMI or collector access denied for {accessDenied}; check the approved IIS identity and target permissions. "
                        : "") +
                    "See application logs for collection details.";
            }

            return snapshots;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load monitoring results.");

            StorageError =
                "Monitoring results are currently unavailable.";

            // Fall back to placeholder snapshots so the dashboard
            // remains usable even when storage cannot be read.
            return CreatePendingSnapshots();
        }
    }

    private List<ServerSnapshot> CreatePendingSnapshots()
    {
        var pendingSnapshots =
            _configuration.Servers
                .Where(server => server.Enabled)
                .Select(server =>
                    ServerSnapshot.Pending(
                        server,
                        "Waiting for data collection."))
                .ToList();

        return pendingSnapshots;
    }
}
