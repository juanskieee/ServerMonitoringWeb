using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ServerMonitoringWeb.Models;

namespace ServerMonitoringWeb.Services;

internal static class CollectorProcess
{
    public static async Task<CollectionMetrics> CollectAsync(
        ServerDefinition server,
        string ipAddress,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var serverJson =
            JsonSerializer.Serialize(server);

        var (exitCode, output) =
            await RunAsync(
                ["--collect-once", ipAddress],
                serverJson,
                timeout,
                cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Collector exited with code {exitCode}");
        }

        return JsonSerializer.Deserialize<CollectionMetrics>(output)
               ?? throw new InvalidDataException(
                   "Collector returned invalid JSON.");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string[] arguments,
        string input,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process =
            new Process
            {
                StartInfo = CreateStartInfo(arguments)
            };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Failed to start collector process.");
        }

        // Continuously drain stdout/stderr while the process runs.
        // This prevents pipe buffers from filling and blocking the child process.
        var outputTask =
            process.StandardOutput.ReadToEndAsync(
                CancellationToken.None);

        var errorTask =
            process.StandardError.ReadToEndAsync(
                CancellationToken.None);

        using var deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        deadline.CancelAfter(timeout);

        try
        {
            // Credentials are sent through stdin rather than command-line
            // arguments so they do not appear in process lists.
            await process.StandardInput.WriteAsync(
                input.AsMemory(),
                deadline.Token);

            process.StandardInput.Close();

            await process.WaitForExitAsync(
                deadline.Token);
        }
        catch
        {
            // If collection hangs, terminate the entire process tree.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(
                CancellationToken.None);

            await Task.WhenAll(
                outputTask,
                errorTask);

            cancellationToken.ThrowIfCancellationRequested();

            if (deadline.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Collector timed out after {timeout.TotalSeconds} seconds.");
            }

            throw;
        }

        var output =
            await outputTask;

        // Drain stderr without exposing raw text. Valid JSON and a successful
        // exit are sufficient even if a headless child writes diagnostics.
        await errorTask;

        return (
            process.ExitCode,
            output);
    }

    private static ProcessStartInfo CreateStartInfo(
        string[] arguments)
    {
        var startInfo =
            new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,

                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                StandardInputEncoding =
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false),

                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,

                WorkingDirectory =
                    AppContext.BaseDirectory
            };

        var executablePath =
            Path.ChangeExtension(
                Assembly.GetExecutingAssembly().Location,
                ".exe");

        if (File.Exists(executablePath))
        {
            // Published deployment.
            startInfo.FileName =
                executablePath;
        }
        else
        {
            // Local development.
            startInfo.FileName = "dotnet";

            startInfo.ArgumentList.Add(
                Assembly.GetExecutingAssembly().Location);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}