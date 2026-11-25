# Running and Configuring the Monitor

Updated: November 25, 2026 (Asia/Taipei).

See `CONTEXT.MD` for implementation details and known limitations.
See `AGENTS.md` for development rules and project constraints.

---

# Start and Deploy

Run from the repository root on Windows using .NET 8:

```powershell
dotnet build .\ServerMonitoringWeb.sln --nologo -m:1 -p:UseSharedCompilation=false
dotnet run --project .\ServerMonitoringWeb.csproj --no-build --launch-profile http
```

The application hosts both:

- Razor Pages dashboard
- Monitoring worker

Stopping or recycling the application stops monitoring. In the default server mode,
closing a browser does not stop the worker. No browser is launched by the server.

For the optional interactive desktop EXE behavior only, launch from an interactive Windows session:

```powershell
.\ServerMonitoringWeb.exe --DesktopMode=true --urls http://localhost:5000
```

This opens Edge and stops the host gracefully after browser heartbeats expire.
Do not enable `DesktopMode` in IIS or unattended deployments.

## IIS Deployment

For continuous monitoring, the administrator must configure a **dedicated** application
pool for this application, rather than changing the shared DefaultAppPool. ASP.NET Core
in-process apps cannot share a worker process with other in-process ASP.NET Core apps.
Verify the actual hosting model and Hosting Bundle, then configure the dedicated pool's
AlwaysRunning start mode, application preload (Application Initialization), and idle
timeout of zero where approved. These are administrator-owned changes, not diagnostic
commands. See [Microsoft IIS hosting guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-8.0).

The background worker runs only while the IIS application host is alive. A standalone
EXE showing a localhost listener is not evidence that IIS has started its own worker.
Startup logs now identify the server implementation, process ID, interactivity and
desktop mode. An old snapshot can remain readable even when collection stops.

Grant the application identity:

- Read access to inventory files
- Write access to the configured data directory

Run only one application instance per data root. Do not start a standalone EXE alongside
the IIS application. Have the administrator arrange the transition during an approved
maintenance window.

### Stage and deploy without overwriting running binaries

The Visual Studio FolderProfile1 profile now publishes to the local repository
`artifacts/iis-publish` directory, rather than the live share. Publishing to this
profile alone does not deploy anything. Use a clean local staging directory; the profile does not delete
existing files. Verify the destination is local before publishing. The build excludes
monitoring Logs from output and publish excludes servers.json.

For an approved deployment, back up the application and preserve the deployed inventory
and monitoring data. The administrator can use the application-specific IIS
`app_offline.htm` procedure to stop only this application, wait for its processes and
files to be released, copy the staged application files without deleting inventory or
logs, and remove the offline file to restart the application. A manually launched EXE
must be stopped separately by its authorized operator; the offline file cannot stop it.
If files remain locked, stop deployment and investigate rather than killing shared IIS
processes or forcing a copy. A partially copied release is not a reliable deployment.
Build and stage on the development machine; validate that the staging output contains
neither servers.json nor Logs before the approved deployment. Do not run IISReset or recycle a shared pool.
See [Microsoft app_offline guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/app-offline?view=aspnetcore-8.0).

If IIS returns HTTP 500.30, check the Windows Application event log and temporarily
enable ASP.NET Core Module stdout logging in the published `web.config`. Ensure
its log directory is writable by the application pool identity; disable stdout logging
after diagnosis. Confirm that the .NET 8 Hosting Bundle/ASP.NET Core Module is installed,
that the published application matches the configured hosting mode, and that the pool
identity can read inventory and write monitoring data. A successful standalone launch
is not proof that IIS can start under its different identity.

After redeployment, verify the site stays running with all browsers closed for at
least one minute, then reopen the dashboard. If 500.30 persists, capture the first
startup exception (redact sensitive information).

## Data Storage

Deployment output does not contain the private inventory or monitoring Logs. Provide the inventory separately on a new installation.

Preserve:

```text
servers.json
```

With `Monitoring:DataRoot` omitted (the repository default), monitoring data is stored in
`<ProjectRoot>\Logs`, beside `servers.json`. `ProjectRoot` is the application's content root:

```text
<ProjectRoot>\
    servers.json
    Logs\
        latest.json
        History\
            yyyy-MM-dd.metrics.csv
```

The data directory and `History` are created automatically on startup, along with
`latest.json` if it does not exist. Grant the application identity write access to `Logs`
and preserve it during deployments.

For storage errors, review the operation label and exception type/HRESULT in the
application log. `Save ping results (storage) failed` is a persistence failure,
not evidence that the target is offline. The save can involve latest.json or
outage history. A missing direct pool ACL entry does not prove access is denied.
Use the [read-only checklist](SERVER-DIAGNOSTIC-TESTS.MD) and have the administrator
review effective access; do not change or recycle a shared production pool.

To override the data directory, configure an absolute local path:

```json
"Monitoring": {
  "DataRoot": "D:\\MonitoringData"
}
```

This stores `latest.json` directly in `D:\MonitoringData` and CSV files in
`D:\MonitoringData\History`, without an additional `Data` subfolder.
Inventory remains at `<ProjectRoot>\servers.json`.

Startup logs show the resolved paths under `Inventory:`, `Data Directory:`,
`Latest Results:`, and `History Directory:`.

Existing files are not automatically migrated. To retain previous results, stop the
application and copy `latest.json` and `History` from the previous data directory
(formerly `C:\ProgramData\ServerMonitoring\Data` by default) into `Logs` or the configured
data directory before restarting.

### latest.json

Contains:

- Latest merged server snapshot
- Current pair of ping-log entries per server (up to two)

### History

Files use the format:

```text
yyyy-MM-dd.metrics.csv
```

History files use UTC dates and record only observed outage/recovery transitions.
See "Outage History CSV Files" below for the schema, upgrade behavior, and retention.

---

# Managing Inventory

## Edit servers.json

Modify the inventory file in either:

- The repository content root
- The deployed application's content root

The active path is written to startup logs.

Changes are automatically applied:

- On the next monitoring cycle
- On the next dashboard request

Inventory must contain a non-empty top-level `Servers` array.

Example:

```json
{
  "Servers": [
    {
      "Id": "example",
      "Name": "Example server",
      "IpAddresses": ["192.0.2.10"],
      "ComputerName": "EXAMPLE-SERVER",
      "Username": "",
      "Password": "",
      "Enabled": true
    }
  ]
}
```

---

## Inventory Rules

### Required

Enabled servers must have:

- Unique ID
- Name
- At least one valid IP address

### Disabled Entries

```json
"Enabled": false
```

Disabled entries:

- Remain in inventory
- Skip ping collection
- Skip metrics collection
- Display as Paused

### Validation

Inventory validation rejects:

- Unknown JSON properties
- Misspelled field names
- Invalid IDs
- Invalid IP addresses
- Duplicate IDs
- Obsolete fields

### Credentials

Credentials are optional.

Behavior:

| Configuration | Result |
|--------------|---------|
| Username missing | WMI skipped |
| Username blank | WMI skipped |
| Password missing | WMI skipped |
| Password empty | WMI skipped |
| Ping available | Ping still runs |

Notes:

- Password whitespace is preserved.
- Credentials remain plain text.
- Inventory must remain outside `wwwroot`.

Supported account formats:

```text
COMPUTER\User
DOMAIN\User
```

JSON requires escaped backslashes:

```json
"Username": "DOMAIN\\User"
```

---

### WMI access denied

`0x80070005` and `0x80041003` are authorization failures, even when the password is
correct. The warning identifies local versus remote WMI. For local WMI, check the
actual IIS application pool/process identity; Windows does not allow alternate
credentials for local WMI connections. Locality is determined per actual target
IP, not by the inventory hostname. If `0x80041064` persists, review the sanitized
locality check and application version with the administrator. For remote WMI, check the configured account
format and the account's approved DCOM and `root\cimv2` remote WMI permissions with
the server administrator. Compare under the deployed host identity, not just a
logged-in administrator session. Do not weaken UAC, DCOM security or firewall policy
or grant broad administrator privileges to suppress the error. A successful laptop
connection does not establish that the same target permits WMI from the production
host. Ask the administrator to compare existing DCOM/WMI security events on source
and target; do not run additional remote probes on live production without approval. If access still fails,
provide the sanitized warning and relevant Windows event details.

## IP Order

IP ordering is significant.

Collection behavior:

1. All configured IPs are pinged concurrently.
2. The first successful IP wins according to inventory order.
3. Metrics prefer the winning IP.
4. Additional IPs may be attempted when no usable metric data is returned.

WMI may run even when ping fails.

---

## ComputerName

`ComputerName` is optional metadata.

It is not required for connectivity.

Bandwidth collection and the previous:

```json
"NetworkInterface"
```

setting have been removed.

Remove obsolete fields from both:

- Repository inventory
- Deployed inventories

Strict validation rejects unsupported fields.

---

# Server Ordering

Cards remain sorted by existing server IDs.

Preserve:

```text
srv09
```

Do not rename it to:

```text
srv08
```

---

# Monitoring Configuration

## Edit appsettings.json

Modify the `Monitoring` section and restart the application.

If Visual Studio reports a SchemaStore download error while editing
`appsettings.json` or `appsettings.Development.json`, open the affected file and
select **<No Schema Selected>** in the **Schema** dropdown above the JSON editor.
Apply the selection to each affected file. Close and reopen the file if an old
diagnostic remains. This turns off schema-based IntelliSense/validation for that
file; JSON syntax checking and application startup validation remain available.
This is a Visual Studio editor preference, not a project configuration change.
See [Microsoft's JSON editor guidance](https://devblogs.microsoft.com/dotnet/intellisense-for-json-schema-in-the-json-editor/).

Current repository configuration:

| Setting | Value | Purpose |
|----------|----------|----------|
| DataRoot | Omitted; defaults to `<ProjectRoot>\Logs` | Optional monitoring storage directory |
| PingIntervalSeconds | 1 | Ping cycle interval |
| DetailsIntervalSeconds | 10 | Metrics collection interval |
| PingTimeoutMilliseconds | 1000 | Maximum wait per ping |
| MetricsTimeoutSeconds | 20 | Maximum WMI child-process lifetime |
| MaxConcurrentChecks | 4 | Maximum concurrent checks |
| HistoryRetentionDays | 7 | Outage/legacy CSV retention, including today (UTC) |

Monitoring defaults and validation ranges are defined by `MonitoringOptions` in:

```text
Models/Configurations.cs
```

If omitted:

```text
MetricsTimeoutSeconds = 10
```

Repository configuration currently uses:

```text
MetricsTimeoutSeconds = 20
```

---

## Collection Timers

Ping and metrics collection run independently.

Rules:

- Separate timers
- Separate concurrency limits
- Each timer waits for its previous cycle to complete
- Slow metrics collection cannot block ping collection

The former:

```text
StaleAfterSeconds
```

setting has been removed.

---

# Dashboard Configuration

## Typography

Edit the typography tokens in:

```text
wwwroot/css/site.css
```

All dashboard text derives from root typography tokens.

Cards do not override font sizes.

| Variable | Usage |
|-----------|---------|
| --font-sm | Labels, IPs, status, ping logs, theme selector |
| --font-base | Body text and spacing baseline |
| --font-md | Server names and drive percentages |
| --font-lg | Dashboard title |
| --font-xl | CPU/RAM/Disk KPI values |

Example:

```css
--font-xl: 1.75rem;
```

Increase to:

```css
--font-xl: 2rem;
```

for larger KPI values.

### Scaling

Root font-size:

- Scales with viewport height
- Caps at 95% browser default size on desktop
- Shrinks proportionally on smaller screens

The two-column layout uses a reduced scale so four rows continue fitting inside the viewport.

Individual components should not be manually resized unless a design change requires it.

---

## Layout Spacing

The primary card spacing token is:

```css
--card-section-gap
```

Example:

```css
--card-section-gap: 0.75rem;
```

This controls spacing between:

- Heading
- Utilization section
- Information section

Badge padding is controlled independently through:

```css
.status
```

and does not affect section spacing.

---

## Color System

Theme tokens are defined in:

```text
wwwroot/css/site.css
```

Dark mode:

```css
:root
```

Light mode:

```css
:root[data-theme="light"]
```

### Shared Semantic Tokens

| Token | Purpose |
|---------|---------|
| --page | Background |
| --surface | Card surface |
| --surface-2 | Secondary panels |
| --line | Borders, guides, tracks |
| --muted | Secondary text |
| --accent | Interactive emphasis |

Component styling derives from these shared tokens.

Cards use:

- 1px borders
- No shadows

Status badges use:

- Tinted backgrounds
- Tinted borders
- No glow effects

---

## Utilization Colors

CPU, RAM, C: donuts, and disk meters share one utilization palette.

Metric-specific utilization colors have been removed.

Current thresholds:

| Usage | State |
|---------|---------|
| 0-44% | Good |
| 45-64% | Elevated |
| 65-79% | Warning |
| 80-89% | High |
| 90-100% | Critical |

`dashboard.js` applies utilization classes using:

```javascript
getUsageState()
```

Design rules:

- Connectivity state remains visually stronger than utilization.
- Healthy utilization remains visually quiet.
- Warning and critical utilization draw attention.
- Missing values use neutral tracks.

### Connector Guides

Connector guides:

- Use neutral `--line`
- Never inherit utilization colors
- Never inherit status colors
- Have no arrowheads
- Render even when metrics are unavailable

---

# Status and Troubleshooting

| State / Symptom | Meaning / Action |
|-----------------|------------------|
| Online | At least one configured IP replied to ping. |
| Error | Ping failed. Verify network reachability from the monitoring host. |
| Checking | No ping result yet. |
| Paused | Inventory entry is disabled. |
| N/A metrics | Check credentials, WMI permissions, and logs. |
| Measurements not updating | Verify worker operation and DataRoot write access. |
| Configuration error | Verify inventory structure, settings, and validation rules. |
| No warning audio | Interact with the page to satisfy browser autoplay policies. |

Additional notes:

- Metrics failures do not affect connectivity.
- Metrics failures do not trigger audio.
- Per-card collection warnings remain hidden. A shared notice warns when saved ping or resource data may be stale.
- Failures remain available in logs.

---

# Dashboard Behavior

The dashboard displays:

- CPU usage
- RAM usage
- Combined fixed-drive usage
- Disk capacity information
- Ping history

Removed features:

- Bandwidth monitoring
- Last Checked display
- Per-card stale metrics indicators (a shared freshness notice is used instead)

## Disk Display

Only fixed drives reported by the server appear, in name order.
Drives use two columns; a single drive fills both columns. Additional drives wrap
into more rows, with scrolling inside the drive area when needed.

If no drive information is available, the card shows "Drive information unavailable".
A reported drive without a usable capacity reading shows N/A and an empty track.
The DISK gauge shows combined utilization across drives with valid readings.

After upgrading from the C:/D: format, drive information appears after the next
successful metrics collection. Servers without WMI credentials show no drive list.

---

## Ping Log

The ping log displays:

- Up to two entries, with space reserved for both rows
- Newest first
- After initial load and each successful dashboard refresh: first entry immediately,
  second at about 500ms, both hidden at about 1000ms until the next successful refresh
- Dashboard refresh interval: 1500ms; the visual cycle does not delay requests

Each entry contains:

- UTC+08 timestamp (Taipei)
- Connected / Not connected
- Response time or N/A

`latest.json` stores only the current pair of ping entries for each server.
A completed pair is cleared when the next completed ping starts a new stored pair;
the browser animation does not change stored results.

Rules:

- Refreshing the page does not generate ping data.
- Refreshing the page does not append history.
- New stored entries appear only after completed collection cycles. Each refresh
  replays the visual cycle using the saved entries, even if collection has not changed.
- Missing history displays static placeholders; unused rows stay hidden during a cycle.
- Paused servers display N/A.

---

## Viewport Behavior

The dashboard always remains constrained to the viewport.

At 1920×1080:

- Four columns
- Two rows
- Compact header

Smaller widths:

- Two columns
- Same ordering
- Fixed viewport height

No page scrollbar is used.

---

# Outage History CSV Files

`History/yyyy-MM-dd.metrics.csv` records ping connectivity changes:

```csv
Timestamp,ServerId,ServerName,Event
2026-11-19 14:33:01,srv01,RAIS_SERVER_MACHINE,Offline
2026-11-19 14:41:15,srv01,RAIS_SERVER_MACHINE,Recovered
```

Both timestamps and filenames use UTC. The timestamp is when the ping completed.
Names and IDs are quoted/escaped in actual files for safe CSV use.

| Observed change | CSV event |
|-----------------|-----------|
| Online to Error | Offline |
| Error to Online | Recovered |
| Still Online or still Error | No row |
| First observation (Checking to Online/Error) | No row; establishes the baseline |

Metrics collection never appends history, including successful collections and WMI
failures. A day without state transitions creates no new event file. Dashboard
refreshes do not write history. The `.metrics.csv` filename is retained for compatibility;
new rows contain no CPU or RAM samples.

Keep `latest.json` when restarting or deploying to retain the previous connectivity
state. Deleting it or changing a server's configuration establishes a new baseline
on the next ping. An initially failing server may later record `Recovered` without
an earlier `Offline` row. Paused servers and stopped monitoring provide no observations
during the gap.

When a transition first needs to write into an existing daily file with the old
header, the application preserves that file as
`yyyy-MM-dd.metrics.legacy-<unique-id>.csv` and starts a new event CSV. No manual
conversion is needed. Older daily files retain their original schema; inspect their
headers before importing. Historical metrics are not converted into outage events.

`HistoryRetentionDays` includes today: the default of seven retains today and the
preceding six UTC dates. Cleanup runs after the first completed ping cycle at startup and
once per UTC day, including days without outages. It removes expired dated CSVs,
including preserved legacy files; failed cleanup is retried on the next ping cycle.
Keep separate backups if outage history must outlive that retention period.

If an expected transition is missing, check that the application was running, the
inventory was valid/enabled, and the data directory was writable. See application
logs for storage errors. Two-file persistence is not transactional; interruption
can leave a partial or duplicate event, so inspect the final row after a storage failure.

---

# Monitoring Scope

The monitor performs read-only operations.

Collection methods:

- ICMP
- WMI

The application does not modify target systems.

The current editing session uses a hotspot and cannot validate intranet connectivity.

Runtime network and IIS verification remains the responsibility of the user.
