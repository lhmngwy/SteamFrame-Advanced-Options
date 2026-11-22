# Patch maintenance engine

## Local Contracts

- Keep durable state and retry reservations persisted before driver replacement or notification delivery.
- Polling updates live `LastCheck` without rewriting the journal unless a durable field changes. Keep the comparison baseline at the last successful save.

## Verification

- Run the `PatchMaintenanceTests` suite, including idle/restart and failed-save regressions.
