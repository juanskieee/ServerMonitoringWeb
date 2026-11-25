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
                var operation = collectDetails
                    ? "Read latest results (storage)"
                    : "Ping collection";

                try
                {
                    if (collectDetails)
                    {
                        var currentSnapshot = store.ReadLatest([server])[0];

                        operation = "Metrics collection";
                        var metrics = await collector.CollectMetricsAsync(
                            server,
                            currentSnapshot.IpAddress,
                            cancellation);

                        operation = "Save metrics results (storage)";
                        await store.SaveMetricsAsync(
                            server,
                            metrics,
                            cancellation);
                    }
                    else
                    {
                        var pingResult = await collector.PingAsync(
                            server,
                            cancellation);

                        operation = "Save ping results (storage)";
                        await store.SavePingAsync(
                            server,
                            pingResult,
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
                        "{ServerId}: {Operation} failed: {Error} ({ExceptionType}, 0x{ErrorCode:X8})",
                        server.Id,
                        operation,
                        CollectionFailure.Message(
                            ex,
                            server.Password),
                        ex.GetType().Name,
                        unchecked((uint)ex.HResult));
                }
            });
    }
}
