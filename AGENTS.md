# SteamFrame Advanced Options

## Purpose

Windows tools for Steam Frame and Steam Link VR settings, diagnostics, and driver patch maintenance.

## Ownership

- `SteamLinkVRSettings/` owns the WinUI app and shared PC-side source. `Frame/` owns headset-side source and diagnostics.
- `Native/` owns native helpers and their test harness. `SteamFrame.DriverPatching/` builds the shared patcher independently of WinUI.
- `docs/` owns user-facing setup, operation, and research material. Root scripts build and package the app.

## Verification

- Run `Build.ps1` without test skips for the complete Windows build and native/managed regression gates. See `docs/building.md` for fixtures and optional live checks.

## Child DOX Index

- `SteamFrame.PatchMaintenance/AGENTS.md` owns reconciliation and durable state.
- `SteamFrame.PatchService/AGENTS.md` owns the installed service and its operator boundary.
- `Tests/AGENTS.md` owns managed regression suites.
