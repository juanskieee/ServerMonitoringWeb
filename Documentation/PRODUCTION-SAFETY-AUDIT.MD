# Production Safety and Server Impact Audit

Reviewed: November 19, 2026 (Asia/Taipei).

This report predates outage-only CSV history and daily retention checks. Its per-metrics CSV write rates and schema are historical; see `CONTEXT.MD` for current storage behavior.

**Production decision: NO, as currently implemented, for unattended 24/7 use. Overall safety score: 6/10. Risk of materially affecting monitored servers: Low in normal operation; Moderate during authentication failures, unhealthy WMI providers, or duplicated monitoring.**

The architecture is suitable for a small internal monitor. Read-only, narrow WMI queries, separate ping and details loops, bounded concurrency, and disposable child collectors are sensible protections. The decision above is driven by missing failure controls and operational visibility, rather than evidence that normal polling will overload a healthy server. A production deployment may already mitigate some security and hosting concerns externally; that configuration was not supplied or inspected.

## Scope and evidence limits

This is a source and configuration audit. No application, collector, intranet probe, remote-server check, IIS verification, load test, or soak test was run. Application code and configuration were not changed. No build was needed for documentation-only work. The existing build-success statement in CONTEXT.MD describes earlier verification, not a build performed for this audit.

Reviewed Program.cs, all current Models and Services, Razor handlers and partials, dashboard.js, theme.js, relevant CSS, project settings, operational documentation, and the active servers.json. Legacy prototypes are excluded from the production execution path by the project configuration.

The active inventory contains eight entries: **three enabled servers, five enabled IP addresses, and two enabled servers with credentials**. Both credentialed servers have two addresses. Credential values are deliberately omitted. The local Logs directory contained no stored results to measure.

External search and direct Microsoft documentation requests failed. Installed Microsoft System.Management 8.0.0 and .NET 8 reference XML were inspected instead. Vendor alert thresholds, deployed authentication, firewall rules, runtime patch/support status, hardware capacity, actual resource consumption, and actual RPC byte counts remain unverified. Estimates below are explicitly conditional; they are not benchmark results.

## Architecture assessment

The application combines a Razor Pages dashboard and a BackgroundService in one Windows .NET 8 process. Two PeriodicTimer loops load inventory and await their complete collection batches. Each loop uses Parallel.ForEachAsync with MaxConcurrentChecks=4. The loops do not launch overlapping batches of their own; overruns reduce the achieved collection rate rather than enqueueing unlimited batches. A pending timer tick can cause the next batch to start immediately after a slow batch.

Ping creates a disposable Ping object for every configured address, concurrently within each server check. Metrics start a new copy of the application in --collect-once mode for each attempted address. Collector mode branches before building the web host, so children do not start more monitoring workers. Successful WMI collection normally uses one connection scope and three queries; a CPU fallback can add a fourth query. Addresses are tried sequentially until any useful metrics are returned.

ResultStore merges individual results into a shared JSON file under one process-local semaphore. Metrics also append a CSV row. Dashboard requests read stored results and inventory, then render HTML. They never initiate target collection. More browser clients therefore increase web/storage/rendering load without multiplying ICMP or WMI work.

This remains a reasonable small-system design. Its main architectural limits are whole-file persistence after every result, web-host lifetime controlling monitoring lifetime, and isolation that depends on a healthy parent process.

Source: [Program.cs](../Program.cs), lines 15-20 and 126-166; [MonitoringWorker.cs](../Services/MonitoringWorker.cs), lines 24-95 and 103-151; [ServerCollector.cs](../Services/ServerCollector.cs), lines 18-41 and 89-139.

## Ranked findings

| Priority | Finding and production consequence | Evidence |
|---|---|---|
| High | Authentication failures have no backoff or stop condition. Wrong or rotated credentials are retried against alternate IPs every completed details cycle. This can lock a monitoring account, affect services sharing that account, and generate large authentication/security logs. | ServerCollector.cs:93-155; WmiCollector.cs:19-35,57-65; MonitoringWorker.cs:49-95 |
| High | Stored status has no freshness requirement. If saves fail while latest.json remains readable, HTTP refresh can keep succeeding and show an old Online state indefinitely. A hung details loop can also leave old metrics appearing current. There is no metrics collection timestamp or worker heartbeat. | ResultStore.cs:31-60; ServerSnapshot.cs; Index.cshtml.cs:90-105; dashboard.js:389-406 |
| High, conditional on deployment | Application pages require no authenticated identity or authorization policy. UseAuthorization alone does not restrict them. Plaintext credentials are copied with the inventory during build/publish. Anyone reaching an unprotected site can see internal names, addresses, status, and capacity; anyone reading deployment files/backups may obtain credentials. | Program.cs:126-166; ServerMonitoringWeb.csproj:12; ServerDefinition.cs:20-24; _ServerCards.cshtml:61-75 |
| Medium | Every individual ping and details result reads and rewrites all saved snapshots. This creates quadratic byte/serialization work as a stable fleet grows and couples both loops through storage latency. | ResultStore.cs:151-215; MonitoringWorker.cs:154-190 |
| Medium | Collector cleanup is not an unconditional lifetime guarantee. Abrupt parent exit has no application-level child cleanup; Kill can fail; waits after timeout and pipe drains have no deadline. A stuck cleanup can consume one of the four metrics slots indefinitely. | CollectorProcess.cs:50-115 |
| Medium | Corrupt latest.json is not automatically repaired or quarantined. Every update first deserializes it, so malformed data blocks both new ping saves and metrics collection until intervention. A single shared file failure affects all servers. | ResultStore.cs:63-83,160-161; MonitoringWorker.cs:173-174 |
| Medium | Single-instance operation is documented but not enforced. Multiple application instances, IIS overlap, or two monitors aimed at the same targets multiply polling. Sharing a data root also races on latest.json.tmp and loses updates because the semaphore is process-local. | ResultStore.cs:17,185-200; OPERATIONS.MD:39 |

## Monitoring host impact

| Resource | Normal behavior | Peak and long-running implications |
|---|---|---|
| CPU | Event-driven waits should keep genuinely idle CPU low. Even without browsers, enabled monitoring is not idle: inventory parsing, JSON serialization, file operations, and child runtime startup recur. | Four simultaneous cold collectors plus the web process can create short CPU/antivirus bursts. There is no CPU quota, so no defensible percentage or peak ceiling can be inferred from source. At stable inventory and workload, no time-driven CPU increase is evident. Whole-file I/O and concurrent browsers can make larger fleets expensive. |
| Memory | Inventory/request objects, hashes, snapshots, JSON strings, and WMI dictionaries are transient. Ping history is trimmed to three entries per updated server. SnapshotStore is request-scoped and has no growing cache. | Normally at most four active child working sets, plus parent allocations and web requests. Full file reads/serializations produce substantial temporary allocation at scale. The JSON file has no read-size cap and retains removed IDs, so inventory churn can increase file size and allocation indefinitely. No measured MB figure is available. |
| File/process handles | File streams, CSV writers, Ping objects, Process objects, and timers use using/await using. Semaphore acquisition is paired with finally release. | No ordinary handle leak was found in these paths. A hung operation retains its live resources; orphaned child processes can outlive the parent. Disposal of a Process wrapper does not terminate a surviving OS process. |
| WMI/RPC resources | Searchers, result collections, and individual ManagementBaseObject instances are explicitly disposed. Remaining COM state is confined to a short-lived child. | Child exit is a useful local cleanup boundary. It does not guarantee prompt cleanup of remote provider work. Provider leaks or delays on targets are outside the parent's memory boundary. |
| Threads/tasks | Two awaited loops, two timers, and bounded parallel batches. No recurring Task.Run chain, unbounded work queue, or timer-per-server pattern was found. | Four metrics slots and four ping server slots are separate, not four total. With eight addresses per server, four ping slots can create 32 in-flight ping operations. A thread count is not the same as a task count; WMI may block native/COM threads inside children. A blocked cleanup reduces capacity rather than automatically growing normal batches. HTTP request pressure has no application-level rate limit. |

For all credentialed targets at a five-second details cadence, successful child starts would be:

| Servers | Starts/second | Starts/day |
|---:|---:|---:|
| 8 | 1.6 | 138,240 |
| 25 | 5 | 432,000 |
| 50 | 10 | 864,000 |
| 100 | 20 | 1,728,000 |

These rates require collection to finish fast enough. With four slots, the approximate average per-server time budget is 20/N seconds: 2.5s at eight servers, 0.8s at 25, 0.4s at 50, and 0.2s at 100, before allowing for shared storage overhead and batch imbalance. Increasing concurrency to force a cadence increases host/provider pressure; allowing a longer interval is safer.

For the current inventory, normal successful WMI collection would start about 0.4 children/second, or 34,560/day. Missing credentials skip WMI but do not skip the server's details snapshot/history write.

**24/7 resource verdict:** no obvious ordinary managed-memory, handle, or thread leak was found. This does not establish 24/7 safety: stale data, storage failures, process cleanup failures, inventory churn, and host recycling still need controls and runtime validation.

## Ping collection and monitored-server traffic

Assuming one enabled IP per server, one echo request per second, and cycles completing on schedule:

| Servers | Requests/second | Requests/minute | Requests/day |
|---:|---:|---:|---:|
| 8 | 8 | 480 | 691,200 |
| 25 | 25 | 1,500 | 2,160,000 |
| 50 | 50 | 3,000 | 4,320,000 |
| 100 | 100 | 6,000 | 8,640,000 |

These count outbound requests. If all succeed, replies add the same number of packets. Use the total number of enabled addresses instead of the server count when servers have multiple addresses. The code pings every address, even after another address succeeds. The current inventory therefore nominally sends five requests/second, 300/minute, and 432,000/day; with replies, about ten ICMP packets/second overall.

For an illustrative IPv4 ping with a 32-byte payload, 20-byte IP header, and 8-byte ICMP header, a request/reply pair is 120 IP-layer bytes. Eight one-address servers would use about 0.96 kB/s and 82.9 MB/day; 100 would use about 12 kB/s and 1.04 GB/day. This excludes Ethernet/VLAN/VPN overhead, ARP, IPv6 differences, and failures. The implementation uses the .NET default payload rather than specifying its own; the byte example is an assumption, not captured traffic.

One small ping per address per second is normally light work for healthy servers and ordinary LANs. It is not a realistic direct damage mechanism. It can still be logged, blocked, or classified as discovery by local policy. Across many destinations it becomes a recognizable sweep pattern. Daily packet totals alone do not imply flood-level throughput.

The one-second interval is a scheduling target, not a per-server guarantee. With four slots and all pings taking the one-second timeout, 100 servers need roughly 25 seconds per batch plus overhead. There is no accumulating ping backlog; outage detection becomes slower.

## WMI collection and target load

Each successful attempt creates a new child and connects a fresh ManagementScope to root/cimv2 over DCOM/RPC. This is a new application connection attempt; exact transport connection counts depend on Windows RPC behavior. It performs:

1. SELECT PercentProcessorTime from Win32_PerfFormattedData_PerfOS_Processor for _Total.
2. If needed, SELECT LoadPercentage and NumberOfLogicalProcessors from Win32_Processor.
3. SELECT TotalVisibleMemorySize and FreePhysicalMemory from Win32_OperatingSystem.
4. SELECT DeviceID, FreeSpace, and Size from local fixed Win32_LogicalDisk instances.

There are normally three queries, with four when CPU fallback is used. Disk collection reads capacity metadata; it does not scan files, read disk contents, or benchmark storage. No method invocation, service restart, configuration write, or target modification appears in the collector.

At five seconds, per credentialed server, nominally:

| Operation | Per minute | Per hour | Per day |
|---|---:|---:|---:|
| Successful connection/child attempt | 12 | 720 | 17,280 |
| Three normal queries | 36 | 2,160 | 51,840 |
| Four queries with CPU fallback | 48 | 2,880 | 69,120 |

For 100 credentialed servers this is 60-80 WMI queries/second across the fleet, assuming sufficient throughput. Target sessions need endpoint-mapper/negotiated RPC access; this is not just ICMP and not WinRM. Authentication, provider initialization, result marshaling, and security logging can cost more than the returned metric bytes suggest.

**Expected target CPU/memory impact:** low for these narrow queries on healthy providers, with temporary provider/RPC/authentication allocations. Exact CPU percentages, memory deltas, or a guarantee of no WmiPrvSE growth cannot be derived from application code. Repeated requests can stress an already unhealthy provider, and killing the local collector does not kill remote WmiPrvSE or guarantee cancellation of provider work already started. Other monitoring systems and duplicate inventory entries can multiply this load. Validation prevents duplicate IPs within an entry, but not reuse of an IP across different entries.

**Failure pressure:** there is no backoff, jitter, per-account lockout protection, or cooldown. A two-address entry with quick authentication failures can make about 24 collector attempts/minute or 34,560/day. The current two credentialed entries together could make about 48/minute or 69,120/day. These are collector attempts, not a guarantee of one Windows authentication event per attempt. WMI continues to be attempted even when ping reports offline, which avoids treating ICMP filtering as proof of WMI failure but sustains traffic to unreachable targets.

**Interval recommendation:** keep one-second ping only where that detection latency is useful; use two to five seconds otherwise. Start details polling at 30 seconds, or 60 seconds for larger fleets/fragile providers. Keep MaxConcurrentChecks=4 initially. A 30-second interval cuts WMI starts, successful query volume, and ordinary authentication attempts by about six times. It does not remove the need to stop repeated invalid-credential attempts, and it only modestly reduces latest.json writes because ping dominates them.

Source: [WmiCollector.cs](../Services/WmiCollector.cs), lines 19-65,85-164,167-245,248-282; [ServerCollector.cs](../Services/ServerCollector.cs), lines 77-155.

## Child process isolation

| Control | Assessment |
|---|---|
| Normal timeout | Linked cancellation and CancelAfter(20s) cover stdin writing and the parent waiting for child exit, per attempted address. Startup itself precedes that deadline. |
| Hung WMI call | Normally contained by killing the child while the parent is alive. The apparent three-second ConnectionOptions timeout is not a dependable connection deadline: the installed System.Management 8.0.0 constructor documentation marks its timeout parameter as reserved. No explicit enumeration timeout is set on the searcher. |
| Multiple addresses | Two fully timed-out addresses can consume roughly 40 seconds for a server; eight can consume roughly 160 seconds, before cleanup overhead. This is not a 20-second server-wide deadline. |
| Process tree | Kill(entireProcessTree:true) is the right intent for timeout/cancellation. It does not provide kill-on-parent-exit ownership. No Windows Job Object, child self-watchdog, or parent-lifetime mechanism is present. |
| Cleanup failure | Kill may throw, including partial tree termination. The catch block has no second cleanup boundary. A surviving process can lose its parent wrapper and later attempts can start more children. This is a failure-path accumulation risk, not evidence of routine accumulation. |
| Cleanup duration | WaitForExitAsync(None) and subsequent pipe drains are unbounded. They can hold a metrics slot or delay graceful shutdown beyond the configured deadline. Waiting for the immediate process does not itself establish that every descendant has exited. |
| Pipes | Both stdout and stderr are drained concurrently before stdin is written; this avoids the usual full-pipe deadlock. Reads use ReadToEndAsync with no size limit. The normal child emits one small JSON object, but unexpected output can consume unbounded parent memory. A descendant retaining a pipe handle can prevent EOF. |
| Credentials | Passed over redirected stdin, not process arguments. Shell execution is disabled and ArgumentList is used. The command line contains the address, not the password. Credentials still exist in parent/child memory and local dumps; pipes are not a defense against administrators or a compromised service identity. |
| Collector mode | Does not start the web app or another worker. It has no independent total deadline, so an orphan in a hung WMI call can run indefinitely. |

On Windows, the practical risk is surviving/orphaned processes and retained resources, rather than Unix-style zombie entries. Normal exits and normal successful kills are handled reasonably. Abrupt parent failure and exceptional cleanup can escape the protection. Remote provider hangs remain outside the local process tree.

Source: [CollectorProcess.cs](../Services/CollectorProcess.cs), lines 38-126 and 129-178; [Program.cs](../Program.cs), lines 55-95. Installed Microsoft reference XML documents Kill exceptions and disposal; it does not make this code's cleanup failure-proof.

## File system writes, growth, and interruption safety

Let N be the number of enabled servers and K the number of snapshots ever retained in latest.json. Under the requested one-second ping and five-second details cadence:

- latest.json replacements/second = N/1 + N/5 = 1.2N.
- History row appends/second = N/5 = 0.2N.
- Total logical data writes/second = 1.4N.

One replacement means creating/writing the temporary file and moving it over latest.json. These are logical application operations, not exact disk IO requests or physical NAND writes. Metadata changes, headers, retention deletes, cache coalescing, and antivirus activity are additional factors. Startup adds one initial write only when latest.json is missing. Failed/slow cycles reduce successful write counts.

| Enabled servers | latest/minute | latest/hour | latest/day | CSV rows/minute | CSV rows/hour | CSV rows/day |
|---:|---:|---:|---:|---:|---:|---:|
| 3, current inventory | 216 | 12,960 | 311,040 | 36 | 2,160 | 51,840 |
| 8 | 576 | 34,560 | 829,440 | 96 | 5,760 | 138,240 |
| 25 | 1,800 | 108,000 | 2,592,000 | 300 | 18,000 | 432,000 |
| 50 | 3,600 | 216,000 | 5,184,000 | 600 | 36,000 | 864,000 |
| 100 | 7,200 | 432,000 | 10,368,000 | 1,200 | 72,000 | 1,728,000 |

For eight enabled servers, combined logical writes are 672/minute, 40,320/hour, and 967,680/day. At 100 they are 8,400/minute, 504,000/hour, and 12,096,000/day. The current three enabled servers produce nominally 252/minute, 15,120/hour, and 362,880/day.

**Write volume versus retained capacity:** latest.json replaces its previous version; all those rewrites do not accumulate as separate files. However, every save deserializes and serializes all K entries. Work scales approximately as O(NK); when K=N, byte/serialization work is quadratic in fleet size. Each details check also reads the complete file before collection, and dashboard requests read it again.

Illustration only: assume 1,000 bytes of indented JSON per stored snapshot, K=N, and 64 bytes per CSV row. These are convenient estimates, not measured file sizes.

| Enabled servers | latest.json logical bytes written/day | CSV growth/day | Seven days of CSV |
|---:|---:|---:|---:|
| 3 | 0.93 GB | 3.32 MB | 23.2 MB |
| 8 | 6.64 GB | 8.85 MB | 61.9 MB |
| 25 | 64.8 GB | 27.65 MB | 193.5 MB |
| 50 | 259.2 GB | 55.30 MB | 387.1 MB |
| 100 | 1,036.8 GB | 110.59 MB | 774.1 MB |

Values use decimal units and exclude metadata/headers. Actual byte volume scales linearly with the actual serialized file size. At eight servers this is usually a modest capacity/endurance concern, although the operation count is wasteful. At 50-100, write traffic, serialization, storage contention, and device endurance merit serious attention. Logical write estimates are not NAND wear measurements; OS caching and controller behavior can change physical writes. SSD model, endurance rating, remaining life, and measured host writes are needed for a wear prediction.

**Retention:** the cutoff includes the current UTC date and six prior dates with HistoryRetentionDays=7. Expiration runs after each completed details batch and understands both dated .csv and .metrics.csv names. Ordinary daily CSV growth is controlled while the app runs, inventory is valid, and enumeration/deletion succeeds. It is a date policy, not a byte quota. Malformed filenames, future-dated files, other logs, and a stopped/blocked worker are outside effective cleanup. A deletion exception can stop that cleanup pass. Retention runs as often as every five seconds even though once daily would suffice.

**Unbounded data case:** latest.json never prunes removed server IDs. A fixed fleet has bounded current data and three ping records per updated server; continuously changing IDs leaves old snapshots behind forever. External IIS/application/EDR logs also have no retention policy in this code.

**Replacement correctness:** the temporary file is in the same directory, the process-local lock serializes writers, and readers allow delete sharing. This is the appropriate temporary-file/rename pattern for ordinary local-file interruption protection. An interrupted temporary write ordinarily leaves the prior latest.json usable, and normal exception cleanup removes the temporary file. It is not a power-loss durability guarantee: there is no durable flush, backup copy, schema/integrity recovery, or universal filesystem transaction guarantee. A hard crash may leave a temporary file; the next write normally replaces it. The practical rename assumption is a normal local Windows filesystem such as NTFS; arbitrary mapped/reparse-backed storage has not been validated.

CSV appends have no atomic transaction or recovery; interruption can leave a partial last row, and latest.json and history can disagree because they are separate writes. The CSV intentionally has no per-row timestamp, limiting its usefulness for precise incident reconstruction.

Source: [ResultStore.cs](../Services/ResultStore.cs), lines 19-83,151-215,218-319; [DataPaths.cs](../Services/DataPaths.cs), lines 23-53.

## Network, security products, and dashboard traffic

ICMP volume is small at the listed scales. WMI byte volume cannot be calculated from query count alone: connection/authentication setup, negotiated transport, provider responses, retries, and security logging matter. Its more important concern here is frequent new authenticated attempts, especially after failures. Per application, normal active WMI collectors are bounded to four; there is no enforced aggregate limit across multiple monitors or duplicate target entries.

Dashboard refresh fetches the complete card fragment approximately once per second per tab, with no response-compression middleware configured in the app. A reverse proxy may compress it externally. Let H be the transferred bytes per refresh and C the concurrent tabs: approximate traffic is C*H bytes/second and 86,400*C*H bytes/day. For an illustrative 50 kB response, one tab transfers about 4.32 GB/day and ten tabs about 43.2 GB/day, before headers. That response size is an example, not a measurement. All cards, including disabled entries, are rendered, so HTML grows with total inventory rather than only enabled targets.

Average browser request rate is about C/second, with bursts if clients synchronize. The client prevents overlap within one tab, but the server has no application-level client limit or rate limiter. A request taking longer than one second is followed by an immediate next attempt. An eight-second client abort does not guarantee a server-side render stops: the handler's synchronous inventory/result reads do not accept RequestAborted. Pathological disk latency or many clients can therefore exhaust web capacity even though target polling remains bounded.

The following are deployment checks and operational inferences, not claims about universal vendor detection rules. External documentation and the site's actual policies were unavailable.

| Product | Potential interpretation/impact to check |
|---|---|
| Microsoft Defender | Repeated child execution and remote WMI authentication may create endpoint telemetry or alerts under enabled rules. No target process execution is requested by these queries. |
| Windows Firewall | ICMP and WMI/DCOM/RPC require the appropriate scoped rules. Blocking them can look like a server outage or a metrics failure respectively; neither proves the server is unhealthy. |
| Sophos | Endpoint/network controls may classify repeated WMI access or many failed authentications as suspicious. Verify enabled modules and policy with the security team. |
| FortiGate | IPS/anomaly/logging policy may react to multi-destination ICMP/RPC or denied connection volume. Low bandwidth does not guarantee low log volume. |
| Palo Alto | Threat/zone/application policy may flag discovery-like patterns or repeated authentication failures, depending on configuration. |
| Cisco Firepower | Intrusion/access-control/logging policy may identify ICMP sweeps, RPC patterns, or repeated denials according to configured rules. |

Register the authorized monitor source and scope required permissions/network access to the intended targets. Broad firewall openings or disabling endpoint protection are unnecessary. Five-second WMI retries without backoff are the most plausibly aggressive behavior in this implementation; one-second small ICMP requests alone are generally a minor load.

## Browser runtime

**Memory/listeners:** one refresh chain, one in-flight fetch, an abort timeout cleared in finally, two document audio listeners, one ResizeObserver observing the persistent container, one theme change listener, and one localStorage key. Refresh does not reinitialize the script or add new listeners to every incoming card. Per-refresh maps, parsed documents, and arrays are local; removed cards are removed from the DOM and no persistent collection of detached cards was found. No obvious indefinite JavaScript retention pattern appears in the code.

**DOM/SVG:** existing cards and SVG ring nodes are reused; changed headings, information, and labels are replaced selectively. Initial Web Animations are finite and only run for initial/new cards; CSS ring transitions are finite too. Every refresh still parses the entire HTML response and recalculates guide geometry for every card. Interleaved geometry reads and SVG writes can force layout, and repeated ping-log replacement creates garbage. This is low concern for eight cards and greater CPU/GC work for large inventories or slower kiosk hardware. The fixed viewport layout is designed around a small inventory; 50-100 cards need usability/capacity validation as well as a memory check.

**Audio:** one looping audio element, with a flag preventing simultaneous play attempts. There is no audio-object creation per refresh. Autoplay rejection is caught; normal interaction retries playback. A hung play promise can suppress later attempts but does not create an expanding promise queue. Single ping failures immediately trigger the error state/audio; there is no failure hysteresis, so flapping can create alarm fatigue.

**24/7 behavior:** no obvious deterministic leak was found, but continuous refresh, no hidden-tab pause, no retry backoff, full HTML parsing, and repeated layout work are sustained costs. Browser/driver/media behavior still requires a real kiosk soak test. Refresh failure retains the last cards and sets a retry notice; it does not make old card values current. Successful HTTP refresh cannot detect a stopped collector without server-side freshness checks.

Source: [dashboard.js](../wwwroot/js/dashboard.js), lines 100-145,147-225,237-272,294-357,373-410; [theme.js](../wwwroot/js/theme.js); [_Layout.cshtml](../Pages/Shared/_Layout.cshtml); [_ServerCards.cshtml](../Pages/_ServerCards.cshtml).

## Failure modes and containment

| Failure | Current response | Safety consequence |
|---|---|---|
| Invalid/rotated WMI credentials | Returns warnings, tries the next address when no metric was usable, logs, retries on the next details batch. | Ping remains independent; authentication pressure and possible account lockout are not contained. |
| One WMI metric fails | Other metric readers continue; any usable result stops address fallback. | Good partial-result containment. Warnings are stored/logged but not shown in cards. |
| Slow/hung WMI provider | Parent normally kills the child after the per-address deadline, then may try another address. | Protects the usual web-process path, with orphan/unbounded-cleanup gaps described above. Four slow targets can delay all other metrics in the batch. |
| All ping targets unreachable | Each address has a one-second configured timeout; up to four server checks proceed at once. | No task pileup, but complete-batch cadence stretches with fleet size. ICMP filtering can generate false outage alarms. |
| Intermittent/partial network failure | Repeated checks and address fallback; connectivity and metrics are separate. | Limits metric-to-ping coupling, but lacks backoff and alert hysteresis. A failed address can slow a successful multi-address ping because all replies are awaited. |
| Disk full or permission loss after startup | Save errors are caught per server; previous file can remain readable. History append may fail after the latest snapshot succeeded. | Whole monitor loses persistence; old Online values can remain visible. Repeated log output can add pressure to log infrastructure on the same host. No automatic storage recovery/health state. |
| Missing write permission at startup | Initialize throws outside the cycle-level catch. | Hosted service startup can fail; there is no degraded collection mode. This is explicit failure, but requires operator visibility. |
| Malformed/unreadable latest.json | SnapshotStore shows generic unavailability/placeholders if read throws. Updates also fail on read; metrics cannot reach collection. | Dashboard fails visibly for an unreadable file, but both collection paths remain blocked until repair. A valid old file is more deceptive because it shows no storage error. |
| Malformed inventory | All monitoring pauses for that batch; errors displayed; inventory reloaded next time. | No invalid target operations. One entry's invalid configuration stops the whole inventory; frequent error logs and skipped retention can persist. |
| One history deletion fails | Error logged at cycle level; later cycles retry. | Old data can accumulate; remaining files in that pass may not be deleted. No disk quota. |
| Parent crash, IIS recycle, idle shutdown, machine restart | Monitoring stops with the web host; normal cancellation requests cleanup. | No independent monitor service or durable scheduler. Abrupt termination can leave collectors behind; restart success and dashboard availability do not establish collection freshness. |
| Two app instances share a data root | No cross-process lock; same temporary filename and separate read/modify/write operations. | Lost updates, sharing errors, competing history writers, and duplicate target load. IIS overlapping recycle can create a temporary second instance even if a web garden is not intended. |

Failures are mostly localized at the individual collection task. Shared storage, inventory validity, host lifetime, and monitoring-account authentication are cross-server failure domains. Therefore failures can cascade into lost observability, account lockout, or host/log pressure even though the target queries themselves are read-only.

## Credential and data security

- Plaintext credentials reside in servers.json, which is excluded by .gitignore but explicitly copied to build/publish output. Git ignore is not an access-control or backup policy. Restrict source, deployment, artifact, backup, and dump access; use a dedicated account with only required remote WMI/DCOM/read permissions, without sharing a service's operational account.
- Passwords travel to the child through stdin. CollectionFailure replaces the exact current password in ordinary collector messages; raw child stderr is suppressed in parent errors. This is a useful defense, not comprehensive secret scanning. Encoded/modified forms, unrelated exception paths, dumps, and copies of the inventory remain outside that redaction.
- PacketPrivacy is explicitly requested for WMI RPC authentication. This is the correct intent for protected WMI payloads; it does not secure dashboard HTTP, local files, backups, or a compromised endpoint. Negotiated authentication, server identity assurance, and any IP-address-based authentication-policy restrictions require deployment validation.
- Snapshot/history output does not contain the password directly. The stored configuration revision is SHA-256 over the complete definition, including credentials; treat stored monitoring files as sensitive too. A fingerprint is not a credential vault.
- Razor's ordinary encoded output is used for inventory values. No direct unencoded rendering of server names/passwords was found. CSV fields are quoted and formula-leading text is prefixed, reducing spreadsheet-injection risk.
- The dashboard exposes names/IPs even for disabled entries. It has no built-in authentication requirement, no application-level rate limit, and no specific security-header policy beyond production HSTS/HTTPS redirect. External IIS/proxy authentication may supply access control, but was not verified. Development launch settings enable anonymous access; they do not establish production behavior.
- Default inventory/data paths are outside wwwroot and are not served by the normal ASP.NET static-file middleware. DataRoot validation does not reject a directory under wwwroot, mapped drives, or reparse-backed storage; keep it private and local operationally. A custom IIS/proxy static-file mapping can also change exposure. HTTPS redirection alone does not configure a certificate or prove the deployed site is HTTPS-only.

**Security verdict:** no direct command-line password leak or direct password rendering was found. File/artifact confidentiality and dashboard access control remain material concerns. This audit is not a package-CVE or deployment-penetration assessment.

## Production risk table

Ratings are for the supplied implementation/configuration, with deployment controls and actual hardware unverified.

| Area | Risk | Notes |
|---|---|---|
| Monitoring Host | Medium | Bounded normal tasks/children; process churn, shared JSON storage, unbounded cleanup, and web-host lifetime limit resilience. |
| Monitored Servers | Low | Read-only, narrow queries and small ICMP load. Becomes Moderate during bad-credential retries, provider problems, or duplicated monitoring. |
| WMI Collection | Medium | Five-second reconnects are frequent; four slots bound usual concurrency but no backoff or remote-work cancellation guarantee exists. |
| Network Impact | Low | ICMP bandwidth is minor. WMI setup, client refresh traffic, security logging, and multi-monitor aggregation need measurement. |
| Disk I/O | Medium | Usually manageable for the current three enabled servers; whole-file rewrites scale poorly, with High concern at 50-100 under the nominal cadence. |
| Browser Runtime | Low | No obvious expanding listener/node/task retention; continuous parsing/layout work and missing freshness remain concerns. Larger inventories need validation. |
| Security | High | Plaintext copied credentials and no application-enforced dashboard access control; external controls may reduce this rating. |

## Final decisions

**Would I personally run it in production 24/7 as supplied? NO.** I would first add failure backoff, freshness/worker health visibility, access protection, and reliable collector lifetime handling, then validate the actual deployment. The basic design can be retained; a new platform or dependency-heavy redesign is unnecessary. A supervised small internal pilot is a different readiness bar from unattended production operation.

**Could it damage, overload, degrade, destabilize, or materially affect monitored servers? Low risk in normal operation.** There is no code path that intentionally alters target state, scans target files, or launches remote processes. Routine one-second ping and these small WMI reads should be light on healthy targets. This is not “no realistic risk”: invalid-credential retries can lock accounts, unhealthy providers can consume resources, and duplicated monitoring can multiply load. Under those failure conditions the operational risk is Moderate, including possible impact to services sharing a locked account. No measurements support a universal overload threshold.

**Overall production safety score: 6/10.** Bounded concurrency, separate loops, disposal, partial-result handling, protected child stdin, PacketPrivacy, and temporary-file replacement earn credit. Missing stale-data detection, backoff, crash-safe child ownership, enforceable single-instance operation, corruption recovery, and verified deployment access controls prevent an 8/10 production-ready rating. This is an engineering judgment, not a measured reliability statistic.

### Top five risks

1. Invalid credentials repeatedly retried, causing account lockout and security/logging pressure.
2. Collection/persistence failures leave apparently healthy but old data visible.
3. Plaintext credential copies and a dashboard with no application-enforced authentication.
4. Collector lifetime depends on the parent; kill/drain failures can retain slots or surviving processes.
5. Whole-file persistence and a shared process-local lock amplify storage work and shared failures; multiple instances make this worse.

### Top five improvements, preserving the architecture

1. **Reduce and control retries.** Start WMI at 30 seconds, add a short per-target cooldown that increases on transient failures, and stop alternate-IP/repeated attempts for explicit authentication failures until credentials change or an operator resets the state. Apply account-aware protection when targets share an account. Rate-limit repeated identical warnings. Keep ping separate.
2. **Make data age explicit.** Record metrics attempt/success times and worker/store last-success state. Render stale connectivity/metrics as stale or unknown, even when HTTP reads succeed. Surface collection/storage problems distinctly from target outages, and expose a simple health response based on worker progress and persistence, not merely web availability.
3. **Secure deployment boundaries.** Require existing IIS/Windows/proxy authentication for the dashboard, enforce HTTPS and restrictive ACLs, and isolate a least-privilege monitoring identity. Prefer an existing Windows-protected secret mechanism over plaintext copies. If inventory format remains unchanged initially, tightly control credential-bearing publish/backups and document rotation. Keep data outside served directories.
4. **Finish process and instance ownership.** Bound cleanup/pipe draining, cap child output, handle kill errors explicitly, and use Windows kill-on-close Job Object ownership or a carefully implemented independent child watchdog. Acquire a named single-instance guard before starting the worker; configure hosting to avoid competing writers and deliberate monitoring gaps. Keep one-shot child isolation.
5. **Batch and recover file persistence.** Merge bounded in-memory updates and flush one latest snapshot per chosen cadence, preserving temporary-file replacement. Remove retired IDs, cap accepted result-file size, quarantine/recover malformed files from a last-known-good copy, and report low space/write failures. Run date retention daily and continue after individual deletion failures. Keep JSON and CSV; a database is not required at this scale.

### User-run validation needed before approval

The following are proposed checks, not checks executed in this session: a 24-72 hour soak at intended fleet/client size; baseline and trend measurements for parent/child CPU, private bytes, handles, threads, child counts, write bytes, target WmiPrvSE, and security logs; controlled invalid-credential, WMI-hang, disk-full/permission, file-corruption, and app-recycle tests; confirmation of authentication/ACLs, single-instance hosting, restart behavior, and runtime patch/support status. Use an isolated environment and a dedicated test account for failure injection. No intranet conclusions can be drawn from this hotspot development session.

## Evidence index

| File | Relevant locations |
|---|---|
| [MonitoringWorker.cs](../Services/MonitoringWorker.cs) | Initialization 16; two loops 24-33; scheduling/error boundary 40-95; per-loop concurrency 107-151; storage ordering 154-190. |
| [ServerCollector.cs](../Services/ServerCollector.cs) | Concurrent/disposed pings 18-41; credential skip 80-83; sequential address retries and partial success 89-155. |
| [CollectorProcess.cs](../Services/CollectorProcess.cs) | JSON input 17-25; process/pipe/deadline/cleanup 44-121; safe argument/start settings 129-178. |
| [WmiCollector.cs](../Services/WmiCollector.cs) | Authentication/connect 19-35; partial errors 37-81; fixed query set 85-245; explicit WMI object disposal 248-282. |
| [ResultStore.cs](../Services/ResultStore.cs) | Init/read 19-83; three ping records 95-110; full read/modify/write and rename 151-215; CSV 218-258; date retention 266-317. |
| [SnapshotStore.cs](../Services/SnapshotStore.cs) | Request-scoped reader and generic storage fallback 16-62. |
| [Configurations.cs](../Models/Configurations.cs) | Option ranges 18-39; inventory reload 60-66; 1 MiB cap 68,96-113; per-entry validation 137-272. |
| [ServerSnapshot.cs](../Models/ServerSnapshot.cs) | LastPingUtc exists; no metrics timestamp/freshness policy. |
| [ServerDefinition.cs](../Models/ServerDefinition.cs) | Plaintext fields 20-24; complete-definition fingerprint 29-33. |
| [Program.cs](../Program.cs) | Child-mode branch 15-20; collector input 55-95; startup validation 98-116; services/pipeline 126-166. |
| [Index.cshtml.cs](../Pages/Index.cshtml.cs) | Read-only partial handler 27-33; no-store/cards 35-70; connectivity-only display state 88-105. |
| [dashboard.js](../wwwroot/js/dashboard.js) | One-time initialization 100-113; audio 116-144; DOM/SVG reuse 237-325; fetch and scheduling 336-410. |
| [theme.js](../wwwroot/js/theme.js) | One listener and bounded storage 16-65. |
| [appsettings.json](../appsettings.json) | One-second ping, five-second details, one-second ping timeout, 20-second child deadline, four concurrent server checks per loop, seven-day history. |
| Installed Microsoft System.Management 8.0.0 reference XML | %USERPROFILE%/.nuget/packages/system.management/8.0.0/lib/net8.0/System.Management.xml:162,175 (connection timeout reserved),1271 (ManagementOptions.Timeout). |
| Installed Microsoft .NET 8 reference XML | Microsoft.NETCore.App.Ref/8.0.19/ref/net8.0/System.Diagnostics.Process.xml:173-192 (tree kill and documented failure cases); Process.Dispose and WaitForExitAsync member documentation also inspected. |

Line numbers refer to the source at review time. No live-resource measurements or external vendor-policy verification are implied.
