# Server monitoring implementation context

Updated: November 14, 2026 (Asia/Taipei).

## Project rules and framework decision

The user confirmed that this session uses only their hotspot and that testing
will be performed on their end. `Documentation/AGENTS.md` records this rule: do
not run tests or intranet probes unless explicitly requested. Phase 2 verification
is limited to a necessary build and source/configuration review; runtime, browser,
and intranet testing remain with the user. Earlier Phase 1 results below are historical.

The user selected **.NET 8 only**, matching the existing web project. Both
`ServerMonitoringWeb.csproj` and `Tools/CollectionProbe/CollectionProbe.csproj`
target `net8.0`. Build with the installed system SDK and .NET 8 reference packs;
there is no workspace-local SDK prerequisite or special launch path.

`AGENTS.md` now records the user's rules: run only necessary task-related checks,
keep code readable and easy to debug, remove superseded optimizations/workarounds
and unused logic, and update this context with each task. The obsolete local SDK
setup, download metadata, and old probe build artifacts have been removed.
Current probe outputs are rebuilt for `net8.0`. The prototype uses .NET 8's
existing platform APIs without additional dependencies.

## Revised architecture decision: November 14, 2026

The user requested a simpler internal MVP with fewer than 20 mixed domain and
workgroup targets, AD-independent operation, and .NET 8 only. `PLAN.MD` now
supersedes the single-web-process, forced-Kerberos design:

- Two independent .NET 8 programs on one Windows host: the existing Razor Pages
  web role and a Windows Service owning collection. IIS/browser shutdown must
  not stop monitoring. A small shared library contains only actual shared code.
- Recommend direct C# WMI/DCOM (`System.Management`) plus C# Ping and explicit
  target-local accounts for both domain and workgroup targets. No mandatory
  FQDN or domain service identity. WMI/DCOM, permissions, and permitted local
  authentication need live proof on both server types; no access is assumed.
  If policy excludes this path, revise the single collector to WinRM HTTPS with
  appropriate authentication rather than adding fallback transports or weakening
  host policy.
- The plan compares WMI, CIM, WinRM, PowerShell, and native .NET APIs. Remove
  PowerShell from runtime after C# proof; interactive diagnostics remain optional.
  Keep a small local child mode in the service executable for deadlines around
  blocking native WMI calls, reusing process cleanup without another deployment.
- One local SQLite store holds latest observations and seven days of the same
  ten Phase 1 fields. The service is the sole metric writer; the web reads it.
  Start with the default rollback journal and short transactions/finite busy
  timeouts, avoiding WAL sidecar provisioning for a strictly read-only IIS reader.
  Add the requested small inventory form and bounded history table. No additional
  monitoring features, service HTTP API, or enterprise layers.
- Plan CredentialKey references, DPAPI-protected secrets with service/admin ACLs,
  local IIS viewer/admin groups, independent deployment, and a 120-second initial
  stale threshold. These are proposed changes, not implemented security features.
- Document file-by-file migration/removal work and mixed-target, AD-unavailable,
  IIS-stopped, restart, failure, and persistence acceptance. All targets remain
  `net8.0`; current inventory and actual server settings remain unchanged.

Only `PLAN.MD` and this context changed for this task. Review used current source
and official Microsoft/SQLite documentation. No code, credentials, permissions,
or deployment settings changed. No build, tests, app launch, DNS lookup, ping,
or remote collection ran. Live proof remains with the user on the intranet.
Earlier phase descriptions/results below document existing code and history,
not completion of the revised Phase 1 MVP.

## Existing implementation and historical phase state

The earlier plan's Phase 1 collection tooling and Phase 2 configuration/initial
table are implemented. That prototype's live acceptance gate is **still pending**:
collection must work from the intended monitoring host under its intended service
identity. The user explicitly requested continuing to Phase 2 while those live
checks are deferred; they have not been reported as complete.
The user confirmed that this session is connected through a **hotspot**, with no
access to the intranet monitoring targets. Live checks must wait until the
monitoring host is connected to the intranet; do not repeat target probes from
the hotspot or interpret their timeouts as evidence that the servers are down.
The application and probe remain on `net8.0`. The initial server table is present;
the background worker, JSON polling, authentication, and operational logging
services remain in later phases. Loading the web application does not ping,
resolve DNS, run PowerShell, or make any remote collection calls.

## Authoritative inventory

The user's latest SERVER STATUS screenshot supersedes `Legacy/CONFIG.csv.txt`
as the current inventory. `servers.json` now follows the screenshot's row order
and contains the following eight servers. `Legacy/` remains the original archive.

| ID | Current display name | Monitoring IP |
| --- | --- | --- |
| srv01 | APCSDBSERVER_1 | 10.20.10.252 |
| srv02 | APCSDBSERVER_2 | 10.20.10.253 |
| srv03 | RAIS_SERVER_MACHINE | 172.26.220.153 |
| srv09 | RAIS_SERVER_INTRANET | 10.27.1.192 |
| srv07 | RAIS_DATABASE_MACHINE | 172.26.220.154 |
| srv04 | APCSPRO_SERVER | 172.26.220.152 |
| srv05 | VARIOUS_APP_SERVER | 172.26.220.59 |
| srv06 | ALPS_SERVER | 172.26.220.55 |

Existing IDs are preserved for the corresponding servers, including the ALPS
address change. WAFERMAPPING_SERVER (`srv08`) is no longer in the current list;
the added RAIS_SERVER_INTRANET receives `srv09` rather than reusing another
server's identity. No server-health conclusions were taken from the green lights.

Neither the CSV nor screenshot supplies domain DNS or performance-instance names.
`ComputerName` and `NetworkInterface` are consequently blank; display names have
not been treated as DNS names. Entries remain `Enabled: false` until their
deployment settings are verified. The explicit Phase 1 CLI probe can test a
selected entry regardless of `Enabled`; that flag is for the future worker.

## Files and behavior

- `Models/ServerDefinition.cs`: immutable inventory record with the six planned
  properties; disabled is the default.
- `Models/ServerSnapshot.cs`: immutable complete row with nullable status,
  measurements, response time, completion timestamp, and collection warning.
  An unchecked status is `Checking`; all unavailable measurements stay null.
- `Models/MonitoringOptions.cs`: ordinary settings with range/required validation.
  Defaults are 15-second scan, 1,000-ms ping, 10-second metrics process, four
  concurrent checks, 45-second staleness, `Logs`, and 30-day retention.
  `AllowedViewerGroup` is blank pending deployment; no viewer policy is active yet.
- `Services/MonitoringConfiguration.cs`: loads `servers.json` relative to the
  application's content root once at startup and binds Monitoring settings.
  Collects actionable validation errors for the page and startup logger.
- `Services/SnapshotStore.cs`: singleton with a `ConcurrentDictionary`, seeded
  only with enabled entries when the entire configuration is valid. It returns
  rows in inventory order and replaces complete immutable snapshots atomically.
  `Update` rejects IDs outside the enabled inventory.
- `Program.cs`: registers configuration and snapshots as singletons and logs
  validation failures. No monitoring worker or collector is registered.
- `Pages/Index.cshtml` and its page model: render the ten planned columns,
  initial Checking/N/A rows, inline missing-interface warnings, configuration
  errors, and empty state. Disabled inventory is available in native expandable
  details, so the supplied eight servers can be inspected without enabling them.
- `Pages/Shared/_Layout.cshtml` and `wwwroot/css/site.css`: lightweight local
  shell using Segoe UI/system fonts, neutral surfaces, readable numeric columns,
  text plus status indicators, keyboard focus/skip link, and horizontal table
  scrolling on narrow screens. No external fonts, image assets, UI packages,
  or JavaScript dependencies are needed for Phase 2.
- `servers.example.json`: documentation-only inventory example, disabled by
  default. Only `servers.json` is loaded; the example is excluded from publishing.
- `Scripts/ReadServerMetrics.ps1`: fixed, read-only Windows PowerShell 5.1 collector.
  Opens one WSMan CIM session using Kerberos and the current Windows identity;
  no credential parameters, passwords, remote shell commands, or alternate protocol.
  Closes its session in `finally`. Each CIM operation has a two-second default
  timeout. CPU and network get an initial reading, a one-second delay, then a
  second reading with an advancing target timestamp required. Counter readiness
  and accuracy still need comparison against the actual server.
- `Tools/CollectionProbe`: dependency-free .NET 8 console prototype. Reads its
  copied inventory, pings one selected IP for one second, skips CIM on a failed
  ping, and requires the configured DNS name to resolve to that IP before CIM.
  Invokes only the fixed script using `ProcessStartInfo.ArgumentList`, no profile,
  no interactive prompts, and a hidden window. Reads stdout/stderr concurrently.
  Enforces a ten-second whole-process deadline and kills/waits/disposes the owned
  process on timeout or cancellation. This is a manual probe, not a scheduler.
- `Scripts/Test-ReadServerMetrics.ps1`: small offline check using scoped CIM mocks
  for real zero values, missing D:, literal adapter matching, uninitialized
  counters, invalid memory totals, partial access denial, and connection denial.
  Makes no remote calls and must follow the same script-signing policy.
- `ServerMonitoringWeb.csproj`: excludes the standalone `Tools/` source/content
  from the web project's default globs, avoiding duplicate entry points or probe
  artifacts in future web publishing. Explicitly copies `servers.json` and the
  fixed collection script to build/publish output. The framework remains .NET 8.

Removed unused template Privacy pages, the unused validation partial, placeholder
`site.js`, scoped layout template CSS, and unused ErrorModel logger injection.
The generic error page now uses the same layout and an actionable request ID.
The empty Phase 3 service files and Phase 4 `dashboard.js` placeholder contain no
implementation; no fake monitoring or polling behavior was added.

## Phase 2 configuration and deployment maintenance

`servers.json` remains the authoritative maintained inventory in the project
root. Its eight current names/IPs match the latest screenshot; `ComputerName` and
`NetworkInterface` are still blank and `Enabled` is false. Do not substitute
friendly CSV names for actual domain DNS names.

For the user's setup/verification:

1. Keep a backed-up, environment-specific copy of `servers.json` and the
   Monitoring section of `appsettings.json` outside generated `bin`/`obj` output.
   `servers.example.json` documents the schema and is never loaded automatically.
2. Supply one server's confirmed fully qualified `ComputerName` and exact
   performance-instance `NetworkInterface`, then set `Enabled` to true when
   ready to inspect its initial row. A missing interface is allowed but shown as
   a network warning; the future collector must leave that metric unavailable.
3. Run `dotnet run --project ./ServerMonitoringWeb.csproj --launch-profile http`
   on your end and open `http://localhost:5255`. With the supplied inventory the
   page shows "No servers are enabled" and an expandable list of eight disabled
   servers. A valid enabled entry appears as Checking with N/A values and no
   Last Checked timestamp. No actual collection occurs until Phase 3.
4. Editing inventory or Monitoring settings requires restarting the application;
   this release does not hot-reload those settings or provide an editing UI.
5. During later deployment, stop the app, publish the build, apply the maintained
   environment configuration to the published content root, and restart. Publish
   output includes `servers.json` and `Scripts/ReadServerMetrics.ps1`; restore the
   deployment inventory after each publication instead of relying on generated
   files as the maintained source. Credentials must not be placed in JSON.

Validation behavior:

- Missing/unreadable/malformed `servers.json`, a missing/null Servers array,
  null entries, unexpected inventory fields, missing/whitespace-padded IDs,
  duplicate IDs (case insensitive), malformed supplied IP/DNS values, oversized
  adapter names, and enabled entries missing Name/IP/ComputerName produce
  actionable errors. An invalid inventory seeds no snapshots.
- Disabled entries may leave connection fields blank for setup. Supplied DNS
  values must still use valid fully qualified labels, matching the collector's
  letters/digits/hyphens/dots constraints. No DNS lookup is used for validation.
- Numeric settings must satisfy their displayed ranges. StaleAfterSeconds must
  be at least ScanIntervalSeconds. Invalid bound settings appear with the other
  configuration errors; all errors are logged once at startup.
- An empty `Servers: []` or all-disabled inventory is valid and shows the empty
  enabled-list message. A real numeric zero formats as zero; null formats as N/A.
- A syntax error in `appsettings.json` itself is rejected by ASP.NET Core's
  built-in configuration startup loader, before the custom page can be created.

The initial page renders timestamps in UTC if a completed snapshot is supplied.
The current initial snapshots have no timestamps. Browser-local time formatting,
staleness, a cached JSON status handler, and five-second refresh remain Phase 4.

## Read-only setup information to collect on each server

While connected to each server through VNC/RDP, paste the following into that
server's PowerShell window and send back its output. These queries read local
computer/domain details, adapter addresses, exact performance-instance names,
and WinRM service state. They do not change services, permissions, or firewall
rules. Paste the commands directly; no unsigned script file needs to be run.

```powershell
Get-CimInstance Win32_ComputerSystem | Select-Object DNSHostName, Domain, PartOfDomain | Format-List
Get-CimInstance Win32_NetworkAdapterConfiguration -Filter "IPEnabled = True" | Select-Object Description, IPAddress | Format-List
Get-CimInstance Win32_PerfFormattedData_Tcpip_NetworkInterface | Select-Object -ExpandProperty Name
Get-Service WinRM | Select-Object Name, Status
```

If `PartOfDomain` is true, host/domain details can help identify a domain DNS
name. If false, record hostname/IP without appending WORKGROUP. The revised
design connects by IP and requires no domain DNS name; the existing prototype
still has its FQDN/Kerberos constraint until replaced.
Pick the intended uplink from the exact performance-instance
names, rather than assuming the friendly alias `Ethernet` is the right name.
If multiple adapters appear, identify the one carrying the monitored server IP.
VNC/RDP access and a Running WinRM service do not establish remote CIM permission
or listener/firewall availability; those remain the user's later Phase 1 checks.

Property references: [computer/domain details](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-computersystem),
[adapter addresses](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-networkadapterconfiguration),
[performance-instance names](https://learn.microsoft.com/en-us/previous-versions/aa394293(v=vs.85)).

For this inventory-only update, no build, tests, or server queries were run.
Restart the web app to reload `servers.json`. Rebuild the standalone CLI probe
before using it so its copied inventory reflects the update.

## Interpretation of the user-supplied workgroup sample

On November 14, 2026, the user supplied sample output from the local setup
commands. This is user-provided evidence, not a remote verification from this
session; it does not establish the configuration of the other inventory servers.

- Hostname: `APCSPRO-SERVER-DB`. `Domain: WORKGROUP` and `PartOfDomain: False`
  mean this Windows computer is not joined to an Active Directory domain.
  `WORKGROUP` is not a DNS suffix to append to its hostname.
- The sample maps `172.26.220.59` to `vmxnet3 Ethernet Adapter` and
  `10.27.35.73` to `Intel(R) 82574L Gigabit Network Connection`. Both addresses
  belong to this computer. The first IP matches inventory entry `srv05`
  (`VARIOUS_APP_SERVER`); the hostname alone must not be used to reassign it
  to the similarly named `APCSPRO_SERVER` entry at a different IP.
- If monitoring the inventory IP `172.26.220.59`, the corresponding exact
  `NetworkInterface` is `vmxnet3 Ethernet Adapter`. For the other adapter, the
  performance-instance spelling is `Intel[R] 82574L Gigabit Network Connection`,
  with square brackets. ISATAP/Teredo entries are tunnel interfaces, not the
  adapters associated with the two supplied IPv4 addresses.
- `WinRM Running` confirms only the local service state. Listener setup,
  firewall reachability, authentication, and remote CIM authorization remain
  unverified; successful local CIM queries do not establish remote access.
- This sample contradicts the plan's assumption that every target is domain
  joined. The collector explicitly uses `-Authentication Kerberos` and the
  configuration requires a fully qualified domain DNS name. This workgroup
  target therefore cannot use the current collection path as configured.
  Appending a DNS suffix or changing the monitored IP does not resolve domain
  membership. Supporting a server that stays in a workgroup requires an explicit
  collection design change, such as WinRM HTTPS with Negotiate/local credentials
  or certificate authentication, including credential handling outside JSON.
  Joining the server to a domain is an infrastructure decision, not a setup step
  performed by this explanation.

Only this context was updated. Inventory, application code, collector settings,
and server configuration were not changed. Review used the supplied output,
local source, and Microsoft's WinRM documentation linked in the plan. No builds,
tests, DNS lookups, pings, or remote collection were run for this explanation.

## Phase 1 collector output

The collector emits **one JSON object on stdout**. Diagnostics go to stderr.
Fields are `ComputerName`, `SampleStartedUtc`, `SampleCompletedUtc`, `CpuPercent`,
`TotalMemoryKiB`, `FreeMemoryKiB`, `DiskCFreeBytes`, `DiskDFreeBytes`,
`NetworkBytesPerSecond`, `NetworkInterfaces`, and `Warnings` (an object keyed by
metric). A fatal session failure returns exit code 1 with null measurements;
partial failures return exit code 0 with metric warnings and successful values.
Missing data stays null; zero stays zero. Network matching is literal and never
sums multiple adapters. Leaving the interface blank returns discovered instance
names and a warning, so the intended instance can be selected without guessing.

Conversions stay in C#: RAM = `100 * (total - free) / total`, disks = bytes /
`1_000_000_000` (decimal GB), network = bytes/second * `8 / 1_000_000` (Mbps).
The CLI prints the raw JSON and converted values for comparison, with a UTC
completion timestamp. Null values are the future dashboard's `N/A` values.
Exit codes: 0 = completed collection (inspect warnings for partial failure),
1 = input/setup failure, 2 = failed ICMP, 3 = metrics process/JSON failure,
130 = cancellation.

## Environment and prerequisites

Inspected workstation: `RPLSL027-021`, domain `REPI.LOCAL`, current identity
`REPI\cmbandiola`. This is not evidence that it is the intended IIS host or that
this user is the intended collector identity; neither was supplied.

- **Network constraint confirmed by the user:** this session is on a hotspot,
  and the intranet is unavailable from that connection. The observed ICMP and
  DNS timeouts are connectivity-limited checks, not evidence of incorrect
  inventory, failed servers, or missing DNS records. Run the remaining remote
  proof from the intranet. The installed .NET 8 runtime/reference packs and
  dependency-free probe do not require downloading NuGet packages for that proof.
- System SDK: `9.0.304`, used to compile the existing **.NET 8 targets**. Installed
  .NET and ASP.NET Core 8 runtime/reference-pack version: `8.0.19`. No additional
  SDK or runtime installation is required for the current project.
- Installed IDE: Visual Studio Professional 2022 `17.14.11`. VS Code is also
  installed with C# Dev Kit `3.40.215` and C# `2.160.4`.
- OS reports build `26200`, edition Professional, display version `25H2`.
  The intended IIS host and its Windows Server version remain unconfirmed.
  Verify that host against Microsoft's supported-OS list and use the matching
  .NET 8 Hosting Bundle for the later IIS deployment.
- Effective PowerShell policy is **AllSigned**, from LocalMachine. Higher policy
  scopes are Undefined. No usable CurrentUser code-signing certificate was
  returned by inspection.
  The new project scripts are unsigned and need the organization's approved
  signing/publisher process before executing on this host. Execution policy and
  publisher trust have not been changed; the collector does not bypass policy.

Official references:
[Windows/.NET installation and IDE compatibility](https://learn.microsoft.com/en-us/dotnet/core/install/windows),
[CIM sessions](https://learn.microsoft.com/en-us/powershell/module/cimcmdlets/new-cimsession?view=powershell-5.1),
[CIM queries](https://learn.microsoft.com/en-us/powershell/module/cimcmdlets/get-ciminstance?view=powershell-5.1),
[performance counter initialization](https://learn.microsoft.com/en-us/windows/win32/wmisdk/monitoring-performance-data).

## Historical prototype proof commands (superseded design)

These commands exercise the old domain/Kerberos PowerShell prototype. They do
not prove the revised mixed-environment design and are not the setup path for
workgroup targets. Follow the WMI proof/migration sequence in `PLAN.MD` when
implementing the revised collector. Retained commands/results are historical,
not instructions to restore a mandatory domain dependency.

Run from this repository on the intended domain monitoring host **connected to
the intranet**, under the intended service identity. Use the normal `dotnet`
command with the installed SDK and .NET 8 runtime/reference packs:

```powershell
dotnet --info
dotnet build ./Tools/CollectionProbe/CollectionProbe.csproj
dotnet run --no-build --project ./Tools/CollectionProbe -- --self-check
```

Before remote collection, have **both `.ps1` files signed and trusted using the
organization's approved process**, and edit `servers.json` to set one server's
confirmed `ComputerName` (fully qualified DNS name). Leave its interface blank
for the initial discovery. Rebuild after editing/signing: inventory and script
are copied beside the probe executable at build time.

```powershell
dotnet build ./Tools/CollectionProbe/CollectionProbe.csproj
dotnet run --no-build --project ./Tools/CollectionProbe -- srv01
# Choose the intended exact instance from Metrics.NetworkInterfaces in that output.
# Set NetworkInterface in servers.json, then rebuild before the next probe.
dotnet build ./Tools/CollectionProbe/CollectionProbe.csproj
dotnet run --no-build --project ./Tools/CollectionProbe -- srv01
dotnet run --no-build --project ./Tools/CollectionProbe -- srv01 --invalid-interface
powershell.exe -NoProfile -NonInteractive -File ./Scripts/Test-ReadServerMetrics.ps1
```

Compare measurements with the **target's** tools under stable load over the same
sampling window: PerfMon `Processor(_Total)\% Processor Time`, `Network Interface`
`Bytes Total/sec` for the selected instance, OS visible/free physical memory,
and each logical disk's free bytes. Disk GB is decimal, unlike Explorer's usual
binary display. Task Manager's CPU utilization and in-use-memory definitions may
differ; use the plan's counters/formulas as the comparison. Record times, target,
identity, adapter, observed values, and any variance before accepting the proof.

Exercise genuine access denial with an identity that lacks collection permission
and missing D: on a server that actually has no D: drive. Do not remove drives or
change server permissions just to produce a failure. Use an approved unreachable
management endpoint to verify the remote timeout path; the local `--self-check`
only verifies process timeout/cancellation with a sleeping PowerShell child.
Do not broaden WinRM permissions or change transport architecture to get a pass.

## Phase 1 historical verification and remaining live gate

Completed checks (only those directly relevant to Phase 1):

- At initial migration, all eight name/IP pairs matched the supplied CSV exactly.
  The newer screenshot inventory above now supersedes those historic values.
- Both PowerShell files parse with Windows PowerShell 5.1's parser.
- After retargeting, `dotnet build ./Tools/CollectionProbe/CollectionProbe.csproj
  --ignore-failed-sources --nologo -p:NuGetAudit=false` passed for **.NET 8 with
  zero warnings and zero errors** using the existing system SDK. No additional
  packages, SDKs, or runtimes were installed for this change.
- Both C# project files declare `net8.0`; the rebuilt probe's runtime configuration
  requests `Microsoft.NETCore.App` `8.0.0` (using the installed compatible 8.0 patch).
  The obsolete local SDK directory and its caches are absent, and the maintained
  source/documentation scan found no obsolete framework/setup references.
- The existing `ServerMonitoringWeb.csproj` also builds with **zero warnings
  and zero errors** using the installed system SDK and `--no-restore`. This
  verifies the standalone tool does not interfere with the Razor starter.
- `CollectionProbe --self-check` passed again on the **.NET 8 runtime**, covering
  both real child-process cleanup checks:
  a two-second timeout and cancellation after two seconds. Each verified that
  its owned sleeping PowerShell process no longer existed after cleanup.
- `CollectionProbe srv01` returned Offline / TimedOut, null response time, a UTC
  completion timestamp, and an explicit skipped-CIM warning. This checks the
  actual CLI path; it does not establish the target's status from the intended host.
- A one-second ICMP attempt to `10.20.10.252` timed out from this workstation,
  including a retry outside the execution sandbox. The user subsequently
  confirmed the hotspot/intranet connectivity limitation; this does not prove
  the server is offline or that the intended intranet host cannot reach it.
- Reverse DNS attempts for all eight supplied IPs did not complete within a
  bounded three-second window. No DNS mappings have been invented.
- The mocked PowerShell collector checks were **not executed** under the current
  `AllSigned` policy because the new scripts do not yet have approved signatures.
  Their scenarios are not reported as passing live or offline checks.
- No target CIM session or real metric comparison has been completed. The
  intranet connection, signed-script prerequisite, confirmed DNS/adapter settings,
  intended host, service identity, actual missing-D:/denied-access scenarios,
  and WinRM endpoint failure checks remain required. Phase 1 must not be marked
  complete until those live prerequisites and comparisons pass.

For the framework cleanup, verification was limited to the changed probe's
build, its process-cleanup self-check, and framework/reference/artifact checks.
The unchanged web application was not rebuilt again, the PowerShell mock suite
was not rerun, and no further intranet probes were attempted from the hotspot.

## Phase 2 verification and user handoff

- One necessary build, `dotnet build ./ServerMonitoringWeb.csproj --no-restore
  --nologo`, passed with **zero warnings and zero errors**, including Razor
  compilation. It uses the existing SDK and targets .NET 8; no packages or SDKs
  were installed.
- Static JSON parsing passed for `appsettings.json`, `servers.json`, and
  `servers.example.json`. The inventory and fixed PowerShell collector are
  present in `bin/Debug/net8.0` at their required relative paths.
- The source scan found no remaining references to the removed Privacy page,
  validation partial, placeholder site script, or scoped template styles.
- One source-only review found no material Phase 2 blockers in validation,
  immutable snapshot replacement, initial values, semantic table structure,
  empty/error states, or keyboard access. This is not a browser/runtime verdict.
- The Impeccable context launcher could not initialize its external engine cache
  under the current permissions. Existing documentation and source were used
  directly. No extra tooling was installed and no preview was launched.
- No tests, app/browser sessions, DNS lookups, pings, or remote collection were
  run for Phase 2. All runtime and intranet acceptance testing remains with the user.

Suggested user checks, when ready:

1. Open the page with the unchanged inventory: ten table headers, a clear
   all-disabled message, and eight servers under the expandable disabled list.
2. Set one entry's confirmed domain DNS name and enable it, then restart:
   exactly that row shows Checking and N/A, including N/A for Last Checked.
   A blank interface adds a network warning without blocking the row.
3. In a temporary backed-up copy, try a duplicate ID, invalid IP/DNS, enabled
   entry missing required fields, and a nonpositive Monitoring timeout: each
   should show actionable configuration errors and no enabled snapshot rows.
   Restore the actual inventory/settings after checking.
4. Try `Servers: []`, then restore the inventory: both empty and all-disabled
   configurations should be understandable without fabricated metrics.
5. Check the narrow-screen table scroll and keyboard focus. Complete the deferred
   Phase 1 signing/permissions/real-metric comparisons from the intranet before
   accepting collection or proceeding to production deployment.
