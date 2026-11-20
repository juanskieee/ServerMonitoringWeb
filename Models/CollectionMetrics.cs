namespace ServerMonitoringWeb.Models;

public sealed record CollectionMetrics
{
    // Total CPU utilization percentage (0-100).
    public double? CpuPercent { get; set; }

    // Total physical memory utilization percentage (0-100).
    public double? RamPercent { get; set; }

    // Fixed drives reported by the target server.
    public List<DiskMetrics> Disks { get; set; } = [];

    // Non-fatal collection warnings.
    // Partial metrics may still be available.
    public List<string> Warnings { get; set; } = [];
}
