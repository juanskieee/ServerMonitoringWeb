using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This probe requires Windows PowerShell 5.1 and a domain Windows identity.");
    return 1;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
try
{
    if (args is ["--self-check"])
    {
        await CheckProcessCleanup();
        return 0;
    }

    if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--invalid-interface"))
        throw new ArgumentException("Usage: CollectionProbe <server-id> [--invalid-interface] | --self-check");

    var inventory = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "servers.json")))!;
    var server = inventory["Servers"]!.AsArray().SingleOrDefault(s => (string?)s?["Id"] == args[0])
        ?? throw new ArgumentException($"Server '{args[0]}' was not found in servers.json.");
    var ip = IPAddress.Parse((string)server["IpAddress"]!);
    var computerName = (string?)server["ComputerName"] ?? "";
    var adapter = args.Length == 2 ? "__PHASE1_MISSING_INTERFACE__" : (string?)server["NetworkInterface"] ?? "";

    Console.Error.WriteLine($"Host: {Environment.MachineName}; identity: {WindowsIdentity.GetCurrent().Name}");
    Console.Error.WriteLine("This is one explicit inventory probe; Enabled is reserved for the future monitoring worker.");
    using var ping = new Ping();
    var reply = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(1000), cancellationToken: shutdown.Token);
    var output = new JsonObject
    {
        ["ServerId"] = args[0], ["IpAddress"] = ip.ToString(),
        ["Status"] = reply.Status == IPStatus.Success ? "Online" : "Offline",
        ["PingStatus"] = reply.Status.ToString(),
        ["ResponseTimeMs"] = reply.Status == IPStatus.Success ? JsonValue.Create(reply.RoundtripTime) : null
    };
    if (reply.Status != IPStatus.Success)
    {
        output["Warning"] = "ICMP failed; CIM collection was skipped.";
        WriteResult(output);
        return 2;
    }

    if (string.IsNullOrWhiteSpace(computerName) || !computerName.Contains('.') ||
        Uri.CheckHostName(computerName) != UriHostNameType.Dns || IPAddress.TryParse(computerName, out _))
        throw new ArgumentException("Set ComputerName to the server's confirmed domain DNS name in servers.json; friendly CSV names are not DNS names.");

    var addresses = await Dns.GetHostAddressesAsync(computerName, shutdown.Token).WaitAsync(TimeSpan.FromSeconds(3), shutdown.Token);
    if (!addresses.Contains(ip))
        throw new ArgumentException($"DNS name '{computerName}' does not resolve to monitoring IP {ip}; confirm the mapping before collection.");

    var start = PowerShellStart();
    foreach (var argument in new[] { "-File", Path.Combine(AppContext.BaseDirectory, "ReadServerMetrics.ps1"),
        "-ComputerName", computerName, "-NetworkInterface", adapter })
        start.ArgumentList.Add(argument);

    try
    {
        var (exitCode, stdout, stderr) = await RunProcess(start, TimeSpan.FromSeconds(10), shutdown.Token);
        if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.Trim());
        if (string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException($"PowerShell exited with code {exitCode} without JSON; check script signing/execution policy and stderr.");
        var metrics = JsonNode.Parse(stdout)?.AsObject() ?? throw new InvalidOperationException("Expected one metrics JSON object.");
        output["Metrics"] = metrics;
        var total = ReadNumber(metrics, "TotalMemoryKiB");
        var free = ReadNumber(metrics, "FreeMemoryKiB");
        var cpu = ReadNumber(metrics, "CpuPercent");
        output["DisplayValues"] = new JsonObject
        {
            ["CpuPercent"] = cpu is >= 0 and <= 100 ? cpu : null,
            ["RamPercent"] = total is > 0 && free is >= 0 && free <= total ? 100 * (total - free) / total : null,
            ["DiskCFreeGB"] = ReadNumber(metrics, "DiskCFreeBytes") / 1_000_000_000,
            ["DiskDFreeGB"] = ReadNumber(metrics, "DiskDFreeBytes") / 1_000_000_000,
            ["NetworkMbps"] = ReadNumber(metrics, "NetworkBytesPerSecond") * 8 / 1_000_000
        };
        WriteResult(output);
        return exitCode == 0 ? 0 : 3;
    }
    catch (Exception ex) when (ex is TimeoutException or JsonException or InvalidOperationException)
    {
        output["Warning"] = ex.Message;
        WriteResult(output);
        return 3;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Probe cancelled; the owned metrics process has been stopped.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static void WriteResult(JsonObject output)
{
    output["LastCheckedUtc"] = DateTimeOffset.UtcNow.ToString("O");
    Console.WriteLine(output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

static double? ReadNumber(JsonObject metrics, string key) =>
    metrics[key] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) && number >= 0 ? number : null;

static ProcessStartInfo PowerShellStart() => new()
{
    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    StandardOutputEncoding = Encoding.UTF8,
    StandardErrorEncoding = Encoding.UTF8,
    ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive" }
};

static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcess(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellation)
{
    cancellation.ThrowIfCancellationRequested();
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
    // Begin both reads before waiting, so a full stdout/stderr pipe cannot hang the child.
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    deadline.CancelAfter(timeout);
    try
    {
        await process.WaitForExitAsync(deadline.Token);
        return (process.ExitCode, await stdout, await stderr);
    }
    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
    {
        throw new TimeoutException($"Metrics process exceeded {timeout.TotalSeconds:0.##} seconds; the owned process was stopped.");
    }
    finally
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdout, stderr);
    }
}

static async Task CheckProcessCleanup()
{
    // Only built-in commands: no network, unsigned files, policy bypass, or remote changes.
    foreach (var cancel in new[] { false, true })
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"collection-probe-{Guid.NewGuid():N}.pid");
        try
        {
            var start = PowerShellStart();
            start.Environment["COLLECTION_PROBE_PID_FILE"] = pidFile;
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("[IO.File]::WriteAllText($env:COLLECTION_PROBE_PID_FILE, [string]$PID); Start-Sleep -Seconds 30");
            using var cancellation = new CancellationTokenSource();
            if (cancel) cancellation.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await RunProcess(start, TimeSpan.FromSeconds(cancel ? 10 : 2), cancellation.Token);
                throw new Exception("Expected process timeout/cancellation did not occur.");
            }
            catch (TimeoutException) when (!cancel) { }
            catch (OperationCanceledException) when (cancel) { }

            var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
            try
            {
                using var child = Process.GetProcessById(pid);
                if (!child.HasExited) throw new Exception($"Owned PowerShell process {pid} is still running.");
            }
            catch (ArgumentException) { /* No process with this ID remains. */ }
            Console.WriteLine($"PASS: {(cancel ? "cancellation" : "timeout")} stopped the owned PowerShell process ({pid}).");
        }
        finally { File.Delete(pidFile); }
    }
}
