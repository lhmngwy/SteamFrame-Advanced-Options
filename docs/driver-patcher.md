# Driver patcher

The stock Steam Link VR driver caps the video bitrate at **232 Mbps** when a wireless
adapter is present and **350 Mbps** otherwise. The **Patches & Recovery** page raises both
ceilings to **500 Mbps** and selects **HEVC level 6.2**, which higher bitrates require.

It does not add filler data or guarantee that every scene reaches that bitrate.

## Using it

On first launch, **Set up higher bitrate streaming** inspects the installed driver
and walks through installation. Choosing **Install 500 Mbps override** saves and
verifies the backup, closes SteamVR, applies and verifies the patch, then restarts
SteamVR if it was running. A running game may close. **Set up later** makes no
driver changes. Reopen the walkthrough under **Patches & Recovery**.

An **Already installed** result means the file hash and patch bytes match an
existing override. Opening the app or selecting a preset does not patch it, but
applying a preset can install its required override, including in earlier app
versions. The app does not infer installation from a preset's requested settings.
Unsupported, unreadable or missing files are never shown as a verified patch.

For manual installation or restore:

1. **Close SteamVR.** Install and restore refuse to run while `vrserver.exe` is running,
   and the check is repeated immediately before the file is replaced.
2. Click **Driver file and verification → Check driver** to see whether the installed driver is compatible, already
   patched, or unsupported.
3. Click **Install 500 Mbps override**, or **Restore original driver** to undo it.

SteamVR updates can replace the patched driver. Inspect again after updating, or explicitly
enable the optional automatic maintenance service described below.

## Automatic patch maintenance (optional)

Under **Patches & Recovery → Automatic 500 Mbps patch maintenance**, select
**Install / update watched installation and enable**. Confirm the displayed SteamVR
installation and approve the administrator prompt. The service is disabled by default;
opening the app never installs it.

The installed service starts automatically with Windows, before sign-in, and works with
the app closed. It inspects the installed `driver_vrlink.dll` and uses exactly the same
compatibility scanner, byte verification, original backups and receipts as manual patching.
It raises the two ceilings and HEVC level; it does not change streaming targets or presets.

- File changes are allowed to settle before inspection. Periodic checks recover missed
  filesystem events, resume and driver-directory replacement.
- Steam update/download metadata is an additional deferral hint, not proof that an update
  completed. Exact source bytes and the runtime state are checked again before replacement.
- If SteamVR is running, maintenance waits for it to close. It never stops SteamVR or a game.
  SteamVR can therefore start before maintenance finishes, and that session may remain stock.
- Unsupported files are left untouched. Invalid recovery files or verification failures
  block automatic writes and retain recovery evidence. **Check / retry now** explicitly
  retries inspection without bypassing any safety check.
- Temporarily missing or unreadable drivers are retried; after ten minutes, an availability
  failure can be reported. A later valid driver is still detected.
- Watch one administrator-approved local installation per machine. If SteamVR moves, reopen
  the app and use the installation/update button to authorize the newly discovered path.
  The service does not use its own account's HKCU Steam discovery or silently switch libraries.

**Refresh status** shows the service version, watched path, pending/failure state, last check,
last successful patch and notification-delivery status. **Copy diagnostics** omits credentials,
user identities, local paths and queued message contents.

### Pushover setup

Create your own application API token at <https://pushover.net/apps/build>, then enter it
and your Pushover user/group key in the service's password fields. A device name is optional.
Save the settings (credentials are validated) and select **Send test notification**.
Leave both password fields empty when changing settings without replacing saved credentials.

The two checkboxes are independent:

- **Notify when the driver is successfully patched** — only after this service applies and
  verifies a patch and its matching original backup, including recovery of a completed write
  after a crash. An already-patched startup check does not send a success message.
- **Notify when automatic patching fails** — reports unsupported builds, persistent file
  unavailability and patch/recovery verification failures. Waiting for SteamVR is not failure.

Both boxes start checked, but no messages are sent without explicitly saved credentials.
Patching works without Pushover and with either or both notification types disabled. Turning
off a type removes its queued messages. Disabling maintenance clears all queued messages.

The service coalesces repeated events and persists incident IDs across restarts. A genuine
replacement of the same driver build can produce a new success message. Network/server
failures use bounded backoff; invalid requests pause delivery until settings are saved again,
and quota errors respect the reset time. The queue is capped at 100 messages with a 24-hour
expiry. A timed-out HTTP request can have been accepted, so rare duplicate delivery is possible.
API acceptance is not proof that a device displayed the notification.

Tokens and user keys are DPAPI-encrypted in protected ProgramData storage, never stored in
`frame.json`, presets, diagnostics or release archives. The service uses HTTPS to contact
Pushover; notification contents disclose the patch outcome and available SteamVR build metadata,
but not local paths. **Remove credentials** deletes saved credentials and queued messages.

### Disable, restore and uninstall

**Disable** is durable across reboot but leaves the current driver patch installed. Manual
**Restore original driver**, a stock-driver preset application, and **Back to stock** first
disable and quiesce maintenance, then use the shared writer lock to restore. Re-enable it
explicitly if you later want automatic patching again. These settings are not tuning presets.
If the service is unresponsive, restore is blocked rather than racing it; repair/update or
uninstall the service first.

**Uninstall service** stops and removes it, deletes its installed executable payload and
credentials, and leaves the current driver, original backups and receipts unchanged. Protected
configuration/journal files may remain for diagnostics; subsequent installation can reuse
notification preferences. Uninstall does not restore the driver.

The portable ZIP bundles the installer payload but does not run it automatically. Installation
copies it into `%ProgramFiles%\SteamFrame Advanced Options\PatchService`; deleting or moving
the extracted app does not remove the service.

After updating the portable app, use **Install / update watched installation and enable**
again to update the separately installed service and its compatibility scanner; extracting
a new ZIP alone does not replace the running service.

Its mutable data lives under
`%ProgramData%\SteamFrame Advanced Options\PatchService`. It runs as the dedicated virtual
account `NT SERVICE\SteamFramePatchService`, granted modification rights only to its data and the watched driver directory,
not as LocalSystem. Installing/updating/uninstalling requires administrator approval; normal
control uses a local named pipe restricted to the enabling account and administrators.
The app verifies that the pipe server PID belongs to the running registered service and
that its protected executable path and dedicated account match. This uses read-only Service
Control Manager queries, so the app does not need access to inspect another account's process.
Installation verifies the service identity and tests driver read access plus temporary-file
creation/deletion before accepting the service as healthy. Unexpected service errors record
a sanitized Windows Application event. Crash recovery restarts it after 30 seconds, two minutes
and five minutes, then stops retrying until a manual restart or the failure-count reset.

If installation fails, the status bar reports the failing stage, original error and Windows
error number when available, plus whether rollback completed. These diagnostics return over
a private local pipe; the elevated helper does not write results to a caller-supplied path.
Windows can elevate without displaying a prompt when its UAC policy is configured that way;
the absence of a prompt alone does not mean elevation failed.

## What changes

Six small byte changes:

| Change | Purpose |
| --- | --- |
| Wireless-adapter-present cap | 232 → 500 Mbps |
| General cap | 350 → 500 Mbps |
| HEVC reconfiguration default level | Level 6.2 |
| Preset level stub in unused executable padding | Applies level 6.2 to the encoder preset |
| Preset copy call | Routes the preset through that stub |
| Executable section size | Includes the stub |

Only these byte deltas ship with the app. Valve's DLL is never redistributed.

## Compatibility checks

Compatibility is discovered from the driver's own code and PE metadata, so a new file hash
alone doesn't require an app update. For an unfamiliar driver, the patcher requires all of:

- unique matching instruction contexts inside valid x64 function bounds;
- the expected NVENC structure layout;
- RIP-relative references to the original cap constants;
- a preset copy that calls an imported CRT `memcpy`;
- unused executable padding for the level stub.

Addresses are calculated from that file rather than reused from an old build. Ambiguous or
changed layouts are refused. Full source and output hashes and every byte being replaced are
checked before anything is written. A rewritten encoder can still require an app update.

Reference builds:

| Build | Original SHA-256 | Patched SHA-256 |
| --- | --- | --- |
| First analyzed driver | `5f6f063f1ece088491f572db8feb2f2433a5621d0adf7013de77b91604d146b5` | `82fd637f1d4b844ca349d2a98867cd0ba8f969a14de47c2a3192b875fb62425d` |
| Beta build 25839986 | `d415ba30fab129b4737ae19a3c71d7628c27e450dd8bf0002a1d4dd98ed58a05` | `34265101d56235e2f87e5b0e2f3fc2340ed1513a9b6d26f03c4fe12bafcbbc03` |
| Public stable build 25330290 | `1e846be041039798580a91452371f6288c614fb4c152126b137e2c693fb77eef` | `d0aeec1b80d31da146365b4b65fa4adadd7230d288ebf0d65e040b3991f28c5e` |

The stable and beta drivers use different private member offsets and register selections.
The scanner recognizes each complete instruction family separately, retaining the same
NVENC version, function-boundary, import, cap-constant and executable-padding checks.
Patch compatibility does not establish support for every private encoder or codec hook.

Beta build **26793788** moves the cap selector's target field to `+0x148`. It has its own
complete selector family; the adapter-query slot, selected register, branch and adjacent
350/232 Mbps read-only constants must still match. Its original SHA-256 is
`2c7dc5f17f22644aa4832263134d51fe406aec1e3b9c96c57748e5d599fad355`; the verified patch produces
`5a6e1f3a879a870d3fed128620adc833d61dbbd01d4488d548a5f0cf1e39a82d`.
Isolated application, matching backup/receipt recognition and exact restoration were tested.
On November 20, 2026, the installed 1.0.1 service also reapplied this patch successfully,
verified the installed hash and matching original/receipt, and received Pushover API acceptance
for its success notification, without restarting SteamVR. Live streaming performance on this
build has not been re-measured.
Service 1.0.1 schedules a fresh compatibility check after updating, including a driver hash
that the previous scanner blocked. This does not add codec or encoder-monitor hook support.

For changed preset/reconfiguration layouts, the scanner also decodes x64 instructions
with Iced. It derives the private API-table and embedded encoder-config locations from
their initialization, then verifies the preset query's API slot, output argument,
structure versions and exact imported `memcpy` copy. Reconfiguration must write the
matching config's level/tier fields under a comparison against the actual HEVC codec
GUID. Stack locations, object-field offsets and level registers may change without an
app update when these relationships still hold. Contiguous function fragments are
joined only through verified x64 unwind chains, never merely because they are adjacent.

This path was checked against beta build **26496310** (reported as SteamVR **2.19.2**)
using an isolated copy: patching and exact restoration passed. The installed driver
was not replaced, and live streaming on this patch has not been tested. Existing
known-layout paths remain unchanged. Bitrate-cap selectors, API versions, supported
initialization forms and safe executable padding are still required; this is not
universal support for all future builds. Unattended patching requires the separate explicit
automatic-maintenance opt-in above.

A missing original backup is normal before the first installation. If an unmodified driver
is reported as unsupported, collect **Driver file and verification** details and the SteamVR
build/branch. File verification repairs modified files; it cannot add a layout to the app's
compatibility scanner.

**Patches & Recovery** shows the installed build and branch, the recognized driver,
and the verified bitrate-patch versions. Build information comes from the selected
installation's Steam manifest; it never authorizes a patch. A pending branch switch
reports the installed branch, not the branch still being downloaded. A standalone
DLL copy may have no installation metadata and can still be checked by its bytes.

If a driver is unsupported, update this app and check again. Use **Driver file and
verification → Copy driver details** when reporting it. The report includes the
build/branch when available, full driver and backup hashes, receipt path, and the
specific compatibility check that failed. Missing or damaged recovery files have
separate messages. Restarting SteamVR cannot make an unsupported layout compatible.

Stable and beta support are retained together. The app selects the patch from the
installed file, so changing branches does not require changing app versions.

More detail is in [Compatibility evidence](../Native/VERIFICATION.md).

## Backups and restore

- The first analyzed build is backed up as `driver_vrlink.dll.steam-frame-bitrate-original.bak`.
- Other builds use `driver_vrlink.dll.steam-frame-bitrate-<source sha256>.bak`, so originals
  from earlier SteamVR versions survive updates.
- A `.patch.json` receipt sits beside each backup. Its locations are re-derived from the retained
  original and checked, never trusted blindly.
- Restore needs the matching, hash-verified original backup.

Known reference patches can be recognized even without their receipt or backup.
For other compatible builds, recognition requires the receipt and matching original.
Restore always requires the verified original. If it is missing, restore stays
disabled; Steam's file verification can recover Valve's driver instead.

## Bandwidth reality check

500 Mbps is a software ceiling, not proven link capacity. One measured USB 2 connection carried
about 318–325 Mbps of UDP payload. Real headroom depends on transport overhead, the link and the scene.
