using System.Globalization;
using System.Management;
using ServerMonitoringWeb.Models;

namespace ServerMonitoringWeb.Services;

internal static class WmiCollector
{
    // Monitoring only reads target data. Nothing in this collector modifies
    // remote machines, services, configuration, or operating-system settings.
    public static CollectionMetrics Collect(
        ServerDefinition server,
        string ipAddress)
    {
        var metrics = new CollectionMetrics();

        try
        {
            ManagementScope scope;

            var isLocal =
                !string.IsNullOrWhiteSpace(server.ComputerName) &&
                string.Equals(
                    server.ComputerName,
                    Environment.MachineName,
                    StringComparison.OrdinalIgnoreCase);

            if (isLocal)
            {
                scope = new ManagementScope(
                    @"\\localhost\root\cimv2");
            }
            else
            {
                var options = new ConnectionOptions
                {
                    Username = server.Username,
                    Password = server.Password,
                    Impersonation = ImpersonationLevel.Impersonate,
                    Authentication = AuthenticationLevel.PacketPrivacy,
                    Timeout = TimeSpan.FromSeconds(3)
                };

                scope = new ManagementScope(
                    $@"\\{ipAddress}\root\cimv2",
                    options);
            }

            scope.Connect();
            scope.Connect();

            // Collect each metric independently.
            // One failed metric should not prevent other readings.
            ReadMetric(
                "CPU",
                () => CollectCpu(scope, metrics),
                metrics,
                server.Password);

            ReadMetric(
                "RAM",
                () => CollectMemory(scope, metrics),
                metrics,
                server.Password);

            ReadMetric(
                "Disks",
                () => CollectDisks(scope, metrics),
                metrics,
                server.Password);
        }
        catch (Exception ex)
        {
            metrics.Warnings.Add(
                CollectionFailure.Describe(
                    ex,
                    server.Password));
        }

        return metrics;
    }

    private static void ReadMetric(
        string metricName,
        Action collectMetric,
        CollectionMetrics metrics,
        string password)
    {
        try
        {
            collectMetric();
        }
        catch (Exception ex)
        {
            metrics.Warnings.Add(
                $"{metricName}: {CollectionFailure.Describe(ex, password)}");
        }
    }

    private static void CollectCpu(
        ManagementScope scope,
        CollectionMetrics metrics)
    {
        // PowerShell equivalent:
        // Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor |
        //     Where-Object Name -eq "_Total" |
        //     Select-Object Name, PercentProcessorTime

        const string formattedCpuQuery =
            "SELECT PercentProcessorTime " +
            "FROM Win32_PerfFormattedData_PerfOS_Processor " +
            "WHERE Name='_Total'";

        try
        {
            // Preferred source because Windows already calculated the value.
            var row = Query(
                scope,
                formattedCpuQuery).FirstOrDefault();

            if (row is not null)
            {
                metrics.CpuPercent = GetDouble(
                    row,
                    "PercentProcessorTime");
            }
        }
        catch (ManagementException)
        {
            // Some targets do not expose formatted counters.
            // Fall back to Win32_Processor below.
        }

        if (metrics.CpuPercent is >= 0 and <= 100)
        {
            return;
        }

        metrics.CpuPercent = null;

        const string fallbackCpuQuery =
            "SELECT LoadPercentage, NumberOfLogicalProcessors " +
            "FROM Win32_Processor";

        var processors = Query(
            scope,
            fallbackCpuQuery);

        double weightedLoad = 0;
        double totalLogicalProcessors = 0;

        foreach (var processor in processors)
        {
            var loadPercentage =
                GetDouble(processor, "LoadPercentage");

            var logicalProcessors =
                GetDouble(processor, "NumberOfLogicalProcessors");

            if (loadPercentage is not (>= 0 and <= 100) ||
                logicalProcessors is not > 0)
            {
                throw new InvalidDataException(
                    "Processor load or logical processor count is unavailable.");
            }

            // Weight the load by logical processor count so systems
            // with multiple CPU packages are represented correctly.
            weightedLoad +=
                loadPercentage.Value *
                logicalProcessors.Value;

            totalLogicalProcessors +=
                logicalProcessors.Value;
        }

        if (totalLogicalProcessors == 0)
        {
            throw new InvalidDataException(
                "No CPU reading returned.");
        }

        metrics.CpuPercent =
            weightedLoad / totalLogicalProcessors;
    }

    private static void CollectMemory(
        ManagementScope scope,
        CollectionMetrics metrics)
    {
        // PowerShell equivalent:
        // Get-CimInstance Win32_OperatingSystem |
        //     Select-Object TotalVisibleMemorySize, FreePhysicalMemory

        const string query =
            "SELECT TotalVisibleMemorySize, FreePhysicalMemory " +
            "FROM Win32_OperatingSystem";

        var row = Query(scope, query)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                "No memory reading returned.");

        var totalMemoryKb =
            GetDouble(row, "TotalVisibleMemorySize");

        var freeMemoryKb =
            GetDouble(row, "FreePhysicalMemory");

        if (totalMemoryKb > 0 &&
            freeMemoryKb >= 0 &&
            freeMemoryKb <= totalMemoryKb)
        {
            // Used % = ((Total - Free) / Total) * 100
            metrics.RamPercent =
                100 * (totalMemoryKb - freeMemoryKb) / totalMemoryKb;

            return;
        }

        throw new InvalidDataException(
            "Memory counters are unavailable.");
    }

    private static void CollectDisks(
        ManagementScope scope,
        CollectionMetrics metrics)
    {
        // PowerShell equivalent:
        // Get-CimInstance Win32_LogicalDisk |
        //     Where-Object DriveType -eq 3 |
        //     Select-Object DeviceID, FreeSpace, Size

        const string query =
            "SELECT DeviceID, FreeSpace, Size " +
            "FROM Win32_LogicalDisk " +
            "WHERE DriveType = 3";

        foreach (var disk in Query(scope, query))
        {
            var driveName =
                GetString(disk, "DeviceID");

            if (string.IsNullOrWhiteSpace(driveName))
            {
                continue;
            }

            metrics.Disks.Add(new DiskMetrics
            {
                Name = driveName,
                FreeGb = GetDouble(disk, "FreeSpace") / 1_000_000_000,
                SizeGb = GetDouble(disk, "Size") / 1_000_000_000
            });
        }

        metrics.Disks =
        [
            .. metrics.Disks.OrderBy(disk => disk.Name, StringComparer.OrdinalIgnoreCase)
        ];

        if (metrics.Disks.Count == 0)
        {
            metrics.Warnings.Add(
                "Disks: No fixed drives returned.");
        }
    }

    private static List<Dictionary<string, object?>> Query(
        ManagementScope scope,
        string query)
    {
        using var searcher =
            new ManagementObjectSearcher(
                scope,
                new ObjectQuery(query));

        using var results =
            searcher.Get();

        var rows =
            new List<Dictionary<string, object?>>();

        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                // Convert WMI objects into simple dictionaries.
                // This keeps the rest of the collector easy to debug
                // because every metric reads from the same data shape.
                var row =
                    item.Properties
                        .Cast<PropertyData>()
                        .ToDictionary(
                            property => property.Name,
                            property => (object?)property.Value,
                            StringComparer.OrdinalIgnoreCase);

                rows.Add(row);
            }
        }

        return rows;
    }

    private static string GetString(
        Dictionary<string, object?> row,
        string key)
    {
        if (!row.TryGetValue(key, out var value))
        {
            return "";
        }

        return value?.ToString() ?? "";
    }

    private static double? GetDouble(
        Dictionary<string, object?> row,
        string key)
    {
        if (!row.TryGetValue(key, out var value) ||
            value is null)
        {
            return null;
        }

        var text =
            Convert.ToString(
                value,
                CultureInfo.InvariantCulture);

        if (!double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return null;
        }

        return double.IsFinite(number)
            ? number
            : null;
    }
}
