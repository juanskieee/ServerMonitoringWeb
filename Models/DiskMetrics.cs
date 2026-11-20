using System.Text.Json.Serialization;

namespace ServerMonitoringWeb.Models;

public sealed record DiskMetrics
{
    public required string Name { get; init; }

    // Capacity values use decimal gigabytes (GB).
    public double? FreeGb { get; init; }
    public double? SizeGb { get; init; }

    [JsonIgnore]
    public double? UsedGb =>
        SizeGb is > 0 && FreeGb is >= 0 && FreeGb <= SizeGb
            ? SizeGb - FreeGb
            : null;

    [JsonIgnore]
    public double? UsedPercent =>
        100 * UsedGb / SizeGb;
}
