#requires -Version 5.1
# Focused offline checks. Sign both scripts with an approved publisher on AllSigned hosts.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$collector = Join-Path $PSScriptRoot 'ReadServerMetrics.ps1'
$state = @{ Scenario = ''; CpuCalls = 0; NetworkCalls = 0; Removed = 0 }

# These mocks live only in this test script's scope; no remote server is contacted.
function New-CimSession {
    [CmdletBinding()]
    param($ComputerName, $Authentication, $OperationTimeoutSec)
    if ($Authentication -ne 'Kerberos') { throw 'Expected Kerberos.' }
    if ($state.Scenario -eq 'Denied') { throw [UnauthorizedAccessException]::new('Access denied.') }
    return [pscustomobject]@{ ComputerName = $ComputerName }
}
function Remove-CimSession {
    [CmdletBinding()]
    param($CimSession)
    $state.Removed++
}
function Start-Sleep { param($Seconds) }
function Get-CimInstance {
    [CmdletBinding()]
    param($CimSession, $Namespace, $OperationTimeoutSec, $ClassName, $Filter, $Property)
    switch ($ClassName) {
        'Win32_PerfFormattedData_PerfOS_Processor' {
            $state.CpuCalls++
            $timestamp = if ($state.Scenario -eq 'Uninitialized') { 0 } else { $state.CpuCalls * 10000000 }
            return [pscustomobject]@{ Name = '_Total'; PercentProcessorTime = 0; Timestamp_Sys100NS = $timestamp }
        }
        'Win32_PerfFormattedData_Tcpip_NetworkInterface' {
            $state.NetworkCalls++
            $timestamp = if ($state.Scenario -eq 'Uninitialized') { 0 } else { $state.NetworkCalls * 10000000 }
            return [pscustomobject]@{ Name = 'Ethernet [Team]'; BytesTotalPerSec = 0; Timestamp_Sys100NS = $timestamp }
        }
        'Win32_OperatingSystem' {
            if ($state.Scenario -eq 'MemoryDenied') { throw [UnauthorizedAccessException]::new('Access denied.') }
            $total = if ($state.Scenario -eq 'InvalidMemory') { 0 } else { 8000000 }
            return [pscustomobject]@{ TotalVisibleMemorySize = $total; FreePhysicalMemory = 2000000 }
        }
        'Win32_LogicalDisk' {
            [pscustomobject]@{ DeviceID = 'C:'; FreeSpace = 0 }
            if ($state.Scenario -ne 'MissingD') { [pscustomobject]@{ DeviceID = 'D:'; FreeSpace = 25000000000 } }
        }
        default { throw "Unexpected class: $ClassName" }
    }
}
function Assert-That($Condition, $Message) { if (-not $Condition) { throw $Message } }

foreach ($scenario in @('Healthy', 'MissingD', 'InvalidInterface', 'Uninitialized', 'InvalidMemory', 'MemoryDenied', 'Denied')) {
    $state.Scenario = $scenario
    $state.CpuCalls = 0
    $state.NetworkCalls = 0
    $state.Removed = 0
    $interface = if ($scenario -eq 'InvalidInterface') { 'Ethernet*' } else { 'Ethernet [Team]' }
    $json = & $collector -ComputerName 'server.example.invalid' -NetworkInterface $interface
    $result = $json | ConvertFrom-Json
    Assert-That ($json -is [string]) 'Expected exactly one JSON output string.'
    if ($scenario -eq 'Denied') {
        Assert-That ($LASTEXITCODE -eq 1 -and $null -eq $result.CpuPercent -and $result.Warnings.Collection -eq 'Access denied.') 'Connection denial must return null metrics and a warning.'
        Assert-That ($state.Removed -eq 0) 'No session was created for a denied connection.'
    } else {
        Assert-That ($LASTEXITCODE -eq 0 -and $state.Removed -eq 1) 'A successful connection must remove its session exactly once.'
        Assert-That ($result.DiskCFreeBytes -eq 0) 'Real zero free bytes must remain zero.'
        switch ($scenario) {
            'Healthy' { Assert-That ($result.CpuPercent -eq 0 -and $result.NetworkBytesPerSecond -eq 0 -and @($result.Warnings.PSObject.Properties).Count -eq 0) 'Healthy zero counters must remain available.' }
            'MissingD' { Assert-That ($null -eq $result.DiskDFreeBytes -and $result.Warnings.DiskD -and $result.CpuPercent -eq 0) 'Missing D: must preserve the other metrics.' }
            'InvalidInterface' { Assert-That ($null -eq $result.NetworkBytesPerSecond -and $result.Warnings.Network -and $result.CpuPercent -eq 0) 'Adapter names must match literally, not as wildcards.' }
            'Uninitialized' { Assert-That ($null -eq $result.CpuPercent -and $null -eq $result.NetworkBytesPerSecond -and $result.Warnings.Cpu -and $result.Warnings.Network) 'Uninitialized counters must be null, not zero.' }
            'InvalidMemory' { Assert-That ($null -eq $result.TotalMemoryKiB -and $result.Warnings.Ram) 'A zero memory total must be unavailable.' }
            'MemoryDenied' { Assert-That ($null -eq $result.TotalMemoryKiB -and $result.Warnings.Ram -eq 'Access denied.' -and $result.CpuPercent -eq 0) 'A denied metric must preserve successful metrics.' }
        }
    }
    Write-Output "PASS: $scenario"
}
