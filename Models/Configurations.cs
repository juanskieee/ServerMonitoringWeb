using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServerMonitoringWeb.Services;

namespace ServerMonitoringWeb.Models;

/// <summary>
/// Settings read from the Monitoring section of appsettings.json at startup.
/// </summary>
public sealed class MonitoringOptions
{
    // Program.cs validates these ranges at startup. Restart after changing settings.
    // DataRoot is resolved separately by DataPaths.

    // Ping and metrics use separate timers so slow WMI does not delay ping.
    [Range(1, 3600)]
    public int PingIntervalSeconds { get; set; } = 1;

    // Interval between CPU, RAM, and disk checks.
    [Range(1, 3600)]
    public int DetailsIntervalSeconds { get; set; } = 1;

    // Maximum wait for each IP's ping reply.
    [Range(1, 60000)]
    public int PingTimeoutMilliseconds { get; set; } = 1000;

    // Maximum lifetime of each WMI child process, per attempted IP.
    [Range(1, 300)]
    public int MetricsTimeoutSeconds { get; set; } = 10;

    // This limit applies independently to ping and metrics collection.
    [Range(1, 20)]
    public int MaxConcurrentChecks { get; set; } = 4;

    // Outage history uses UTC dates; retention includes today's file.
    [Range(1, 365)]
    public int HistoryRetentionDays { get; set; } = 7;
}

/// <summary>
/// Inventory and validation errors supplied to the dashboard.
/// </summary>
public sealed class MonitoringConfiguration
{
    public IReadOnlyList<ServerDefinition> Servers { get; }

    public IReadOnlyList<string> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public MonitoringConfiguration(DataPaths paths)
    {
        // Reload on each request so inventory edits appear without restarting.
        var inventory = InventoryConfiguration.Load(paths.Inventory);

        Servers = inventory.Servers;
        Errors = inventory.Errors;
    }
}

/// <summary>
/// Loads and validates servers.json for both the worker and dashboard.
/// </summary>
public sealed class InventoryConfiguration
{
    private const int MaximumInventorySizeBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,

        // Reject unknown fields so misspelled settings are reported.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public IReadOnlyList<ServerDefinition> Servers { get; }

    public IReadOnlyList<string> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    private InventoryConfiguration(
        IReadOnlyList<ServerDefinition> servers,
        IReadOnlyList<string> errors)
    {
        Servers = servers;
        Errors = errors;
    }

    public static InventoryConfiguration Load(string path)
    {
        try
        {
            using var file = File.OpenRead(path);

            // Check the size before loading the inventory into memory.
            if (file.Length > MaximumInventorySizeBytes)
            {
                return new InventoryConfiguration(
                    [],
                    ["Inventory exceeds the 1 MiB limit."]);
            }

            var inventory = JsonSerializer.Deserialize<ServerInventory>(file, JsonOptions);
            var servers = inventory?.Servers;

            if (servers is null || servers.Count == 0)
            {
                return new InventoryConfiguration(
                    [],
                    [$"Inventory must contain a nonempty Servers array. Add servers to {path}."]);
            }

            return ValidateServers(servers);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Return expected file/JSON errors so the dashboard can explain the problem.
            return new InventoryConfiguration(
                [],
                [$"Could not read inventory at {path}: {ex.Message}"]);
        }
    }

    private static InventoryConfiguration ValidateServers(List<ServerDefinition?> servers)
    {
        var loadedServers = new List<ServerDefinition>();
        var errors = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < servers.Count; index++)
        {
            var server = servers[index];

            if (server is null)
            {
                errors.Add($"Servers[{index}]: entry must not be null.");
                continue;
            }

            var serverLabel = string.IsNullOrWhiteSpace(server.Id)
                ? "(missing ID)"
                : server.Id;

            ValidateIdentity(server, serverLabel, seenIds, errors);
            ValidateIpAddresses(server, serverLabel, errors);
            ValidateOptionalFields(server, serverLabel, errors);

            loadedServers.Add(server);
        }

        // Keep invalid entries so all problems can be displayed together.
        // Callers must check IsValid before using the inventory for monitoring.
        return new InventoryConfiguration(loadedServers, errors);
    }

    private static void ValidateIdentity(
        ServerDefinition server,
        string serverLabel,
        HashSet<string> seenIds,
        List<string> errors)
    {
        // IDs become snapshot keys, including for paused servers.
        // HashSet.Add returns false when an ID has already been seen, ignoring case.
        if (!IsValidId(server.Id))
        {
            errors.Add(
                $"{serverLabel}: Id must be 1-64 ASCII letters, digits, hyphens or underscores.");
        }
        else if (!seenIds.Add(server.Id))
        {
            errors.Add($"{serverLabel}: duplicate Id (case insensitive).");
        }

        // Paused servers may have blank names; null or overlong names are always invalid.
        if (server.Name is null ||
            server.Name.Length > 128 ||
            (server.Enabled && string.IsNullOrWhiteSpace(server.Name)))
        {
            errors.Add(
                $"{serverLabel}: Name must be at most 128 characters and is required when enabled.");
        }
    }

    private static void ValidateIpAddresses(
        ServerDefinition server,
        string serverLabel,
        List<string> errors)
    {
        var addresses = server.IpAddresses;

        // Enabled servers need an address; paused servers may have an empty array.
        if (addresses is null ||
            addresses.Length > 8 ||
            (server.Enabled && addresses.Length == 0))
        {
            errors.Add(
                $"{serverLabel}: IpAddresses must be an array of at most eight addresses, with at least one address when enabled.");
            return;
        }

        // Preserve address order because it controls which successful IP is preferred.
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < addresses.Length; index++)
        {
            var address = addresses[index];

            // && stops at a failed check, so null/blank values never reach Trim.
            // TryParse validates without throwing; out _ discards the parsed address.
            bool isValidAddress =
                !string.IsNullOrWhiteSpace(address) &&
                address == address.Trim() &&
                IPAddress.TryParse(address, out _);

            if (!isValidAddress)
            {
                errors.Add(
                    $"{serverLabel}: IpAddresses[{index}] must be a valid IP without surrounding spaces.");
            }

            // Check duplicates even for invalid values so every problem is reported.
            if (!seenAddresses.Add(address))
            {
                errors.Add($"{serverLabel}: duplicate IP address at IpAddresses[{index}].");
            }
        }
    }

    private static void ValidateOptionalFields(
        ServerDefinition server,
        string serverLabel,
        List<string> errors)
    {
        var computerName = server.ComputerName;

        // ComputerName is metadata. Blank is allowed; supplied names must be DNS names.
        if (computerName is null ||
            computerName.Length > 253 ||
            (computerName.Length > 0 &&
             Uri.CheckHostName(computerName) != UriHostNameType.Dns))
        {
            errors.Add($"{serverLabel}: ComputerName must be a hostname, a DNS name, or blank.");
        }

        // Supplied usernames cannot contain control characters such as tabs or newlines.
        // Passwords are left untouched, including whitespace.
        var username = server.Username;

        if (username is not null &&
            (username.Length > 256 || username.Any(char.IsControl)))
        {
            errors.Add(
                $"{serverLabel}: Username must be at most 256 characters without control characters; use COMPUTER\\User or DOMAIN\\User.");
        }
    }

    private static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
        {
            return false;
        }

        // All returns true only when every character follows these ASCII rules.
        return id.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character == '-' ||
            character == '_');
    }

    // Matches the top-level { "Servers": [...] } object in servers.json.
    private sealed class ServerInventory
    {
        public List<ServerDefinition?>? Servers { get; init; }
    }
}
