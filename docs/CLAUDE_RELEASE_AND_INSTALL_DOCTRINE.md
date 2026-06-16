# Claude Release and Install Doctrine for PAX Cookbook

Phase id : `UXR_AGENT_RELEASE_INSTALL_DOCTRINE`
Audience : coding agents (Claude / Copilot / future automated sessions)
Status   : permanent doctrine; this file must be read before any rebuild, packaging, install, update, or repair work on PAX Cookbook.

> If you are an agent that has been asked to rebuild, repackage, install, update, or repair PAX Cookbook and you have **not** read this file end-to-end in the current session, STOP and read it first. Do not improvise.

---

## 1. Purpose

This document exists so that future coding-agent sessions do not guess at PAX Cookbook's build, packaging, installation, update, or repair behavior. It distills the actual behavior of:

- `tools\release\Build-Release.ps1` and `tools\release\Release.psm1`
- `app\install\Install-PAXCookbook.ps1`
- `Install PAX Cookbook.cmd`
- `launcher\Start-PAXCookbook.ps1`, `launcher\Stop-PAXCookbook.ps1`, `launcher\Start-PAXCookbookSupportMode.ps1`
- `docs\RELEASE_PACKAGE.md`
- `docs\OPERATOR_GUIDE.md`
- `docs\TROUBLESHOOTING.md`
- `app\VERSION.json`

into a single set of rules that future sessions must follow.

The trigger for creating this doctrine was a near-miss during the title-bar / logo rebuild: the agent was about to call `update -SourceRoot <extracted>\app` against the live install, having inferred the source-root shape from the release-ZIP layout without verifying the canonical behavior. That call would have *succeeded* the App\ tree copy and *silently skipped* the top-level `launcher\` subtree, leaving the install with stale Start Menu shortcuts and a broken Stop launcher. The lesson is that release-ZIP layout, installer source-root resolution, and installed-tree layout are three different things that look similar enough to invite a wrong guess. This doctrine names each one explicitly so the wrong guess is no longer reachable.

---

## 2. Non-negotiable constitution

These rules are not negotiable. They override default agent heuristics.

1. **Never hand-copy files into `<InstallRoot>\App\`.** The installer is the only sanctioned way to mutate the installed app tree. `Copy-Item` from a source directory into `<InstallRoot>\App\*` outside the installer is forbidden, even when "it's just one file."
2. **Never partially update from `<extracted>\app` unless the installer code path proves the merge is complete.** The installer copies `<extracted>\app\*` into `App\*` *and* separately merges `<extracted>\launcher\*` into `App\launcher\` via `Copy-LauncherSubtree`. A partial source root that lacks the sibling `launcher\` directory will silently produce an install with stale launcher scripts and shortcuts. The only safe sources for `update` mode are (a) a `handoff.json` written by the broker pointing at a properly staged tree, or (b) `-SourceRoot <extracted>\app` where the sibling `<extracted>\launcher\` is *also* present on disk.
3. **Never infer installed layout from ZIP layout.** The ZIP has `app\` and a sibling `launcher\` at the top level. The installed tree has those two collapsed into a single `<InstallRoot>\App\` with `App\launcher\` inside it. The merge is performed by the installer, not by the ZIP shape.
4. **Never mutate `<InstallRoot>\App\` until the canonical procedure is verified against the installer source.** Read `Install-PAXCookbook.ps1` before you call it. If you can't point at the code that does the thing you're about to ask the installer to do, you don't yet have permission to call the installer.
5. **Never mutate the live install while the broker is running** unless the documented update flow you are using explicitly supports it. `update`, `uninstall`, and `repair-shortcuts` modes all refuse if `Test-IsBrokerRunning` says the broker is up.
6. **Never kill the broker as a first-choice happy path.** Killing `pwsh.exe` is a recovery escape hatch documented in `OPERATOR_GUIDE.md §11.3`, not a routine action.
7. **Use the Stop PAX Cookbook Server Start Menu shortcut as the preferred user-facing broker stop path** when a clean stop is required. It invokes `launcher\Stop-PAXCookbook.ps1`, which asks the running broker to shut down cleanly for the selected workspace.
8. **Never modify the PAX script bytes, in any way, shape, or form.** Cookbook is forbidden from patching, rewriting, reformatting, normalizing line endings, transcoding encoding, adding or removing a BOM, injecting wrapper code, appending comments / banners / metadata, replacing or augmenting the script header, signing the script in place, stripping signatures the PAX team applied, adding signatures the PAX team did not apply, "fixing" anything inside, generating a derived script, writing a Cookbook-customized variant under any other name or path, monkey-patching the script's functions at runtime, or performing any other mutation of the script bytes — regardless of the script's source (HTTPS download, chef-supplied local file via the SPA picker, `-PaxScriptPath` automation seed, sideload probe, or the historical Phase 12 embedded artifact). The bytes Cookbook acquires are the bytes Cookbook hashes, the bytes Cookbook copies to disk are byte-for-byte identical to the validated bytes, and the bytes Cookbook invokes are the bytes the PAX team published. The `paxScript.sha256` pin in `app\VERSION.json` is **never** edited to "bless" a locally modified script — it is the expected hash of the approved PAX release, and any validation mismatch is a terminal refusal, never a license to alter the script, the pin, or the trust anchor. A validation failure is a signal to re-acquire from the PAX team's release channel, not to mutate the file. If any phase, refactor, or "improvement" requires modifying the PAX script bytes at any point in the lifecycle, the idea is rejected by definition — stop and escalate. See [_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md §4.5](../_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md#45-pax-script-immutability-contract) for the full forbidden-operations enumeration and the six-operation allowed lifecycle. (Phase 12-shape code that operates on the embedded bundle remains read-only with respect to the script bytes — read, hash, compare; never write or transform.)
9. **Never weaken gates to get green.** A failing baseline-hash check, a failing pre-publish check, or a failing installer guard is a signal to read the code, not to soften the check.
10. **Never mark a known-red gate as acceptable.** Open a separate, explicitly-scoped phase to address a red gate. Do not relabel it as "expected" or postpone it as "to be resolved later."
11. **Never create the directory `app\web\assets\js\`.** Cookbook's SPA does not have a JS subfolder under `assets\`; JavaScript files live directly under `app\web\assets\`. Creating a `js\` directory is a sign that an agent invented a layout that doesn't exist.
12. **Never change launcher, installer, broker, PAX, WebAuthn, or release-script behavior** unless the current phase is explicitly scoped to do so. These surfaces are doctrine-stable.
13. **Always archive any production file before editing it.** Put the pre-edit copy under `_archive\<PHASE_ID>_<UTC_TIMESTAMP>\<path>` so the rebase row in the next baseline check has a forensic anchor.
14. **Always enumerate the source files you created or modified** before reporting "done." A change to an unenumerated file is a bug.
15. **Always distinguish source-tree changes from installed-tree changes.** A change to `app\web\index.html` in the repo does not appear in `<InstallRoot>\App\web\index.html` until `install` or `update` mode is run against a fresh build. Confusing the two leads to "I changed it but my browser still shows the old thing" loops.
16. **`msedge.exe` is the only browser binary the appliance may invoke by name.** All other browser use delegates to the OS-registered HTTP handler via `Start-Process -FilePath <url>` (or equivalent). The Cookbook never enumerates `chrome.exe`, `firefox.exe`, `brave.exe`, or any other browser by image name; the Edge carve-out exists solely to host the canonical app-window experience (see §11). If the Edge launch fails or Edge is not installed, the launcher falls back to the OS-registered default handler.

---

## 3. Source tree, release ZIP, and installed tree are three different shapes

This is the single most important section of this document. The three layouts are deliberately different and the installer is what bridges them.

### 3.1 Source tree (the repo working copy)

```
<RepoRoot>\
├── app\
│   ├── broker\
│   ├── install\
│   │   └── Install-PAXCookbook.ps1
│   ├── lib\
│   ├── launcher\
│   │   └── RuntimeDiscovery.psm1
│   ├── resources\
│   │   └── pax\
│   ├── templates\
│   ├── web\
│   └── VERSION.json
├── launcher\
│   ├── Start-PAXCookbook.ps1
│   ├── Stop-PAXCookbook.ps1
│   ├── Start-PAXCookbookSupportMode.ps1
│   ├── Get-ScheduledCook.ps1
│   ├── Set-ScheduledCook.ps1
│   ├── Invoke-ScheduledCook.ps1
│   └── Invoke-PAXScheduledRecipe.ps1
├── Install PAX Cookbook.cmd
├── tools\release\Build-Release.ps1
├── tools\release\Release.psm1
├── docs\
├── _temp\
├── _archive\
└── dist\
```

`Get-ReleaseFileSet` in `tools\release\Release.psm1` enumerates **only** `app\`, `launcher\`, and the top-level `Install PAX Cookbook.cmd`. Everything else in the repo (`tools\`, `docs\`, `_temp\`, `_archive\`, `dist\`, `.git\`, `.vscode\`, `node_modules\`, etc.) is either excluded by the closed-list regex in `$Script:ReleaseExclusionPatterns` or simply not in the include set. The exclusion list is *defense in depth* — a file that isn't in `app\` or `launcher\` or the named top-level files cannot end up in the ZIP regardless of exclusion patterns.

### 3.2 Release ZIP layout (what the chef downloads and extracts)

```
pax-cookbook-<version>.zip
├── Install PAX Cookbook.cmd
├── app\
│   ├── broker\
│   ├── install\
│   │   └── Install-PAXCookbook.ps1
│   ├── lib\
│   ├── launcher\
│   │   └── RuntimeDiscovery.psm1
│   ├── resources\
│   │   └── pax\
│   ├── templates\
│   ├── web\
│   └── VERSION.json
└── launcher\
    ├── Start-PAXCookbook.ps1
    ├── Stop-PAXCookbook.ps1
    ├── Start-PAXCookbookSupportMode.ps1
    ├── Get-ScheduledCook.ps1
    ├── Set-ScheduledCook.ps1
    ├── Invoke-ScheduledCook.ps1
    └── Invoke-PAXScheduledRecipe.ps1
```

Exactly three top-level entries: `app\`, `launcher\`, `Install PAX Cookbook.cmd`. **The presence of two sibling top-level directories (`app\` and `launcher\`) is doctrinal.** Future ZIPs must preserve this shape. The `app\launcher\` subdirectory (which only contains `RuntimeDiscovery.psm1`) is *not* the same as the top-level `launcher\` directory — they are separate trees that the installer merges at the destination.

### 3.3 Installed tree (what the installer produces under `%LOCALAPPDATA%\PAXCookbook\`)

```
%LOCALAPPDATA%\PAXCookbook\
├── App\
│   ├── broker\
│   ├── install\
│   │   └── Install-PAXCookbook.ps1
│   ├── launcher\                          <- MERGED: <extracted>\app\launcher\ + <extracted>\launcher\
│   │   ├── RuntimeDiscovery.psm1          (came from <extracted>\app\launcher\)
│   │   ├── Start-PAXCookbook.ps1          (came from <extracted>\launcher\)
│   │   ├── Stop-PAXCookbook.ps1           (came from <extracted>\launcher\)
│   │   ├── Start-PAXCookbookSupportMode.ps1
│   │   ├── Get-ScheduledCook.ps1
│   │   ├── Set-ScheduledCook.ps1
│   │   ├── Invoke-ScheduledCook.ps1
│   │   └── Invoke-PAXScheduledRecipe.ps1
│   ├── lib\
│   ├── resources\pax\
│   ├── templates\
│   ├── web\
│   └── VERSION.json
├── Backups\
│   └── App_<oldVersion>_<UTC-timestamp>\  (created on update, kept per retention)
├── Updates\
│   └── <version>\                         (broker-managed staging; not present on first install)
├── EdgeAppData\                           (Edge --user-data-dir for the app-window; see §11; preserved on update, removed on full uninstall)
└── install.log
```

### 3.4 How the installer maps ZIP shape to installed shape

The bridge is `Copy-LauncherSubtree` in `app\install\Install-PAXCookbook.ps1` (line ~445). Both `install` and `update` modes do two distinct copies in sequence:

1. **Main appliance copy** — `Copy-TreeContents -Source <SourceRoot> -Destination <AppDir>` where `<SourceRoot>` resolves to the directory that contains `VERSION.json`, which is **`<extracted>\app\`** (NOT the ZIP root). This brings `app\broker\`, `app\install\`, `app\web\`, `app\lib\`, `app\resources\`, `app\templates\`, and the small `app\launcher\` subtree (only `RuntimeDiscovery.psm1`) into `<InstallRoot>\App\`.
2. **Top-level launcher merge** — `Copy-LauncherSubtree -SourceRoot <SourceRoot> -AppDir <AppDir>` computes `$sourceParent = Split-Path -Parent $SourceRoot` to recover `<extracted>\` and then copies `<extracted>\launcher\*` into `<InstallRoot>\App\launcher\`. This adds `Start-PAXCookbook.ps1`, `Stop-PAXCookbook.ps1`, `Start-PAXCookbookSupportMode.ps1`, `Get-ScheduledCook.ps1`, `Set-ScheduledCook.ps1`, `Invoke-ScheduledCook.ps1`, and `Invoke-PAXScheduledRecipe.ps1` *alongside* the `RuntimeDiscovery.psm1` that step 1 already deposited there.

If `Copy-LauncherSubtree` cannot find a sibling `launcher\` directory at `Split-Path -Parent $SourceRoot`, it logs a WARN and returns `$false`. The install / update **continues** with App\launcher\ populated only by `RuntimeDiscovery.psm1`, and the Start Menu shortcut creation step is skipped because the launcher target is missing. **This is the silent failure mode the constitution exists to prevent.**

Call sites (do not memorize line numbers — re-locate them by symbol):

- `install` mode : `[void](Copy-LauncherSubtree -SourceRoot $Script:InstallerSourceRoot -AppDir $Script:AppDir)`
- `update`  mode : `[void](Copy-LauncherSubtree -SourceRoot $stagedRoot                  -AppDir $Script:AppDir)`

### 3.5 If you are not certain of the mapping

Re-read `Install-PAXCookbook.ps1` and locate:

- `function Copy-TreeContents`
- `function Copy-LauncherSubtree`
- `function Resolve-UpdateSourceRoot`
- `function Invoke-InstallMode`
- `function Invoke-UpdateMode`

If any of these have changed since this doctrine was written, this section is **stale relative to the code** — the code wins. Update this section in the same phase that changed the installer behavior.

---

## 4. Canonical build / package workflow

### 4.1 The one command

From the repo root, in non-elevated PowerShell 7.4+:

```powershell
pwsh -File .\tools\release\Build-Release.ps1 -Force
```

That is the entire build. There is no separate "package" step, no separate "sign" step that ships in the public tree, no manual zipping, no `Compress-Archive` you should call by hand.

`Build-Release.ps1` parameters (all optional unless noted):

- `-RepoRoot <path>` defaults to two levels above the script. Override only when running from outside the repo.
- `-OutputRoot <path>` defaults to `<RepoRoot>\dist\<channel>\pax-cookbook-<version>`.
- `-Channel <name>` defaults to `app\VERSION.json -> channel` (typically `stable`).
- `-SourceEpoch <DateTime>` deterministic ZIP entry timestamp; defaults to parsing `VERSION.json -> cookbook.releaseTimestamp` or a canonical fallback `2024-01-01T00:00:00Z`.
- `-SourceCommit <opaque>` optional, captured into release metadata as a read-only input.
- `-PackageBaseUrl <https-url>` optional; when omitted, manifest snapshot keeps a placeholder until the distributor fills it in.
- `-ReleaseNotesUrl <https-url>` optional.
- `-BuildId <opaque>` optional; defaults to a value derived deterministically from `SourceEpoch`.
- `-Force` overwrite an existing `OutputRoot` directory.

### 4.2 What the build produces

The five files in `<OutputRoot>\` after a successful build:

1. `pax-cookbook-<version>.zip` — the release package.
2. `pax-cookbook-<version>.zip.sha256` — `sha256sum`-format sidecar.
3. `pax-cookbook-<version>.release.json` — release metadata. `signing.state = "unsigned"` for a producer-side build.
4. `pax-cookbook-<version>.manifest.json` — update-manifest snapshot conforming to the broker's manifest schema.
5. `pax-cookbook-<version>.release-notes.md` — distributor-fill-in template generated from `tools\release\RELEASE_NOTES.md.template`.

### 4.3 Exclusions (defense in depth)

`tools\release\Release.psm1 -> $Script:ReleaseExclusionPatterns` is a closed list of case-insensitive regexes applied to forward-slash repo-relative paths. The categories (paraphrased from the actual list — re-read the file for the current set):

- Top-level non-appliance trees: `_temp`, `_backup`, `temp`, `docs`, `tools`, `dist`, `scripts`.
- Source-control + IDE: `.git`, `.gitattributes`, `.gitignore`, `.vs`, `.vscode`, `.idea`, `node_modules`, `__pycache__`.
- OS clutter: `.DS_Store`, `Thumbs.db`, `desktop.ini`.
- User-owned operational state (would never legitimately live in source): `Updates`, `Runs`, `Cooks`, `Logs`, `Backups`, `recipes`, `Trust`, `Workspaces`.
- State / transient file extensions: `.sqlite`, `.sqlite-journal`, `.sqlite-shm`, `.sqlite-wal`, `.db`, `.partial`, `.tmp`, `.temp`, `.log`, `.bak`, `.swp`.
- Signing-secret extensions (hard guard): `.pfx`, `.p12`, `.pem`, `.key`, `.crt`, `.cer`, `.jks`, `.keystore`, `.pkcs12`.
- Trust artifacts: `trusted-signers.json`, `.sig`, `.signer.json`.
- Package-staging sidecars from prior builds: `.zip.metadata.json`, `.zip.sha256`, `.zip`.
- Test sandboxes occasionally leaked into the tree.

### 4.4 Do not do this

- Do not "build" by running `Compress-Archive` against the repo. The deterministic ZIP writer (`New-CanonicalZip` in `Release.psm1`) pins entry timestamps from `SourceEpoch`, sorts entries lexicographically, and writes UTF-8 entry names with normalized separators. `Compress-Archive` does none of these and produces a non-deterministic ZIP that the manifest's pre-publish check will reject.
- Do not invent additional output files alongside the five above. The release-set is closed.
- Do not include `tools\`, `docs\`, `_temp\`, `_archive\`, `dist\`, `.git\`, `.vscode\`, `node_modules\`, or any test-sandbox folder in the package. These are all explicitly excluded; an agent that bypasses the walker has broken doctrine.
- Do not edit `tools\release\Release.psm1` to relax exclusions in order to ship "just one extra file." The closed list is intentional; broaden it only in an explicitly scoped phase.

### 4.5 Post-build validation

After the build, verify (this is `RELEASE_PACKAGE.md §11` rephrased):

1. All five sidecar files exist in `<OutputRoot>\`.
2. `(Get-FileHash -Algorithm SHA256 -LiteralPath <pkg>.zip).Hash` matches the contents of `<pkg>.zip.sha256` (first whitespace-separated token).
3. `<pkg>.release.json -> packageSha256` matches the same value.
4. `<pkg>.manifest.json -> latestCookbook.sha256` matches the same value.
5. `<pkg>.release.json -> paxScript.sha256` matches `app/VERSION.json -> paxScript.sha256` inside the ZIP.

The smoke harness in `_temp\phase_uxr_titlebar_logo_rebuild_recovery\smoke_titlebar_logo_source_contract.ps1` is an example of these checks plus a Sub-iteration-specific source-contract overlay; future build-verifying smokes should follow that shape.

---

## 5. Canonical install workflow

### 5.1 Entrypoint

From the extracted ZIP folder, in a non-elevated PowerShell 7.4+ prompt:

```powershell
cd <extracted-folder>
pwsh -File .\app\install\Install-PAXCookbook.ps1 install
```

Equivalently, double-click `Install PAX Cookbook.cmd` at the top of the extracted folder. The `.cmd` wrapper resolves its own directory, locates `pwsh.exe`, refuses to fall back to Windows PowerShell 5.1, and invokes the same PowerShell installer with `install` mode.

`Mode` is a `ValidateSet` parameter on `Install-PAXCookbook.ps1`:

```
install | update | uninstall | uninstall-prompt | check | repair-shortcuts | remove-shortcuts
```

The default is `check`. `install` must be specified explicitly (or invoked via the `.cmd` wrapper, which passes `install` literally). `uninstall-prompt` is invoked only by the **Uninstall PAX Cookbook** Start Menu shortcut and is the only mode that may show a user-facing dialog; it relocates the installer to `%TEMP%\PAXCookbookUninstall-<UTC>-<8-hex>\` before spawning the actual `uninstall -Yes -Purge` invocation.

### 5.2 What the installer creates

- `<InstallRoot>\App\` — the appliance tree, copied from `<extracted>\app\*` plus `<extracted>\launcher\*` merged under `App\launcher\`.
- `<InstallRoot>\Backups\` — empty on first install, populated by `update` mode.
- `<InstallRoot>\Updates\` — empty on first install, populated by the broker's update-staging flow.
- `<InstallRoot>\install.log` — append-only local log.
- `%APPDATA%\Microsoft\Windows\Start Menu\Programs\PAX Cookbook\` — Start Menu *folder* containing user-scope `.lnk` shortcuts. The canonical v1 set is **five** entries:
  - `PAX Cookbook.lnk` — main launcher. Created LAST and NOT tagged with `ExcludeFromShowInNewInstall` so it surfaces in Windows' "Recently added" list after a fresh install.
  - `PAX Cookbook Support Mode.lnk` — visible-broker launcher.
  - `Repair PAX Cookbook Shortcuts.lnk` — maintenance shortcut that invokes `Install-PAXCookbook.ps1 -Mode repair-shortcuts` to refresh the Start Menu and (when present) Desktop `.lnk` files.
  - `Stop PAX Cookbook Server.lnk` — clean-stop shortcut.
  - `Uninstall PAX Cookbook.lnk` — invokes `Install-PAXCookbook.ps1 -Mode uninstall-prompt`, which shows a WinForms confirmation dialog and (on Yes) relocates the installer to `%TEMP%` and re-spawns it with `uninstall -Yes -Purge`.

  All four utility shortcuts (Support Mode, Repair, Stop, Uninstall) are tagged with `PKEY_AppUserModel_ExcludeFromShowInNewInstall = VARIANT_TRUE` via the installer's `Set-ShortcutExcludeFromShowInNewInstall` helper so Windows keeps them out of the "Recently added" list. The property is applied best-effort: if the property store cannot be opened on the running build of Windows, the installer logs a WARN and continues. Creation order (utilities first, main last) is a belt-and-suspenders mitigation: the main `.lnk` has the newest mtime and is biased toward Windows' "recently added" surfaces regardless of property support.

  Windows' separate **Recommended** list (Start Menu suggestions) is driven by usage heuristics that this property does **not** control. On some machines one or more utility shortcuts may briefly appear there immediately after install. This is a documented Windows limitation, not a Cookbook bug.

  The `remove-shortcuts` installer mode is an internal CLI maintenance surface and is **not** exposed as a Start Menu entry.
- `%APPDATA%\Microsoft\Windows\Desktop\PAX Cookbook.lnk` — Desktop shortcut, on by default (opt out with `-NoDesktopShortcut`).
- `%TEMP%\PAXCookbookUninstall-<UTC>-<8-hex>\Install-PAXCookbook.ps1` — one acceptable leftover artifact, written only by `uninstall-prompt` mode so the running pwsh.exe can release its handle on `<InstallRoot>\App\install\` before the install tree is removed. Documented in OPERATOR_GUIDE.md §14. Command-line `uninstall` does not produce this artifact.

`<InstallRoot>` defaults to `%LOCALAPPDATA%\PAXCookbook` and can be overridden by `-InstallRoot` only as a test-harness convenience.

### 5.3 Posture (what the installer never does)

These are statically verified by `_temp\phase_w_verification\verify_phase_w.ps1`. Future agents must not break any of these:

- No elevation; `Start-Process -Verb RunAs` is forbidden.
- No registry mutation; no `Set-ItemProperty` against `HKLM:` or `HKCU:`.
- No services or scheduled tasks; no `New-Service`, `Set-Service`, `Register-ScheduledTask`, `sc.exe`.
- No outbound HTTP; no `Invoke-WebRequest`, `Invoke-RestMethod`, `System.Net.WebClient`, `System.Net.Http.HttpClient`.
- No certificate or signing mutation; no `Set-AuthenticodeSignature`, `New-SelfSignedCertificate`, `Import-Certificate`, `certutil`.
- No execution-policy / Defender / firewall mutation; no `Set-ExecutionPolicy`, `Set-MpPreference`, `New-NetFirewallRule`, `Unblock-File`.
- No `PATH` / environment-variable mutation; no `[Environment]::SetEnvironmentVariable`, no `setx`.
- No custom bundled-PAX path; `<InstallRoot>\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1` is the only valid PAX script path. **Cookbook v1.** Under §14a, the `-PaxScriptPath` and `-PaxManifestPath` automation-fallback parameters exist. They are NOT custom-path knobs: they pre-stage operator-supplied bytes that the broker validates against an approved-engine manifest and writes to the same canonical path above. The "only valid PAX script path" rule is unchanged.

### 5.4 Workspace handling

The workspace (recipes, bake history, Chef's Keys, trust allowlist) lives in a chef-owned folder **outside** `<InstallRoot>`. `install` mode does not create the workspace; the launcher does that on first run via the bootstrap-pointer flow (`%APPDATA%\PAXCookbook\cookbook.bootstrap.json`). `uninstall`, `update`, `repair-shortcuts`, and `remove-shortcuts` modes all leave the workspace alone.

### 5.5 Post-install verification

After an install completes, verify:

1. `<InstallRoot>\App\VERSION.json` exists and `cookbook.version` matches the ZIP's `version`.
2. `<InstallRoot>\App\launcher\Start-PAXCookbook.ps1` exists. (If absent, `Copy-LauncherSubtree` failed silently — see §3.4.)
3. `<InstallRoot>\App\launcher\Stop-PAXCookbook.ps1` exists.
4. `<InstallRoot>\App\launcher\Start-PAXCookbookSupportMode.ps1` exists.
5. `<InstallRoot>\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1` exists and its SHA-256 matches `App\VERSION.json -> paxScript.sha256`.
6. Start Menu folder `%APPDATA%\Microsoft\Windows\Start Menu\Programs\PAX Cookbook\` exists and contains the expected shortcuts (§5.2).
7. `<InstallRoot>\install.log` records the install with no ERROR lines.

Failure of any of the above is a red flag (§13).

---

## 6. Canonical update workflow

### 6.1 The two supported update paths

The installer's `update` mode has exactly two supported entrypoints. Both are gated by `Test-IsBrokerRunning` and both go through `Resolve-UpdateSourceRoot`.

**Path A — Broker-managed (UI-driven).** The chef-facing happy path described in `OPERATOR_GUIDE.md §8.1`. The broker downloads the ZIP, verifies its SHA-256 against the manifest snapshot, extracts to `<Workspace>\Updates\<version>\extracted\`, writes `<Workspace>\Updates\handoff.json` with `stagedExtractedPath = <Workspace>\Updates\<version>\extracted\app`, exits, and a detached orchestrator runs:

```
pwsh.exe -ExecutionPolicy Bypass -File <stagedExtractedPath>\install\Install-PAXCookbook.ps1 -Mode update -Handoff <Workspace>\Updates\handoff.json
```

This path requires `app\VERSION.json -> updateManifestUrl` to be a real HTTPS URL (it is `null` in the current source tree, which means Path A is intentionally disabled until distribution begins).

**Path B — Manual recovery (chef-facing fallback).** Documented in `OPERATOR_GUIDE.md §8.4`. The chef hand-runs the same command after manually staging an extracted tree and writing a `handoff.json` referencing it.

**Test-harness path — `-SourceRoot`.** The installer also accepts a `-SourceRoot` parameter that bypasses `handoff.json` and points directly at an extracted tree. This path is for the verification harness; it is not documented as a chef-facing surface and an agent should not promote it to a chef-facing recommendation.

### 6.2 What `Resolve-UpdateSourceRoot` actually returns

The function (`app\install\Install-PAXCookbook.ps1`) resolves the source root in this order:

1. If `-SourceRoot` was passed, return `$Script:InstallerSourceRoot` (which was already walked up from `$PSScriptRoot` to the nearest parent containing `VERSION.json`).
2. If `-Handoff` was passed, read the JSON, require `stagedExtractedPath`, require that `<stagedExtractedPath>\VERSION.json` exists, return `<stagedExtractedPath>`.
3. Fallback: return `$Script:InstallerSourceRoot` (harness only).

The returned source root is **the directory containing `VERSION.json`** — i.e., the `app\` subdirectory of the extracted ZIP, not the ZIP root. `Copy-LauncherSubtree` recovers the parent (`<extracted>\`) via `Split-Path -Parent`.

### 6.3 What `update` mode actually does (sequence)

1. Resolve `$stagedRoot = Resolve-UpdateSourceRoot`. On failure, return exit code 1.
2. Refuse if no existing `<InstallRoot>\App\` exists (this is what `install` mode is for). Return exit code 2.
3. Refuse if `Test-IsBrokerRunning` reports the broker is up. Return exit code 3.
4. `Move-Item <InstallRoot>\App\` → `<InstallRoot>\Backups\App_<oldVersion>_<UTC>\`.
5. `New-Item -ItemType Directory <InstallRoot>\App\`.
6. `Copy-TreeContents -Source $stagedRoot -Destination <InstallRoot>\App\`.
7. `Test-IsCanonicalPaxScript -AppRoot <InstallRoot>\App\` (rolls back on failure).
8. `Compare-PaxScriptIntegrity -AppRoot <InstallRoot>\App\` (rolls back on failure if `paxScript.sha256` mismatches).
9. `[void](Copy-LauncherSubtree -SourceRoot $stagedRoot -AppDir <InstallRoot>\App\)` — the second copy that brings in the top-level `launcher\` scripts.
10. Stamp `App\VERSION.json -> cookbook.installedAt`.
11. Refresh Start Menu / Desktop shortcuts via `Invoke-ShortcutCreation`.

A rollback (steps 7 or 8 failing) restores the prior `App\` from the backup directory. A failed `Copy-LauncherSubtree` (step 9) does **not** roll back — it logs WARN and continues, which is the silent-failure trap §3.4 warns about. Future agents must check that the launcher target was actually copied after every update.

### 6.4 Live-update support

`update` mode does **not** support running while the broker is alive. The broker must exit first. The supported Path A flow handles this automatically: the broker writes the handoff, exits, and detaches the orchestrator that calls the installer. Path B and the `-SourceRoot` harness path require the chef (or the agent) to verify the broker is gone before invoking `update`.

The cleanest verified-stop is **Stop PAX Cookbook Server** from the Start Menu (§8). If that shortcut is unavailable (e.g. shortcuts were never created, broker is unhealthy), the documented escape hatch in `OPERATOR_GUIDE.md §11.3` is: end the `pwsh.exe` process running `Start-Broker.ps1` in Task Manager. Agents should not invoke this hatch from automation.

### 6.5 If you are uncertain of the update flow

If you cannot point at the exact lines in `Install-PAXCookbook.ps1` that perform each step in §6.3, **do not run `update`.** Produce a procedure proposal, attach the file/line citations, and stop. The recovery from a botched `update` (a half-merged `App\launcher\`, a missing shortcut, a corrupted handoff) is meaningfully worse than the cost of stopping.

### 6.6 Where chef-facing first installs should land

When the chef-facing update path is disabled (today's state: `updateManifestUrl == null`), the canonical chef-facing flow for upgrading an installed appliance to a new build is **§11.4 of `OPERATOR_GUIDE.md` — Reinstall from scratch**:

```powershell
# Quit Cookbook first (Start Menu -> Stop PAX Cookbook Server, or Task Manager fallback).
pwsh -File <release-folder>\app\install\Install-PAXCookbook.ps1 uninstall -Yes
pwsh -File <release-folder>\app\install\Install-PAXCookbook.ps1 install
```

Note that `uninstall` mode requires `-Yes` (an installer guard not documented verbatim in §11.4; agents must include it). `-Purge` is optional and additionally removes `Backups\` and `Updates\`; without it those folders persist for forensic / rollback access. Workspace data is untouched in either case.

---

## 7. Canonical repair workflow

### 7.1 `repair-shortcuts` mode

```powershell
pwsh -File <InstallRoot>\App\install\Install-PAXCookbook.ps1 repair-shortcuts
```

What it does:

- Force-deletes the existing PAX-owned `.lnk` files in `%APPDATA%\Microsoft\Windows\Start Menu\Programs\PAX Cookbook\` and (if PAX-owned) `%APPDATA%\Microsoft\Windows\Desktop\PAX Cookbook.lnk` so the Windows shell `IconCache.db` re-reads the icon path.
- Re-runs `Invoke-ShortcutCreation` against the current `<InstallRoot>\App\` tree.

What it preserves:

- Everything in `<InstallRoot>\App\` (no file copies happen).
- The chef's workspace.
- `Backups\`, `Updates\`, `install.log`.

What it does **not** do:

- It does not download or extract any new build.
- It does not run `Copy-TreeContents` or `Copy-LauncherSubtree`. If `App\launcher\Start-PAXCookbook.ps1` is missing, `repair-shortcuts` cannot fix it — only `install` or `update` can.

Run this mode after a stale-icon issue, after a shortcut rename (the canonical v1 names are `Stop PAX Cookbook Server` and `Repair PAX Cookbook Shortcuts` with a capital S), or when the chef reports a missing Start Menu entry but the `App\` tree itself is healthy.

### 7.2 `remove-shortcuts` mode

```powershell
pwsh -File <InstallRoot>\App\install\Install-PAXCookbook.ps1 remove-shortcuts
```

Deletes all PAX-owned user-scope shortcuts (Start Menu folder + contents, legacy single-file Start Menu `.lnk`, Desktop `.lnk` if PAX-owned). Does not touch the `App\` tree, `Backups\`, `Updates\`, or workspace data.

As of v1, `remove-shortcuts` is an **internal CLI maintenance mode**. There is no Start Menu entry for it. Chefs who need to refresh shortcuts use **Repair PAX Cookbook Shortcuts**; chefs who want to remove shortcuts entirely run the installer with `-Mode remove-shortcuts` from a shell (or simply run `uninstall`).

### 7.3 Post-repair verification

After `repair-shortcuts`:

1. The expected `.lnk` files exist in the Start Menu folder (§5.2).
2. Each `.lnk` target resolves to a file under `<InstallRoot>\App\launcher\`.
3. The icon shown by Windows Explorer matches the bundled icon (a full icon refresh may require a brief logoff/logon if `IconCache.db` is still stale).

---

## 8. Broker lifecycle rules

### 8.1 Vocabulary

Use exactly these names:

- **PAX Cookbook** — the normal launch.
- **PAX Cookbook Support Mode** — the launch with a visible `pwsh.exe` broker console.
- **Stop PAX Cookbook Server** — the clean-stop Start Menu entry.
- **Repair PAX Cookbook Shortcuts** — the shortcut-repair Start Menu entry (capital S in "Shortcuts").
- **broker** — the local PowerShell-hosted HTTP server (`app\broker\Start-Broker.ps1`).
- **workspace** — the chef-owned folder containing recipes, bake history, and `Trust\`.

Do **not** use:

- "debug mode"
- "developer mode"
- "service mode"
- "admin mode"
- "elevated mode"
- "hidden terminal" *(as an official name; the visible Support Mode console is documented and named, the hidden normal-launch console is just the broker's default behavior)*

### 8.2 Normal launch

The **PAX Cookbook** Start Menu shortcut invokes `<InstallRoot>\App\launcher\Start-PAXCookbook.ps1`. The launcher hard-gates PowerShell 7.4+, locates the broker (`<AppRoot>\broker\Start-Broker.ps1`), resolves the workspace via `%APPDATA%\PAXCookbook\cookbook.bootstrap.json`, and starts the broker. The broker chooses an OS-assigned loopback port, mints a session token, writes `<Workspace>\runtime\broker.lock`, and opens the default browser to `http://127.0.0.1:<port>`.

The broker's `pwsh.exe` console is **not visible** in a normal launch. This is by design.

### 8.3 Support Mode

The **PAX Cookbook Support Mode** Start Menu shortcut invokes `<InstallRoot>\App\launcher\Start-PAXCookbookSupportMode.ps1`. Same workspace, same broker, same auth and unlock — the only difference is that the broker's `pwsh.exe` console is visible so the chef (or a support engineer) can see broker startup, route, health, and error messages in real time. Support Mode does not switch workspaces, does not change recipe / bake behavior, does not bypass authentication, and does not enable additional data collection.

See `TROUBLESHOOTING.md §17b` for the chef-facing description.

### 8.4 Stop PAX Cookbook Server

The **Stop PAX Cookbook Server** Start Menu shortcut invokes `<InstallRoot>\App\launcher\Stop-PAXCookbook.ps1`, which:

1. Reads the bootstrap pointer to locate the active workspace.
2. Reads `<Workspace>\runtime\broker.lock` to discover the PID, port, and session token.
3. Sends a cooperative shutdown request to the broker.
4. Waits briefly for the broker to exit; reports success or surfaces the failure to the chef.

Stop PAX Cookbook Server does **not**:

- Delete workspace data.
- Delete saved recipes.
- Delete bakes or bake output files.
- Remove Chef's Keys.
- Change anything inside `<InstallRoot>\App\`.

See `TROUBLESHOOTING.md §17c`.

### 8.5 Browser tab vs broker process

Closing the browser tab does **not** stop the broker. The launcher's runtime-reuse path keeps the broker alive after the tab closes so the next launch is fast. To fully end a session, use **Stop PAX Cookbook Server** (or close the visible Support Mode console).

### 8.6 Task Manager fallback

If the broker is unhealthy and Stop PAX Cookbook Server cannot reach it, end the `pwsh.exe` process running `Start-Broker.ps1` in Task Manager. The next healthy launch will reclaim a stale `<Workspace>\runtime\broker.lock` automatically. Do not edit or delete `broker.lock` by hand.

Agents must not Task-Manager-kill the broker from automation. This is a chef-facing escape hatch only.

---

## 9. SHA and baseline rules

### 9.1 The bundled PAX SHA is sacred

`app\VERSION.json -> paxScript.sha256` is the canonical hash of the bundled PAX script at `app\resources\pax\PAX_Purview_Audit_Log_Processor.ps1`. The installer's `Compare-PaxScriptIntegrity` recomputes the SHA on every install and update and rolls back if it does not match.

Do not modify the PAX script bytes in any phase, ever. A "PAX upgrade" is Cookbook acquiring a newer **approved** PAX release (unchanged bytes) from the PAX team's release channel, not Cookbook editing the script. The `paxScript.sha256` pin is updated only as a side-effect of pointing at a newer approved-release hash — never to bless a locally modified script. PAX is owned externally; Cookbook stores and runs whatever bytes the PAX team published, byte-for-byte unchanged.

**Script-immutability scope (binds every phase, every architecture).** The "PAX SHA is sacred" rule is the hash-side projection of the script-immutability contract codified in constitution rule 8 and in [_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md §4.5](../_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md#45-pax-script-immutability-contract). The pin is sacred BECAUSE the script bytes are sacred — Cookbook must never modify the PAX script bytes (no patching, no rewriting, no line-ending normalization, no encoding conversion, no BOM insertion, no wrapper injection, no comment / metadata append, no in-place signing, no signature stripping or addition, no Cookbook-customized derived script, no monkey-patching of the script's functions at runtime) and must never change `paxScript.sha256` to bless a locally modified script. This applies in every Cookbook architecture — Phase 12-shape embedded bundle, the v1 external-acquisition target architecture in §14a, and every future architecture — and applies regardless of the script's source (embedded bundle, HTTPS download, chef-supplied local file, automation `-PaxScriptPath` seed, sideload probe). Acquisition may **only** copy approved bytes unchanged into the canonical install path; validation failure is a terminal refusal that requires re-acquisition through the sanctioned pipeline, never a license to alter the script, the pin, or the trust anchor.

**Cookbook v1.** Under the External PAX Engine Acquisition contract codified in §14a, the v1 release ZIP does **not** carry the PAX script bytes. `paxScript.sha256` retains its meaning unchanged: it is the **expected** hash that the broker MUST observe after acquisition. The broker rejects any acquired script whose recomputed SHA-256 does not match this pin (D8: no drift). The signed approved-engine manifest published by the PAX team MUST also carry the same SHA-256 for the named version; a manifest that disagrees is rejected as `script_hash_mismatch`. The SHA pin is therefore still sacred — only the moment of recompute moves from "installer at install time" to "broker at first-launch acquisition." Critically, the script bytes themselves remain immutable across acquisition: the bytes the broker SHA-256s after download are the bytes the broker writes to disk byte-for-byte unchanged, and are the bytes the per-cook re-hash continues to compare against the pin on every cook.

### 9.2 VERSION.json / manifest relationship

`app\VERSION.json` is the source of truth at build time. `Build-Release.ps1` reads it and propagates relevant values into both `release.json` and `manifest.json`:

- `cookbook.version` → `release.json -> cookbookVersion`, `manifest.json -> latestCookbook.cookbookVersion`.
- `paxScript.sha256` → `release.json -> paxScript.sha256`, `manifest.json -> latestCookbook.paxScript.sha256`.
- `channel` → `release.json -> channel`, `manifest.json -> channel`.

The post-build validation (§4.5) cross-checks these. An edit to `VERSION.json` that is not paired with a re-run of `Build-Release.ps1` produces a release where `release.json` / `manifest.json` disagree with the ZIP-internal `VERSION.json`. Always re-build after any `VERSION.json` change.

### 9.3 What to do when a sanctioned edit changes a baseline hash

Many phase verification harnesses pin source-file SHA-256s to detect drift. When a sanctioned edit changes one of those pinned files, the correct rebase procedure is:

1. **Archive the pre-edit baseline.** Copy the file to `_archive\<PHASE_ID>_<UTC_TIMESTAMP>\<original\path>` before editing.
2. **Identify the inherited baseline files.** A baseline file is a `*.txt` (or similar) under a prior `_temp\phase_*_verification\` folder that pins one or more SHA-256s of source files. Identify *every* baseline file that pins the file you are editing.
3. **Rebase exactly one row in each baseline.** Update only the row corresponding to the file you edited. Leave every other row in every baseline untouched. Note both the pre-edit and post-edit hash + byte size in the phase report for forensic anchoring.
4. **Do not rebase a baseline file as a whole.** If you find yourself rewriting an entire baseline, you are doing something wrong — stop and inspect why so many rows drifted.
5. **Do not update a baseline to silence an unrelated drift.** If a hash check fails for a file you did not touch, that is a real signal. Investigate the drift; do not paper over it.

#### 9.3.1 The Support Mode polish example

`UXR_SETTINGS_SUPPORT_MODE_POLISH` is the canonical reference for §9.3. In that phase:

- `docs\TROUBLESHOOTING.md` was intentionally extended with new §17b (Support Mode) and §17c (Stop PAX Cookbook Server) sections.
- Three inherited baseline files pinned a SHA of `docs\TROUBLESHOOTING.md` from prior phases.
- Only the `docs\TROUBLESHOOTING.md` row was rebased in each of the three baselines (pre-edit `EF2CD320...` / 207290 bytes → post-edit `F53AD0E8...` / 211736 bytes).
- Every other pinned hash across the 29-row aggregator was left untouched and continued to enforce.
- The aggregator reached GREEN 73/73 *because* the un-touched pins continued to enforce, not because the gates were softened.

That phase report (`_temp\phase_uxr_settings_support_mode_polish_verification\UXR_SETTINGS_SUPPORT_MODE_POLISH_REPORT.md`) is the reference shape future single-file-rebase phases should imitate.

### 9.4 Never touch SHA lockfiles to hide unrelated drift

If a vendored-payload SHA (e.g. `app\lib\sqlite\lockfile.json`) fails, do not regenerate the lockfile to make the check pass. Investigate. The lockfile encodes a producer-side trust statement about the vendored binary; replacing it without an explicit upgrade phase is a doctrine violation.

---

## 10. Installed-tree verification after package/install/update

After any install, update, or repair, verify all of the following before declaring success:

### 10.1 Source-tree changes propagated

For every source file you changed this iteration:

- Read the corresponding file inside `<InstallRoot>\App\` (or `<InstallRoot>\App\launcher\` for top-level launcher edits).
- Confirm the new text is present.
- Confirm the old text is absent.

This is the single check that catches "I changed it but the browser still shows the old thing."

### 10.2 Launcher scripts present

- `<InstallRoot>\App\launcher\Start-PAXCookbook.ps1` exists.
- `<InstallRoot>\App\launcher\Stop-PAXCookbook.ps1` exists.
- `<InstallRoot>\App\launcher\Start-PAXCookbookSupportMode.ps1` exists.

Absence here means `Copy-LauncherSubtree` silently failed — see §3.4.

### 10.3 Start Menu shortcuts

In `%APPDATA%\Microsoft\Windows\Start Menu\Programs\PAX Cookbook\`:

- `PAX Cookbook.lnk` exists and its target resolves to `<InstallRoot>\App\launcher\Start-PAXCookbook.ps1`.
- `PAX Cookbook Support Mode.lnk` exists and its target resolves to `<InstallRoot>\App\launcher\Start-PAXCookbookSupportMode.ps1`.
- `Stop PAX Cookbook Server.lnk` exists and its target resolves to `<InstallRoot>\App\launcher\Stop-PAXCookbook.ps1`.
- `Repair PAX Cookbook Shortcuts.lnk` (capital S) exists and its target invokes `<InstallRoot>\App\install\Install-PAXCookbook.ps1 -Mode repair-shortcuts`.
- Pre-rename `Stop PAX Cookbook.lnk` is absent.
- Pre-rename lowercase `Repair PAX Cookbook shortcuts.lnk` is absent.
- Legacy `Remove PAX Cookbook shortcuts.lnk` is absent (no Start Menu entry for the `remove-shortcuts` mode).

### 10.4 Functional checks

These require the chef (or the agent under explicit Brian instruction):

- Click **PAX Cookbook** — broker starts, browser opens, SPA renders.
- Click **PAX Cookbook Support Mode** — broker console is visible, broker starts, browser opens.
- Click **Stop PAX Cookbook Server** — broker exits cleanly within a few seconds.
- Click **Repair PAX Cookbook Shortcuts** — the installer rewrites the Start Menu / Desktop `.lnk` files; rerun the SPA launch to confirm.
- The SPA's appliance version display matches `App\VERSION.json -> cookbook.version`.

### 10.5 PAX integrity

`Compare-PaxScriptIntegrity -AppRoot <InstallRoot>\App\` returns `ok = $true`. (The installer runs this during install / update; agents should re-run it after any out-of-band touching of `App\resources\pax\`.)

### 10.6 No dev artifacts shipped

Confirm `<InstallRoot>\App\` contains no `_temp\`, `_archive\`, `temp\`, `tools\`, `docs\`, `dist\`, `.git\`, `.vscode\`, or `node_modules\` subtrees, and no `.bak`, `.log`, or `.sqlite-journal` files. If any of these are present, the build process leaked — investigate `Get-ReleaseFileSet` and the exclusion patterns.

### 10.7 No partial launcher/app mismatch

`<InstallRoot>\App\VERSION.json -> cookbook.version` and the new build's `VERSION.json` agree. `App\launcher\Start-PAXCookbook.ps1`'s mtime is at least as recent as `App\install\Install-PAXCookbook.ps1`'s mtime. If `launcher\` looks stale while `App\` looks fresh, `Copy-LauncherSubtree` skipped.

---

## 11. Edge app-window and stable localhost identity contract

The Cookbook UI is hosted by the broker at a loopback HTTP origin. The launcher opens that UI in a Microsoft Edge "app window" (`msedge.exe --app=<runtimeUrl>`) so the chef sees a chrome-less, Cookbook-branded surface that Windows treats as a first-class app. To keep Windows shortcut grouping, taskbar identity, and recent-app behavior stable across launches, the loopback origin, the Edge `--user-data-dir`, and the AppUserModelID are all canonical literals.

### 11.1 Canonical literals

| Concept                              | Canonical value                                                |
|--------------------------------------|----------------------------------------------------------------|
| Preferred broker port                | `17654`                                                        |
| Broker port fallback range           | `17654`–`17664` (inclusive)                                    |
| Browser-facing loopback host         | `localhost` (NOT `127.0.0.1`; Chrome/Edge reject 127.0.0.1 for WebAuthn) |
| Internal loopback prefix (kept)      | `http://127.0.0.1:<port>/`                                     |
| Edge AppUserModelID                  | `PAXCookbook.Local.v1`                                         |
| Edge `--user-data-dir`               | `%LOCALAPPDATA%\PAXCookbook\EdgeAppData`                       |
| Bootstrap pointer                    | `%APPDATA%\PAXCookbook\cookbook.bootstrap.json`                |
| Browser-window sidecar               | `<Workspace>\Runtime\browser.window.json`                      |
| PROPERTYKEY for AppUserModelID       | `{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}` pid `5`, type `VT_LPWSTR` |

These literals are referenced by file path or constant in `app\broker\Start-Broker.ps1`, `app\launcher\RuntimeDiscovery.psm1`, `launcher\Start-PAXCookbook.ps1`, `launcher\Start-PAXCookbookSupportMode.ps1`, `app\install\Install-PAXCookbook.ps1`, and `app\web\manifest.webmanifest`. Changing any one of them is a doctrine-revision phase, not an incremental fix.

### 11.2 Stable port behavior

The broker binds the loopback HttpListener using a deterministic port-selection sequence:

1. Read `cookbook.bootstrap.json -> selectedBrokerPort`. If present and in the fallback range, attempt to bind it first.
2. Otherwise (or on bind failure), scan `17654` → `17664` in order, taking the first port that is either free or already owned by a prior Cookbook broker for the **same workspace** (see §11.3).
3. If the full range is unusable, the broker fails loudly. There is **no silent fallback to a random ephemeral port** — that was the previous behavior and is now forbidden.
4. On successful bind, the broker persists `selectedBrokerPort` (and `preferredBrokerPort` / `portRangeStart` / `portRangeEnd` for diagnostics) back into `cookbook.bootstrap.json` so the next launch hits the same port first.

Both loopback prefixes are bound on the same port: `http://127.0.0.1:<port>/` (back-compat for health probes) and `http://localhost:<port>/` (canonical browser-facing origin; required for WebAuthn). Browser-facing URLs constructed by the broker MUST use the `localhost` host.

### 11.3 Reuse vs scan vs fail

On launch, the launcher's existing `Test-LiveBrokerAvailable` decides whether to reuse an already-running Cookbook broker for the same workspace. The broker's own listener-bind logic is separate: if `Test-LiveBrokerAvailable` did not detect a reusable broker, the broker proceeds to bind. When it encounters a port that is already in use by an **unrelated** process (a non-Cookbook broker, a stranger), it skips to the next port in the range. When it encounters a port already owned by the current Cookbook broker for the same workspace (atomic-restart race), it reuses. The distinction is observable via `/api/v1/health` + `workspaceFolderPath` match, which `Test-PriorBrokerActive` already implements.

### 11.4 Edge app-window launch contract

The launcher opens the browser via a single dispatcher (`Open-CookbookUi`) that:

1. Attempts to locate `msedge.exe` using the discovery order: `HKLM\Software\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe` → `HKLM\Software\WOW6432Node\...` → `HKCU\Software\...` → `$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe` → `${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe` → `$env:LOCALAPPDATA\Microsoft\Edge\Application\msedge.exe` → `Get-Command msedge.exe`. The first hit whose file `ProductName` matches `Microsoft Edge*` wins.
2. If Edge is found, launches it with the canonical command line:
   ```
   msedge.exe --app=<runtimeUrl> ^
              --user-data-dir=<EdgeAppData> ^
              --profile-directory=Default ^
              --no-first-run ^
              --no-default-browser-check ^
              --disable-sync ^
              --disable-features=msEdgeSignInPromo,msAccountAuthMSAUI,EdgeWelcomePage ^
              --start-maximized
   ```
   The runtime URL is passed **only** inside `--app=`. The URL is validated against the same loopback-HTTP guard the default-browser path uses (`Test-LoopbackHttpUrl`). `-ArgumentList` is the array form so the URL is never re-parsed by a shell. The three trailing flags are appliance-policy flags added by `UXR_EDGE_APP_WINDOW_VISUAL_IDENTITY_AND_LAUNCH_POLISH_01`: `--disable-sync` suppresses Edge's MSA sync onboarding (the appliance is local-only, no cloud account), `--disable-features=msEdgeSignInPromo,msAccountAuthMSAUI,EdgeWelcomePage` suppresses the residual sign-in promo + welcome surfaces, and `--start-maximized` ensures the chef sees a full-window UI on first launch. The launcher MUST NOT use `--guest` or `--inprivate` (both break WebAuthn and discard Cookbook state).
3. After `Start-Process -PassThru`, the launcher writes `<Workspace>\Runtime\browser.window.json` with `{browser, mode, processId, url (token redacted), aumid, userDataDir, startedUtc}`.
4. **Cross-launch duplicate-window guard.** Before spawning a new Edge app-window, the dispatcher reads `<Workspace>\Runtime\browser.window.json`. If the recorded `processId` is still alive AND its image name is `msedge` AND it exposes a real main window handle, the dispatcher calls Win32 `ShowWindowAsync(SW_RESTORE) + SetForegroundWindow` on that window and skips the new spawn (returning `mode = "app-window-reused"`). Any failure in this guard falls through to a fresh spawn so the chef always ends up at the Cookbook UI. This complements the launcher's in-process `Invoke-BrowserOpenOnce` guard (which prevents accidental double-open within a SINGLE launcher run) by preventing the SECOND Start Menu click from opening a SECOND app-window when the broker is being reused.
5. If Edge is not found, the dispatcher falls back to the existing `Open-DefaultBrowserToRuntime` (which uses the OS-registered HTTP handler).

### 11.5 Shortcut AppUserModelID stamping

The installer stamps `PKEY_AppUserModel_ID = "PAXCookbook.Local.v1"` on every PAX-owned `.lnk` it creates (Start Menu, Desktop, all utility shortcuts). This is what makes Windows group the Edge app-window with the Cookbook shortcuts in the taskbar and on the recent-apps list. The stamping uses the existing `IPropertyStore` interop in `Initialize-ShortcutPropertyStoreType` extended with a `SetAppUserModelID(string lnkPath, string aumid)` static (PROPERTYKEY fmtid `{9F4C2855-...}` pid `5`, VT_LPWSTR). The installer never pins to the taskbar; pinning is a manual user action.

### 11.6 EdgeAppData ownership

`%LOCALAPPDATA%\PAXCookbook\EdgeAppData\` is owned by the appliance:

- Created lazily by Edge on first app-window launch (the launcher passes `--user-data-dir` and Edge creates the directory).
- **Preserved** by `update` mode and `repair-shortcuts` mode (the chef's Edge session state and cookies live here).
- **Removed** by `uninstall` mode after `App\` removal. The same uninstall confirmation text mentions "local Edge app data" so the chef knows what is being removed.

### 11.7 Default-browser fallback

When Edge is unavailable, the dispatcher delegates to the OS-registered HTTP handler via `Start-Process -FilePath <url>`. This path is the only sanctioned non-Edge browser launch; it does NOT name any non-Microsoft browser binary (per constitution rule 16). The fallback loses the app-window experience but preserves UI access.

---

## 12. Required "before you mutate install" checklist

Before any agent runs `install`, `update`, `uninstall`, `repair-shortcuts`, or `remove-shortcuts` mode against the live install:

- [ ] I have read `docs\RELEASE_PACKAGE.md` in this session (or recently enough that I can answer §4.5 from memory).
- [ ] I have read `docs\OPERATOR_GUIDE.md` §3 (First install), §8 (Update lifecycle), and §11.4 (Reinstall from scratch).
- [ ] I have read `app\install\Install-PAXCookbook.ps1` and can name the function that corresponds to the mode I am about to run.
- [ ] I have read `tools\release\Release.psm1` — at minimum `Get-ReleaseFileSet`, `Get-ReleaseExclusionPatterns`, and the include-roots constants.
- [ ] I know whether the installer's `SourceRoot` is the ZIP root or the `app\` subfolder. (Answer: the `app\` subfolder.)
- [ ] I know whether `update` mode copies the top-level `launcher\` scripts. (Answer: yes, via `Copy-LauncherSubtree`.)
- [ ] I know whether the broker must be stopped before the mode I'm about to run. (Answer: yes for `update`, `uninstall`, `repair-shortcuts`.)
- [ ] Brian has manually clicked **Stop PAX Cookbook Server** (or confirmed the broker is already stopped) if the broker stop is required.
- [ ] I am using the documented command from `OPERATOR_GUIDE.md` (or the smoke harness recipe under `_temp\phase_uxr_titlebar_logo_rebuild_recovery\`).
- [ ] I have not hand-copied any file into `<InstallRoot>\App\`.
- [ ] I have run a smoke against the freshly built ZIP and it passed (§4.5 + the §3 / §5 / §10 checks).

If any checkbox is unchecked, stop. Do not run the installer.

---

## 13. Required response format for future agents

When Brian asks an agent to rebuild, install, update, or repair PAX Cookbook, the agent's report must include the following sections in this order:

1. **Source files changed.** Exact list with mtimes. Distinguish "this session" from "earlier in the day" — never claim credit for prior-session work.
2. **Build command run.** The exact command line, copy-paste-able.
3. **Package produced.** Output directory, the five sidecar filenames, and the ZIP SHA-256.
4. **Package contents verified.** A reference to the smoke harness that ran and its pass count. ZIP root entries enumerated.
5. **Install / update / repair run.** Yes or no.
6. **If run, the exact command.** Copy-paste-able. With absolute paths.
7. **Whether the broker was stopped, and how.** "Brian clicked Stop PAX Cookbook Server" or "Brian confirmed the broker was not running" or "the agent did not call the installer because the broker was running."
8. **Installed files changed.** A diff between the previous `App\` and the new `App\`, or a statement that the installer ran and produced a backup at `<InstallRoot>\Backups\App_<oldVersion>_<UTC>\`.
9. **Shortcuts verified.** The list of `.lnk` files in `%APPDATA%\Microsoft\Windows\Start Menu\Programs\PAX Cookbook\` and whether each target resolves.
10. **Gates passed.** Smoke, integrity check (`Compare-PaxScriptIntegrity`), no-dev-artifacts check.
11. **Pending for Brian.** Explicit list of manual verifications Brian still needs to perform (visual checks, functional checks like clicking Stop PAX Cookbook Server, etc.).

Reports that omit any of these sections are incomplete.

---

## 14. Red flags that require stopping

If any of the following is true during a build / install / update / repair task, **stop immediately** and produce a procedure proposal rather than acting:

1. **Docs and scripts disagree.** `RELEASE_PACKAGE.md` says one thing about installer modes and the `ValidateSet` in `Install-PAXCookbook.ps1` says another. (Known drift: §9 of RELEASE_PACKAGE.md lists four modes; the code's `ValidateSet` has six. Naming a doc-behind-code drift in a phase report is fine; mutating install based on the wrong source is not.)
2. **`SourceRoot` ambiguity.** You cannot point at the function or line that resolves the source root for the mode you're about to run.
3. **Live broker** running and the mode you want to run requires a stopped broker, and Brian has not yet stopped it.
4. **Top-level `launcher\` would be skipped.** Your proposed source root is `<extracted>\app\` and the sibling `<extracted>\launcher\` does not exist on disk (or you cannot prove it does).
5. **Update command not documented.** You want to run `update -SourceRoot ...` and you cannot show that the documented chef-facing path is either Path A (`-Handoff`) or Path B (the `§8.4` recovery), not the `-SourceRoot` harness path.
6. **Installed-tree mapping unclear.** You cannot trace, file-by-file, how the ZIP layout becomes the installed layout.
7. **PAX SHA drift.** `App\VERSION.json -> paxScript.sha256` does not match the SHA-256 of `App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1`, or you are about to edit either.
8. **Unexpected source file drift.** A baseline hash check fails for a file you did not edit in this session. Investigate before rebasing anything.
9. **Proposed mutation of the PAX script bytes.** Your plan, refactor, "improvement", or proposed code path would patch, rewrite, normalize line endings of, reformat, transcode, BOM-stamp, wrap, prepend / append metadata to, sign in place, strip signatures from, add signatures to, "fix" anything inside, generate a derived form of, monkey-patch the functions of, or in any other way alter the bytes of `App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1` — whether at install time, at acquisition time (v1 architecture), at update time, at runtime, in tests, in smoke harnesses, or in telemetry / audit paths. **Stop immediately.** Per constitution rule 8 and §9.1, this is rejected by definition. The acceptable response is to (a) take the PAX script as published and do nothing to it, (b) escalate the underlying need to the PAX team, or (c) implement the desired behavior in Cookbook code outside the script. Never modify the script, and never change `paxScript.sha256` to bless a locally modified script.
10. **Proposed sideways "fix" of a validation failure.** A signature failure, hash mismatch, manifest rejection, or compatibility-window rejection is in front of you, and the proposed remediation is to edit the PAX script, edit `paxScript.sha256`, edit `engineManifestTrustAnchorThumbprint`, or substitute an unapproved file. **Stop immediately.** Per constitution rule 8 and §14a, every validation failure is a terminal refusal that requires re-acquisition through the sanctioned pipeline (re-download via the signed manifest, or re-obtain the correct file from the PAX team). Validation gates are never softened to make a mutated script pass.
9. **Package includes dev artifacts.** Smoke check finds `_temp\`, `_archive\`, `tools\`, `docs\`, `dist\`, `.git\`, `.vscode\`, `node_modules\` inside the ZIP. The exclusion list is broken or was bypassed.
10. **Start Menu shortcut behavior unclear.** You're about to rename a shortcut and cannot point at both (a) the new-name handling in `Get-ShortcutStopPath`-style helpers, and (b) the legacy sweep that removes the old shortcut.
11. **A user-visible string change is not present in the packaged file.** Source has the new string but the ZIP entry still shows the old string — the build was not re-run after the edit. Re-build, re-verify, do not proceed.
12. **An exclusion-list change is being requested without a phase ticket.** The closed list in `Release.psm1` is intentional; broaden it only under an explicit scope.

---

## 14a. External PAX engine acquisition (Cookbook v1)

This section codifies the Cookbook v1 architecture defined by [`_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md`](../_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md) and the Stage 0 alignment in [`_temp/phase_13_release_packaging/SUPERSEDED.md`](../_temp/phase_13_release_packaging/SUPERSEDED.md). Cookbook v1 is the active architecture and the contract below is binding for the acquisition surface. When code and this section disagree, the code wins and this doctrine is the work-item.

### 14a.1 The one-architecture model

There is **one** v1 architecture for both internal-test and production. The two release artifacts are produced from the same source tree, by the same release pipeline, with the same `VERSION.json.paxScript.acquisitionPolicy = "external"` value. They differ **only** by:

1. Signing / cert status (production carries the production code-signing cert; internal-test may be unsigned or signed by a development cert).
2. Distribution channel (verifier/harness vs customer-facing surface).
3. Manifest-signature policy (production sets `VERSION.json.paxScript.manifestSignaturePolicy = "required"` with a populated trust anchor and a signed manifest; an internal-test-unsigned build may set `"internal-test-bypass"` with a `null` trust anchor, which skips detached-signature verification **only**). See §14a.5.

Apart from the manifest-signature policy in item 3, the runtime, the SPA, the broker state machine, the acquisition pipeline, the SHA-256 pin check, and the per-cook integrity model are the same in both artifacts. Every check other than detached-signature verification runs identically regardless of policy. Any doctrine elsewhere in this document that distinguishes "internal-test" from "production" on *other* behavioral grounds is wrong for v1 and must be revised under the same phase that surfaces the discrepancy.

### 14a.2 The broker-first-run acquisition contract

For any installed appliance whose `install-state.json -> paxAcquisition.pending = true` (which is every freshly-installed v1 appliance, before any cook has run), the broker's startup sequence is:

1. **Refuse all cook-class routes.** Every recipe / cook / scheduler / runtime endpoint returns a structured `acquisition_required` response. The broker does not start any cook, does not load any recipe into the runtime, and does not advance any scheduled bake while pending.
2. **Render the acquisition surface.** The broker serves the SPA's acquisition view (Settings page + first-launch modal). The SPA presents the three-button dialog per D1: **Download** (broker fetches from `VERSION.json.paxScript.engineManifestUrl` over HTTPS), **Use a local script file** (native HTML `<input type="file" accept=".ps1">` picker), and **Cancel** (cookbook remains blocked).
3. **Validate before persisting.** Whichever path the operator chooses, the broker:
   - Fetches and verifies the approved-engine manifest signature using `Update/Trust.psm1` + `Update/Signature.psm1` (RSA-PKCS1v15-SHA256, detached `.sig` envelope, thumbprint pinned at `VERSION.json.paxScript.engineManifestTrustAnchorThumbprint`).
   - Locates the manifest entry whose `version` matches `VERSION.json.paxScript.version` and whose `cookbookVersion` window contains the running cookbook.
   - Recomputes SHA-256 of the candidate script bytes and rejects on any mismatch against either `VERSION.json.paxScript.sha256` or the manifest entry's SHA-256.
   - Writes the validated bytes **UNCHANGED** (byte-for-byte identical to the bytes that were SHA-256-verified above) to `<InstallRoot>\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1` (the canonical path; never a temp path). No normalization, no re-encoding, no BOM insertion, no line-ending conversion, no wrapping, no metadata injection, no in-place signing — per the immutability contract in [_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md §4.5](../_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md#45-pax-script-immutability-contract) and constitution rule 8.
   - Updates `install-state.json -> paxAcquisition` with `pending = false`, `source`, `version`, `sha256`, `manifestId`, `manifestHash`, `manifestVersion`, `validatedAtUtc`, and `activatedAtUtc`.
4. **Resume normal operation.** Subsequent launches start in normal mode and the existing `Test-BundledPaxIntegrity` startup check is load-bearing for every cook.

The installer **never** performs an HTTPS fetch. The installer's no-outbound-HTTP rule from §5.3 is absolute and unchanged. The choke-point that allows HTTPS at all is the **broker** process, which is already the only sanctioned outbound-HTTP surface in the appliance (for update-manifest fetches). The acquisition flow re-uses that exact surface; no new outbound-HTTP code paths are introduced outside the broker.

**Script-immutability binding (load-bearing for the acquisition pipeline).** The entire acquisition flow — Download path, Use-a-local-script-file path, automation `-PaxScriptPath` / `-PaxManifestPath` seed path, sideload-probe path — is bound by constitution rule 8 and the immutability contract referenced above. The bytes the broker hashes are the bytes the broker stores; the bytes the broker stores are the bytes the broker invokes. No path in this contract is permitted to patch, rewrite, normalize, re-encode, sign, wrap, or otherwise mutate the script bytes between acquisition and invocation. A chef-supplied local file remains untouched on the chef's disk regardless of the validation outcome. A downloaded temp file is hashed in place; no edits before hash, no edits after hash. Validation failure is a terminal refusal that requires re-acquisition through the sanctioned pipeline; it is never a license to alter the script, the `paxScript.sha256` pin, or the trust anchor.

### 14a.3 The no-`embedded`-in-v1 rule (with Phase 12 historical-archive carve-out)

For every new v1 release artifact — internal-test or production — `VERSION.json.paxScript.acquisitionPolicy` MUST be `"external"`. The release pipeline's post-build check rejects any artifact whose policy is `"embedded"`. The runtime broker's startup integrity check rejects any installed appliance whose policy is `"embedded"` unless the historical-archive carve-out below applies.

**Historical-archive carve-out.** Exactly one historical artifact is preserved: the Phase 12 internal-test self-extracting `PAXCookbookSetup.exe` whose SHA-256 is `8DD2CF81AF7986775F7F26E1E54DF8DFA545E8BDE2C47B10CA2F9C3D7C5FF975`. This artifact's `VERSION.json.paxScript.acquisitionPolicy = "embedded"` is accepted **only** by audit tooling reading the historical bytes. The carve-out emits a `legacy_historical_artifact_detected` warning on every read. The Phase 12 artifact is not regenerated, not re-signed, not redistributed, and not used as a Stage 1+ acceptance baseline. It exists as forensic evidence of the embedded-payload era and nothing else.

No other `"embedded"` artifact is permitted to exist. If one is discovered in a phase report, in a smoke harness, or in a customer environment, that is a doctrine violation and a red flag (§14).

### 14a.4 What this contract does not change

The following doctrine is unchanged by external PAX acquisition:

- The installer's posture (§5.3): no elevation, no registry, no services, no outbound HTTP **in the installer process**, no signing mutation, no execution-policy / Defender / firewall mutation, no env-var mutation.
- The installed-tree shape (§3.3): `<InstallRoot>\App\` is still the appliance root and `App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1` is still the only valid PAX script path.
- The SHA-baseline rules (§9): the PAX SHA pin in `VERSION.json` is still sacred; only the moment of recompute moves from install-time to broker-first-launch.
- The Edge app-window contract (§11): the acquisition SPA view is served by the broker over loopback HTTP, in the same Edge app window as the rest of the appliance.
- The pre-mutate checklist (§12) and the response format (§13): both still apply unchanged to install / update / repair work.

The following is new in Cookbook v1 relative to the Phase 12 embedded-payload era:

- `app\broker\Engine\Acquisition.psm1` (new module).
- `app\broker\Routes\Setup.ps1` (four new routes for status, manifest fetch, validate-local-file, and apply).
- `app\web\` acquisition view (one new SPA view + integration into the Settings page).
- `app\install\Install-PAXCookbook.ps1` gains the `-PaxScriptPath` and `-PaxManifestPath` automation-fallback parameters (carved-out from the no-custom-PAX-path rule because they route through the same validation pipeline; see [`app/install/README.md §6`](../app/install/README.md#6-what-this-installer-is-permanently-not-allowed-to-do)).
- `install-state.json` (new file at `%LOCALAPPDATA%\PAXCookbook\install-state.json`) carries the `paxAcquisition` block; the installer creates it with `pending = true` and the broker mutates it during acquisition.
- New runtime telemetry: `cook_runs` rows gain `paxAcquisition.version` / `.manifestId` / `.source` for forensic anchoring.
- New chef-facing failure modes (codified in [`docs/TROUBLESHOOTING.md §17e`](TROUBLESHOOTING.md#17e-pax-engine-acquisition-errors)): `acquisition_required`, `manifest_fetch_failed`, `manifest_signature_invalid`, `no_compatible_engine`, `script_hash_mismatch`, `local_file_not_approved`.

No PAX-script export route exists in the broker (D6); the Settings page exposes no export affordance. `paxScript.exportEnabled = false` in active metadata is a doctrine declaration backed by absence — there is no runtime consumer to disable. Cookbook owns the customer-facing acquisition docs (D10); the PAX team owns the engine release and the signed approved-engine manifest.

### 14a.5 Internal-test-unsigned vs production close

An internal-test-unsigned build sets `VERSION.json.paxScript.manifestSignaturePolicy = "internal-test-bypass"` and `engineManifestTrustAnchorThumbprint = null`. The `internal-test-bypass` policy relaxes **only** detached-signature verification of the approved-engine manifest; JSON-schema validation, approved-entry selection, the SHA-256 pin check (D8: no drift), and exact-byte activation at the canonical install path all run identically to a production build. The acquisition dialog renders the **Internal-test build (not customer-facing)** banner so the build cannot be mistaken for production. An internal-test-unsigned build is **for internal testing only and MUST NOT be published to customers**, and there is no path that promotes it to a production release.

Closing to a customer-facing production release requires **all** of the following before the production artifact is built. None are supplied by an internal-test-unsigned build, and this subsection enumerates the prerequisites for a future production workstream — it is not a license to perform that close now:

1. **HTTPS `engineManifestUrl`** — `VERSION.json.paxScript.engineManifestUrl` points at the PAX team's production manifest over `https://` (not a loopback / internal-test endpoint).
2. **Trust-anchor thumbprint** — `VERSION.json.paxScript.engineManifestTrustAnchorThumbprint` is a populated 40-hex-character SHA-1 thumbprint (not `null`).
3. **Signed approved-engine manifest** — the served manifest carries a valid detached RSA-PKCS1v15-SHA256 signature chained to that trust anchor.
4. **`manifestSignaturePolicy = "required"`** — set in both `VERSION.json.paxScript` and `app/resources/manifest.json.includedPaxScript`, so the broker enforces detached-signature verification.
5. **Production code-signing certificate** — the release ZIP / installer is Authenticode-signed (and/or carries the detached envelope) under the production code-signing certificate.

This subsection mirrors [`docs/RELEASE_PACKAGE.md §11c.7–11c.8`](RELEASE_PACKAGE.md#11c-external-pax-engine-acquisition-cookbook-v1). Weakening `manifestSignaturePolicy = "required"` behavior, populating any of the above with placeholder values to "pass", or re-labeling an internal-test-unsigned artifact as production are all doctrine violations.

---

## 15. Where this doctrine stops

This document is doctrinal scaffolding. It is *not* a substitute for reading the actual scripts and docs. If you are an agent and the script or doc has changed since this doctrine was written, the script or doc wins — update this doctrine in the same phase that changed the underlying behavior.

If you find yourself thinking "the doctrine is wrong, I'll just do the thing," you are wrong. The doctrine is conservative on purpose. Open a doctrine-revision phase or ask Brian.

---

*This file is permanent. Future agents must read it before any rebuild, packaging, install, update, or repair work on PAX Cookbook.*
