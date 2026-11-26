using ServerMonitoringWeb.Models;

namespace ServerMonitoringWeb.Services;

public sealed class SnapshotStore(
    ResultStore store,
    MonitoringConfiguration configuration,
    ILogger<SnapshotStore> logger)
{
    private readonly ResultStore _store = store;
    private readonly MonitoringConfiguration _configuration = configuration;
    private readonly ILogger<SnapshotStore> _logger = logger;

    public string? StorageError { get; private set; }

    public IReadOnlyList<ServerSnapshot> GetAll()
    {
        StorageError = null;

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

            return _store.ReadLatest(enabledServers);
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
