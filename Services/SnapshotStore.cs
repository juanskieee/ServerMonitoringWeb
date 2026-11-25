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

    public IReadOnlyList<ServerSnapshot> GetAll()
    {
        StorageError = null;
        FreshnessWarning = null;

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
