using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ServerMonitoringWeb.Models;
using ServerMonitoringWeb.Services;

namespace ServerMonitoringWeb.Pages;

public sealed class IndexModel(
    SnapshotStore snapshots,
    MonitoringConfiguration configuration)
    : PageModel
{
    public MonitoringConfiguration Configuration { get; } = configuration;

    public IReadOnlyList<ServerCard> Cards { get; private set; } = [];

    public string? StorageError =>
        snapshots.StorageError;

    public string? FreshnessWarning =>
        snapshots.FreshnessWarning;

    public void OnGet()
    {
        LoadCards();
    }

    public PartialViewResult OnGetCards()
    {
        LoadCards();

        return Partial(
            "_ServerCards",
            this);
    }

    private void LoadCards()
    {
        Response.Headers.CacheControl = "no-store";

        if (!Configuration.IsValid)
        {
            return;
        }

        // Snapshots are keyed by ServerId so cards can quickly
        // locate their latest saved monitoring result.
        var latestSnapshots =
            snapshots.GetAll()
                .ToDictionary(
                    snapshot => snapshot.ServerId,
                    StringComparer.OrdinalIgnoreCase);

        // IDs are intentionally zero-padded (srv01, srv02, ...).
        // Preserve this order during both initial load and refresh.
        Cards =
        [
            .. Configuration.Servers
                .OrderBy(
                    server => server.Id,
                    StringComparer.OrdinalIgnoreCase)
                .Select(server =>
                {
                    var snapshot =
                        latestSnapshots.GetValueOrDefault(server.Id)
                        ?? ServerSnapshot.Pending(server);

                    return new ServerCard(
                        server,
                        snapshot);
                })
        ];
    }

    public static string Value(
        double? value,
        string format = "F2")
    {
        return value?.ToString(
                   format,
                   CultureInfo.InvariantCulture)
               ?? "N/A";
    }
}

public sealed record ServerCard(
    ServerDefinition Definition,
    ServerSnapshot Snapshot)
{
    // A card is considered an alert only when monitoring is enabled
    // and ping connectivity has failed.
    public bool HasAlert =>
        Definition.Enabled &&
        Snapshot.IsOnline == false;

    public bool HasUsableMetrics =>
        Snapshot.CpuPercent is >= 0 and <= 100 ||
        Snapshot.RamPercent is >= 0 and <= 100 ||
        Snapshot.Disks.Any(disk => disk.UsedPercent is >= 0 and <= 100);

    // Resource readings are only shown when the server is online.
    // Offline and paused cards intentionally hide saved metrics.
    public bool ShowReadings =>
        Definition.Enabled &&
        Snapshot.IsOnline == true;

    public string? EmptyStateMessage
    {
        get
        {
            if (!Definition.Enabled)
            {
                return "Monitoring Paused";
            }

            if (HasAlert)
            {
                return "Ping Failed";
            }

            if (Snapshot.IsOnline is null)
            {
                return "Waiting for First Scan";
            }

            return HasUsableMetrics ? null : "Metrics Unavailable";
        }
    }

    public string State =>
        !Definition.Enabled
            ? "Paused"
            : HasAlert
                ? "Error"
                : Snapshot.Status;

    public string StateClass =>
        State.ToLowerInvariant();
}

public sealed record UsageGauge(
    string Label,
    double? Value,
    string Tone,
    int Radius)
{
    // Donut charts only support valid percentages.
    // Invalid, missing, NaN, or out-of-range values become N/A.
    public double? Percent =>
        Value is >= 0 and <= 100 &&
        double.IsFinite(Value.Value)
            ? Value
            : null;

    public string Number =>
        Percent?.ToString(
            "F1",
            CultureInfo.InvariantCulture)
        ?? "N/A";

    // SVG rings use pathLength="100".
    // A value of 100% = offset 0.
    // A value of 0% = offset 100.
    public string Offset =>
        (100 - (Percent ?? 0))
            .ToString(
                "0.###",
                CultureInfo.InvariantCulture);
}
