# Project Context

Updated: November 26, 2026 (Asia/Taipei).

See `AGENTS.md` for development rules and `OPERATIONS.MD` for setup and troubleshooting. This document describes the current implementation and serves as the primary technical reference.

---

# Application and Inventory

- One Windows .NET 8 application hosts Razor Pages and MonitoringWorker together.
  The worker is always registered; the old RunWorker setting is no longer used.
  No separate Windows Service is required. The obsolete --worker-service switch
  is rejected rather than starting another host. IIS publishing explicitly uses
  in-process hosting. IIS must start and keep the application alive; recycling
  or stopping IIS interrupts monitoring.
- Desktop browser/heartbeat behavior remains explicitly interactive, not IIS
  hosting. One-shot WMI subprocesses use redirected UTF-8 JSON without changing
  console code pages; stderr is drained and never exposed. Successful exit and
  valid JSON determine success.
- Worker startup yields to the host, acquires the exclusive writer lease and
  initializes storage. On I/O or access errors it retries every 15 seconds
  without starting collection, and exposes a sanitized startup notice. A shared
  resource warning counts online servers whose collection failed and identifies
  known access-denied codes without raw messages. Monitoring intervals,
  authorization and the snapshot schema are unchanged.

- `servers.json` in the content root is the only inventory source.
  The worker loads it every collection cycle and the dashboard loads it every request.
  Invalid inventory data stops checks for that cycle and displays configuration errors.

- Read the current inventory file when investigating behavior. Do not rely on historical inventory snapshots documented elsewhere.

- Credentials are optional:
  - Missing or blank Username skips WMI collection.
  - Missing or empty Password skips WMI collection.
  - Ping continues normally.
  - Password whitespace is preserved.
  - `Enabled = false` pauses all checks for that server.

- Passwords remain plain text outside `wwwroot`.
- WMI connects once per collection attempt. The actual selected IP address is
  compared to loopback and local interface unicast addresses (including IPv4-mapped
  IPv6 normalization); a local match uses the process identity and no alternate
  credentials. Otherwise remote WMI uses the existing inventory credentials and
  packet-privacy authentication. ComputerName is metadata, not a locality override.
  Interface enumeration is local and sends no network probes. If identification
  throws, the attempt reports a warning rather than trying explicit credentials.
  The existing missing-credentials skip rule is unchanged.
- Connection warnings identify the mode that failed, without disclosing credentials.
  Correct passwords alone do not establish WMI/DCOM authorization.
- The deployed inventory is not available in Git. Issue #9 reports metrics
  missing under IIS; single-host registration, startup diagnostics and headless
  collection changes require build and approved deployment/runtime validation.
  Remote WMI/DCOM authorization remains a separate deployment concern.

- Inventory validation checks:
  - IDs
  - Names
  - IP addresses
  - Optional fields
  - Unknown JSON properties
  - 1 MiB maximum file size

- The local inventory is copied to build output for development only. Publish output
  excludes servers.json and monitoring Logs; provision and preserve them separately.
  Both checked-in folder profiles stage locally and disable destination deletion.
  Project-level publish guards reject destructive cleanup, UNC paths, destinations
  outside approved project output directories, and staging containing inventory or
  Logs. These guards apply to standard VS folder and CLI publish targets; they do
  not protect manual copies, other deployment tooling, or old builds.

- `Models/Configurations.cs` contains `MonitoringOptions`, `MonitoringConfiguration`, and
  `InventoryConfiguration`. Path resolution is defined in `Services/DataPaths.cs`.
- Visual Studio's JSON editor may request the external SchemaStore appsettings schema.
  Schema download failures are editor diagnostics; monitoring settings are validated
  independently at application startup. See `OPERATIONS.MD` for the editor setting.
- Inventory validation uses separate helpers for identity, IP addresses, and optional fields.
  Errors retain their validation order, and non-null entries remain available for display
  even when invalid. Callers check `IsValid` before monitoring or rendering server cards.

---

# Monitoring Storage

- With `Monitoring:DataRoot` omitted, monitoring data defaults to `<ProjectRoot>\Logs`,
  where `ProjectRoot` is the application's content root containing `servers.json`.
- `DataPaths.DataDirectory` is the resolved data root without an additional `Data` subfolder.
- An explicit `Monitoring:DataRoot` overrides the default and must remain an absolute local path.
  For example, `D:\MonitoringData` stores results directly in that directory.
- Inventory always remains at `<ProjectRoot>\servers.json`, including when the data root is overridden.

Default structure:

```text
<ProjectRoot>\
    servers.json
    Logs\
        latest.json
        History\
            yyyy-MM-dd.metrics.csv
```

- `ResultStore.Initialize()` creates the data directory and `History` automatically,
  and creates `latest.json` containing `{}` if it is absent.
- Startup logs report `Inventory`, `Data Directory`, `Latest Results`, and `History Directory` paths.
- Each completed per-server ping or details result reads and rewrites the complete
  `latest.json` file through a same-directory temporary file and replacement move.
  A process-local semaphore serializes snapshot/history writes. Every upgraded worker
  also holds an exclusive collector.lock handle in DataRoot for its lifetime before
  initialization. Readers do not acquire it. The file remains; the handle establishes ownership.
- Worker errors identify the failing stage (ping collection, metrics collection,
  storage read, save ping results or save metrics results) and exception type/HRESULT.
  Storage failures are not labeled as failed network pings. Storage behavior and
  the existing connectivity/outage rules remain unchanged.
- `History/yyyy-MM-dd.metrics.csv` contains outage transitions only, with columns
  `Timestamp,ServerId,ServerName,Event`. A known Online to Error ping transition
  writes `Offline`; Error to Online writes `Recovered`. Timestamp and filename
  use the completed ping's UTC time (`yyyy-MM-dd HH:mm:ss` for the row).
- Repeated Online/Error results, metrics collection, and dashboard refreshes do not
  append CSV rows. A day without transitions creates no event CSV. WMI failures,
  missing credentials, and CPU/RAM/disk readings do not determine connectivity.
- The previous state comes from `latest.json` under the existing storage lock, so
  restarting preserves the baseline. A missing snapshot or changed configuration
  revision starts at Checking; the first ping establishes state without an event.
  An initially failing server can therefore have a later `Recovered` event without
  a recorded outage start. Paused/removed servers produce no observations; gaps
  while monitoring is stopped or paused cannot be reconstructed.
- History is appended before replacing the prepared latest-results file. An append
  failure leaves the previous snapshot in place so the next ping can retry the
  transition. These two files have no shared transaction: interruption can leave a
  partial CSV row or duplicate an event if history succeeds but snapshot replacement fails.
- On the first event for a date, an existing CSV with an incompatible header is
  preserved as `yyyy-MM-dd.metrics.legacy-<unique-id>.csv` before the new event CSV
  is created. Older dates are left untouched; legacy metrics are not converted
  into inferred outage events. CSV quoting and spreadsheet-formula protection remain.
- Retention runs after the first completed ping batch at startup and once per UTC
  day thereafter, even without transitions. Failed cleanup is retried on the next
  ping cycle. `HistoryRetentionDays` includes today and applies to dated event CSVs,
  old daily CSVs, and preserved legacy CSVs.
- Existing monitoring files are not automatically migrated from the previous location.
- Verification: one `dotnet build ServerMonitoringWeb.sln --no-restore` passed
  targeting `net8.0` with zero warnings/errors. Verification uses solution builds
  and source inspection; runtime monitoring, browser layout, and IIS validation
  remain with the user.

---

# Production Safety and Known Limits

- The source/configuration audit is recorded in `PRODUCTION-SAFETY-AUDIT.MD`.
  Its per-collection CSV write rates and old CSV schema predate outage-only history.
  It made no application/configuration changes and ran no application, collector,
  intranet checks, IIS validation, or new build. External documentation requests failed; installed
  Microsoft reference XML supplied limited API verification.
- Ping and details use separate awaited batches, each with concurrency four under
  current settings. Slow cycles reduce cadence rather than queue unlimited batches.
- Child deadlines normally contain WMI hangs while the parent is healthy. Cleanup
  waits have no deadline, kill failures are not separately contained, and children
  have no application-level kill-on-parent-exit ownership or independent watchdog.
- WMI authentication failures have no backoff or stop condition; alternate-IP retries
  can lock accounts and amplify authentication/security logging.
- The dashboard displays a shared freshness notice when saved ping observations or
  usable resource metrics are overdue. Thresholds allow for configured timer intervals,
  concurrency, target count, address count and timeouts, with a 60-second minimum.
  Metrics now record LastMetricsUtc when usable readings return; old snapshots without
  that field are treated as unverified until refreshed. Stored connectivity status,
  audio and outage history are not changed by the notice. A fresh HTTP response alone
  does not establish collection health.
- Removed server IDs are not pruned from `latest.json`; malformed results block
  subsequent updates until operator repair. CSV date retention has no byte quota
  and depends on completed ping cycles, valid inventory, and successful file deletion.
- The writer lease excludes concurrent upgraded workers sharing the same local
  data directory. Old binaries and external writers do not honor it and must be
  excluded operationally. The IIS dashboard and worker share the same data root.
- Dashboard access has no application-enforced authentication requirement. Production
  access control, HTTPS, private data paths, and inventory/deployment/backup ACLs must
  be supplied by the deployment. Plaintext inventory credentials are copied to
  local build output.
- No ordinary unbounded task/listener/handle retention pattern was found, but 24/7
  resource use and target-provider impact remain unmeasured. The audit rates the
  supplied implementation 6/10 and does not approve unattended production operation.

---

# Dashboard

## Layout and Scaling

- The dashboard fills exactly `100dvh`, with `100vh` used as a fallback.
- `html`, `body`, and page overflow remain hidden.
- The header occupies the first row and the server grid fills the remaining height.
- Flex and grid descendants use `min-height: 0`.
- Server rows use `minmax(0, 1fr)` so content cannot expand the page vertically.

- At 1920×1080:
  - Eight cards display in four columns and two equal-height rows.
  - The grid remains fixed within the viewport.

- Below 1280px width:
  - The dashboard uses two columns.
  - Server ordering remains unchanged.
  - Cards compact to fit the viewport rather than switching to a scrolling single-column layout.

- Root typography is defined exclusively in `:root`.
  Typography tokens control:
  - Page title
  - Card headings
  - KPI values
  - Labels
  - Status badges
  - Ping logs
  - Theme controls

- Card-level font overrides are intentionally avoided.

- Root font-size scales using viewport height and is capped at 95% of the browser default size on desktop.

- Both themes define a matching browser `color-scheme`. Light mode supplies darker
  muted text, accent/status colors, utilization strokes, and error borders for
  readable controls, empty states, badges, ping logs, and gauges on light surfaces.

## Notices

- Configuration, storage, worker startup, metrics and freshness notices are limited to 25% of the available content height.
- Notice areas scroll independently.
- Server cards and page height remain fixed.

---

## Connectivity and Status

Connectivity depends exclusively on ping results.

| Ping State | Dashboard State |
|------------|----------------|
| Successful ping | Online |
| Failed ping | Error |
| No ping result | Checking |
| Disabled entry | Paused |

Additional notes:

- Metrics collection does not affect connectivity status.
- CPU, RAM, and disk readings cannot force a server Offline.
- Collection warnings do not affect connectivity.
- Audio alerts trigger only when one or more servers are in the Error state.

### Status Badges

Status badges are the primary health indicator.

- Online → Success color
- Error → Danger color
- Checking → Accent color
- Paused → Muted color

Implementation principles:

- Tinted backgrounds
- Tinted borders
- No glow effects
- No badge shadows

### Ping Badges

- A green ping badge displays a real response time only for enabled, online servers.
- Error cards retain the original red N/A ping badge with crossed-out signal bars.
- Paused/checking cards and online cards without a response time omit the ping
  badge. Real zero remains a valid `0 ms`.
- A reserved header grid row keeps status badges and heading dividers aligned with
  active cards when the ping badge is absent.

---

## Card Structure

Active server cards contain three primary sections:

1. Heading
2. Utilization
3. Information

Spacing between sections is controlled by:

```css
--card-section-gap
```

### Empty States

- Paused/checking cards and online cards without usable resource readings replace
  utilization with a centered inline SVG and a short message. Icons and messages
  use the existing muted theme color in both themes, except the red icon on
  all error cards.

| Condition | Message and icon | Ping log |
|-----------|------------------|----------|
| Inventory entry disabled, even if saved readings exist | Monitoring Paused; existing pause-in-circle icon | Hidden |
| Enabled, with no ping result yet | Waiting for First Scan; crossed-out Wi-Fi icon | Hidden |
| Online, with no usable CPU, RAM, or drive percentage | Metrics Unavailable; crossed-out Wi-Fi icon | Visible |
| Enabled, ping failed, regardless of credentials or saved metrics | Ping Failed; red cross-in-circle icon matching the paused icon's outline | Visible |

- Only percentages from 0 through 100 are usable. Zero is valid; null, NaN,
  infinity, and out-of-range values do not prevent an empty state. A usable
  partial metric result keeps the active layout.
- Empty cards render no donut, metric labels, or drive placeholders. The server
  identity, connectivity status, and any real online ping time remain visible.
- Every error card shows the icon/message and ping log. No error card renders a
  donut, resource labels, or drive information, including servers with credentials
  and saved metrics. Credential-dependent error presentation and red alert-ring
  rendering have been removed. Error status/audio still depend exclusively on failed
  ping. Recovery without metrics shows Online/Metrics Unavailable; usable metrics
  restore the active layout. Missing credentials alone never cause Error.
- Paused/checking content spans both body rows. Online Metrics Unavailable and
  all error cards keep the ping-log section below the icon/message. Card
  dimensions, header placement, padding, and viewport layout are preserved.
- Refreshes replace the card content when entering, leaving, or changing an empty
  state. Active-to-active refreshes preserve SVG nodes and existing animations.
- Error cards blink their red border and tinted background on an 800ms CSS cycle.
  The same animation supplies the ping-log panel's border color and the icon's
  opacity. The panel keeps its normal theme background throughout the cycle; only
  its border blinks. The crossed-out ping badge's backing matches the animated card
  surface. Colors follow the current theme, including while switching themes.
  Only a numeric on/off phase is animated; background/border colors resolve from
  the current theme outside the keyframes, so active errors adapt immediately to
  a theme switch without retaining colors from the previous theme.
  All three stay synchronized through refreshes. Reduced motion keeps alerts static.
- Verification: one `dotnet build ServerMonitoringWeb.sln --no-restore`, JavaScript
  syntax validation, and source inspection cover the unified error presentation.
  Theme refinements use focused palette/contrast checks and source inspection of
  the numeric-only blink keyframes and theme-dependent color declarations;
  no rebuild is needed for CSS and a static SVG stroke-value change.
  Browser layout and live monitoring remain user-validated; no application,
  collector, intranet probes, or IIS checks were run.

## Drives

- WMI collects every fixed logical drive (`Win32_LogicalDisk`, `DriveType = 3`).
  Drive names and decimal GB capacities travel in a `Disks` list through collection,
  storage, and rendering; there are no C:/D: fields or assumed drive letters.
- Drives appear in name order in two columns. A single drive spans both columns.
  Additional drives wrap into more rows; the drive area scrolls above 11em to keep
  the dashboard within the viewport.
- On active cards, an empty list displays "Drive information unavailable" without inventing drives.
  A reported drive with missing capacity displays N/A and "Capacity unavailable".
- The DISK gauge uses combined used capacity divided by combined total capacity
  across drives with valid readings. Offline/paused readings remain hidden.
- Cached results using the previous C:/D: fields show unavailable drive information
  on otherwise active cards, or an empty state when no resource metrics are usable,
  until the next successful metrics collection supplies the `Disks` list.

## Ping Log

- Each server stores one pair of completed ping checks, newest first.
- A new result after a full pair clears both old entries and starts the next pair.
  Both successful and failed pings follow this storage behavior, unchanged by the UI.
- Dashboard refreshes run every 3000ms without waiting for animations. Each successful
  refresh replaces only the ping list and starts a browser-only CSS cycle, even if
  the saved pair is unchanged: first entry immediately, second at 900ms, both hidden
  at 1800ms until the next successful refresh. Initial page load uses the same cycle.
- `visibility` preserves row heights. Unused rows stay hidden during a cycle;
  the waiting message stays visible when a displayed ping log has no real entries.
- Removing a ping list ends its animation. No ping animation timers, callbacks,
  storage changes, or delays are added to the refresh loop. Disk markup updates
  separately on cards with gauges. Empty-card content refreshes together, including
  the ping log on online Metrics Unavailable and all error cards.
- The error card, icon, and ping-log panel border blink together; the panel
  background stays steady in both themes. Ping-log row visibility keeps its existing
  cycle. Both use CSS without new timers or refresh changes.
- Refreshes do not create stored entries or reset a stored pair. Metrics updates
  preserve the pair; only a completed ping result advances it.
- Ping logs remain visible on active/error cards and online Metrics Unavailable
  cards; paused/checking cards omit them. Browser animation timing remains
  user-validated; refresh and collection cadence are independent.
