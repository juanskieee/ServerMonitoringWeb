#requires -Version 5.1
<#
Read-only target metrics over one Kerberos/WSMan CIM session, using the current
Windows identity. Sign this file with your approved publisher on AllSigned hosts.
Run through Tools/CollectionProbe to enforce the whole-process timeout.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(?=.{1,253}$)(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$')]
    [string]$ComputerName,

    [ValidateLength(0, 256)]
    [string]$NetworkInterface = '',

    [ValidateRange(1, 5)]
    [int]$OperationTimeoutSeconds = 2,

    [ValidateRange(1, 3)]
    [int]$SampleSeconds = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

function Get-FailureWarning($Failure) {
    [Console]::Error.WriteLine($Failure.ToString())
    if ($Failure.CategoryInfo.Category -eq 'PermissionDenied' -or
        $Failure.Exception.Message -match 'access.denied|0x80070005|0x80041003') {
        return 'Access denied.'
    }
    return 'Collection failed; see stderr diagnostics.'
}

$result = [ordered]@{
    ComputerName = $ComputerName
    SampleStartedUtc = [DateTime]::UtcNow.ToString('o')
    SampleCompletedUtc = $null
    CpuPercent = $null
    TotalMemoryKiB = $null
    FreeMemoryKiB = $null
    DiskCFreeBytes = $null
    DiskDFreeBytes = $null
    NetworkBytesPerSecond = $null
    NetworkInterfaces = @()
    Warnings = [ordered]@{}
}
$session = $null
$exitCode = 0

try {
    $session = New-CimSession -ComputerName $ComputerName -Authentication Kerberos `
        -OperationTimeoutSec $OperationTimeoutSeconds -ErrorAction Stop
    $query = @{
        CimSession = $session
        Namespace = 'root/cimv2'
        OperationTimeoutSec = $OperationTimeoutSeconds
        ErrorAction = 'Stop'
    }

    # Discard the first performance readings. Require advancing target timestamps
    # after the sampling delay so an uninitialized reading does not become zero.
    $cpuBaseline = $null
    $networkBaseline = @()
    try {
        $cpuBaseline = Get-CimInstance @query -ClassName Win32_PerfFormattedData_PerfOS_Processor `
            -Filter "Name = '_Total'" -Property Name, PercentProcessorTime, Timestamp_Sys100NS
    } catch { $result.Warnings.Cpu = Get-FailureWarning $_ }
    try {
        $networkBaseline = @(Get-CimInstance @query -ClassName Win32_PerfFormattedData_Tcpip_NetworkInterface `
            -Property Name, BytesTotalPerSec, Timestamp_Sys100NS)
        $result.NetworkInterfaces = @($networkBaseline | ForEach-Object { $_.Name })
    } catch { $result.Warnings.Network = Get-FailureWarning $_ }

    Start-Sleep -Seconds $SampleSeconds

    if ($null -ne $cpuBaseline) {
        try {
            $cpu = Get-CimInstance @query -ClassName Win32_PerfFormattedData_PerfOS_Processor `
                -Filter "Name = '_Total'" -Property Name, PercentProcessorTime, Timestamp_Sys100NS
            if ($null -eq $cpu -or $null -eq $cpu.PercentProcessorTime -or
                $cpuBaseline.Timestamp_Sys100NS -le 0 -or
                $cpu.Timestamp_Sys100NS -le $cpuBaseline.Timestamp_Sys100NS) {
                $result.Warnings.Cpu = 'CPU counter has not initialized or advanced.'
            } else {
                $result.CpuPercent = $cpu.PercentProcessorTime
            }
        } catch { $result.Warnings.Cpu = Get-FailureWarning $_ }
    } elseif (-not $result.Warnings.Contains('Cpu')) {
        $result.Warnings.Cpu = 'CPU counter is unavailable.'
    }

    try {
        $memory = Get-CimInstance @query -ClassName Win32_OperatingSystem `
            -Property TotalVisibleMemorySize, FreePhysicalMemory
        if ($null -eq $memory -or $null -eq $memory.TotalVisibleMemorySize -or
            $null -eq $memory.FreePhysicalMemory -or $memory.TotalVisibleMemorySize -le 0 -or
            $memory.FreePhysicalMemory -gt $memory.TotalVisibleMemorySize) {
            $result.Warnings.Ram = 'Memory totals are unavailable or invalid.'
        } else {
            $result.TotalMemoryKiB = $memory.TotalVisibleMemorySize
            $result.FreeMemoryKiB = $memory.FreePhysicalMemory
        }
    } catch { $result.Warnings.Ram = Get-FailureWarning $_ }

    try {
        $disks = @(Get-CimInstance @query -ClassName Win32_LogicalDisk `
            -Filter "DeviceID = 'C:' OR DeviceID = 'D:'" -Property DeviceID, FreeSpace)
        foreach ($drive in @('C', 'D')) {
            $disk = $disks | Where-Object { $_.DeviceID -eq "${drive}:" } | Select-Object -First 1
            if ($null -eq $disk -or $null -eq $disk.FreeSpace) {
                $result.Warnings["Disk$drive"] = "Drive ${drive}: is absent or its free space is unavailable."
            } else {
                $result["Disk${drive}FreeBytes"] = $disk.FreeSpace
            }
        }
    } catch {
        $warning = Get-FailureWarning $_
        $result.Warnings.DiskC = $warning
        $result.Warnings.DiskD = $warning
    }

    # Compare names literally in memory. No adapter name is inserted into WQL,
    # and multiple interfaces are never summed (teams/virtual NICs can overlap).
    if ([string]::IsNullOrWhiteSpace($NetworkInterface)) {
        $result.Warnings.Network = 'Network interface is not configured; choose a NetworkInterfaces entry.'
    } elseif (-not $result.Warnings.Contains('Network')) {
        $baseline = @($networkBaseline | Where-Object { $_.Name -ceq $NetworkInterface })
        if ($baseline.Count -ne 1) {
            $result.Warnings.Network = 'Configured network interface was not found uniquely.'
        } else {
            try {
                $adapters = @(Get-CimInstance @query -ClassName Win32_PerfFormattedData_Tcpip_NetworkInterface `
                    -Property Name, BytesTotalPerSec, Timestamp_Sys100NS)
                $adapter = @($adapters | Where-Object { $_.Name -ceq $NetworkInterface })
                if ($adapter.Count -ne 1) {
                    $result.Warnings.Network = 'Configured network interface disappeared during sampling.'
                } elseif ($null -eq $adapter[0].BytesTotalPerSec -or
                    $baseline[0].Timestamp_Sys100NS -le 0 -or
                    $adapter[0].Timestamp_Sys100NS -le $baseline[0].Timestamp_Sys100NS) {
                    $result.Warnings.Network = 'Network counter has not initialized or advanced.'
                } else {
                    $result.NetworkBytesPerSecond = $adapter[0].BytesTotalPerSec
                }
            } catch { $result.Warnings.Network = Get-FailureWarning $_ }
        }
    }
} catch {
    $result.Warnings.Collection = Get-FailureWarning $_
    $exitCode = 1
} finally {
    if ($null -ne $session) {
        try { Remove-CimSession -CimSession $session -ErrorAction Stop }
        catch { $result.Warnings.Cleanup = Get-FailureWarning $_ }
    }
}

$result.SampleCompletedUtc = [DateTime]::UtcNow.ToString('o')
$result | ConvertTo-Json -Depth 4 -Compress
exit $exitCode
