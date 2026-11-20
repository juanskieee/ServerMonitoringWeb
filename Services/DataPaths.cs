namespace ServerMonitoringWeb.Services;

public sealed class DataPaths
{
    // Root directory used for monitoring data.
    public string DataDirectory { get; }

    // Path to the active inventory file (servers.json).
    public string Inventory { get; }

    // Latest merged monitoring results.
    public string LatestResults =>
        Path.Combine(
            DataDirectory,
            "latest.json");

    // Directory containing daily outage-event CSV files.
    public string HistoryDirectory =>
        Path.Combine(
            DataDirectory,
            "History");

    public DataPaths(
        string? dataRoot,
        string contentRoot)
    {
        // When Monitoring:DataRoot is not configured,
        // default to:
        // <ContentRoot>\Logs
        dataRoot ??=
            Path.Combine(
                Path.GetFullPath(contentRoot),
                "Logs");

        // Require an absolute local path.
        // Prevent relative paths and network shares.
        if (!Path.IsPathFullyQualified(dataRoot) ||
            dataRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Monitoring:DataRoot must be an absolute local directory, such as D:\\MonitoringData.");
        }

        // Monitoring data is stored under:
        // <DataRoot> (without an additional Data subfolder).
        DataDirectory =
            Path.GetFullPath(dataRoot);

        // Inventory remains beside the application.
        Inventory =
            Path.Combine(
                Path.GetFullPath(contentRoot),
                "servers.json");
    }
}
