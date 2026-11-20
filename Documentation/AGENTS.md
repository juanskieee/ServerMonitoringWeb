# Project Rules

These rules apply to all work on the Server Monitoring System unless the user explicitly requests otherwise.

---

# Platform and Framework

- Keep the application and all related C# tooling on:

```text
.NET 8 (net8.0)
```

- Do not upgrade or downgrade the target framework unless the user explicitly requests a framework change.

- Preserve compatibility with the existing solution structure, deployment model, and monitoring workflow.

---

# Verification and Testing

The user performs runtime validation.

Do not:

- Run intranet monitoring probes
- Execute live connectivity checks
- Perform IIS verification
- Test remote servers
- Infer server health from local connectivity failures

Unless the user explicitly requests otherwise.

Use only the minimum verification needed for the current task.

Allowed examples:

- Solution builds
- Static analysis
- Source inspection
- Compile-time validation

Avoid:

- Repeated builds
- Duplicate verification
- Unrelated checks
- Long validation passes for unchanged areas

---

# Code Style

Favor simple, readable, beginner-friendly code.

Code should prioritize:

- Clear names
- Straightforward execution paths
- Easy debugging
- Predictable behavior

When choosing between approaches, prefer the one that is easier to:

- Read
- Trace
- Diagnose
- Maintain

---

# Design Principles

Avoid introducing unnecessary complexity.

Unless required by the task, do not add:

- New dependencies
- Additional abstraction layers
- Premature optimizations
- Speculative architecture
- Generic helper frameworks
- Infrastructure that solves hypothetical future problems

Prefer explicit implementation over abstraction when the implementation remains manageable.

---

# Cleanup Requirements

When replacing an implementation:

1. Introduce the replacement.
2. Remove the superseded implementation.
3. Remove related workarounds.
4. Remove obsolete optimizations.
5. Remove dead code.
6. Remove unused logic.
7. Remove temporary generated artifacts.

A task is not complete if old and new approaches coexist unnecessarily.

---

# Documentation Maintenance

Documentation must reflect the current implementation.

After each completed task:

- Update `CONTEXT.MD` with current behavior.
- Update design decisions when they change.
- Update verification notes when appropriate.
- Update limitations and constraints when they change.
- Remove obsolete instructions.
- Consolidate historical notes instead of leaving conflicting guidance.

Keep documentation concise and authoritative.

Do not allow multiple sections to describe the same behavior differently.

---

# Documentation Responsibilities

## CONTEXT.MD

`CONTEXT.MD` is the technical source of truth.

Document:

- Current implementation
- System behavior
- Architectural decisions
- Operational constraints
- Current verification status
- Known limitations

Avoid:

- Setup walkthroughs
- Deployment instructions
- Historical change logs

---

## OPERATIONS.MD

`OPERATIONS.MD` contains operational guidance.

Document:

- Startup procedures
- Deployment steps
- Configuration instructions
- Troubleshooting guidance
- Maintenance procedures

Avoid:

- Internal implementation details
- Source-code design decisions

---

# Inventory and Monitoring

The inventory file remains the source of truth.

Always verify behavior against:

```text
servers.json
```

Do not assume:

- Enabled states
- Credentials
- Server assignments
- Current inventory contents

from historical documentation.

Current inventory values must be read from the active inventory file.

---

# Network Constraints

This development session uses the user's hotspot connection.

The hotspot environment cannot reach:

- Intranet monitoring targets
- Internal network resources
- Production monitoring endpoints

Before considering connectivity-related changes:

1. Read the constraints documented in `CONTEXT.MD`.
2. Verify whether a reported issue depends on intranet access.
3. Separate implementation concerns from network availability concerns.

Do not:

- Treat timeouts from the hotspot environment as server failures.
- Infer monitoring health from unreachable intranet resources.
- Draw conclusions about target availability based on local connectivity limitations.

---

# Monitoring-Specific Guidance

When investigating monitoring behavior:

- Distinguish ping failures from metrics failures.
- Distinguish dashboard refresh behavior from collection behavior.
- Distinguish inventory configuration issues from runtime issues.
- Distinguish monitoring-host connectivity from target-system health.

Evidence should come from:

- Source code
- Configuration
- Logs
- Stored monitoring results

and not from assumptions about network reachability.

---

# Preferred Development Workflow

For most tasks:

1. Understand the current implementation.
2. Make the smallest appropriate change.
3. Remove obsolete logic.
4. Build only if build validation is relevant.
5. Update documentation.
6. Stop when the requested outcome is complete.

Avoid broad refactoring when a targeted change solves the problem.

Favor maintainability and clarity over cleverness.