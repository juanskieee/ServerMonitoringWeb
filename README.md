# Server Monitoring Web

A Windows-based ASP.NET Core Razor Pages application for monitoring server reachability and resource utilization. It modernizes the legacy Windows Forms ping checker using the same .NET 8 build for the dashboard and monitoring worker.

## Features

- ICMP connectivity checks and recent ping results.
- Read-only WMI collection of CPU, RAM, and fixed-drive utilization.
- Independent background ping and metrics cycles.
- Dashboard with light/dark themes, automatic refresh, and connectivity warning audio.
- Latest results in JSON and outage/recovery history in daily CSV files.
- Independent Windows Service monitoring, IIS dashboard-only hosting, and combined development/desktop mode.

## Requirements

- Windows and .NET 8 SDK for development.
- Visual Studio or a terminal with the .NET CLI.
- Approved network access to monitored systems and appropriately authorized WMI/DCOM accounts.
- For IIS: the ASP.NET Core Hosting Bundle, a configured application pool, and access to the deployed inventory and data directory.

## Quick start

Create a local `servers.json` in the project root using the inventory format documented in [OPERATIONS.MD](Documentation/OPERATIONS.MD#managing-inventory). Use only authorized credentials; never commit the inventory or monitoring logs.

From the project root, run:

```powershell
dotnet build .\ServerMonitoringWeb.sln
dotnet run --project .\ServerMonitoringWeb.csproj --no-build --launch-profile http
```

Open the address reported by the application. Closing the browser does not stop monitoring in combined mode, but stopping the host does. Production can use the independent Windows Service.

## Deployment and data

Publish the application in Release mode to the local IIS staging profile (`artifacts/iis-publish`), then use the administrator-approved deployment procedure in Operations. The staged output excludes the private inventory and monitoring Logs. Use the [administrator-approved Windows Service migration](Documentation/OPERATIONS.MD#production-windows-service--iis-dashboard): the service collects independently, and IIS uses Monitoring:RunWorker=false. Keep service binaries separate from the IIS deployment. Do not run a second collector against the same data directory. Service installation is not automatic.

Preserve the deployed `servers.json` and existing `Logs` directory. The default data directory is `Logs` beside the application; `Monitoring:DataRoot` can override it with an absolute local path.

| File | Purpose |
| --- | --- |
| `servers.json` | Inventory, configured addresses and optional WMI credentials |
| `appsettings.json` | Monitoring intervals, timeouts, retention and logging |
| `Logs/latest.json` | Latest monitoring snapshots and recent ping entries |
| `Logs/History/` | Daily outage/recovery CSV files |

Grant the worker identity read access to the inventory and Modify access to the data directory only. Dashboard-only IIS requires read access. The repository does not contain the deployed inventory. Keep it outside the publicly served `wwwroot` directory and protect it with appropriate filesystem ACLs.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| IIS HTTP 500.30 | Inspect ASP.NET Core Module startup logs, hosting prerequisites and application identity permissions. |
| Save ping results (storage) failed | Inspect existing storage access and the logged exception type/HRESULT. This is not evidence of failed ICMP. |
| WMI `0x80070005` / `0x80041003` | Verify approved DCOM and WMI namespace permissions, not just the password. |
| WMI `0x80041064` | Verify the actual selected IP and deployed version; local IPs use the process identity without alternate credentials. |

See [Operations](Documentation/OPERATIONS.MD) for the complete deployment, configuration and troubleshooting instructions. Do not weaken security policy or publish credentials, private infrastructure details or raw production logs.

## Project layout

| Path | Responsibility |
| --- | --- |
| `Program.cs` | Startup, service registration and collector mode |
| `Models/` | Inventory configuration and monitoring data models |
| `Services/` | Monitoring worker, ICMP/WMI collection and data persistence |
| `Pages/` | Razor Pages dashboard |
| `wwwroot/` | CSS, JavaScript and other static assets |
| `Legacy/` | Original implementation and collection prototypes |
| `Documentation/` | Technical context, operating procedures and development rules |

## Development documentation

- [CONTEXT.MD](Documentation/CONTEXT.MD): technical source of truth and known limitations.
- [OPERATIONS.MD](Documentation/OPERATIONS.MD): configuration, deployment and maintenance.
- [SERVER-DIAGNOSTIC-TESTS.MD](Documentation/SERVER-DIAGNOSTIC-TESTS.MD): server-side diagnostic commands and results checklist.
- [AGENTS.md](Documentation/AGENTS.md): development rules and verification boundaries.
- [PRODUCTION-SAFETY-AUDIT.MD](Documentation/PRODUCTION-SAFETY-AUDIT.MD): production safety audit.

Keep changes targeted, preserve legacy business behavior, and validate builds locally before deploying. Production network, WMI and IIS validation must be performed in the authorized environment.
