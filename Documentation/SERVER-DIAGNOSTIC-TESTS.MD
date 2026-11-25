# Server-side diagnostic tests

For [issue #3 — SERVER SIDE ISSUE #2](https://github.com/juanskieee/ServerMonitoringWeb/issues/3).

Run on the actual monitoring server in Windows PowerShell 5.1 (powershell.exe). Use an authorized administrator session for IIS and ACL inspection. Record results before changing settings. All tests are read-only except the explicitly marked optional remediation.

## 1. Identify the execution context
```powershell
whoami
hostname
Import-Module WebAdministration
Get-WebApplication | Select-Object Path, ApplicationPool
Get-Website | Select-Object Name, ApplicationPool
Get-ChildItem IIS:\AppPools |
    Select-Object Name, State, @{N='IdentityType';E={$_.processModel.identityType}}
& "$env:windir\System32\inetsrv\appcmd.exe" list wp
```
The terminal account is not necessarily the IIS application identity. Identify the actual hosting mode and pool. Do not run two application instances using the same data root.

## 2. Inspect storage permissions
Use the deployed content root and actual data directory from startup logs. The example assumes the default Logs location.
```powershell
Set-Location 'C:\inetpub\wwwroot\ServerMonitoringWeb'
icacls .\Logs
icacls .\Logs\latest.json
icacls .\Logs\History
Get-Item .\Logs\latest.json | Select-Object Length, LastWriteTime, Attributes
```
The hosting identity needs permission to read, create, replace and delete monitoring files. Check for deny rules, read-only files and conflicting instances. A successful administrator inspection does not prove IIS can write. The ping warning may originate in snapshot/history persistence, not ICMP.

### Optional remediation (requires administrator approval)
Only if the actual IIS pool uses ApplicationPoolIdentity and its data-directory ACL is insufficient, substitute the real pool name and grant Modify on the data directory only:
```powershell
$pool = 'YOUR_POOL_NAME'
icacls .\Logs /grant "IIS AppPool\${pool}:(OI)(CI)M" /T
```
For a custom DataRoot or different identity, have the administrator apply equivalent targeted permissions. Do not grant Everyone or broad administrative privileges.

## 3. Check local server detection (srv05)
```powershell
Set-Location 'C:\inetpub\wwwroot\ServerMonitoringWeb'
$inventory = Get-Content .\servers.json -Raw | ConvertFrom-Json
$server = $inventory.Servers | Where-Object { $_.Id -eq 'srv05' }
if (-not $server) { throw 'srv05 was not found in the active inventory.' }
$localIPs = (Get-NetIPAddress).IPAddress
[PSCustomObject]@{
    ServerId = $server.Id
    HostnameMatches = ($server.ComputerName -eq $env:COMPUTERNAME)
    LocalIPMatches = [bool]($server.IpAddresses | Where-Object { $_ -in $localIPs })
}
Get-WmiObject -Namespace root\cimv2 -Class Win32_OperatingSystem -ErrorAction Stop |
    Select-Object Caption, Version
```
If HostnameMatches is False and LocalIPMatches is True, confirm the entry represents the monitoring host. The current collector identifies local WMI using ComputerName. A local target incorrectly classified as remote can produce 0x80041064. Correct the deployed ComputerName only after confirming the target identity. The final command tests local WMI without explicit credentials, under the terminal identity rather than necessarily IIS.

## 4. Test remote WMI (srv01, srv02, srv09)
Choose one authorized affected remote target first. Use the same approved credentials as the deployed inventory. Do not print the inventory or repeatedly retry failed authentication.
```powershell
$target = Read-Host 'Enter authorized remote target IP'
$cred = Get-Credential
Get-WmiObject -Class Win32_OperatingSystem `
    -Namespace root\cimv2 `
    -ComputerName $target `
    -Credential $cred `
    -Authentication PacketPrivacy `
    -Impersonation Impersonate `
    -ErrorAction Stop |
    Select-Object Caption, Version
```
| Result | Interpretation |
| --- | --- |
| OS information returned | WMI works for this target, credential and terminal context; IIS may differ. |
| 0x80070005 or 0x80041003 | Administrator should verify account authorization, DCOM and WMI namespace permissions. |
| 0x800706BA | Investigate RPC connectivity, target availability and approved firewall policy. |
| 0x80041064 | Explicit credentials were supplied to a local WMI connection. |
| 0x8007052E | Check credentials and account format; avoid repeated attempts. |

Do not disable UAC, firewall or DCOM security. Consult the administrator if access remains denied.

## 5. Verify application behavior
After approved corrections and deployment, verify that the IIS site and monitoring worker remain running for at least one minute with browsers closed. Check that latest.json continues updating and authorized CPU/RAM/disk readings return. If HTTP 500.30 persists, inspect Windows Application event logs and ASP.NET Core Module startup logs per [OPERATIONS.MD](OPERATIONS.MD).

## Report results
Post only sanitized results in issue #3. Do not publish passwords, usernames, hostnames, IP addresses, full ACLs or raw production logs.

- [ ] Hosting mode and pool identity type (no account names)
- [ ] Data directory access for actual application identity: confirmed / denied / unknown
- [ ] srv05 HostnameMatches: True / False
- [ ] srv05 LocalIPMatches: True / False
- [ ] Local WMI test: success / error code
- [ ] Remote WMI test: server ID and success / error code
- [ ] IIS startup and one-minute browser-closed check: pass / fail / not tested
- [ ] Latest snapshot updates: yes / no
- [ ] Remaining errors, sanitized

These tests require the authorized server environment and have not been executed by the repository assistant.
