using System.Net;
using System.Net.NetworkInformation;
using ServerMonitoringWeb.Models;

namespace ServerMonitoringWeb.Services;

public sealed class ServerCollector(
    MonitoringOptions options,
    ILogger<ServerCollector> logger)
{
    public async Task<ServerSnapshot> PingAsync(
        ServerDefinition server,
        CancellationToken cancellationToken)
    {
        // Ping all configured addresses concurrently.
        // This prevents a slow or unreachable first address from delaying
        // successful responses on other addresses.
        var replies =
            await Task.WhenAll(
                server.IpAddresses.Select(async address =>
                {
                    using var ping = new Ping();

                    try
                    {
                        var reply =
                            await ping.SendPingAsync(
                                IPAddress.Parse(address),
                                TimeSpan.FromMilliseconds(
                                    options.PingTimeoutMilliseconds),
                                cancellationToken: cancellationToken);

                        return reply.Status == IPStatus.Success
                            ? reply
                            : null;
                    }
                    catch (PingException)
                    {
                        return null;
                    }
                }));

        var firstSuccessfulReplyIndex =
            Array.FindIndex(
                replies,
                reply => reply is not null);

        var isOnline =
            firstSuccessfulReplyIndex >= 0;

        return ServerSnapshot.Pending(server) with
        {
            IsOnline = isOnline,

            // Keep the address that actually responded so metrics
            // can try it first.
            IpAddress = isOnline
                ? server.IpAddresses[firstSuccessfulReplyIndex]
                : "",

            ResponseTimeMs = isOnline
                ? replies[firstSuccessfulReplyIndex]!.RoundtripTime
                : null,

            LastPingUtc = DateTimeOffset.UtcNow
        };
    }

    public async Task<ServerSnapshot> CollectMetricsAsync(
        ServerDefinition server,
        string preferredIp,
        CancellationToken cancellationToken)
    {
        var snapshot =
            ServerSnapshot.Pending(server);

        // Ping requires no credentials.
        // If credentials are unavailable, skip WMI collection and
        // leave metric fields empty.
        if (string.IsNullOrWhiteSpace(server.Username) ||
            string.IsNullOrEmpty(server.Password))
        {
            return snapshot;
        }

        // Prefer the IP address that successfully answered ping.
        // If metrics cannot be collected from that address,
        // try the remaining configured addresses.
        var addresses =
            server.IpAddresses
                .OrderByDescending(address => address == preferredIp);

        foreach (var address in addresses)
        {
            try
            {
                var metrics =
                    await CollectorProcess.CollectAsync(
                        server,
                        address,
                        TimeSpan.FromSeconds(
                            options.MetricsTimeoutSeconds),
                        cancellationToken);

                snapshot = snapshot with
                {
                    IpAddress = address,

                    CpuPercent = metrics.CpuPercent,
                    RamPercent = metrics.RamPercent,

                    Disks = metrics.Disks,

                    CollectionWarning =
                        metrics.Warnings.Count > 0
                            ? string.Join(
                                " | ",
                                metrics.Warnings)
                            : null
                };

                // A partial result is still useful.
                // Stop trying additional addresses once any
                // usable metric has been collected.
                var hasMetrics =
                    metrics.CpuPercent is not null ||
                    metrics.RamPercent is not null ||
                    metrics.Disks.Any(disk => disk.UsedPercent is not null);

                if (hasMetrics)
                {
                    snapshot = snapshot with
                    {
                        LastMetricsUtc = DateTimeOffset.UtcNow
                    };

                    break;
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                snapshot = snapshot with
                {
                    CollectionWarning =
                        CollectionFailure.Message(
                            ex,
                            server.Password)
                };
            }
        }

        if (snapshot.CollectionWarning is { } warning)
        {
            logger.LogWarning(
                "{ServerId}: {Warning}",
                server.Id,
                warning);
        }

        return snapshot;
    }
}
