using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
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

var builder = WebApplication.CreateBuilder(args);

var options = BuildMonitoringOptions(builder);

var paths = CreateDataPaths(builder);

RegisterServices(
    builder.Services,
    options,
    paths);

var app = builder.Build();

app.Logger.LogInformation(
    "Inventory: {Inventory}",
    paths.Inventory);

app.Logger.LogInformation(
    "Data Directory: {DataDirectory}",
    paths.DataDirectory);

app.Logger.LogInformation(
    "Latest Results: {LatestResults}",
    paths.LatestResults);

app.Logger.LogInformation(
    "History Directory: {HistoryDirectory}",
    paths.HistoryDirectory);

ConfigurePipeline(app);

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

static MonitoringOptions BuildMonitoringOptions(
    WebApplicationBuilder builder)
{
    var options =
        new MonitoringOptions();

    builder.Configuration
        .GetSection("Monitoring")
        .Bind(options);

    Validator.ValidateObject(
        options,
        new ValidationContext(options),
        validateAllProperties: true);

    return options;
}

static DataPaths CreateDataPaths(
    WebApplicationBuilder builder)
{
    return new DataPaths(
        builder.Configuration["Monitoring:DataRoot"],
        builder.Environment.ContentRootPath);
}

static void RegisterServices(
    IServiceCollection services,
    MonitoringOptions options,
    DataPaths paths)
{
    services.AddSingleton(paths);
    services.AddSingleton(options);

    services.AddSingleton<ResultStore>();
    services.AddSingleton<ServerCollector>();

    // Reload inventory data for each request so
    // servers.json changes appear automatically.
    services.AddScoped<MonitoringConfiguration>();

    services.AddScoped<SnapshotStore>();

    // Runs the background monitoring loops.
    services.AddHostedService<MonitoringWorker>();

    services.AddRazorPages();
}

static void ConfigurePipeline(
    WebApplication app)
{
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    app.UseStaticFiles();

    app.UseRouting();

    app.UseAuthorization();

    app.MapRazorPages();
}