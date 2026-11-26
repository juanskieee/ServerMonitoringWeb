using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using ServerMonitoringWeb.Models;
using ServerMonitoringWeb.Services;


if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Windows only.");
    return 1;
}

// Collector mode is launched by CollectorProcess.
// It performs one WMI collection and exits.
if (args.FirstOrDefault() == "--collect-once")
{
    return await RunCollectorModeAsync(args);
}

// Reject an obsolete service launch rather than starting a second web host.
if (args.FirstOrDefault() == "--worker-service")
{
    Console.Error.WriteLine("Separate service mode has been removed. Host this application in IIS.");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
var options = BuildMonitoringOptions(builder.Configuration);
var paths = CreateDataPaths(builder.Configuration, builder.Environment.ContentRootPath);

// One application always owns both the dashboard and monitoring worker.
RegisterStorage(builder.Services, options, paths);
RegisterWorker(builder.Services);

builder.Services.AddScoped<MonitoringConfiguration>();
builder.Services.AddScoped<SnapshotStore>();
builder.Services.AddRazorPages();

var app = builder.Build();

// Desktop behavior is opt-in and never enabled for non-interactive hosts.
var desktopMode = app.Configuration.GetValue<bool>("DesktopMode") &&
                  Environment.UserInteractive &&
                  app.Services.GetRequiredService<IServer>().GetType().Assembly.GetName().Name == "Microsoft.AspNetCore.Server.Kestrel.Core" &&
                  string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_PORT"));


app.Logger.LogInformation(
    "Host: {ServerType}; Process ID: {ProcessId}; Interactive: {Interactive}; Desktop mode: {DesktopMode}; Monitoring worker: enabled",
    app.Services.GetRequiredService<IServer>().GetType().Name,
    Environment.ProcessId,
    Environment.UserInteractive,
    desktopMode);

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

static async Task<int> RunCollectorModeAsync(string[] args)
{
    if (args.Length != 2)
    {
        return 1;
    }

    // CollectorProcess redirects stdin/stdout as UTF-8 pipes. Do not change
    // console code pages: a headless IIS or service child has no console.
    using var input = Console.OpenStandardInput();
    var server = await JsonSerializer.DeserializeAsync<ServerDefinition>(input);

    if (server is null)
    {
        return 1;
    }

    var metrics = WmiCollector.Collect(server, args[1]);

    using var output = Console.OpenStandardOutput();
    await JsonSerializer.SerializeAsync(output, metrics);
    await output.FlushAsync();

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
    services.AddSingleton<MonitoringWorker>();
    services.AddHostedService<MonitoringWorker>(provider => provider.GetRequiredService<MonitoringWorker>());
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