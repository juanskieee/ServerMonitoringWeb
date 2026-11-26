using ServerMonitoringWeb.Models;

namespace ServerMonitoringWeb.Services;

public sealed class MonitoringWorker(
    DataPaths paths,
    MonitoringOptions options,
    ResultStore store,
    ServerCollector collector,
    ILogger<MonitoringWorker> logger)
    : BackgroundService
{
    private DateTime? _lastHistoryCleanupDate;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        store.Initialize();

        logger.LogInformation(
            "Monitoring {Inventory}: ping every {Ping}s, details every {Details}s.",
            paths.Inventory,
            options.PingIntervalSeconds,
            options.DetailsIntervalSeconds);

        // Run ping checks and metrics collection independently.
        // A slow WMI query should never delay ping updates.
        await Task.WhenAll(
            RunChecksAsync(
                collectDetails: false,
                stoppingToken),

            RunChecksAsync(
                collectDetails: true,
                stoppingToken));
    }

    private async Task RunChecksAsync(
        bool collectDetails,
        CancellationToken cancellationToken)
    {
        var intervalSeconds =
            collectDetails
                ? options.DetailsIntervalSeconds
                : options.PingIntervalSeconds;

        using var timer =
            new PeriodicTimer(
                TimeSpan.FromSeconds(intervalSeconds));

        do
        {
            try
            {
                var inventory =
                    InventoryConfiguration.Load(
                        paths.Inventory);

                if (!inventory.IsValid)
                {
                    logger.LogError(
                        "Invalid servers.json: {Errors}",
                        string.Join(
                            " | ",
                            inventory.Errors));

                    continue;
                }

                await RunServerChecksAsync(
                    inventory,
                    collectDetails,
                    cancellationToken);

                // Check retention once per UTC day in the ping loop,
                // including days with no outage events.
                var todayUtc = DateTime.UtcNow.Date;
                if (!collectDetails && _lastHistoryCleanupDate != todayUtc)
                {
                    await store.DeleteExpiredAsync(
                        options.HistoryRetentionDays,
                        cancellationToken);
                    _lastHistoryCleanupDate = todayUtc;
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Monitoring cycle failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(
            cancellationToken));
    }

    private async Task RunServerChecksAsync(
        InventoryConfiguration inventory,
        bool collectDetails,
        CancellationToken cancellationToken)
    {
        var enabledServers =
            inventory.Servers
                .Where(server => server.Enabled);

        await Parallel.ForEachAsync(
            enabledServers,
            new ParallelOptions
            {
                MaxDegreeOfParallelism =
                    options.MaxConcurrentChecks,

                CancellationToken =
                    cancellationToken
            },
            async (server, cancellation) =>
            {
                try
                {
                    if (collectDetails)
                    {
                        await CollectMetricsAsync(
                            server,
                            cancellation);
                    }
                    else
                    {
                        await CollectPingAsync(
                            server,
                            cancellation);
                    }
                }
                catch (OperationCanceledException)
                    when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        "{ServerId}: {Check} failed: {Error}",
                        server.Id,
                        collectDetails
                            ? "Details"
                            : "Ping",
                        CollectionFailure.Message(
                            ex,
                            server.Password));
                }
            });
    }

    private async Task CollectPingAsync(
        ServerDefinition server,
        CancellationToken cancellationToken)
    {
        var pingResult =
            await collector.PingAsync(
                server,
                cancellationToken);

        await store.SavePingAsync(
            server,
            pingResult,
            cancellationToken);
    }

    private async Task CollectMetricsAsync(
        ServerDefinition server,
        CancellationToken cancellationToken)
    {
        var currentSnapshot =
            store.ReadLatest([server])[0];

        var metrics =
            await collector.CollectMetricsAsync(
                server,
                currentSnapshot.IpAddress,
                cancellationToken);

        await store.SaveMetricsAsync(
            server,
            metrics,
            cancellationToken);
    }
}
