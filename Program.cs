using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;
using ServerMonitoringWeb.Models;
using ServerMonitoringWeb.Services;


if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Windows only.");
    return 1;
}

// Collector mode is launched by CollectorProcess.
// It performs one WMI collection and exits.
if (await TryRunCollectorModeAsync(args))
{
    return 0;
}

// Service mode starts only the collector, not an HTTP server.
if (args.FirstOrDefault() == "--worker-service")
{
    return await RunWorkerServiceAsync(args);
}

var builder = WebApplication.CreateBuilder(args);
var options = BuildMonitoringOptions(builder.Configuration);
var paths = CreateDataPaths(builder.Configuration, builder.Environment.ContentRootPath);

// Keep combined mode for existing deployments and development until the
// administrator explicitly switches IIS to dashboard-only mode.
var runWorker = builder.Configuration.GetValue("Monitoring:RunWorker", true);

RegisterStorage(builder.Services, options, paths);
if (runWorker)
{
    RegisterWorker(builder.Services);
}

builder.Services.AddScoped<MonitoringConfiguration>();
builder.Services.AddScoped<SnapshotStore>();
builder.Services.AddRazorPages();

var app = builder.Build();

// Desktop behavior is opt-in and never enabled for non-interactive hosts.
var desktopMode = app.Configuration.GetValue<bool>("DesktopMode") &&
                  Environment.UserInteractive;


app.Logger.LogInformation(
    "Host: {ServerType}; Process ID: {ProcessId}; Interactive: {Interactive}; Desktop mode: {DesktopMode}; Monitoring worker: {RunWorker}",
    app.Services.GetRequiredService<IServer>().GetType().Name,
    Environment.ProcessId,
    Environment.UserInteractive,
    desktopMode,
    runWorker);

LogPaths(app.Logger, paths);

ConfigurePipeline(app);

// IIS and unattended monitoring must not depend on an open browser.
if (desktopMode)
{
    long lastHeartbeat = Stopwatch.GetTimestamp();

    app.MapPost("/heartbeat", () =>
    {
        Interlocked.Exchange(ref lastHeartbeat, Stopwatch.GetTimestamp());
        return Results.Ok();
    });

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        Interlocked.Exchange(ref lastHeartbeat, Stopwatch.GetTimestamp());
        _ = Task.Run(async () =>
        {
            var stopping = app.Lifetime.ApplicationStopping;

            try
            {
                await Task.Delay(1500, stopping);

                try
                {
                    using var browser = Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments =
                            "/c start \"LSD Monitoring\" msedge --new-window http://localhost:5000",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                catch (Exception ex)
                {
                    app.Logger.LogWarning(ex, "Could not open the desktop browser.");
                }

                while (!stopping.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stopping);

                    if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastHeartbeat)) >
                        TimeSpan.FromSeconds(20))
                    {
                        app.Logger.LogInformation("Desktop heartbeat expired; stopping application.");
                        app.Lifetime.StopApplication();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // Normal host shutdown.
            }
        });
    });
}

app.Run();

return 0;

static async Task<bool> TryRunCollectorModeAsync(
    string[] args)
{
    if (args.Length != 2 ||
        args[0] != "--collect-once")
    {
        return false;
    }

    Console.InputEncoding = Encoding.UTF8;
    Console.OutputEncoding = Encoding.UTF8;

    var input =
        await Console.In.ReadToEndAsync();

    var server =
        JsonSerializer.Deserialize<ServerDefinition>(
            input);

    if (server is null)
    {
        Environment.ExitCode = 1;
        return true;
    }

    var metrics =
        WmiCollector.Collect(
            server,
            args[1]);

    Console.WriteLine(
        JsonSerializer.Serialize(metrics));

    return true;
}

static async Task<int> RunWorkerServiceAsync(string[] args)
{
    if (args.Length != 2 ||
        !Path.IsPathFullyQualified(args[1]) ||
        args[1].StartsWith(@"\\", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Worker service requires one absolute local application content root.");
        return 1;
    }

    if (!WindowsServiceHelpers.IsWindowsService())
    {
        Console.Error.WriteLine("Start worker-service mode through the Windows Service Control Manager only.");
        return 1;
    }

    // Use the dashboard's explicit content root even when the service binaries
    // are installed in a different directory.
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = [],
        EnvironmentName = Environments.Production,
        ContentRootPath = Path.GetFullPath(args[1])
    });

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "ServerMonitoringWorker";
    });

    builder.Logging.AddFilter<EventLogLoggerProvider>("ServerMonitoringWeb", LogLevel.Information);

    var options = BuildMonitoringOptions(builder.Configuration);
    var paths = CreateDataPaths(builder.Configuration, builder.Environment.ContentRootPath);
    RegisterStorage(builder.Services, options, paths);
    RegisterWorker(builder.Services);

    using var host = builder.Build();
    var logger = host.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("ServerMonitoringWeb.WorkerService");

    logger.LogInformation("Windows monitoring service; Process ID: {ProcessId}", Environment.ProcessId);
    LogPaths(logger, paths);

    await host.RunAsync();
    return 0;
}

static void LogPaths(ILogger logger, DataPaths paths)
{
    logger.LogInformation("Inventory: {Inventory}", paths.Inventory);
    logger.LogInformation("Data Directory: {DataDirectory}", paths.DataDirectory);
    logger.LogInformation("Latest Results: {LatestResults}", paths.LatestResults);
    logger.LogInformation("History Directory: {HistoryDirectory}", paths.HistoryDirectory);
}

static MonitoringOptions BuildMonitoringOptions(
    IConfiguration configuration)
{
    var options =
        new MonitoringOptions();

    configuration
        .GetSection("Monitoring")
        .Bind(options);

    Validator.ValidateObject(
        options,
        new ValidationContext(options),
        validateAllProperties: true);

    return options;
}

static DataPaths CreateDataPaths(
    IConfiguration configuration,
    string contentRoot)
{
    return new DataPaths(
        configuration["Monitoring:DataRoot"],
        contentRoot);
}

static void RegisterStorage(
    IServiceCollection services,
    MonitoringOptions options,
    DataPaths paths)
{
    services.AddSingleton(paths);
    services.AddSingleton(options);

    services.AddSingleton<ResultStore>();
}

static void RegisterWorker(IServiceCollection services)
{
    services.AddSingleton<ServerCollector>();
    services.AddHostedService<MonitoringWorker>();
}

static void ConfigurePipeline(
    WebApplication app)
{
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    // Disabled for EXE deployment
    // app.UseHttpsRedirection();

    app.UseStaticFiles();

    app.UseRouting();

    app.UseAuthorization();

    app.MapRazorPages();
}