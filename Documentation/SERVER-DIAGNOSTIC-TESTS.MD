# Production server diagnostics — read-only only

Follow-up to issues #3, #5 and #7. These checks are intended for an authorized operator on a live production Windows server. They inspect only the monitoring application and its existing data. They do not change server settings, write test files, restart services, recycle pools, launch another application instance, or authenticate to remote servers.

**Do not run the old version of this checklist.** Permission remediation and active WMI tests are deliberately excluded. Do not change permissions, app pool settings, credentials, inventory, firewall, registry or DCOM configuration as part of this investigation. Any remediation or active remote test requires a separately approved procedure.

## 1. Hosting context — already collected

The hosting context was reported in issue #5. No need to repeat the broad IIS inventory commands. The monitoring application shares an IIS pool with other applications; do not restart, recycle or reconfigure it.

The terminal identity and the IIS application identity are not necessarily the same. A running pool alone does not prove the monitoring application started successfully.

## 2. Inspect the monitoring data directory

Run in Windows PowerShell. Substitute the actual data directory reported by the monitoring application if it differs from the example. These commands read file metadata and ACLs only. Do not paste raw ACL output into GitHub.

```powershell
$root = 'C:\inetpub\wwwroot\ServerMonitoringWeb'
$data = Join-Path $root 'Logs'

foreach ($path in @($data, (Join-Path $data 'latest.json'), (Join-Path $data 'History'))) {
    if (Test-Path -LiteralPath $path) {
        Get-Item -LiteralPath $path |
            Select-Object Name, Attributes, LastWriteTime
        (Get-Acl -LiteralPath $path).Access |
            Where-Object { $_.IdentityReference.Value -eq 'IIS AppPool\DefaultAppPool' } |
            Select-Object FileSystemRights, AccessControlType, IsInherited
    } else {
        Write-Output ('Missing: ' + (Split-Path $path -Leaf))
    }
}
```

The filtered output shows only directly matching pool ACL entries, not effective permissions. Group membership, inherited rules and deny entries may also affect access. An absent matching entry does not prove a permission failure. Ask the administrator to review effective access if needed. No write probe is permitted.

## 3. Check local server identification (srv05)

Read the deployed inventory in memory, compare with local interface addresses and print only non-sensitive boolean results. Do not print the inventory, addresses, names, credentials or raw errors. This does not query remote machines or change configuration.

```powershell
$root = 'C:\inetpub\wwwroot\ServerMonitoringWeb'
$inventory = $null
$entry = $null
try {
    $inventory = Get-Content -LiteralPath (Join-Path $root 'servers.json') -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    $entry = $inventory.Servers | Where-Object { $_.Id -eq 'srv05' } | Select-Object -First 1
    if ($null -eq $entry) {
        Write-Output 'srv05 entry not found'
    } else {
        $localIPs = @(Get-NetIPAddress -ErrorAction Stop | Select-Object -ExpandProperty IPAddress)
        [PSCustomObject]@{
            ServerId = 'srv05'
            HostnameMatches = ($entry.ComputerName -eq $env:COMPUTERNAME)
            LocalIPMatches = [bool]($entry.IpAddresses | Where-Object { $_ -in $localIPs })
        }
    }
} catch {
    Write-Output 'Local identification could not be completed; review privately.'
} finally {
    Remove-Variable inventory, entry, localIPs -ErrorAction SilentlyContinue
}
```

A hostname mismatch with a local IP match was already reported in issue #7. The revised collector identifies locality from the actual selected IP; this passive check is only needed again if configuration changes or the administrator requests it. Do not edit the inventory or run additional tests on production without approval.

## 4. Remote WMI errors — deferred

Use only the errors already present in existing application logs. Do not run remote WMI, credential prompts, ping probes, port tests or other active network tests under this checklist. They can create load or audit events, and failed authentication can contribute to lockout. Remote authorization testing must be separately planned and approved by the production administrator.

## 5. Observe the existing deployment

Without changing browser state, launching the EXE, redeploying or restarting IIS, observe whether the existing dashboard is available and whether the existing latest.json timestamp advances. Repeat the read-only metadata command later if needed:

```powershell
Get-Item -LiteralPath (Join-Path $data 'latest.json') |
    Select-Object Name, LastWriteTime, Length
```

A changing timestamp indicates a file is being updated; it does not prove every monitoring check succeeds.

## 6. Issue #9 — inspect hosting configuration without changes

Use existing logs first. If the production administrator authorizes another passive inspection,
the following commands show only the monitoring application's configured hosting model and
pool settings. They do not start a worker, query remote machines, or alter configuration.

```powershell
$root = 'C:\inetpub\wwwroot\ServerMonitoringWeb'
[xml]$config = Get-Content -LiteralPath (Join-Path $root 'web.config') -Raw
$config.SelectNodes('//aspNetCore') | Select-Object hostingModel

Import-Module WebAdministration
$application = Get-WebApplication |
    Where-Object { $_.Path -eq '/ServerMonitoringWeb' }
foreach ($item in $application) {
    $pool = Get-Item -LiteralPath ('IIS:\AppPools\' + $item.ApplicationPool)
    [PSCustomObject]@{
        StartMode = $pool.startMode
        IdleTimeout = $pool.processModel.idleTimeout
        IdentityType = $pool.processModel.identityType
    }
}
```

If the deployed application is not in the expected root or no matching IIS application is
found, stop and report that privately. Do not change the shared pool. A dedicated IIS pool
and preload configuration require administrator approval. The standalone EXE must not run
alongside the IIS host. Check the existing logs for the new Host startup message, and observe
the dashboard's freshness notice after approved deployment. The notice detects potentially
old readings, not their root cause.

## 7. Independent monitoring service (after approved installation only)

These checks do not install, start, stop or configure a service. Do not launch
the service EXE manually.

```powershell
Get-Service -Name ServerMonitoringWorker -ErrorAction SilentlyContinue |
    Select-Object Status, StartType
```

Inspect existing authorized logs privately: service startup should be present,
and the dashboard startup should show Monitoring worker: False. Observe the
existing snapshot timestamps over multiple collection cycles. A changing file
timestamp alone may reflect pings; verify LastMetricsUtc advances for servers
with authorized metrics as well. Do not close or restart production applications,
run remote WMI tests or change permissions for this checklist.

## 8. Headless subprocess smoke test — development laptop only

Do not execute this on production. Build the solution on a development Windows
machine, then run from the repository root. The subprocess receives null JSON
and exits before WMI, inventory access, or either monitoring loop; it exercises
the redirected input path without an attached console. It does not test service
installation or actual metrics collection.

```powershell
dotnet build .\ServerMonitoringWeb.sln
if ($LASTEXITCODE -ne 0) { throw 'Build failed; do not continue.' }

$exe = (Resolve-Path .\bin\Debug\net8.0\ServerMonitoringWeb.exe).Path
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $exe
$start.Arguments = '--collect-once 127.0.0.1'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [System.Diagnostics.Process]::Start($start)
try {
    $process.StandardInput.Write('null')
    $process.StandardInput.Close()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(10000)) {
        $process.Kill()  # Only the dedicated local smoke-test child.
        throw 'Collector smoke test timed out.'
    }
    if ($process.ExitCode -ne 1 -or
        $stdout.Result.Length -ne 0 -or $stderr.Result.Length -ne 0) {
        throw 'Collector smoke test failed.'
    }
    Write-Output 'PASS: headless collector rejected null input without a console error.'
} finally {
    $process.Dispose()
}
```

## Report only sanitized results

Do not upload terminal screenshots, complete logs, IIS inventories, hostnames, account names, credentials, IP addresses or raw ACLs to a public repository. Use an approved internal channel for sensitive evidence. Redact previously posted sensitive information and notify the appropriate administrator in accordance with company policy.

- [x] Hosting context collected (no repeat required).
- [ ] Monitoring data directory exists: yes / no / unknown.
- [ ] latest.json exists and timestamp advances: yes / no / unknown.
- [ ] Pool-specific ACL entry observed: allow / deny / none / unknown (not effective access).
- [ ] srv05 HostnameMatches: True / False / unknown.
- [ ] srv05 LocalIPMatches: True / False / unknown.
- [ ] Existing dashboard available: yes / no / unknown.
- [ ] Existing error codes only, without sensitive details.
- [ ] Windows service running: yes / no / unknown.
- [ ] Dashboard worker disabled: yes / no / unknown.
- [ ] Ping and authorized metrics timestamps advance without a manual EXE: yes / no / unknown.

These commands were source-reviewed, not executed by the developer assistant on the production server. Even read-only inspection must comply with local access and change-management policies.
