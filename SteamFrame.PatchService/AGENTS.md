# Windows patch service

## Local Contracts

- Serialize reconciliation and control mutations through the worker gate. Route worker journal changes through the engine so its last-save baseline stays current.
- Idle polling and status replies do not write to disk. Explicit health probes may create and remove a temporary file.
- The packaged install/update action explicitly enables maintenance, requires operator confirmation and administrator approval, and retains existing notification settings.

## Verification

- Use the read-only endpoint smoke in `docs/building.md` against the installed service. For idle-write changes, also sample journal metadata and process write I/O across multiple poll intervals.
