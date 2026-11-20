using System.Text.Json;
using System.Security.Cryptography;

namespace ServerMonitoringWeb.Models;

public sealed record ServerDefinition
{
    // Unique inventory identifier (for example: srv01).
    public string Id { get; init; } = "";

    // Display name shown on the dashboard.
    public string Name { get; init; } = "";

    // IP addresses used for ping and metrics collection.
    public string[] IpAddresses { get; init; } = [];

    // Optional computer name used for identification.
    public string ComputerName { get; init; } = "";

    // Account used for WMI collection.
    public string Username { get; init; } = "";

    // Password for the WMI account.
    public string Password { get; init; } = "";

    // Enables or pauses monitoring for this server.
    public bool Enabled { get; init; }

    // Configuration fingerprint used to invalidate outdated snapshots.
    public string GetRevision() =>
        Convert.ToHexString(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(this)));
}