# Installer / update foundations

This folder documents what the bundled installer does and the package layout it expects.

This folder contains a single canonical artifact:

| File | Purpose |
| --- | --- |
| `Install-PAXCookbook.ps1` | The one script that performs both first-time install and in-place update of PAX Cookbook on a user machine. Ships inside every release zip. Never split into multiple scripts. |

---

## 1. Appliance install root

PAX Cookbook is a **user-scoped Windows appliance**. There is exactly one canonical install root, derived from `LocalApplicationData`:

```
%LOCALAPPDATA%\PAXCookbook\
├── App\                              <- APPLICATION-OWNED. Replaced atomically by every update.
│   ├── VERSION.json                  <- canonical local version metadata (installer is the only writer).
│   ├── launcher\
│   │   └── Start-PAXCookbook.ps1
│   ├── broker\                       <- broker scripts, routes, helpers.
│   ├── lib\                          <- vendored runtime libraries (SQLite native + managed shims).
│   ├── resources\
│   │   ├── manifest.json             <- manifest cache (installer ships it; broker reads it).
│   │   └── pax\
│   │       └── PAX_Purview_Audit_Log_Processor.ps1
│   ├── templates\                    <- bundled template blueprints (read-only at runtime).
│   ├── web\                          <- SPA bundle (HTML, CSS, JS, vendored assets).
│   └── install\
│       └── Install-PAXCookbook.ps1   <- this script (same content as the staged copy).
├── Backups\
│   └── App_<oldVersion>_<UTC-timestamp>\   <- rollback copies produced by every successful update.
├── Updates\
│   └── <stagedVersion>\              <- broker-managed staging area (download / extract / hand off).
│       ├── release.zip
│       ├── release.zip.sha256
│       ├── extracted\                <- byte-for-byte the future App\ tree.
│       └── handoff.json              <- one-shot record the broker writes before exiting.
└── install.log                       <- bounded local installer log. Append-only; no rotation.
```

**Ownership table** (binding):

| Path | Owner | Mutated on update? | Mutated on uninstall? |
| --- | --- | --- | --- |
| `App\` | Installer (writes); broker (reads). | Yes — entire subtree replaced atomically. | Yes (uninstall removes `App\`). |
| `Backups\` | Installer. | New backup added; old backups preserved. | Only with `-Purge`. |
| `Updates\` | Broker writes; installer reads + cleans. | Cleaned by installer on successful update. | Only with `-Purge`. |
| `install.log` | Installer. | Appended. | Only with `-Purge`. |
| Workspace folder (wherever the user put it) | Broker. | **Never** touched by the installer. | **Never** touched by the installer. |
| `%APPDATA%\PAXCookbook\cookbook.bootstrap.json` | Launcher. | **Never** touched by the installer. | **Never** touched by the installer. |

`workspace/` is **not** a subdirectory of `%LOCALAPPDATA%\PAXCookbook\`. Workspace data lives wherever the user pointed the launcher (recipes, cooks, artifacts, logs, SQLite database). The installer does not see it, does not read it, does not write it, and does not back it up.

---

## 1a. PAX engine acquisition (Cookbook v1)

This section codifies the Cookbook v1 architecture defined by [`_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md`](../../_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md). Cookbook v1 is the active architecture; the doctrine below binds the installer's role in the acquisition flow.

In v1, both the internal-test and production release artifacts ship with `VERSION.json.paxScript.acquisitionPolicy = "external"`. The PAX Purview Audit Log Processor script (`resources/pax/PAX_Purview_Audit_Log_Processor.ps1`) is **not** present in the release ZIP for either artifact and is **not** copied to the installed tree by `install` mode. The installer's job ends after the rest of the payload is materialized; broker first launch handles acquisition.

**Installer behavior on an external-policy artifact:**

- `install` mode materializes `App\` exactly as it does today, **except** that the canonical PAX path `App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1` is absent and the post-install bundled-PAX SHA-256 check (§5 step 1) is **skipped**.
- The installer writes an install-state record at `%LOCALAPPDATA%\PAXCookbook\install-state.json` containing a `paxAcquisition` block with `pending = true` and `policy = "external"`. The block records the source (`download` \| `local-file` \| `automation`), version, sha256, manifestId, manifestHash, manifestVersion, validatedAtUtc, and activatedAtUtc fields once acquisition completes.
- `install` exits **install complete**, NOT Mode A PASS. Mode A acceptance requires the second-phase acquisition drive plus the §12.1 A1–A7 proofs in the plan above.
- The installer still performs zero outbound HTTP. All HTTPS fetches happen in the broker process at first launch (the only sanctioned outbound HTTP choke-point in the appliance).

**Broker behavior on first launch when `paxAcquisition.pending = true`:**

- The broker enters an `acquisition_required` startup state. The SPA renders the three-button acquisition dialog (Download / Use a local script file / Cancel) per [EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md §6](../../_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md#6-acquisition-ux--the-three-buttons-d1).
- Until acquisition succeeds and `paxAcquisition.pending = false`, every recipe / cook / scheduler / runtime route is blocked with a structured `acquisition_required` response. The broker does not start any cook.
- Validated script bytes are written to the canonical path `<InstallRoot>\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1`. After write, the existing `Test-BundledPaxIntegrity` startup check (§5 step 1) becomes load-bearing for every subsequent launch.

**Automation / support fallback parameters:**

Two new installer parameters survive **only** as automation/support fallbacks (per D1 in the plan). They are explicitly NOT the primary customer UX:

| Parameter | Purpose |
| --- | --- |
| `-PaxScriptPath <path>` | Pre-stages an operator-supplied `.ps1` for the broker to validate at first launch. The bytes are validated against a signed approved-engine manifest before being copied to the canonical path; an unapproved or hash-mismatched file is rejected. |
| `-PaxManifestPath <path>` | Pre-stages an operator-supplied signed approved-engine manifest for offline / air-gapped environments. Without this parameter, the broker fetches the manifest from `VERSION.json.paxScript.engineManifestUrl` over HTTPS at acquisition time. |

These parameters route through the **same** signed-manifest + SHA-256 validation pipeline as the GUI Download / Use a local script file paths. They do not weaken any validation. They exist for two cases: (a) CI / verifier harnesses driving non-interactive Mode A acceptance, and (b) air-gapped enterprise installs where the broker cannot reach the engine-manifest URL.

**Script-immutability binding for installer and broker (binding under [`docs/CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md` constitution rule 8](../../docs/CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md#2-constitution-quick-reference) and [`_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md` §4.5](../../_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md#45-pax-script-immutability-contract)).** `-PaxScriptPath` and `-PaxManifestPath` are **seed inputs**, not transformation permissions. The installer treats the supplied script bytes as opaque: it does NOT alter encoding, NOT normalize line endings, NOT add or remove a BOM, NOT inject a wrapper / preamble / postamble / header, NOT append comments or banners, NOT sign the script in place, NOT strip or add signatures, NOT "prepare" / "stage" / "rebuild" the script body, and NOT generate a Cookbook-customized variant. The validation step hashes the **EXACT bytes** the operator supplied (or the broker downloaded), with no pre-hash conversion. The activation step copies those **EXACT bytes UNCHANGED** — byte-for-byte identical to what was SHA-256-verified — to the canonical path `<InstallRoot>\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1`, using a raw copy (`Copy-Item -LiteralPath` or `[System.IO.File]::WriteAllBytes` from the verified buffer) and nothing else. The operator-supplied source file at `-PaxScriptPath` is **never** modified by the installer or the broker; it is read, hashed, and left untouched. A downloaded temp file is hashed in place and the same bytes are written to the canonical path; no edits before or after hashing. A validation failure (signature, hash, manifest, compatibility window) is a terminal refusal that requires re-acquisition of the approved PAX release through the sanctioned pipeline; it is **never** a license to alter the script, the `paxScript.sha256` pin in `VERSION.json`, the trust anchor thumbprint, or any other trust pin. The installer's per-file hash registry and the broker's per-cook re-hash both continue to compare against the unchanged pinned value across acquisition.

---

## 2. Release-zip layout (what the installer reads from)

Every release of Cookbook ships as a single zip whose extracted root looks **identical** to the on-disk `App\` layout above (minus `Backups\`, `Updates\`, and `install.log`, which are post-install runtime artifacts):

```
<release-extracted-root>/
├── VERSION.json
├── launcher\
├── broker\
├── lib\
├── resources\
│   ├── manifest.json
│   └── pax\
│       └── PAX_Purview_Audit_Log_Processor.ps1
├── templates\
├── web\
├── install\
│   └── Install-PAXCookbook.ps1
└── signing\                          <- FUTURE-RESERVED.  See §4 below.
```

The installer **self-locates** by walking up from `$PSScriptRoot` to the nearest parent that contains `VERSION.json`. That parent is the release-zip root. The installer never reads `cwd`, never reads an environment variable, and never reads a config file to discover its source location.

This same layout is also produced when the broker stages an update under `<InstallRoot>\Updates\<stagedVersion>\extracted\`. The staged tree and a freshly-extracted release zip are interchangeable: the installer cannot tell them apart and does not try.

---

## 3. Install vs update detection

There is **one** invocation surface (`Install-PAXCookbook.ps1`) and **four** explicit modes:

| Invocation | Used by | Pre-condition | Post-condition |
| --- | --- | --- | --- |
| `-Mode install` | Human, once, after first download. | `<InstallRoot>\App\VERSION.json` must NOT exist. | `App\` materialized; `VERSION.json.cookbook.installedAt` stamped. |
| `-Mode update -Handoff <path>` | Broker only, after explicit `Install and Restart Cookbook` click. | `<InstallRoot>\App\VERSION.json` MUST exist; broker process must have exited. | Old `App\` moved to `Backups\App_<oldVersion>_<ts>\`; new `App\` materialized from staged tree; PAX SHA-256 verified; `installedAt` re-stamped. |
| `-Mode uninstall [-Purge] -Yes` | Human, deliberately. | None. | `App\` removed. With `-Purge`, `Backups\` and `Updates\` also removed. Workspace untouched. |
| `-Mode check` | Anyone, anytime, read-only. | None. | Resolved paths, installed version, PAX integrity status printed to stdout and appended to `install.log`. |

The installer **refuses** to overwrite an existing install in `-Mode install`. The user must explicitly uninstall first, or wait for the broker to hand off an update. This prevents accidental clobbering when a chef double-clicks the script inside an already-installed sandbox.

---

## 4. Signing-readiness structure

The release zip layout reserves a top-level `signing\` directory whose **shape and slot names are fixed now**, even though the contents are not yet populated. This lets the distributor wire in signing later without changing the package layout.

```
<release-extracted-root>/signing/
├── release.zip.sha256        <- FUTURE: hex SHA-256 of the release zip (matches manifest.latestCookbook.sha256).
├── files.sha256              <- FUTURE: per-file hashes of every .ps1/.psm1 under App\ (for tamper-detection).
└── operator.cer              <- FUTURE: public X.509 cert of the distributor (DER- or PEM-encoded).
                                  Provided for distributor-identification only; trust is established via Windows
                                  Authenticode against the distributor's pre-existing certificate, NOT via this file.
```

Hard rules for `signing\` (binding, enforced by the bundled verification harness):

- **No placeholder hashes.** If `signing\` is empty, it is left empty (or absent), not populated with `<TODO>` strings, `00000…` filler, or made-up SHA-256s.
- **No private keys.** Ever. The distributor's signing key never enters the repo, never enters the release zip, never enters `%LOCALAPPDATA%\PAXCookbook\`.
- **No certificate generation by the installer.** The installer never calls `New-SelfSignedCertificate`, `Set-AuthenticodeSignature`, `Import-Certificate`, or `certutil`. Authenticode signing is performed *outside* the installer, by the distributor, against the distributor's own certificate.
- **No trust-store mutation.** The installer never modifies `Cert:\CurrentUser\Root`, `Cert:\LocalMachine\Root`, AppLocker rules, WDAC policies, SmartScreen settings, or Defender exclusions.

When `signing\` is populated for a release, the contract is:

1. `release.zip.sha256` is the load-bearing trust artifact for the *zip-as-a-whole*. The broker already verifies this against `manifest.latestCookbook.sha256` at download time; the file in `signing\` is just an in-bundle copy for offline / disconnected verification.
2. `files.sha256` is the load-bearing trust artifact for *individual files inside the bundle*. The installer may consult it to detect post-extraction tampering; today, the installer relies on Windows Authenticode verification at script-invoke time.
3. `operator.cer` is informational. It lets a sufficiently-paranoid chef confirm the in-zip signatures were produced by the same certificate they expect, without needing network access to fetch the cert chain. *(The filename `operator.cer` is retained for layout stability; the file represents the **distributor's** public X.509 cert.)*

---

## 5. Integrity preservation flow

Two SHA-256 checks are mandatory and run on every install and every update:

1. **Bundled PAX integrity** (post-copy). The installer hashes `<App>\resources\pax\PAX_Purview_Audit_Log_Processor.ps1` and compares the hex against `<App>\VERSION.json.paxScript.sha256`. Mismatch ⇒ rollback (update) or abort (install).
2. **VERSION.json authority** (read-only). The installer reads, never invents, the `cookbook.version`, `paxScript.version`, and `paxScript.sha256` fields. The only field the installer is allowed to write back is `cookbook.installedAt`.

There is no MD5, no SHA-1, no truncated hash. All hashes are uppercase hex, no whitespace, no `0x` prefix.

---

## 6. What this installer is permanently NOT allowed to do

The following behaviors are permanently forbidden **for the installer script itself**. The bundled verification harness scans the installer's script source for each forbidden token and fails the run if any are found. This list scopes to the Cookbook installer; it does not constrain the bundled PAX script's runtime behavior, which is covered separately in [docs/RELEASE_PACKAGE.md §11a](../../docs/RELEASE_PACKAGE.md#11a-bundled-workflow-runtime-behavior).

- No elevation (`Start-Process -Verb RunAs`).
- No registry writes (`Set-ItemProperty -Path HKLM:\...`, `New-ItemProperty -Path HKCU:\Software\...`, `reg.exe`).
- No services or scheduled tasks (`New-Service`, `Set-Service`, `Register-ScheduledTask`, `sc.exe`).
- No signing or trust-store mutation (`Set-AuthenticodeSignature`, `New-SelfSignedCertificate`, `Import-Certificate`, `certutil`).
- No execution-policy / Defender / firewall mutation (`Set-ExecutionPolicy`, `Set-MpPreference`, `Add-MpPreference`, `New-NetFirewallRule`, `Unblock-File`).
- No outbound network (`Invoke-WebRequest`, `Invoke-RestMethod`, `System.Net.WebClient`, `System.Net.Http.HttpClient`, `Start-BitsTransfer`, `Net.Sockets.TcpClient`, `wget`, `curl`).
- No package managers (`winget`, `choco`, `scoop`, `pip`, `npm`, `Install-Module`, `Install-Package`, `Install-Script`).
- No PATH / environment-variable mutation (`[Environment]::SetEnvironmentVariable`, `setx`).
- No custom-PAX-path support (no `-PaxPath` / `-PaxScript` / `-CustomPaxPath` parameter; no `PAX_COOKBOOK_PAX_SCRIPT_PATH` env var). **Carve-out (Cookbook v1):** the `-PaxScriptPath` and `-PaxManifestPath` parameters defined in §1a are permitted because they route validated bytes through the same signed-manifest + SHA-256 pipeline as the GUI acquisition flow and **always** terminate at the canonical path `<InstallRoot>\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1`. They are not arbitrary-path knobs. The carve-out covers transport only: the installer and broker may **read** the seed file at the supplied path and may **copy** its exact bytes to the canonical install location, but may not **modify** the seed file on disk and may not **transform** the seed bytes between read and copy (no normalization, re-encoding, BOM insertion, wrapping, comment / metadata injection, in-place signing, signature stripping, or any other mutation — see the script-immutability binding at the end of §1a).

The verification harness ships with this repository and encodes the prohibitions above as 12 named checks. It is the authoritative regression gate for the installer.

Unchanged by Cookbook v1: the installer's no-elevation / no-registry / no-services / no-outbound-HTTP / no-signing-mutation / no-execution-policy-mutation / no-Defender-or-firewall-mutation / no-env-var-mutation rules remain absolute. HTTPS fetches for the approved-engine manifest happen in the **broker** process at first launch, not in the installer process. This separation is the only reason the new acquisition feature can coexist with the installer's no-HTTP hard rule. See [`_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md §22`](../../_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md#22-setup--acquisition-ui-feasibility-finding-brians-q1q5) for the feasibility finding.

---

## 7. Test-only knob

The installer exposes a hidden `-InstallRoot <path>` parameter so the verification harness can sandbox the installer against a temp directory. This parameter is **not** documented to users, **not** surfaced in any UI, and **not** read from any config file or environment variable. It exists exclusively so the harness can run end-to-end install/update cycles without touching the chef's real `%LOCALAPPDATA%\PAXCookbook\`. Production invocations omit the parameter and always resolve the install root from `[Environment]::GetFolderPath('LocalApplicationData')`.

---

## 8. User-scope shortcuts (Phase S)

Cookbook is launched by the chef through a **user-scope shortcut**. The shortcut is the only blessed entry point: it must never point at `index.html`, a hard-coded `http://localhost:PORT` URL, a browser bookmark, or a stale port. The shortcut points at the canonical launcher; the launcher decides the URL at run time from `workspace.lock`.

### 8.1 Shortcut switches (install + update + repair-shortcuts modes)

| Switch | Effect |
| --- | --- |
| *(none)* | Default. Creates the Start Menu folder `PAX Cookbook` (with the launcher, Repair, and Remove `.lnk`s) AND a Desktop shortcut at `%USERPROFILE%\Desktop\PAX Cookbook.lnk`. |
| `-CreateDesktopShortcut` | Accepted as a no-op. The Desktop shortcut is created by default; the switch is honored for callers that pass it explicitly. |
| `-NoDesktopShortcut` | OPT-OUT. Do not create a Desktop shortcut. Does NOT delete an existing Desktop shortcut. If supplied together with `-CreateDesktopShortcut`, `-NoDesktopShortcut` wins and the installer logs a warning. |
| `-NoShortcuts` | Creates NO shortcuts (neither Start Menu nor Desktop). The chef must launch the appliance manually via the launcher script. Mutually exclusive with `-CreateDesktopShortcut`. |

All shortcut paths are resolved via `[Environment]::GetFolderPath('StartMenu' / 'Desktop')`. No machine-scope (`CommonStartMenu` / `CommonDesktopDirectory`) folders are ever written.

### 8.2 Shortcut shape (binding)

Every shortcut the installer writes has exactly this shape:

| Field | Value |
| --- | --- |
| `TargetPath` | Full path of `pwsh.exe` (PowerShell 7.4+). Windows PowerShell 5.x (`powershell.exe`) is not supported and is refused. |
| `Arguments` | `-NoProfile -File "<InstallRoot>\App\launcher\Start-PAXCookbook.ps1"` |
| `WorkingDirectory` | `<InstallRoot>\App` |
| `WindowStyle` | `1` (Normal). The broker console is visible by design. |
| `IconLocation` | `<InstallRoot>\App\web\images\pax-cookbook.ico,0` if the file exists, else `<pwsh.exe>,0`. |
| `Description` | `PAX Cookbook - local appliance for Purview Audit Log Processor.` |

The argument string is permanently constrained to `-NoProfile -File "<path>"`. The installer never writes a shortcut containing:

- `-ExecutionPolicy Bypass` (or any other `-ExecutionPolicy` value).
- `-EncodedCommand`.
- `-Command` with embedded script blocks.
- `-WindowStyle Hidden` (the broker terminal MUST be visible).

### 8.3 Launcher path stability

The launcher script ships in the release zip at `<extracted>/launcher/Start-PAXCookbook.ps1` (sibling of the `app/` folder). On install and update, the installer copies the **contents** of `<extracted>/launcher/` into `<InstallRoot>\App\launcher\` so the shortcut target is always:

```
<InstallRoot>\App\launcher\Start-PAXCookbook.ps1
```

The launcher self-locates and supports BOTH the source-tree layout (`<repo>\launcher\Start-PAXCookbook.ps1`) and the installed layout (`<InstallRoot>\App\launcher\Start-PAXCookbook.ps1`). Prior-phase verification harnesses (B, E, Q) continue to invoke the source-tree launcher directly; they are unaffected by the installed-tree copy.

### 8.4 Shortcut lifecycle in install / update / uninstall

| Mode | What the installer does to shortcuts |
| --- | --- |
| `install` | After PAX integrity passes, creates shortcuts per the switches above. Logs every `.lnk` path written. |
| `update` | After PAX integrity passes, re-writes the same shortcuts. The launcher path is unchanged across versions, so this primarily refreshes shortcut state and recovers shortcuts the chef may have deleted between updates. |
| `uninstall` | Removes BOTH the Start Menu shortcut and the Desktop shortcut (regardless of which switches the chef originally used during install). Missing files are logged and skipped, never thrown. Even when the install root is absent, the uninstall path still attempts orphan-shortcut removal so the system is left clean. |

### 8.5 Runtime reuse model (launcher-side)

When the chef clicks the shortcut, the launcher:

1. Reads `<workspace>\Runtime\workspace.lock` (written by the broker).
2. If the recorded `brokerProcessId` is alive, its image is `pwsh.exe`, the recorded `brokerPort` accepts a loopback TCP connection, and `/api/v1/health` returns 200 with a matching `workspaceFolderPath`: **reuses** the running broker and opens the user's default browser at `http://127.0.0.1:<port>/`. Does NOT spawn a second broker.
3. Otherwise: spawns the broker synchronously in the foreground console and schedules a short-lived background watcher (a `ThreadJob`) that polls for the broker's port and health, then opens the default browser. The watcher is bounded by a deadline so it cannot linger if the broker fails to come up.

The launcher refuses to open any URL that is not loopback `http`. A malformed `workspace.lock` cannot cause the launcher to navigate the chef's browser to an external host.

### 8.6 Permanently forbidden in shortcut handling

The Phase S verification harness scans the installer and launcher source for the following tokens and fails the run if any are found in shortcut-handling code paths:

- No machine-scope shortcut folders (`CommonStartMenu`, `CommonDesktopDirectory`, `CommonPrograms`).
- No registry autorun (`HKCU:\Software\Microsoft\Windows\CurrentVersion\Run`, `HKLM:\Software\Microsoft\Windows\CurrentVersion\Run`).
- No scheduled tasks (`Register-ScheduledTask`, `schtasks.exe`).
- No services (`New-Service`, `Set-Service`, `sc.exe`).
- No system-tray / autorun helpers (`Set-StartupApp`, Startup folder writes outside Start Menu).
- No PWA installs, browser-extension wiring, Electron shells, or Edge-bookmarks API.
- No hidden-window powershell (`-WindowStyle Hidden`, `-EncodedCommand`).
- No outbound HTTP from the launcher or installer (only loopback `127.0.0.1` for the runtime health probe).

