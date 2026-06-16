# PAX Cookbook — Chef Guide

> *Throughout this guide the **chef** is the person running the appliance — picking a workspace, editing recipes, running bakes, managing the local trust allowlist. (The filename `OPERATOR_GUIDE.md` is the canonical name used by the project; the role label is **chef**.)*

> *Diagnostic paths, SQLite tables, JSON fields, log filenames, and API routes carry literal identifiers such as `cookId`, `Cooks\`, `cook.log`, `cook_folder`, `/api/v1/cooks`, `authProfileId`, and `auth_profiles`. They appear verbatim in this guide where they apply. Treat them as implementation identifiers shown for troubleshooting.*

This guide describes how to install, launch, use, maintain, recover, and uninstall **PAX Cookbook** as a local Windows appliance. It documents what the appliance actually does today. It does not describe roadmap items, cloud features, or behavior that exists only in proposals.

If something in this guide does not match what the appliance does, the guide is wrong. Open an issue and reference the section.

---

## 1. What PAX Cookbook is

PAX Cookbook is a **local-first, Windows-only, zero-admin operational shell** around the [PAX Purview Audit Log Processor PowerShell script](https://github.com/Microsoft/PAX) (referred to throughout this guide simply as **PAX**).

Cookbook is:

- a PowerShell-based broker that runs locally on the chef's machine,
- a single-page browser UI the launcher opens at `http://localhost:<port>` (the broker itself binds loopback only),
- a thin orchestration layer over PAX.

Cookbook is **not**:

- a service or daemon (no `New-Service`, no `Register-ScheduledTask`),
- a cloud product,
- an Electron / PWA / Tauri application,
- a replacement for PAX (PAX remains authoritative and standalone),
- something that requires administrator rights at any point.

PAX is the authoritative data-collection engine. Cookbook orchestrates it.

---

## 2. What PAX is

PAX is the **PAX Purview Audit Log Processor** — a Microsoft PowerShell script for pulling Purview / Microsoft 365 audit data at scale, with output, rollup, and reporting flexibility. It is maintained separately from Cookbook.

Cookbook ships a **bundled copy** of PAX in the install tree at:

```
<InstallRoot>\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1
```

The bundled PAX script's name, version, relative path, and SHA-256 are pinned in `<InstallRoot>\App\VERSION.json` under `paxScript`. Cookbook does **not** rewrite the PAX script, patch it, monkey-patch its functions, or alter its behavior. Every bake executes the bundled PAX script directly with whatever arguments the recipe expands to.

You may inspect the bundled PAX script at any time; it is a plain `.ps1` file on disk.

**Cookbook v1 — external PAX engine acquisition.** Under the External PAX Engine Acquisition contract (see §4a), the PAX engine is **not** present in the release ZIP and is acquired by the broker at first launch through an approved-engine manifest. The pinned path above, the `paxScript` pin in `VERSION.json`, and the "Cookbook does not rewrite PAX" rule all continue to hold — the only change from earlier builds is that the bytes arrive via acquisition rather than via installer copy. The export surface previously available at **Settings → Export bundled PAX script** is removed in v1 (§4a.5 / §9).

---

## 3. First install

### 3.1 Prerequisites

**Cookbook itself** requires only:

| Requirement | Why |
| --- | --- |
| Windows 10 / 11 (64-bit) | The appliance is Windows-only. |
| PowerShell 7.4 or newer | The launcher hard-gates this. Install from <https://aka.ms/PSWindows>. |
| A local folder you can write to | This is the **workspace**. See §5. |

No administrator rights are needed at any point. Installing and launching Cookbook does not exercise any runtime other than PowerShell.

**Bundled-recipe prerequisite — PAX rollup workflows.** Certain bundled recipes (notably the **M365 Usage Analytics Dashboard** and **AI-in-One Dashboard** templates) invoke PAX in `Rollup` or `RollupPlusRaw` mode. PAX's rollup post-processor is implemented in Python (3.10 or newer). When such a recipe runs:

- **PAX** — not Cookbook — detects whether a compatible Python interpreter is on `PATH`.
- **PAX** — not Cookbook — performs a per-user silent install (`winget Python.Python.3.13` → python.org installer fallback) if no compatible interpreter is found.
- Cookbook orchestrates the PAX invocation, streams PAX's stdout / stderr verbatim into the bake's `cook.log`, and records the exit code. Cookbook does not detect, install, configure, validate, or vendor Python itself.

If you do not run rollup-based recipes, Python is not exercised at any point. Non-rollup PAX invocations (raw / 1-1 mode) do not invoke the Python post-processor and have no Python dependency.

This separation is doctrinal. PAX owns its own runtime dependencies. Cookbook remains a thin orchestration layer over PAX.

### 3.2 Steps

1. Download the release ZIP (`pax-cookbook-<version>.zip`) and its SHA-256 sidecar (`pax-cookbook-<version>.zip.sha256`).
2. (Optional but recommended.) Verify the package hash:
   ```powershell
   (Get-FileHash -Algorithm SHA256 -LiteralPath 'pax-cookbook-<version>.zip').Hash
   Get-Content -LiteralPath 'pax-cookbook-<version>.zip.sha256'
   ```
   The two values must match. If they do not, do not extract; re-download.
3. Extract the ZIP to a local folder (any folder you can write to — `Documents\PAXCookbookRelease` is fine).
4. From an ordinary (non-elevated) PowerShell 7.4+ prompt, run:
   ```powershell
   cd <extracted-folder>
   pwsh -File .\app\install\Install-PAXCookbook.ps1 install
   ```
   Or double-click `Install PAX Cookbook.cmd` at the top of the extracted folder — it invokes the same PowerShell installer with the same `install` mode.
5. The installer copies the appliance into `%LOCALAPPDATA%\PAXCookbook\App\`, creates a per-user **Start Menu** folder named `PAX Cookbook` containing five shortcuts (`PAX Cookbook`, `PAX Cookbook Support Mode`, `Repair PAX Cookbook Shortcuts`, `Stop PAX Cookbook Server`, `Uninstall PAX Cookbook`), and writes `install.log` next to the install tree. No registry keys, no services, no PATH changes, no firewall rules.

### 3.3 What the installer creates

```
%LOCALAPPDATA%\PAXCookbook\
├── App\                       # The appliance (replaced atomically on update)
├── Backups\                   # Previous App\ versions (kept per retention policy)
├── Updates\                   # Staged update packages (broker-managed)
└── install.log                # Append-only local log
```

Plus per-user shortcuts (no admin required):

```
%APPDATA%\Microsoft\Windows\Start Menu\Programs\PAX Cookbook\
├── PAX Cookbook.lnk                       # Launch the appliance (NOT suppressed from Recommended)
├── PAX Cookbook Support Mode.lnk          # Open broker console + logs for diagnostics
├── Repair PAX Cookbook Shortcuts.lnk      # Recreate this folder if shortcuts go stale
├── Stop PAX Cookbook Server.lnk           # Cleanly terminate the running broker
└── Uninstall PAX Cookbook.lnk             # Confirm-and-remove (per-user, no admin)

%APPDATA%\Microsoft\Windows\Desktop\PAX Cookbook.lnk                 # Desktop shortcut (default; opt out with -NoDesktopShortcut)
```

The four utility `.lnk` files in the folder are tagged with the
`PKEY_AppUserModel_ExcludeFromShowInNewInstall` property so Windows
keeps them out of the **Recently added** list. Only the main `PAX
Cookbook.lnk` is allowed to surface there. Windows' separate
**Recommended** list (Start Menu suggestions) is driven by Windows
heuristics that this property does not control, so on some machines
one or more utility shortcuts may briefly appear there. The
installer also creates the utility shortcuts BEFORE the main
shortcut so `PAX Cookbook.lnk` has the newest modification time and
is biased toward Windows' "recently added" surfaces.

### 3.4 Optional installer switches

| Switch | Effect |
| --- | --- |
| `-CreateDesktopShortcut` | Accepted as a no-op. The Desktop shortcut is created by default; the switch is honored for callers that pass it explicitly. |
| `-NoDesktopShortcut` | Opt out of Desktop shortcut creation. Does not delete an existing Desktop shortcut. |
| `-NoShortcuts` | Create no shortcuts at all. Mutually exclusive with `-CreateDesktopShortcut`. |

The installer never asks for elevation. The installer never edits the registry. The installer never invokes a package manager. If you do not want it to write `%LOCALAPPDATA%\PAXCookbook\`, do not run it.

---

## 4. First launch

Launch Cookbook via the Start Menu shortcut, the optional Desktop shortcut, or by running the launcher directly:

```powershell
pwsh -File "$env:LOCALAPPDATA\PAXCookbook\App\..\launcher\Start-PAXCookbook.ps1"
```

The launcher will:

1. Hard-gate PowerShell version. PS < 7.4 exits with a link to <https://aka.ms/PSWindows>.
2. Read the bootstrap pointer at `%APPDATA%\PAXCookbook\cookbook.bootstrap.json`.
3. On first run (no bootstrap), ask you to pick a workspace folder (§5).
4. Validate the workspace folder against the hard-requirement rules (§5.2).
5. Start the broker bound to loopback only with a per-session bearer token.
6. Open your default browser to the SPA at `http://localhost:<port>`.

The broker binds **loopback only** (`127.0.0.1`). It is not reachable from the network. The browser is launched against `http://localhost:<port>` so that platform credential APIs that require a `localhost` origin work without manual reconfiguration. The session token is required by every API call and is regenerated on every launch.

### 4.1 Reopening the browser while the broker is still running

If you close the browser while Cookbook is still running, open PAX Cookbook from the Start Menu or Desktop shortcut again. Cookbook will reconnect to the running local broker and reopen the browser. You do not need to kill the broker just to reopen the page.

The Start Menu **Repair PAX Cookbook Shortcuts** entry rewrites the Start Menu and (when present) Desktop `.lnk` files against the current install. It does not delete workspace data, the install tree, or any bake outputs. (To delete shortcuts entirely, run the installer with `-Mode remove-shortcuts` from a shell; it is an internal maintenance mode and is not exposed as a Start Menu entry.)

---

## 4a. PAX engine acquisition

This section describes how Cookbook v1 obtains the PAX engine script at first launch. Cookbook v1 does not embed the PAX engine in the install ZIP; it acquires the engine at first launch through an approved-engine manifest. The implementation history is tracked under [`_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md`](../_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md).

**Why this exists.** PAX (the Purview Audit Log Processor script Cookbook orchestrates) is owned and signed by a separate team and ships on a different cadence than Cookbook itself. Cookbook v1 stops embedding the PAX script inside its release ZIP and instead acquires it at first launch through a signed approved-engine manifest. Two practical consequences for chefs:

1. A fresh install does **not** include the PAX script bytes. The first launch of Cookbook on a new machine presents an acquisition dialog before any recipe can run.
2. Cookbook updates do not automatically deliver PAX upgrades. PAX upgrades come from the PAX team's distribution channel via the same acquisition dialog (run from **Settings → PAX engine**).

**What Cookbook never does to the PAX script.** Cookbook **does not edit the PAX script**. It does not patch it, rewrite it, reformat it, normalize line endings, change its encoding, add or remove a BOM, inject wrapper code, append comments or banners, rewrite its header, sign it, strip signatures the PAX team applied, or "fix" anything inside it. Cookbook also **does not "prepare", "stage", or "rebuild" the script** before storing or running it; there is no Cookbook-customized variant, no per-recipe specialization, and no Cookbook-side derived form of the engine. If you choose **Use a local script file** and point Cookbook at a `.ps1` you've already downloaded, **the original file on your disk is never modified** by Cookbook — Cookbook reads it, hashes it, validates the exact bytes against the signed approved-engine manifest, and then **copies those exact bytes unchanged** into Cookbook's managed location at `%LOCALAPPDATA%\PAXCookbook\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1`. The managed copy is byte-for-byte identical to the approved PAX release; the SHA-256 you'd compute on the managed copy is the same SHA-256 you'd compute on the source you supplied (and the same SHA-256 the PAX team published in the manifest). If validation fails — wrong version, wrong hash, manifest signature invalid, file not in the approved-engine manifest — Cookbook refuses to install the script and asks you to obtain the correct PAX release. There is no chef-side override, no "use anyway" path, and no scenario in which Cookbook will edit the script to make validation pass; the corrective action is always to obtain the approved PAX engine from the PAX team's distribution channel, not to alter the script.

### 4a.1 First-launch acquisition dialog (the three buttons)

When Cookbook starts and has no validated PAX engine on disk (`install-state.json -> paxAcquisition.pending = true`), the SPA opens an acquisition dialog before showing the Recipes page. The dialog has exactly three buttons:

| Button | What happens |
| --- | --- |
| **Download latest approved PAX script** | The broker fetches the signed approved-engine manifest from the URL pinned in `VERSION.json.paxScript.engineManifestUrl`, verifies the manifest signature against the trust anchor pinned in `VERSION.json.paxScript.engineManifestTrustAnchorThumbprint`, downloads the PAX engine script for the version Cookbook expects, recomputes SHA-256, and writes the validated bytes to the canonical path in your install tree. Requires outbound HTTPS to the PAX team's distribution surface. |
| **Use local PAX script file** | Opens the standard Windows file picker (filtered to `*.ps1`). Select a PAX engine `.ps1` you've already downloaded or received out-of-band. The broker still validates the file against a signed approved-engine manifest before accepting it; unapproved or hash-mismatched files are rejected. |
| **Cancel** | Closes the dialog. Cookbook remains blocked: every recipe / cook / scheduler / runtime action returns an `acquisition_required` response and the SPA shows a persistent banner pointing back at **Settings → PAX engine**. No data is lost; nothing is removed. Re-open the dialog at any time. |

The dialog never offers a "use anyway" override. There is no path that accepts an unapproved or hash-mismatched script, because the chef-facing trust model depends on the manifest verification step. If neither **Download** nor **Use a local script file** can be satisfied (no network access and no offline-distributed script), see §4a.4 for the air-gapped / automation fallbacks.

### 4a.2 Re-acquiring after a PAX upgrade

When the PAX team releases a new engine version, the workflow is:

1. The PAX team publishes the new version in the signed approved-engine manifest at the URL Cookbook is pinned to. (For Microsoft-internal distribution, this is the channel your Cookbook contact has shared with you; for self-hosted distribution, see Cookbook's distributor docs.)
2. Cookbook itself does **not** auto-acquire the new version. The currently-validated engine on disk continues to be used for cooks.
3. From **Settings → PAX engine**, click **Check for engine updates** to re-fetch the manifest. If a newer version is available and your Cookbook version is within its compatibility window, the **Download** button becomes available. The acquisition flow is identical to first-launch.

If the new engine version is outside the compatibility window (the manifest entry's `minCookbookVersion` / `maxCookbookVersion` does not contain your running Cookbook version), the dialog reports `no_compatible_engine` and instructs you to update Cookbook first. See [TROUBLESHOOTING.md §17e](TROUBLESHOOTING.md#17e-pax-engine-acquisition-errors) for the full failure-mode triage.

### 4a.3 Where to obtain the PAX team's distribution channel

The distribution channel URL is pinned inside your Cookbook release — you don't have to type it. If you need to confirm it (for firewall allow-listing, proxy configuration, or audit), open `%LOCALAPPDATA%\PAXCookbook\App\VERSION.json` and read the `paxScript.engineManifestUrl` field. The accompanying `paxScript.engineManifestTrustAnchorThumbprint` field is the SHA-1 thumbprint of the manifest signing certificate Cookbook will accept; both values are immutable for the duration of a Cookbook release and only change when you install a new Cookbook version.

If you operate in an environment that blocks outbound HTTPS, your IT contact will need to either (a) allow the engine-manifest URL through the proxy, or (b) provide you with the engine script and the signed manifest on disk and use the automation-fallback parameters in §4a.4.

### 4a.4 Air-gapped / automation fallbacks

For unattended provisioning, CI / verifier harnesses, or air-gapped enterprise environments, the installer accepts two optional parameters that pre-stage the acquisition inputs:

| Parameter | Purpose |
| --- | --- |
| `-PaxScriptPath <path>` | Path to a PAX engine `.ps1` file you have already obtained. The broker validates it against the approved-engine manifest at first launch before copying it to the canonical install location. |
| `-PaxManifestPath <path>` | Path to a signed approved-engine manifest file you have already obtained. Use this when the broker cannot reach `engineManifestUrl` over HTTPS. |

Example (air-gapped install):

```powershell
pwsh -File .\app\install\Install-PAXCookbook.ps1 install `
    -PaxScriptPath 'C:\Provisioning\PAX_Purview_Audit_Log_Processor_v1.11.3.ps1' `
    -PaxManifestPath 'C:\Provisioning\pax-engine-manifest.signed.json'
```

Both parameters route the bytes through the **same** signed-manifest + SHA-256 validation pipeline as the GUI dialog. They are convenience inputs, not trust overrides: an unapproved script or a manifest with an invalid signature is rejected exactly as it would be from the GUI. These parameters exist for two cases: (a) CI / verifier harnesses driving non-interactive acceptance, and (b) air-gapped enterprise installs where the broker cannot reach the engine-manifest URL. They are not the primary customer UX.

`-PaxScriptPath` and `-PaxManifestPath` are **seed inputs**, not "give Cookbook permission to rewrite the script" toggles. The script bytes you supply are validated as-is, hashed as-is, and copied byte-for-byte unchanged into the canonical managed location at `%LOCALAPPDATA%\PAXCookbook\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1`. Cookbook does not normalize, re-encode, sign, wrap, or otherwise modify the supplied script before storing it, and the file you pointed `-PaxScriptPath` at remains untouched on disk regardless of the validation outcome.

### 4a.5 What the export endpoint used to do (and what it does now)

Prior Cookbook builds shipped an **Export bundled PAX script** button on the Settings page (§9 of older OPERATOR_GUIDE revisions). Under Cookbook v1, that surface is removed:

- The Settings page no longer renders the Export button.
- No PAX-script export route exists in the broker. There is no endpoint to call; any client request to a legacy export path returns the broker's standard 404 for an unknown route.

If you need the exact PAX engine bytes Cookbook is currently using, read them directly from `%LOCALAPPDATA%\PAXCookbook\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1` (after acquisition has succeeded). Cookbook does not redistribute the PAX engine; the PAX team owns its release channel.

### 4a.6 Internal-test builds (`manifestSignaturePolicy = "internal-test-bypass"`)

Some Cookbook builds are produced for internal testing only and are **not customer-facing**. You can recognize one two ways:

- The acquisition dialog shows a banner titled **Internal-test build (not customer-facing)**.
- `%LOCALAPPDATA%\PAXCookbook\App\VERSION.json` reports `paxScript.manifestSignaturePolicy = "internal-test-bypass"` and `paxScript.engineManifestTrustAnchorThumbprint = null`.

In an internal-test build, the approved-engine manifest is fetched **without detached-signature verification**. This is the *only* check that is relaxed. Every other acquisition check runs exactly as it does in a production build:

- the manifest is parsed and validated against its JSON schema;
- the approved entry for the version Cookbook expects is selected from the manifest;
- the PAX engine bytes are SHA-256 verified against the hash pinned in that manifest entry;
- the validated bytes are activated, byte-for-byte unchanged, at the canonical install path.

There is still no "use anyway" override, no export route, and no path that accepts an unapproved or hash-mismatched script. An internal-test build does **not** weaken the SHA-256 pin, the approved-entry selection, or the no-edit guarantee on the PAX script.

A production release sets `paxScript.manifestSignaturePolicy = "required"` with a populated `engineManifestTrustAnchorThumbprint`, a signed approved-engine manifest, and a real trust anchor. Internal-test builds are for internal validation only and are not published to customers.

---

## 5. Workspace selection

The **workspace** is a local folder you own. Everything Cookbook produces during normal operation — recipes, bake history, bake outputs, the SQLite database, runtime state — lives inside it.

### 5.1 Workspace layout

After the broker has started in a fresh workspace, you will see:

```
<Workspace>\
├── Database\
│   └── cookbook.sqlite        # SQLite catalog of recipes + bake history
├── Recipes\
│   └── <recipe>.recipe.json   # One JSON file per recipe (chef-owned, editable)
├── Cooks\
│   └── <cookId>\              # One directory per bake (logs, outputs, sentinels)
├── Runtime\
│   ├── workspace.lock         # Lock handle (broker-owned during runtime)
│   └── workspace.lock.acquire # Acquire-time sentinel
├── Updates\                   # Staged update packages and their sidecars
└── Trust\                     # Optional: chef's trusted-signers.json
```

Everything in the workspace is plain JSON, plain SQLite, or plain text. You can inspect it with any tool.

**Workspace portability.** The workspace is designed to be physically movable. All paths Cookbook stores in its database that live *inside* the workspace are stored workspace-relative. You can:

- Quit Cookbook, copy `C:\PAXCookbookWorkspace` to `D:\PAXCookbookWorkspace` (or to another machine at a new path), point Cookbook at the new location, and the existing recipes and bake history continue to work.
- Quit Cookbook, rename the workspace folder, point Cookbook at the new name, same result.

The one case Cookbook does not silently rewrite: a row whose stored bake-folder path is *absolute and outside the current workspace*. This can happen if the absolute path was captured by an earlier installation and the workspace folder itself has since been moved, so the stored path no longer matches anything on disk. Cookbook leaves these rows untouched (it never destroys recorded evidence it cannot prove belongs to the current workspace) and writes a recent-error visible on `/api/v1/health` so you can see exactly which bakes need manual attention. New bakes always store the portable form.

**What is not portable.** The install root (`<InstallRoot>\App`), the bootstrap pointer (`%APPDATA%\PAXCookbook\cookbook.bootstrap.json`), and any reference to absolute paths *outside* the workspace are machine-local and are not carried by copying the workspace.

### 5.2 Workspace hard-requirement rules

The launcher rejects any of the following with a specific, named reason (no generic "invalid path"):

| Reason | Why it is rejected |
| --- | --- |
| UNC path (`\\server\share`) | SQLite + Windows file locks are unreliable over SMB. |
| Mapped UNC drive | Same reason; a drive letter does not change the underlying transport. |
| WSL path (`\\wsl$\...` or `\\wsl.localhost\...`) | Cross-namespace file semantics break locking. |
| Network drive (`DriveType.Network`) | Same SMB / cross-host reliability concerns. |
| CD-ROM drive (`DriveType.CDRom`) | Read-only transport. |
| RAM drive (`DriveType.Ram`) | Volatile; contents disappear on reboot. |
| Invalid drive root (`DriveType.NoRootDirectory`) | Drive root cannot be opened. |
| Unknown drive type (`DriveType.Unknown`, or any other reported type Cookbook does not understand) | Cookbook refuses to guess what the transport actually is. |
| OneDrive folder (any of four detection layers: `$env:OneDrive*`, OneDrive `Accounts` registry, `desktop.ini` marker, conflict-file residue) | Sync agents lock, freeze, and silently mutate files mid-write. |
| SharePoint sync folder | Same reason as OneDrive (same client; Business accounts). |
| DFS-N reparse point | Indirect transport; same SMB-class concerns. |
| Workspace path is not writable by the current user | Cookbook needs to create `Database\`, `Cooks\`, etc. |
| Workspace path does not exist or is not a folder | The launcher does not auto-create chef-owned data roots. |

The launcher also **accepts** the following but emits a soft warning at launch time (the bake can proceed):

| Soft warning | Why it warns (but does not reject) |
| --- | --- |
| Removable drive (`DriveType.Removable`, e.g. USB stick) | Cookbook can run from a removable drive — but if you unplug it mid-bake, sentinels and `cookbook.sqlite` may be left half-written. The launcher prints a one-line warning and continues. |
| Path is inside `%TEMP%` / `%TMP%` / matches `\Temp\` | Bakes, recipes, and audit artifacts may be lost if the OS cleans the folder. |
| Workspace path longer than 200 characters | Long paths may cause issues with deeply nested bake output. Consider moving the workspace closer to a drive root. |
| Workspace drive has less than 1 GB free | Bake history and SQLite growth may fail on a full disk. |

Pick a **local folder on a fixed drive that you own and can write to**. `C:\PAXCookbookWorkspace` is a fine default.

### 5.3 Changing workspaces

The bootstrap pointer remembers your last workspace. If you want to switch:

1. Quit Cookbook (close the browser tab; the broker exits cleanly).
2. Delete or edit `%APPDATA%\PAXCookbook\cookbook.bootstrap.json`.
3. Relaunch. You will be re-prompted for a workspace.

You can keep multiple workspaces — Cookbook does not require exactly one. The bootstrap pointer just remembers which one was used most recently.

---

## 6. Recipe lifecycle

A **recipe** is a JSON document describing a repeatable PAX invocation: what data to collect, what time window, what output destination, and any advanced parameters.

| Stage | What happens | Where it lives |
| --- | --- | --- |
| Create | You author a recipe in the SPA's recipe editor. | `<Workspace>\Recipes\<id>.recipe.json` (written on save). |
| Edit | You re-open the recipe in the editor and modify it. | Same file, replaced atomically on save. |
| Preview | The SPA asks the broker to expand the recipe to its concrete PAX command line. | No file written. Display only. |
| Delete | You move the recipe to the trash. | The file is moved to `<Workspace>\Recipes\_trash\<id>.recipe.<stamp>.json`. Cookbook does not permanently delete recipe files. |

> **Restore:** there is no "restore" button or endpoint. Trashing is reversible only by manually moving the `.recipe.json` file from `<Workspace>\Recipes\_trash\` back into `<Workspace>\Recipes\`. After you do, the broker will refresh its visibility on the next list, but the SQLite metadata index will still carry the prior trash mark — a fresh broker start re-syncs the on-disk truth.

Recipes are chef-owned. You may version-control your `Recipes\` folder with git, copy it between machines, edit the JSON by hand, etc. Cookbook does not encrypt, obfuscate, or hide its recipe format.

### 6.1 Recipe doctrine boundaries

The recipe surface is bounded on purpose. Knowing where the boundaries sit lets you predict how Cookbook will react to edge cases instead of guessing.

**Schema version is locked.** Every recipe carries `"recipeSchemaVersion": 1`. This broker accepts that single value. There is no auto-migration path, no "best-effort upgrade", no silent rewriting of older or newer recipes into the current shape. A recipe written by a future broker version cannot be loaded by this broker.

**Unknown fields are rejected, not ignored.** The schema declares `additionalProperties:false` at every level. A recipe that carries an unknown top-level field, an unknown leaf, or a typo'd key name is rejected with a validator error that names the path. Cookbook does not silently strip extra fields.

**Provenance is one-way.** When a recipe is created, the broker stamps a `createdBy` block (cookbook version, bundled PAX version, release channel, and — for template-materialized recipes — the source template id and version). PUT preserves it verbatim. If a recipe on disk has no `createdBy` block (a recipe created before provenance shipped, or one that a chef hand-edited), the broker deliberately does **not** infer one. The absence stays observable.

**Recipes are plain JSON on disk.** Anything you put into a recipe — including `advanced.extraArguments` — is persisted in cleartext to `<Workspace>\Recipes\<id>.recipe.json`. The recipe file travels with the workspace (OneDrive sync, backups, git history, manual copy). **Do not embed secrets, tokens, passwords, certificate thumbprints, or other credentials in any recipe field**, including the verbatim trailer. Cookbook does not detect, redact, or encrypt such values; they will appear in the file, in bake command logs, and in the bake's stored `recipe_snapshot_json`.

**Recipes are not portable by default.** The `destinations.fact.path` field is whatever string you type into it. If you type a machine-specific absolute path (e.g. `C:\Users\Alice\Reports\rollup.csv`), the recipe will only resolve correctly on a machine where that path exists. Copying a recipe between chefs typically means editing the output path before the first bake on the new machine. Cookbook does not rewrite paths, does not infer workspace-relative semantics for the destination, and does not warn at load time about machine-specific paths.

**The output path is at PAX's mercy.** The destination cell is passed to PAX verbatim as `-OutputPath`. Cookbook enforces only the OneLake / Fabric rejection guard — every other path policy (file overwrite, parent-directory creation, share permissions) is whatever PAX's `PAX_Purview_Audit_Log_Processor.ps1` does at the time the bake runs.

**M2-only switches are removed and cannot be reintroduced.** The recipe schema does not surface the removed PAX v1.11.2 switches as leaves (`ExportWorkbook`, `ExplodeArrays`, `ExplodeDeep`, `RawInputCSV`, the four `Agent365Info` variants). The projection layer also scans `advanced.extraArguments` for those names and refuses any recipe that contains them. There is no auto-strip and no compatibility shim.

### 6.2 Load-failure vocabulary

When Cookbook reads a recipe file off disk, exactly one of four outcomes can occur. The error labels in the JSON response let you tell them apart:

| Label | HTTP | Meaning | What to do |
| --- | --- | --- | --- |
| _(no error)_ | 200 | File present, JSON parses, `recipeSchemaVersion` matches this broker. The response is the recipe. | Proceed. |
| `recipe_file_missing` | 404 (GET / preview) or 422 (PUT) | The SQLite row says this recipe exists but the `.recipe.json` file is gone from `<Workspace>\Recipes\`. | Either restore the file from `_trash\` (or a backup), or accept the loss and create a new recipe — the SQLite row is stale either way. |
| `recipe_file_malformed` | 422 | The file exists but is not parseable JSON, or parses to something that isn't a JSON object. The `detail` field carries the parser's verbatim message. | Open the `.recipe.json` file in a text editor and fix the syntax. Cookbook will not auto-heal. |
| `recipe_unsupported_schema_version` | 422 | The file parses cleanly, but its `recipeSchemaVersion` is missing, non-numeric, or different from this broker's supported version (the `supportedSchemaVersion` field in the response names it). | This usually means a future broker wrote the file. Stop using a downgraded broker, or hand-edit the file to bring it back into the supported shape. Cookbook does not migrate across versions. |

This taxonomy is enforced symmetrically: GET, PUT, and recipe-id preview all use the same four labels for the same four conditions. PUT will refuse to silently overwrite a malformed or unsupported file.

### 6.3 What recipes deliberately do not provide

These are intentional non-features, not roadmap items. Asking the broker to do any of these will not work today and is not planned for this surface:

- **No raw `.recipe.json` import workflow.** Cookbook does not expose a generic "upload any `.recipe.json` file" endpoint, a bulk recipe-file import surface, or a drag-and-drop drop zone that swallows arbitrary recipe JSON. The supported user-facing way to bring a recipe in from another workspace is **Recipe Takeout** (see §6a) — `Import Recipe Takeout` on the Recipes page validates and sanitizes a `.paxrecipe.json` transport file before creating a brand-new local recipe. Advanced chefs can still back up or inspect the underlying `.recipe.json` files directly under `<Workspace>\Recipes\`, and a hand-placed file will be picked up by the next list view (after the SQLite row is created on the first PUT or on the broker's reconcile pass), but that path bypasses the validate/sanitize/duplicate-name pipeline and is not how chefs are expected to move recipes between machines.
- **No raw workspace recipe bundle export.** Cookbook does not export the entire `<Workspace>\Recipes\` folder, the `cookbook.sqlite` database, or a generic recipe-file bundle through any HTTP route. To move one recipe through the UI, use **Export Recipe Takeout** in the Prep Station (§6a); the broker builds a sanitized `.paxrecipe.json` envelope and the browser downloads it. To back up the entire workspace (recipes, bake history, scheduled tasks, lock state), stop Cookbook and copy the workspace folder with file-system tools — Cookbook does not provide a packaged backup.
- **No cloud sync.** Cookbook does not connect to any remote recipe store, registry, marketplace, or gallery. The only recipe storage is the local file system.
- **No template upload, no template marketplace, no community templates.** The two bundled Pantry templates live under `App\templates\` and are loaded once at broker startup. There is no per-request rescan, no template install path, no remote catalog fetch.
- **No auto-migration.** A recipe authored against `recipeSchemaVersion: 2` (when that exists) will not be migrated by a v1 broker. The chef updates the broker, not the recipe file.
- **No auto-heal for malformed JSON.** A `.recipe.json` file that has been damaged by a text editor stays in its damaged state on disk. Cookbook surfaces the damage; the chef repairs it.
- **No silent stripping of unknown fields.** A recipe with extra keys is rejected at validate time. Cookbook does not "best-effort" the load by discarding what it does not understand.
- **No secret detection / no secret encryption.** Cookbook does not scan recipe content for credential-like substrings, does not encrypt files at rest, and does not warn the chef about embedded secrets. The chef is responsible for keeping secrets out of recipes.
- **No collaborative editing, no live-share, no co-author surface.** Recipes are single-writer artifacts owned by the chef on the local workspace. Two SPAs editing the same recipe simultaneously is undefined behavior (the last write wins; nothing is merged).

### 6.4 Append mode

A recipe can append rows to an existing fact CSV instead of writing a fresh one. Two coupled fields on `destinations.fact` control this:

| Field | Type | Allowed values | When required |
| --- | --- | --- | --- |
| `appendBehavior` | string | `fresh`, `append` | Always optional. Omission means the same as `fresh`. |
| `appendFile` | string (non-empty) | filename or full path | Required when `appendBehavior` is `append`; must be absent otherwise. |

The two values are projected onto the bundled PAX engine as a single switch:

- `appendBehavior: append`, `appendFile: <value>` → PAX is invoked with `-AppendFile <value>`. The value is passed verbatim; PAX accepts either a bare filename (resolved relative to `-OutputPath`) or a full path.
- `appendBehavior: fresh` or omitted → no append switch is added to the command. PAX writes a new fact CSV at `-OutputPath` exactly as before.

Cookbook does **not** emit a separate `-AppendMode` switch and does **not** emit `-OutputFilePath`; the bundled PAX engine declares neither parameter. `-AppendFile` is the only append surface Cookbook currently projects. The bundled PAX engine itself additionally declares `-AppendUserInfo` (active in PAX v1.11.3 — would merge this run's EntraUsers snapshot into a target users CSV and auto-enable `-IncludeUserInfo`) and `-AppendAgent365Info` (declared in the PAX param block but on the temporarily-disabled list in PAX v1.11.3). Cookbook does not surface either of those: there is no schema field, no UI control, and no adapter projection for them. The `AppendAgent365Info` family is additionally on the removed-switch trailer-gate list, so it cannot re-enter via `advanced.extraArguments`. Surfacing `-AppendUserInfo` is a known coverage gap; if a chef needs incremental user-info merge today the only path is to run PAX directly outside Cookbook.

Pitfalls:

- The append target file must already exist and must have the column layout that this bake would produce. PAX raises a runtime error if the file is missing or its header row does not match. Cookbook does not pre-validate the file on disk.
- Append bakes share all the path caveats from §6.1: the value is passed straight to PAX, so a machine-specific path will only resolve on that machine.
- Switching a recipe from `append` back to `fresh` requires clearing the `appendFile` field. The validator rejects a recipe that carries an `appendFile` value while `appendBehavior` is `fresh` or omitted — this is intentional, so an old append target cannot silently re-attach itself the next time the chef flips the behavior.
- The verbatim escape hatch (`advanced.extraArguments`) is **not** a second append surface. Putting `-AppendFile foo.csv` into the trailer would be passed to PAX a second time and PAX would refuse the duplicate. Use the structured fields.

### 6.5 Rollup-blocker pre-validation

Every Cookbook recipe is a rollup run (`processing.rollup` is required and `Rollup` is its only value). The bundled PAX engine refuses to start a rollup run when certain other switches are also present, and Cookbook mirrors that refusal at validate time so the recipe is rejected before any PAX child process is spawned.

A recipe is rejected when its `advanced.extraArguments` trailer contains any of the following switches:

| Switch in trailer | Rule ID | Validator keyword |
| --- | --- | --- |
| `-UseEOM` | `L2.ROLLUP.NO_USEEOM` | `rollupBlockedByUseEOM` |
| `-ExportWorkbook` | `L2.ROLLUP.NO_EXPORTWORKBOOK` | `rollupBlockedByExportWorkbook` |
| `-OnlyUserInfo` | `L2.ROLLUP.NO_ONLYUSERINFO` | `rollupBlockedByOnlyUserInfo` |
| `-OnlyAgent365Info` | `L2.ROLLUP.NO_ONLYAGENT365INFO` | `rollupBlockedByOnlyAgent365Info` |
| `-RAWInputCSV` | `L2.ROLLUP.NO_RAWINPUTCSV` | `rollupBlockedByRawInputCsv` |

A recipe is also rejected when the trailer contains `-ExcludeCopilotInteraction` AND `ingredients.m365Usage.includeM365Usage` is not `true` (rule `L2.ROLLUP.EXCLUDE_COPILOT_REQUIRES_M365_USAGE`, keyword `rollupExcludeCopilotRequiresM365Usage`). The remedy is to flip the M365 usage ingredient on, or to remove the switch from the trailer.

These rules are pre-flight only. Cookbook does **not** validate that the bundled PAX engine will actually accept every other combination — PAX remains the authoritative interpreter of its own switches. Cookbook does not perform L3 reachability checks (network, tenant, Graph API, dataset existence) on a recipe at validate time; those failures surface only at bake time.

### 6.6 Readiness (advisory pre-flight)

The recipe editor and the bake detail view both expose a **Check readiness** button. Clicking it runs a non-mutating, fast pre-flight that bundles the existing pre-bake gates — recipe loadability and schema, the local runtime (PowerShell version, workspace path, free disk, bundled PAX script), the referenced Chef's Key (presence, mode match, secret presence — never the secret itself), the destination path (parent directory writable, append target presence/compatibility), and resume preconditions when invoked from a bake — into a single result. The result is rendered inline as a list of checks with status chips (**ok**, **warning**, **blocked**, **not checked**), each carrying a short detail and a remediation hint.

Readiness is **advisory**:

- **Green readiness does not guarantee a successful bake.** PAX still performs its own auth, reachability, and destination validation at bake time. PAX is the authoritative interpreter; Cookbook readiness only surfaces the conditions Cookbook itself knows how to check.
- **Network and external API reachability are intentionally left as `not_checked`.** Cookbook does not probe Graph, Purview, or any tenant API at readiness time. Reachability of those services depends on the operator's Chef's Key and tenant context; probing them from Cookbook would either require holding live credentials in the broker process or produce misleading results. PAX performs that validation when the bake actually starts.
- **Secrets are never returned.** The auth check reports only whether the configured credential or token is present on disk, never the value.
- **Readiness does not mutate state.** No `cooks` row is created, no checkpoint is touched, PAX is never spawned.
- **The Bake Recipe (and Resume) button blocks only on blockers.** Warnings and `not_checked` results do not refuse the action. If readiness reports one or more blockers, the bake (or resume) button refuses to start the action and tells you how many blockers were found. There is no override flag; resolve the blockers or clear the readiness panel (Refresh on the bake view; edit any field on the editor) and try again.
- **The button is the only trigger.** Readiness never auto-runs, never polls, and never re-runs on edit. Editing a field after a successful readiness check invalidates the displayed result so you do not mistake an old green for a current one.

Readiness is a separate tool from validation. The validator (`§6.1`) enforces structural and L2 rules that Cookbook will never let you bake past. Readiness combines those rules with environmental checks that *might* be wrong on a specific machine at a specific moment (disk full, Chef’s Key relabeled, workspace path moved). The validator is a hard gate; readiness is a flashlight you point at the runway before takeoff.

---

### 6.7 Pantry — using a bundled template

The **Pantry** is the read-only catalog of recipe templates bundled with this install. It is the only place Cookbook surfaces templates, and the only way to create a recipe from a template. From the Pantry you can read what a template produces, see what it requires, preview the recipe that would be built, and create a recipe in one click.

**What the Pantry is.** A catalog browser. Templates are JSON files shipped inside the install, loaded once by the broker at startup, and never refreshed from a network source. The list does not change while the broker is running.

**What the Pantry is not** (cross-link `§6.3`). There is no remote catalog, no marketplace, no community gallery, no template upload, no plugin system, no auto-update of templates, no ratings, no comments. If you do not see a template you want, the install is the surface that ships them.

**How to read a template detail page.** Each detail page surfaces the same blocks in the same order:

| Block | What it tells you |
|---|---|
| Produces | A one-line summary plus the artifacts the resulting bake is expected to emit (fact CSVs, metric JSONs, log files, manual side-data references). Reading these tells you whether the template's outputs match what you need before you commit to creating a recipe. |
| Prerequisites | Which sign-in modes the template supports, which per-instance inputs you will supply, that the template is bundled locally, and that the template does not store any credentials. |
| Required inputs | The exact recipe leaves the template requires from you (one row per leaf with a short description). |
| Limitations | What Create recipe does *not* do — see below. |
| Template defaults | The recipe leaves the template will stamp into the new recipe, shown as a labeled table with a "Show raw JSON" disclosure for exact inspection. |
| Manual guidance | Optional chef notes attached to the template — runtime caveats, manual export steps that happen outside Cookbook, or any other operator-facing instruction the template author chose to surface. |
| Provenance | Where the template came from (bundled), when it was last reviewed, and what template version this is. The resulting recipe records the same template id and version in its own `createdBy.fromTemplate` block. |

**What Preview does.** The Preview recipe button on the create form renders the recipe that *would* be built from the template defaults plus the current form values. Preview is purely client-side: it does not call the broker, does not write a recipe file, does not insert a SQLite row, does not start a bake, does not run readiness, and does not run PAX. Fields that the broker stamps authoritatively (the recipe id, the recipe schema version, the bundled PAX adapter version, creation timestamps, and the `createdBy.user` block) appear as `<server-assigned>` in the preview. Preview is a sanity check — what you read in the preview panel is what the broker will receive, minus the server-assigned fields.

**What Create recipe does.** Submitting the form sends the per-instance inputs to the broker. The broker combines them with the template defaults, validates the result against the recipe schema, writes the recipe file, inserts a SQLite row that records `source = 'template'` and `source_ref = '<templateId>@<templateVersion>'`, and returns the new recipe id. On success the page opens the new recipe in the editor (history is replaced so Back returns to the Pantry list, not the Pantry detail). Create recipe never starts a bake, never runs readiness, and never runs PAX — those remain separate, explicit actions you take on the recipe afterward.

**Provenance, in three places.** A template-created recipe carries its template lineage in three observable locations: the recipe file itself (`createdBy.fromTemplate.{templateId, templateVersion}`), the SQLite recipes row (`source`, `source_ref`), and the Pantry detail page that produced it (its Provenance block). The lineage is recorded once at materialize time and is never recomputed.

**Finding the recipe you just created.** Cookbook opens the new recipe in the editor immediately on a successful Create recipe. If you navigate away before reading the result, the recipe is in the Recipes list under the name you typed; the bake history will later show its `source_ref` if you bake from it.

**Templates and secrets.** Templates do not store any credentials. They declare which sign-in modes (`WebLogin`, `DeviceCode`) the resulting recipe will support, but the actual sign-in happens at bake time through your selected Chef's Key. No part of the Pantry flow asks for a client secret, and no part of the Pantry flow writes a secret anywhere.

**Templates and Chef's Keys.** Materializing a template does not create a Chef's Key. Chef's Keys are managed separately in the Chef's Keys surface (`§12`) and are selected at bake time on the recipe.

**Validation is still required.** A recipe created from a template is the same shape as any other recipe in Cookbook. Before you bake it for the first time you still run the validator (`§6.1`) and the readiness check (`§6.6`), and the bake itself still goes through the same gates as a manually-authored recipe. Template-created does not mean validated, does not mean ready, and does not mean a bake will succeed.

---

### 6.8 Supported run shapes

Cookbook supports exactly three recipe run shapes. The recipe editor projects the form to whichever shape the chef selected; the validator rejects anything else.

| Shape | Description | When to use |
| --- | --- | --- |
| **Default audit run** | A CopilotInteraction audit query against the unified audit log. `query.mode` is omitted (default). `ingredients.m365Usage.includeM365Usage` is false. `destinations.fact` is required. | The most common recipe shape: extract Copilot interaction audit rows for a date window. |
| **M365 usage bundle audit run** | An audit query that also pulls the M365 usage bundle (Exchange + OneDrive + SharePoint audit activity). `query.mode` is omitted. `ingredients.m365Usage.includeM365Usage = true`. `destinations.fact` is required. | When the dashboard needs M365 active-user signals joined with the audit rows. |
| **User-info-only run** | Skips the audit query entirely; runs only Entra user / license enrichment. `query.mode = 'userInfoOnly'`. `destinations.userInfo` is required; `destinations.fact` is forbidden. | When you need a fresh user / license snapshot without spending any audit-log quota. |

All three shapes go through the same validator, projection layer, and PAX engine. The differences are purely in which fields the recipe carries; nothing about how Cookbook runs PAX changes shape-by-shape.

### 6.9 Fact output mode (OutputPath vs AppendFile)

A recipe&rsquo;s `destinations.fact` block writes the fact CSV in exactly one of two modes. The two modes are **mutually exclusive** &mdash; a recipe carries one at a time.

| Mode | What it does | Required fields |
| --- | --- | --- |
| **OutputPath** | PAX writes a new fact CSV at the supplied path. Existing recipes that predate the mode discriminator continue to behave as OutputPath. | `destinations.fact.path` |
| **AppendFile** | PAX appends rows to an existing fact CSV. The append target file must already exist and must have the column layout this bake would produce. | `destinations.fact.appendFile` |

The S26 schema accepts an explicit `destinations.fact.mode` discriminator (`'outputPath'` or `'append'`). The `destinations.fact.appendBehavior` field is also accepted: when set to `'append'`, it maps to `mode = 'append'`. Recipes that specify only an OutputPath are accepted as-is and behave as OutputPath.

The validator refuses recipes that carry both `path` and `appendFile`, and refuses recipes that omit both under a shape that requires a fact destination.

### 6.10 User-info output mode (OutputPathUserInfo vs AppendUserInfo)

When a recipe enables Entra user-info enrichment, the user/license rows can be sent to one of three destinations:

| Mode | What it does |
| --- | --- |
| **Default** | User-info rows ride along with the fact output. No separate user-info file. |
| **OutputPathUserInfo** | PAX writes a separate fresh user-info CSV at the supplied path. |
| **AppendUserInfo** | PAX appends user-info rows to an existing user-info CSV. |

The path and append destinations are **mutually exclusive**. The validator refuses recipes that carry both `path` and `appendFile` on `destinations.userInfo`.

Choosing **OutputPathUserInfo** or **AppendUserInfo** requires user-info enrichment permissions because the destination causes user-info output: `User.Read.All` and `Organization.Read.All`. The recipe editor pairs the destination with `ingredients.entraUserData.includeUserInfo = true`; the permissions rail then shows the required scopes.

### 6.11 User-info-only run shape

The **user-info-only** shape is selected via `query.mode = 'userInfoOnly'`. It is a separate run shape, not an option layered onto an audit run.

A user-info-only recipe:

- Requires `destinations.userInfo` (either OutputPathUserInfo or AppendUserInfo).
- Requires `ingredients.entraUserData.includeUserInfo = true` (the editor enforces this).
- Requires `User.Read.All` and `Organization.Read.All`.
- Does **not** require `AuditLogsQuery.Read.All` (no audit query runs).

A user-info-only recipe forbids:

- Audit window dates (`query.startDate`, `query.endDate`).
- A fact destination (`destinations.fact`).
- Rollup mode (`processing.rollup`).
- `ActivityTypes`.
- `UserIds`.
- `GroupNames`.
- Agent filters.
- `PromptFilter`.
- The M365 usage bundle (`ingredients.m365Usage.includeM365Usage` must be false; `includeCopilotInteraction` must be absent).

The validator emits explicit keyword errors for each of these violations so the chef can correct the recipe before save.

### 6.12 M365 usage bundle and ExcludeCopilotInteraction

The **M365 usage bundle** is selected via `ingredients.m365Usage.includeM365Usage = true`. It tells PAX to include Exchange, OneDrive, and SharePoint audit activity alongside the CopilotInteraction rows.

The bundle exposes one optional toggle: `includeCopilotInteraction`. When it is set to `false`, PAX is invoked with `-ExcludeCopilotInteraction`, which omits CopilotInteraction audit data from the bundle while still pulling Exchange / OneDrive / SharePoint activity.

`ExcludeCopilotInteraction` is **only valid when `includeM365Usage = true`.** The validator rejects a recipe that sets `includeCopilotInteraction = false` while `includeM365Usage` is not true (`rollupExcludeCopilotRequiresM365Usage`). The toggle does not affect required permissions; the M365 usage bundle audit permissions remain required.

### 6.13 ActivityTypes, UserIds, and GroupNames

| Field | Shape in S26 | Permission impact |
| --- | --- | --- |
| `query.activityTypes` | Supported only as the constrained `['CopilotInteraction']` value. `RecordTypes` and `ServiceTypes` are deliberately not exposed and are blocked at trailer-gate. | No new Graph scopes beyond the base audit query. |
| `query.userIds` | Limits audit results to the listed UPNs / object ids. | No new Graph scopes beyond the base audit query. |
| `query.groupNames` | Expands the listed Entra groups to their members and limits audit results to those members. | Requires `GroupMember.Read.All` plus the approved user/organization read permissions (`User.Read.All`, `Organization.Read.All`) for the resolved members. |

`GroupNames` is the only one of the three that adds Graph scopes. `UserIds` and the constrained `ActivityTypes` do not.

### 6.14 Agent filters

An audit recipe can carry at most one `query.agentFilter`. The three filter modes are **mutually exclusive**:

| Mode | What it does |
| --- | --- |
| `agentIds` | Limits the audit query to interactions with the listed agent ids (`agentIds` list required). |
| `agentsOnly` | Keeps only interactions that involved an agent. |
| `excludeAgents` | Removes interactions that involved an agent. |

The agent filters are **active filters on the existing audit query**. They are not the Agent365Info catalog/export surface. The Agent365Info switches (`IncludeAgent365Info`, `OnlyAgent365Info`, `OutputPathAgent365Info`, `AppendAgent365Info`) remain disabled / unsupported in this Cookbook version; the schema does not expose them and the verbatim trailer gate refuses them.

Agent filters do not add Graph scopes beyond the base audit query and do not remove base audit permissions.

### 6.15 PromptFilter

`query.promptFilter` limits audit results by which prompt / response fields are present in the row. Allowed values: `Prompt`, `Response`, `Both`, `Null`.

> **Privacy / governance caution.** This filters by prompt/response presence. Use intentionally.

`PromptFilter` does **not** add Graph scopes. It is a content filter on data the audit query is already authorized to read.

### 6.16 ClientCertificatePath

`auth.clientCertificatePath` selects certificate-based authentication for `auth.mode = 'AppRegistrationCertificate'`. It changes auth mechanics only and does not change Graph scopes.

Cookbook surfaces a certificate file path only for the **passwordless** PFX case (a PFX with no protecting password). Password-protected PFX files require future secure secret storage; Cookbook does not yet have a UI for that and deliberately refuses any path that would put a certificate password into the recipe JSON or onto the bake&rsquo;s argv.

For password-protected certificates today, use the thumbprint / certificate-store auth mode instead. Cookbook never writes a certificate password to disk or to argv.

### 6.17 Unsupported / blocked switches

The recipe schema does not expose the following switches as leaves, and the verbatim trailer (`advanced.extraArguments`) is scanned at validate time and rejects any recipe that carries them:

- `RecordTypes`
- `ServiceTypes`
- `IncludeAgent365Info`
- `OnlyAgent365Info`
- `OutputPathAgent365Info`
- `AppendAgent365Info`
- `UseEOM`
- The removed PAX v1.11.2 switches: `ExportWorkbook`, `ExplodeArrays`, `ExplodeDeep`, `RawInputCSV`.
- Tuning switches not in Cookbook v1.

Blocking is enforced before any bake is spawned. The validator errors name the path and the blocked switch so the chef can correct the recipe.

---


## 6a. Recipe Takeout

Recipe Takeout is the chef's mechanism for moving a single recipe definition between Cookbook workspaces. It exists so a recipe authored on one machine can be brought to another — a different chef's workstation, a fresh install, a backup folder — without re-typing the recipe or re-deriving its options. It is **recipe-definition transport only**, not a workspace backup; see §6a.6 for the full exclusion list.

### 6a.1 What Recipe Takeout is, and is not

Recipe Takeout is:

- An on-demand export of one recipe's editable definition (identity, query, ingredients, advanced switches, schedule shape) into a single transport file named `<slug>.paxrecipe.json`.
- An on-demand import of a `.paxrecipe.json` transport file into the current workspace as a brand-new local recipe.

Recipe Takeout is **not**:

- A backup of the workspace, the `cookbook.sqlite` database, the `Cooks\` tree, or anything else outside the recipe-definition envelope.
- A live link between workspaces. The transport file is a snapshot at the moment of export; nothing the importer does flows back to the exporting workspace.
- A way to move credentials. Chef's Keys, client secrets, certificates, refresh tokens, access tokens, ID tokens, bearer tokens, and Credential Manager target names are all excluded from the envelope at export time. See §12 for the Chef's Keys surface itself.
- A roaming recipe identifier. The imported recipe always gets a **fresh** local recipe identifier on the importing workspace; the source workspace's identifier is not reused.

### 6a.2 Export flow

1. Open a saved recipe in the **Prep Station**. If the editor shows unsaved changes, save first — the export button is disabled while the recipe is dirty.
2. Click **Export Recipe Takeout** in the Prep Station header.
3. The broker builds the takeout envelope, runs it through the sanitizer (which fails closed on any forbidden field), and the browser downloads `<slug>.paxrecipe.json` to your normal Downloads location.

The downloaded filename is a deterministic slug of the recipe's display name plus a short identifier component. The slug exists so different exports from the same machine do not clobber each other in the Downloads folder. It is convenience for the file picker; **it is not the recipe name**. The recipe name lives inside the envelope.

If you rename the downloaded file before sending it on (`my-team-report.paxrecipe.json`, `audit-2025-q4.paxrecipe.json`, `random-name.json`, anything you like), Recipe Takeout still imports it. The importer reads identity from the envelope contents, not from the filename.

### 6a.3 Import flow

1. Open the **Recipes** page.
2. Click **Import Recipe Takeout** and pick the `.paxrecipe.json` file. (Any filename is fine.)
3. Cookbook validates the envelope and shows a preview card: the proposed recipe name, the included options, and the date window.
4. Review the **Recipe name** input above the import button. This is the display name the new recipe will have in your workspace. If a recipe with the same name already exists in your workspace, the input pre-fills with the next free suggestion (see §6a.4).
5. Edit the name if you want. The name in this input is the authority on the imported recipe's name — the source name in the envelope is advisory only.
6. Click **Import**. On success, click **Open Recipe** to land in the Prep Station for the imported copy.

The import is a single explicit action. Cookbook does not auto-import on file drop, does not maintain an "import history" surface, and does not re-import the same file on a later launch.

### 6a.4 Recipe names and duplicate detection

Recipe Takeout uses the **recipe display name** for duplicate detection. There is no content hash, no envelope hash, no source-identifier ledger.

When the validate step runs on the import side, Cookbook:

1. Reads the proposed name from the envelope.
2. Walks the list of recipes already in the current workspace.
3. If the proposed name is unused, the name input pre-fills with the proposed name and import proceeds normally.
4. If the proposed name is already taken, the name input pre-fills with the next free Windows-style suggestion — `Name (1)`, `Name (2)`, … up to `Name (99)` — and the preview card flags that a collision was found.

If you click **Import** with a name that is in fact still in use (for example because the same window was open in two browser tabs), the broker returns `409 recipe_name_conflict` with a `nextSuggestion` field that tells the wizard which name to pre-fill next. The wizard surfaces this as an inline message; nothing on disk has been written.

You can deliberately import the **same takeout file multiple times** under different explicit names. Each import is an independent new recipe with its own fresh local identifier. This is the supported way to use one envelope as a starting point for several variants.

### 6a.5 Chef's Keys and Needs Prep

Chef's Keys are never included in a takeout envelope (see §6a.6). If the source recipe used an app-registration Chef's Key, the imported recipe arrives in **Needs Prep** on the importing machine:

- The auth card shows the original `auth.mode` (for example, `AppRegistrationSecret`) and the original tenant ID. These two values are preserved as advisory metadata so the importing chef knows what kind of Chef's Key to bind.
- The Chef's Key binding itself is empty.
- The Bake Recipe button is disabled while the recipe is in Needs Prep; Cookbook refuses to spawn a bake that has no resolved credential.

To take an imported app-registration recipe out of Needs Prep:

1. Open the imported recipe in the Prep Station.
2. In the auth card, pick a local **Chef's Key** that matches the tenant the recipe expects. If no suitable key exists yet, open the **Chef's Keys** page (§12) and add one first, then return.
3. Save the recipe. Saving binds the local Chef's Key into the recipe's `auth.authProfileId` slot on this machine. The Needs Prep banner clears immediately.

Recipes that use **device-code** or **interactive WebLogin** sign-in do not need a Chef's Key on disk, so they do not land in Needs Prep. They will prompt for sign-in at bake time exactly as a freshly-authored recipe would.

### 6a.6 What the envelope excludes

The sanitizer excludes every field below by category, and the broker re-checks the envelope at import time so a hand-edited file with secret-shaped data is refused before any disk write:

- Chef's Key bindings, client secrets, certificates, certificate base64 / PFX bytes, private keys, passwords, passphrases.
- Refresh tokens, access tokens, ID tokens, bearer tokens, API keys, connection strings.
- Windows Credential Manager target names.
- The `Cooks\` folder, individual `cookId` directories, and every artifact inside them (bake logs, PAX logs, output CSV, sentinels).
- The `cookbook.sqlite` database and every row in it.
- Runtime lock state, session state, and the broker's per-launch session token.
- The trust allowlist, package trust state, and update trust files.
- The source workspace's local recipe identifier.

The fact that a category is excluded is recorded in the envelope's own `excluded` field as a metadata enumeration (so a reviewer can confirm at a glance which categories were sanitized). The category names appear there as documentation; they are never the actual values.

### 6a.7 File independence after import

After a successful import the imported recipe is **a normal local recipe** in your workspace. It is in `cookbook.sqlite`, the recipe JSON is in `<Workspace>\Recipes\`, and it appears on the Recipes page next to recipes you authored locally.

Specifically, after import:

- You can delete the original `.paxrecipe.json` file. The imported recipe keeps working.
- You can move the original `.paxrecipe.json` file. The imported recipe does not follow it.
- You can re-import the same `.paxrecipe.json` later under a different name. That produces a second, distinct recipe.
- You can re-export the imported recipe (it is just a saved recipe) and the new export is a fresh envelope built from the recipe as it now stands in your workspace.

There is no live dependency on the transport file, and Cookbook does not record the file's name or path against the imported recipe row.

### 6a.8 Safety model

Recipe Takeout is designed to fail closed on anything that looks like a credential.

- **Export side.** The sanitizer is an allow-list shape with a forbidden-field deny-list overlay. If the source recipe somehow carries a value in any forbidden field (a corruption, a hand edit, a developer mistake) the sanitizer refuses to build the envelope and surfaces `takeout_sanitization_failed` or `takeout_secret_leak_detected`. The export is aborted; no file is written.
- **Validate / import side.** The broker re-runs the same forbidden-field check on the inbound envelope and refuses to import a file that contains any of them, even if the file was hand-edited after export. The wizard surfaces `takeout_contains_forbidden_secret_field` and names the offending field.
- **Schema check.** The envelope is JSON-schema-validated by the broker on both validate and import. Unknown top-level fields, missing required fields, an unsupported `schemaVersion`, or a wrong `kind` value all produce a named error before anything is written.
- **No silent overwrites.** Import always lands a new recipe with a fresh local identifier; it never overwrites the recipe Cookbook believes is the source. The name-conflict check (§6a.4) catches collisions on the display name before disk write.

These guardrails are the reason Recipe Takeout can be moved freely between workspaces without a separate review step.

---


## 7. Bake lifecycle

A **bake** is one execution of one recipe. The on-disk and database identifier for a bake is `cookId`; this guide uses *bake* in prose and the literal `cookId` / `Cooks\` / `cook.log` when referring to storage and log surfaces.

| Stage | What happens | Where it lives |
| --- | --- | --- |
| Start | You click **Bake Recipe** in the SPA. The broker spawns a child PowerShell process that runs the bundled PAX script with the expanded arguments. | A row is inserted into `cookbook.sqlite` (table `cooks`); a directory is created at `<Workspace>\Cooks\<cookId>\`. |
| Run | The child process streams stdout/stderr. The SPA renders it live via an embedded terminal. | Logs are written to `<Workspace>\Cooks\<cookId>\` in real time. |
| Output | PAX writes whatever output the recipe requested (CSV, JSON, SQLite, etc.). | PAX-determined paths — typically inside the bake's output folder or wherever the recipe pointed. |
| Complete | The child process exits. Cookbook records the exit code, runtime metrics, and a terminal sentinel. | The `cooks` row in `cookbook.sqlite` is updated to a terminal state. |
| Interrupted | One of three things happened: you clicked **Stop** on the bake detail page; the broker process exited or the machine rebooted mid-bake; or the bake was force-killed. The broker reconciles whichever pathway applies and marks the bake **interrupted**, recording the closure cause in `closure_reason`. | Sentinel files in the bake's output folder; the `cooks` row reflects the reconciled state. Section 7.2 explains which interrupted bakes can be resumed and which cannot. |

Cookbook never re-runs an interrupted bake automatically. It is always a chef decision whether to **Resume** a soft-stopped bake from its checkpoint (see §7.2) or to start a fresh run.

Bake output folders are append-only during execution and read-only after completion. You may delete old bake folders whenever you want; Cookbook will reflect their absence in the Bakes list but will not regenerate them.

### 7.1 Bake history and artifacts

Each finished bake is a complete record on disk. The **Runs** list (and any open bake's detail view) surfaces what is actually there — Cookbook does not reconstruct or interpolate anything after the fact.

Three categories of files commonly appear inside a bake's output folder:

| File | Origin | What it carries |
| --- | --- | --- |
| **Fact** | PAX. The recipe's output target (CSV, Parquet, SQLite, etc.). | The actual data the recipe produced. Recorded as the single canonical artifact in the `cook_artifacts` table; its path is stored as the artifact's `location`. |
| **`pax-summary.json`** *(optional)* | PAX, if a future version emits one. Not currently produced by the bundled PAX script. | A short summary intended for fast inspection (status, counts, warnings). When absent the detail view shows "Not emitted for this bake." — this is normal. |
| **Metrics JSON** *(optional)* | PAX, when invoked with `-EmitMetricsJson`. The file name is `<factBaseName>_metrics_<runTimestamp>.json` or, in older runs, `metrics.json`. | Per-run telemetry: row counts, query/export milliseconds, parameters used. The bundled PAX script writes this beside the fact destination. |

The bake detail view shows three things derived from those files:

1. **Summary & metrics card** — chips for `Rows produced`, `Output files`, `Warnings`, `Errors`, and `Duration (sec)` when the corresponding key is present in either JSON. Missing keys are simply omitted, not shown as zero. The raw JSON is rendered verbatim in collapsible blocks underneath the chips, with the file path the broker actually read from.
2. **Discovered output files table** — additional output-shaped files the broker observed in the bake's output folder (everything except the metadata files Cookbook itself writes) and beside the fact destination (files matching the fact base name plus any `metrics*.json` and `pax-summary.json`). The table is read-only and exists purely to make on-disk artifacts visible; nothing here is part of the canonical artifact row.
3. **Rows / Artifacts columns in the Runs list** — when the broker was able to parse a row count out of the metrics JSON at bake-finish, it is stored on the bake's fact artifact and surfaced as `Rows`. `Artifacts` is the count of `cook_artifacts` rows for the bake (typically 1 — the fact). Missing values render as an em-dash; an explicit zero renders as `0`.

**Failure semantics — important.** Missing, empty, or malformed JSON files **never** fail a bake:

- If the metrics JSON is absent, the bake is still recorded with whatever status PAX exited with; the summary card shows "Not emitted for this bake." and `Rows` is em-dash.
- If the metrics JSON exists but is unparseable, the bake is still terminal-success (assuming PAX itself succeeded); the summary card shows the parse error verbatim and the file path it tried to read; `Rows` is em-dash.
- If a key the chips depend on is present but non-numeric, that chip is omitted; other chips still render.

This is deliberate. A bake's lifecycle is owned by PAX's exit code and the broker's sentinel reconciliation. Summary and metrics are decoration on top of that contract; they cannot break it.

**Troubleshooting.**

- *Summary card says "Not emitted" but I expected metrics.* Confirm the recipe's PAX invocation includes `-EmitMetricsJson`. The recipe snapshot card on the same page shows the frozen argv used for the bake.
- *Metrics file path looks wrong.* The broker probes, in order: `<BakeFolder>\pax-summary.json`, `<BakeFolder>\metrics.json`, `<FactDestDir>\metrics.json`, then `<FactDestDir>\<factBaseName>_metrics_*.json` (most recent by modified time). The first existing file wins. The path shown in the card is the one the broker actually read from.
- *Discovered output files table is empty even though the fact exists.* The fact itself is shown above in the **Outputs** card (the bake's authoritative artifact row). The discovered table only adds *additional* on-disk files; if the fact is the only one, the discovered table is correctly absent.
- *`Rows` shows em-dash even though the metrics file has a row count.* The broker probes a small set of universally-named keys (`rowsWritten`, `rows`, `totalRows`, `records`, `recordCount`, `TotalStructuredRows`, `TotalRecordsFetched`, and a few nested variants). If a custom PAX wrapper uses a different key, the per-bake detail view still renders the full JSON; only the at-a-glance `Rows` column is blank.

### 7.2 Stopping, interrupting, and resuming a bake

Cookbook distinguishes three terminal-but-not-completed states. Knowing which one a bake is in tells you whether anything can be resumed.

| State | What caused it | Resumable? |
| --- | --- | --- |
| **interrupted** *(soft-stop)* | You clicked **Stop** on the bake detail page. The broker sent the PAX process a cooperative cancel signal; PAX wrote a checkpoint file (`.pax_checkpoint_<RunTimestamp>.json` inside the output directory) and exited cleanly. | **Yes**, if a checkpoint file is still on disk. The `cooks` row carries `closure_reason = cancel_stop` (or `cancel_stop_escalated_kill` if the soft-stop did not exit in time and was escalated). |
| **interrupted** *(broker restart)* | The broker process exited mid-bake (machine reboot, broker crash, broker upgrade). On next startup the broker reconciled the orphan sentinels and marked the bake **interrupted**. | **Yes**, if PAX got far enough to write at least one checkpoint before the broker died. The `cooks` row's `closure_reason` starts with `broker_restart_`. |
| **interrupted** *(force-kill)* | You clicked **Kill** on the bake detail page, or the broker had to escalate to a hard kill for a reason other than soft-stop timeout. PAX was terminated abruptly and may or may not have written a checkpoint depending on where it was in its main loop. | **No** — Cookbook will not resume a force-killed bake even if a checkpoint exists on disk. The `cooks` row carries `closure_reason = cancel_kill`. |

**The Resume button.** When the broker confirms a bake is resumable, the bake detail page shows a **Resume** button next to the (now hidden) Stop button, the status detail line reads `interrupted — resumable`, and the Runs list adds a small `resumable` marker next to the **interrupted** chip.

Clicking **Resume** asks the broker to spawn a *new* bake that re-attaches to the parent's checkpoint file:

- A new `cookId` is allocated; a new directory is created at `<Workspace>\Cooks\<recipeId>\<newCookId>\`.
- The new `cooks` row records `parent_cook_id = <previous cookId>` and `trigger = 'resume'`. Lineage is preserved across the database; the parent row itself is **not** mutated.
- The recipe snapshot is copied verbatim from the parent — the resume run uses the exact same frozen recipe as the interrupted bake. There is no opportunity to edit anything mid-resume.
- The PAX invocation appends `-Resume <checkpointPath> -Force`. Authentication overrides (tenant, client id, app-registration secrets / certificate thumbprints) are re-applied from the snapshot's Chef's Key; nothing else changes.
- The Resume action goes through the same per-operation re-auth gate as a normal Bake (you will be prompted for Windows Hello / PIN every time).

**Why a new bake instead of mutating the old one.** Bake output folders are append-only and `cooks` rows are immutable evidence. Re-using either would destroy the truth of what happened on the interrupted run. The new bake is a separate, fully recorded run that *references* the parent via `parent_cook_id`. You can navigate parent → child by opening either bake's detail page.

**Why Cookbook still never auto-resumes.** Resume is always a deliberate operator decision. Cookbook never decides on your behalf that an interrupted run should continue — not on broker startup, not on schedule, never. This is doctrine, not a limitation. If a bake was interrupted for a reason you have not yet diagnosed (a Purview policy change, a quota exhaustion, a network outage), auto-resuming would just re-encounter the same failure with less context. Cookbook's job is to make the manual resume cheap and truthful; the decision is yours.

**Resume refusal vocabulary.** If a bake *looks* resumable in the list view but the broker refuses the Resume action when you click, the bake detail page will surface one of these reasons:

- *`checkpoint_missing`* — the checkpoint file existed when the row was projected but is no longer on disk. Most often this means somebody (or another tool) deleted the bake's output folder. The Resume action is permanently gone for that lineage; the parent row stays `interrupted`.
- *`closure_reason_excluded`* — the row was force-killed (`cancel_kill`) or terminated for some other reason that is not allowlisted for resume. The button should not have been visible; refresh the page to re-fetch the projection.
- *`recipe_busy`* — another bake for the same recipe is currently running. Wait for it to finish (or stop it) before resuming.
- *`recipe_invalid`* — the parent's recipe snapshot is not in `executionMode = local-manual`. Cookbook will not resume scheduled or batch-mode runs from the SPA. (This refusal is rare; the parent must have been created manually for the Resume button to have ever appeared.)
- *`insufficient_disk_space`* / *`workspace_path_too_long`* — the same pre-spawn checks that gate a fresh Bake also gate a Resume.

In every refusal case, **no new `cooks` row is created and no new folder is left on disk**. The broker is conservative by design: a resume is a fully spawned child or nothing.

### 7.3 Scheduling (Windows Task Scheduler)

A recipe can be registered with **Windows Task Scheduler** so that the same projection runs unattended on a recurring cadence — daily at a fixed time, or weekly on a chef-selected set of days. The doctrine here is narrow on purpose and worth reading before you click **Save schedule**.

**The three roles.**

- **PAX** is the execution engine. Whether a bake is started by a chef click or by Task Scheduler, PAX runs unchanged with the exact same projection (the frozen argv set, redacted-where-secret, hashed for drift detection).
- **Windows Task Scheduler** is the scheduler. It owns the firing decision, the next-run time, the run history Windows shows in Task Scheduler MMC, and any credential material the task action carries. Cookbook does not run a scheduler loop and never polls a tick to decide whether to fire a bake.
- **Cookbook** authors the recipe, registers / updates / deletes one task per recipe under `\PAXCookbook\<recipeId>\`, hosts the wrapper script Task Scheduler invokes at fire time, and imports the resulting bake into the local SQLite database the same way a manual bake is imported. Cookbook owns nothing about *when* the task fires.

**The Schedule card (recipe editor).** The Schedule card is visible only in edit mode (a saved recipe) and is enabled only when **both** of these are true:

- `executionMode = local-manual`. Other execution modes are reserved for future work and cannot be scheduled.
- The recipe's Chef's Key uses `AppRegistrationSecret` or `AppRegistrationCertificate`. **WebLogin / DeviceCode / ManagedIdentity are refused** — the first two require interactive user presence (no chef sits in front of the task at 3 a.m.), and ManagedIdentity is not in scope for V1. The card explains which rule fired when it is disabled.

**Recurrence.** Daily fires once a day at the chosen hour and minute in the chef's local time zone. Weekly fires at that hour and minute on each selected day of week (Sun=0 … Sat=6). At least one day must be selected for weekly. There is no calendar control, no cron expression, no business-day awareness, no holiday list, no time-zone picker — by design.

**Secret behavior (one-shot, every save).** For `AppRegistrationSecret` recipes, the Schedule card asks for the **client secret** on every save (create *and* update). This is not a UI lapse: Cookbook hands the plaintext to Windows Credential Manager via `Set-AuthProfileSecret` immediately and discards the plaintext in the same call. The broker never short-circuits to a "secret already bound, skip prompt" path; every `PUT /api/v1/recipes/<id>/scheduled-task` re-binds CredMan. If you have not entered a secret in the form, the save is refused with `auth_profile_secret_missing` and no task is created or updated. **Delete never prompts** — removing a scheduled task does not touch the secret material.

`AppRegistrationCertificate` recipes are preferred over secret-based ones whenever the AAD application is set up for it: certificate auth lets the wrapper authenticate with no secret on the wire and no credential rebind step on every save. The Schedule card omits the secret field entirely for certificate recipes.

**What the registrar writes to Windows.** The task is created with `New-ScheduledTask`/`Register-ScheduledTask` from the only file in the codebase that imports the `*-ScheduledTask` cmdlets: `app/install/Register-PAXScheduledRecipe.ps1`. The action is `pwsh.exe -File <InstallRoot>\launcher\Invoke-PAXScheduledRecipe.ps1 -RecipeId <ulid>`. The wrapper resolves the recipe and the bundled PAX script at fire time; the task action carries **no broker URL, no broker token, no redacted argv, and no demo identifiers**. `StartWhenAvailable` is `false` — Windows will not catch up on missed runs after a long sleep or shutdown; the chef sees the gap in the runs list and decides whether to bake manually.

**Stale schedules.** Cookbook stores a SHA-256 hash of the redacted projection (PAX script version, then the full argv with secret-bearing switches replaced by `<REDACTED>`) at registration time. On every GET of the scheduled-task and on every wrapper fire, Cookbook compares stored vs. live. If they differ, the schedule is reported **stale** in the Schedule card and the wrapper refuses to invoke PAX with exit code 30 and refusal name `refused_stale_projection`. To clear staleness: open the recipe, review the change, and click **Save schedule** again (re-entering the secret if the recipe uses one). A stale schedule is a deliberate fence — re-saving is the only way to confirm the new projection is what the chef wants Task Scheduler to fire.

**Where the run shows up.** When the wrapper completes (success or failure), Cookbook imports the run into the local database exactly the same way a manual bake is imported — a `cooks` row with `trigger = 'scheduled'` and `schedule_id = <scheduled_task row id>`, a per-bake directory under `<Workspace>\Cooks\<cookId>\`, the PAX log discovered in the fact directory (`Purview_Audit*.log`, excluding the interim `_PARTIAL.log` form), and any `pax_metrics` artifact PAX emitted. The bake is visible in the recipe's **Recent runs** rail and in the global Runs list with the same fields as a manual bake.

**Reconciliation timing (when imports actually happen).** The wrapper never calls Cookbook at fire time. Instead, the wrapper writes its evidence directly into `<Workspace>\Cooks\<cookId>\` — `wrapper-started.json` at spawn, then either `wrapper-finished.json` after PAX exits or `wrapper-refused.json` if the wrapper refused to invoke PAX (for example because the schedule went stale). Cookbook reads those three envelope files lazily, on two occasions: (1) when the broker starts, it scans every workspace folder under `<Workspace>\Cooks\` and imports any scheduled run that is not yet in the `cooks` table; (2) immediately before serving `GET /api/v1/cooks` — i.e., every time the Runs page is loaded or refreshed. The Runs page therefore reflects on-disk truth without a polling loop and without a watcher process. The reconciler is bounded: it imports at most 256 new folders per call and never throws.

**Trigger chip in the Runs list.** Scheduled-task imports are visible alongside manual and resume bakes in the Runs list. The Recipe column carries a small `scheduled` chip prepended to the recipe name so the chef can tell at a glance which rows came from a wrapper folder rather than a chef click; `resume` bakes carry an analogous chip. Manual bakes have no chip. The chip is informational; the Bake ID, Status, and PAX-script-version columns remain authoritative.

**Scheduled-run evidence in the bake detail.** Opening a scheduled bake reveals a **Scheduled run** card immediately under the Status card. The card surfaces, verbatim, the operationally interesting fields from the wrapper envelope files — `scheduledTaskId`, `windowsTaskName`, `recipeProjectionHash`, the at-fire-time Chef's Key and execution mode, the bundled PAX script and version that fired, wrapper started/finished timestamps, `wrapperOutcome` (`pax_ok`, `pax_nonzero`, `spawn_failed`, or `wrapper_internal`), `wrapperReason`, the PAX exit code, the wrapper-recorded duration in milliseconds, and the wrapper-recorded PID. If the wrapper refused to invoke PAX, the card also shows `refusedAt`, the refusal `reason` (e.g. `refused_stale_projection`), and the refusal `detail` payload. Manual and resume bakes do not carry wrapper envelope files and the card stays hidden for them.

**Synthesised recipe snapshot.** Manual bakes write a `recipe-snapshot.json` into the bake folder at spawn time; the wrapper does not. To keep the `cooks.recipe_snapshot_json` column truthful for scheduled rows, the importer synthesises a minimal snapshot whose top-level marker is `reconciledFromWrapperEnvelope = true`. The synthesised snapshot carries the recipe identity columns from the live `recipes` row (read at import time) and every `*AtFire` field the wrapper recorded into `wrapper-started.json` (`paxScriptPath`, `paxScriptVersion`, `authProfileId`, `authMode`, `recipeProjectionHash`, `executionMode`, `windowsTaskName`, `wrapperHost`, `wrapperUser`, `startedAt`). The synthesised snapshot is NOT an authoritative re-creation of what the chef would have edited in the Recipe editor — it is a forensic record of what the wrapper saw at fire time. The marker is the operator's signal to read it as such.

**Idempotency and re-import.** The reconciler uses `INSERT OR REPLACE` on the `cooks` row keyed by `cook_id`, and a select-then-insert pattern on `cook_artifacts` keyed by `(cook_id, stream, location)`. Running the reconciler many times against the same wrapper folder produces the same database state — no duplicate rows, no duplicate artifacts. The `scheduled_tasks.last_imported_cook_id` / `last_imported_at` columns advance only when the candidate's wrapper-recorded `startedAt` is strictly later than the existing watermark, so a late-arriving older folder cannot regress the watermark.

**Malformed wrappers.** If a folder under `<Workspace>\Cooks\` looks like a scheduled-task bake but the envelope files are corrupted (invalid JSON, missing required fields, or the folder name does not match the ULID shape), the reconciler skips the folder, increments a `skippedMalformed` counter in the import summary, and never writes a partial `cooks` row. A missing `wrapper-finished.json` for a folder whose wrapper-recorded process is still alive produces a row with `status = 'running'`; a missing `wrapper-finished.json` whose wrapper-recorded process is no longer alive after a grace window produces a row with `status = 'interrupted'` and `error_class = 'wrapper_orphan_classified'`. The chef sees the row in the Runs list with the matching status chip; the per-bake detail surfaces the envelopes Cookbook did manage to parse.

**Important boundaries.**

- Cookbook **does not** alter PAX. The bundled engine at `app/resources/pax/PAX_Purview_Audit_Log_Processor.ps1` is the same script for scheduled and manual bakes. No new flags, no scheduler-only branches.
- Cookbook **does not** retain the client secret on disk. Plaintext lives only in the request body and in the in-memory SecureString long enough to bind Credential Manager. The CredMan target name is `PAXCookbook.AuthProfile.<authProfileId>.ClientSecret`.
- Cookbook **does not** make any claim about what Windows Task Scheduler or Windows itself retain. The task action and any credential material the OS associates with it are managed by Windows. Certificate auth avoids the question entirely; secret auth carries the standard caveat that Windows may persist credential material outside Cookbook's purview.
- Cookbook **does not** auto-create or auto-delete scheduled tasks. The chef creates them by clicking **Save schedule** and removes them by clicking **Delete schedule**. The installer creates none. The uninstaller removes none — see §14.

**Scheduled-task health (the Schedule card pill).** The Schedule card opens with a small **health pill** that gives the chef an at-a-glance signal about the registered task. The pill is computed by the broker on every `GET /api/v1/recipes/<id>/scheduled-task` from data Cookbook already owns — the `scheduled_tasks` row, the live projection hash, and the bake history Cookbook has already imported. The broker **does not** call Windows Task Scheduler cmdlets to compute the pill; the boundary that Cookbook owns nothing about *when* Windows fires the task is preserved. The pill takes exactly one of these eight values, evaluated in priority order (first match wins):

1. **Not registered** — there is no `scheduled_tasks` row for this recipe. Click **Save schedule** to register one.
2. **Stale** — the live projection hash differs from the hash that was stored at registration time. The wrapper will refuse to invoke PAX (exit 30, `refused_stale_projection`) until the chef re-saves. Open the recipe, review the change, click **Save schedule**.
3. **Last run refused** — the most recent terminal scheduled bake was refused (typically `refused_stale_projection`). Update / re-register the scheduled task.
4. **Last run failed** — the most recent terminal scheduled bake failed. The pill is paired with a one-line message that names the wrapper outcome class (`pax_nonzero_exit`, `wrapper_spawn_failed`, `wrapper_internal_error`); the chef opens the run from the **Last terminal scheduled run** link and inspects the PAX log + wrapper envelopes in the bake detail.
5. **Last run interrupted** — the most recent terminal scheduled bake is interrupted. The most common cause is the orphan-classification path (`wrapper_orphan_classified`) — Cookbook saw a `wrapper-started.json` whose PID is no longer alive after the grace window and never finished. Inspect the wrapper folder under `<Workspace>\Cooks\<cookId>\` and Task Scheduler history.
6. **Running** — a scheduled bake is currently in `status = 'running'` (wrapper started, PAX still in flight, grace window not yet expired). No action.
7. **Current** — registered, hash matches, no failed / refused / interrupted terminal outcome, nothing running. The paired message reads "Last scheduled run completed" if a successful terminal bake is on record, or "No scheduled runs have completed yet" if the task was just registered.
8. **Unknown** — registered, but the live projection hash could not be recomputed (the recipe failed to load). Open the recipe, fix the underlying issue, re-save.

**The "Last checked" watermark.** The Schedule card renders a line `Last checked: <iso>` underneath the pill. The watermark is the broker's `scheduled_tasks.last_stale_check_at` column, updated every time the Schedule card is loaded (every `GET` of `/api/v1/recipes/<id>/scheduled-task`). It records the moment the broker last compared the live projection to the stored projection — distinct from `registeredAt` (when the chef saved the schedule) and `lastImportedAt` (when the reconciler most recently advanced the watermark for an imported scheduled bake). A watermark that is many days old means the Schedule card has not been opened recently; the broker still re-checks on every GET, so opening the card refreshes both the watermark and the pill in the same trip. The watermark is **never** a substitute for the wrapper's at-fire-time stale check; the wrapper recomputes the hash independently when Task Scheduler fires.

**"Last terminal scheduled run" link.** Below the recurrence and the last-imported-run line, the Schedule card shows the most recent terminal scheduled bake with a hyperlink that opens that bake's detail page. The link's summary text mirrors the pill: "Last scheduled run completed" for a successful run, "Last scheduled run failed in PAX" for a `pax_nonzero_exit` failure, "Last scheduled run refused: recipe changed since registration" for a stale-projection refusal, "Last scheduled run was interrupted or orphan-classified" for an interrupted run. The link is distinct from the "Last imported run" line above it: *imported* names the most recently reconciled bake (which may still be `running`), whereas *terminal* names the most recent bake that reached a terminal status (`completed`, `failed`, `refused`, or `interrupted`). Both fields can point to the same `cookId` when the most recent imported run is also terminal.

**PAX log is authoritative for failure detail.** The Schedule card's pill and message are designed to point the chef at the right run, not to summarise *why* a run failed. The PAX log inside the bake detail — discovered by the reconciler under the wrapper's fact directory as `Purview_Audit*.log` (excluding `_PARTIAL.log`) — remains the source of truth for the exact PAX exit code, the exception class, and any per-row processing failure. The Schedule card never paraphrases the PAX log; the chef opens the run and reads it.

**Health is advisory; nothing repairs itself.** The health surface is read-only. Loading the Schedule card never registers, updates, re-registers, or deletes a Windows scheduled task. The broker never auto-saves a recipe to clear a `stale` pill, never auto-restarts an `interrupted` bake, and never auto-deletes a `not_registered` row. Every operation that changes Windows Task Scheduler state remains a deliberate chef click: **Save schedule** or **Delete schedule**.

---

## 7a. Notifications

When a bake reaches a terminal status, Cookbook emits a single notification *event* and fans it out to whichever surfaces the chef has turned on. The event is the same shape for every surface and for both manual and scheduled bakes; the surfaces differ only in where the chef sees it.

**The event.** One event is emitted per terminal bake. It carries a fixed, privacy-safe set of fields: a timestamp, an event id, the cook id, the recipe id and recipe name, the source (`manual` or `scheduled`), the status, a derived severity, the process exit code, the duration in seconds, the row count, and a short human-readable message. Severity is derived from status: a `completed` bake is **info**, an `errored` bake is **error**, and an `interrupted` bake (stopped by the chef, or orphan-classified after a crash/reboot) is **warning**. The event never carries file or output paths, URLs, tenant or user identifiers, auth-profile names, tokens, secrets, raw error text, stack traces, script arguments, the PAX command line, the Windows username, or the machine name.

**Notifications are best-effort and never block a bake.** Every surface is attempted after the bake has already reached its terminal status and been written to cook history. A surface that is unavailable, disabled, or failing can never change a bake's outcome, hold up finalization, delay a scheduled reconcile, or fail the broker. The bake's terminal status in the SPA and in cook history remains the system of record; notifications are a convenience layer on top of it.

### 7a.1 The durable notification log (JSONL)

Every emitted event is appended as one JSON line to a dated file at:

```
<Workspace>\Notifications\<YYYY-MM-DD>.jsonl
```

This file is the durable source of truth for the notification layer. It is written first, before any other surface is attempted, so the log is complete even when toasts are off and no webhook is configured. Each line records the event fields above plus a record of which surfaces were attempted, which succeeded, and a bounded reason for any surface that failed. External tooling can watch the `Notifications\` folder and react to new lines; this is the supported integration point for chefs who want to wire their own automation. The folder rolls to a new file each day by date; Cookbook never deletes old notification files.

### 7a.2 In-app notifications

While the SPA is open, terminal bakes surface in-app — a toast on completion and a persistent banner for failures — so a chef watching Cookbook sees the outcome without opening the bake. In-app notifications are a live replay of the same events written to the JSONL log; dismissing one clears it from the in-app feed only and never alters the durable log or cook history. In-app notifications require the SPA to be open; if Cookbook's browser tab is closed, the event is still written to the log and still delivered to any other enabled surface.

### 7a.3 Windows notifications (Action Center)

Cookbook can raise a native Windows toast through the Action Center when a bake finishes, so a chef who has Cookbook minimized or in the background still sees the outcome. These toasts are **text only**: a title, the bake status, and the short message. They carry **no action buttons and no deep links** — clicking a Cookbook toast does not navigate anywhere or run anything. Windows notifications are best-effort: if the platform surface is unavailable, the OS suppresses notifications (Focus Assist / Do Not Disturb, or notifications turned off for the app in Windows Settings), or the session cannot raise a toast, Cookbook silently skips it and records the skip in the JSONL line. The durable log and the other surfaces are unaffected. Windows notifications are controlled by a single setting (see §7a.5) and are independent of the in-app and webhook surfaces.

### 7a.4 Outbound webhook (opt-in)

Cookbook can POST a finished-bake summary to a webhook endpoint the chef configures. This surface is **disabled by default** — a fresh install performs zero outbound webhook traffic — and is only attempted when the chef has explicitly turned it on *and* supplied an endpoint. Its constraints are deliberate and fixed:

- **HTTPS only.** The endpoint must be an `https://` URL. `http`, `file`, and every other scheme is rejected. Loopback, link-local, private, and cloud-metadata hosts are rejected, and a URL with embedded credentials is rejected. The endpoint is authored entirely by the chef; Cookbook performs no auto-discovery.
- **No redirects.** The POST disables redirection, so the configured endpoint cannot bounce the request — or its body — to a different origin.
- **One bounded attempt.** A single request with a short, fixed timeout. There is **no background retry queue, no scheduled re-send, and no delivery history** — a webhook that fails is reported as a bounded reason in the JSONL line and dropped.
- **Best-effort.** A webhook failure (validation, transport, timeout, or any HTTP error) never throws, never blocks bake finalization, and never affects the durable log, in-app, or Windows surfaces.
- **Privacy-safe body.** The webhook body carries only the same privacy-safe event fields the JSONL line exposes, plus a fixed app label and a schema version. It never carries the endpoint URL itself, file or output paths, identifiers, auth-profile names, tokens, secrets, raw error text, stack traces, script arguments, or the PAX command line.
- **No secret / signature.** This surface is URL-only. Cookbook neither requests, stores, nor transmits a webhook secret, and the body is not HMAC-signed.

**Payload format.** The webhook supports two body shapes, selected per the format setting:

- **`generic`** — a flat JSON object: a fixed app label, a schema version, and the twelve event fields. Use this for your own automation, a Power Automate "When an HTTP request is received" trigger, or any endpoint you control.
- **`teams`** — a Microsoft Teams–compatible MessageCard with the bake summary rendered as a small set of facts and a severity-mapped theme color (green for info, amber for warning, red for error). This preset is **webhook-based**: it posts to a Teams *incoming webhook* / Power Automate URL. It is **not** a Microsoft Graph integration and does not post to Teams on a user's or app's behalf — there is no sign-in, no token, and no Graph call.

### 7a.5 Notification settings

Notification preferences live in **Settings** and are stored durably. Each is a simple switch except the webhook URL and format:

| Setting | Default | Effect |
|---|---|---|
| Notify on completed bakes | on | Emit for bakes that finished `completed`. |
| Notify on errored bakes | on | Emit for bakes that `errored`. |
| Notify on interrupted bakes | on | Emit for bakes that were stopped / orphan-classified. |
| Windows notifications | off-by-default unless enabled in Settings | Raise the native Action Center toast (§7a.3). |
| Webhook enabled | **off** | Master switch for the outbound webhook (§7a.4). |
| Webhook URL | empty | The chef-authored `https://` endpoint. |
| Webhook format | `generic` | `generic` or `teams` body shape. |

The per-status switches gate **every** surface for that status: turning off "errored", for example, suppresses the JSONL line, the in-app banner, the Windows toast, and the webhook for errored bakes alike. The durable JSONL log is always written for any status whose switch is on. Disabling a surface (Windows notifications, or the webhook) suppresses only that surface; the others, including the durable log, are unaffected.

### 7a.6 What notifications do **not** do in this release

- No **email** of any kind — no SMTP target and no Microsoft Graph `sendMail`.
- No **Graph-based Teams** posting (no sign-in, no token, no Graph call). The only Teams path is the opt-in MessageCard webhook in §7a.4.
- No **Slack** or other vendor-specific channel integration.
- No webhook **secret, shared key, or HMAC signature**, and no webhook **retry queue or delivery history**.
- No toast **action buttons, deep links, or protocol/COM activation** — Windows toasts are display-only.
- No **notification history page** in the SPA beyond the live in-app feed and the durable JSONL log on disk.

---

## 8. Update lifecycle

The appliance updates by replacing the `App\` subdirectory. The previous `App\` is preserved under `Backups\` so any update can be reversed by hand.

### 8.1 The three explicit clicks

The Settings page exposes the update flow as three buttons, in this order. Each button is one explicit operator action. Cookbook performs no work between clicks. Nothing runs on a timer.

1. **Check for Cookbook Update.** The broker fetches the update manifest from the URL configured in `<InstallRoot>\App\VERSION.json` (`updateManifestUrl`). If the URL is `null`, the button stays available but the broker reports that update checks are disabled. On a successful check the broker reports the manifest version, your installed version, whether an update is available, and (if the manifest provides one) a link to release notes. No package is fetched.

2. **Download Cookbook Update.** Visible only after a successful check that reports an available update. The broker downloads the release ZIP to `<InstallRoot>\Updates\<version>\` and verifies its SHA-256 against the manifest snapshot captured during the check. If the digest does not match, the staged file is rejected and the button surfaces the failure. Trust state is then evaluated against the chef's `<Workspace>\Trust\trusted-signers.json` allowlist (see §10). The staged package is preserved on disk; nothing in `App\` is touched.

3. **Install and Restart Cookbook.** Visible only after a staged package reports a green-severity trust state (`verified` or `unsigned`). On click, the broker re-runs the SHA-256 verification against the staged bytes on disk and re-evaluates apply preconditions (re-auth, no active bakes, no in-flight downloads). If any precondition fails, the broker returns a structured refusal and the staged package remains untouched. When every precondition passes, the broker extracts the staged ZIP into `<Workspace>\Updates\<version>\extracted\`, writes a small `handoff.json` file alongside it, returns HTTP 202 with `applyStatus = restart_initiated`, and then exits. The launcher detects the apply-exit, spawns a short-lived detached orchestrator, and exits as well. The orchestrator waits for the launcher to be gone, runs the staged installer once against the extracted tree, and on success relaunches Cookbook from your existing shortcut. The relaunched Cookbook opens a refreshed UI in your default browser; the previous browser tab will briefly show Cookbook offline while the new broker starts on a fresh loopback port (the port is OS-assigned and not pinned across restart), and you can close the old tab once the new one loads.

### 8.2 What the update flow never does

- Never auto-applies an update.
- Never auto-fetches the manifest. Each check, each download, each apply is one explicit click.
- Never elevates.
- Never edits the registry.
- Never bypasses the SHA-256 check.
- Never deletes `Backups\` without an explicit chef action.
- Never silently retries a failed step. Each failure surfaces in the Settings page and waits for the chef to click again.
- Never runs an unattended retry loop. The detached orchestrator makes one installer attempt; on failure Cookbook exits and the operator decides whether to retry.

### 8.3 Reverting an update

There is no `Revert` button in the SPA. The supported revert procedure is:

1. Quit Cookbook.
2. Replace `<InstallRoot>\App\` with the contents of the most recent `<InstallRoot>\Backups\App-<timestamp>\`.
3. Relaunch.

Backups are plain copies. No archive format, no encryption.

### 8.4 Recovery: finishing the install by hand

The three-click flow described in §8.1 performs the full install end-to-end. The manual path below is a recovery fallback only — used when the detached orchestrator fails to relaunch Cookbook, or when the apply request returns HTTP 500 with `update_apply_extraction_failed`, `update_apply_extracted_tree_malformed`, or `update_apply_handoff_write_failed`, and the chef wants to finish the install without re-staging.

1. Open `<Workspace>\Updates\handoff.json`. The `stagedExtractedPath` field names the directory holding the extracted `App\` tree.
2. Confirm Cookbook is fully exited (no `pwsh.exe` process is still running the broker or the launcher).
3. From a PowerShell prompt, run:
    `pwsh.exe -ExecutionPolicy Bypass -File <stagedExtractedPath>\app\install\Install-PAXCookbook.ps1 -Mode update -Handoff <Workspace>\Updates\handoff.json`
4. Relaunch Cookbook from your existing shortcut. The launcher will start the broker against the new `App\` tree, and the previous `App\` will be preserved under `Backups\App-<timestamp>\` per §8.3.

If any of the three clicks fails on the normal path:

- **Check fails.** The Settings page renders the broker's reason. Common causes: `updateManifestUrl` is `null` (set it in `VERSION.json` and retry), the manifest URL is unreachable from the broker host, the manifest payload is malformed. No staged file is created.
- **Download fails.** The page renders the failure reason. Common causes: SHA-256 mismatch (network corruption, manifest tampering, or a stale manifest snapshot), package URL unreachable, signer not in the trust allowlist. The staged file is removed.
- **Apply refused before extraction.** The page renders the structured refusal: a re-auth was required, the package no longer matches its manifest-pinned digest, a bake is active, or no verified staged package exists. The staged package stays on disk for retry once the underlying condition is cleared.
- **Apply accepted but install fails.** The broker exited and Cookbook did not relaunch. Use the recovery steps above; `handoff.json` and the extracted tree are still on disk.

### 8.5 Where Cookbook release packages come from

The signed release zip and the `latest.json` manifest the Update flow consumes are produced by the release tooling under `tools/release/` (`Build-Release.ps1` + `Sign-Release.ps1`). Distributor-facing detail — package layout, the SHA-256 sidecar contract, the trust-allowlist shape, and the manifest fields the broker reads — lives in [docs/RELEASE_PACKAGE.md](RELEASE_PACKAGE.md). Chefs consume the produced zip + manifest through the normal Settings → Updates flow described in §8.1–§8.4; the release tooling itself is **not** a chef-facing surface and chefs are not expected to invoke `tools/release/` directly.

---

## 9. Export bundled PAX script

**This surface is removed under Cookbook v1.** See §4a.5 for the new behavior: the Settings page does not render an Export button, no PAX-script export route exists in the broker, and no client can request the PAX engine bytes from the appliance. The PAX engine bytes Cookbook is currently using live at `%LOCALAPPDATA%\PAXCookbook\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1` (after acquisition has succeeded) and may be read directly from disk; Cookbook does not redistribute the PAX engine. Existing references to this section elsewhere in the guide should be read as pointing at §4a.

*Historical note.* Older Cookbook builds (Phase 12 and earlier) shipped the PAX engine embedded in the release ZIP and exposed the export button as a read-only convenience. The export button operated against the embedded copy; with external acquisition (§4a), the embedded copy no longer exists, the doctrine of "Cookbook owns the bytes it serves" no longer applies to the PAX engine, and the export surface is therefore not just disabled but architecturally meaningless. The route, the SPA button, and the CSS for the export affordance have all been removed from the source tree — absence is the contract.

---

## 10. Trust / signature lifecycle

Cookbook's trust model is **truthful and bounded**. It does not lie about signature verification.

### 10.1 What integrity attestation Cookbook provides today

Every release package ships with a SHA-256 sidecar (`<package>.zip.sha256`). The broker recomputes this hash before staging any downloaded update and refuses to proceed on mismatch. This is **cryptographic integrity attestation** — it proves the bytes you downloaded match the bytes the release pipeline produced, given an unmodified manifest.

### 10.2 What cryptographic signing Cookbook can do today

The broker contains a **read-only** RSA-PKCS1v15-SHA256 signature verifier (`app/broker/Update/Signature.psm1`). It can verify a detached `.sig` envelope produced by a release-signing key (held by the **distributor** of a release, or by the **chef** in the self-distribution case) against a `<workspace>\Trust\trusted-signers.json` allowlist that the chef places on disk.

The signing key is **never owned by Cookbook**. The release pipeline does **not** ship a signing key. The release pipeline emits release metadata with `signing.state = "unsigned"` and `signing.verified = false` honestly when nobody has signed the package.

### 10.3 Trust states the broker actually reports

`Get-PackageTrustState` in `app/broker/Update/Trust.psm1` returns one of:

| State | Meaning |
| --- | --- |
| `verified` | SHA-256 matches AND signature is cryptographically valid AND the signer thumbprint is in `trusted-signers.json`. |
| `unsigned` | SHA-256 matches; no `.sig` on disk. This is the default for any unsigned release. |
| `signaturePresentNotVerified` | `.sig` is on disk but the verifier module could not run (partial install, etc.). |
| `signatureInvalid` | `.sig` is on disk and the cryptographic verification failed. Refuse to apply. |
| `signerUnknown` | `.sig` verified, but the signer thumbprint is not in the allowlist. Refuse to apply unless the chef adds the thumbprint. |
| `hashMismatch` | The downloaded ZIP's SHA-256 does not match the manifest. Refuse to apply. Terminal. |
| `hashUnknown` / `missing` | Inventory error states. Not promotable to apply. |

### 10.4 What Cookbook does **not** claim

- Cookbook does **not** claim SmartScreen reputation.
- Cookbook does **not** claim cloud-side trust validation.
- Cookbook does **not** claim Authenticode validation of itself; that is the user-invoked PowerShell execution policy's job.
- Cookbook does **not** silently auto-promote `unsigned` packages to `verified`.

**Unsigned means unsigned. Verified means cryptographically verified against the chef's allowlist. There is no third option.**

---

## 11. Recovery flows

This section is the canonical recovery reference. See [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for operational triage of specific symptoms.

### 11.1 Appliance exit codes (launcher and broker)

The launcher and the broker each emit a stable, documented exit code when they refuse to start or finish abnormally. The launcher forwards the broker's exit code to its own caller, so what the chef sees in the console window after a failed launch is the same code regardless of which layer originated it.

| Exit code | Originates at | Constant / meaning | Recovery |
| --- | --- | --- | --- |
| `0` | Launcher or broker (`EXIT_OK`) | Normal shutdown. | None. |
| `1` | Launcher (pre-broker) | PowerShell precondition failed. Either the running `pwsh` is older than 7.4, **or** the launcher cannot locate `pwsh.exe` on disk. | Install PowerShell 7.4+ from <https://aka.ms/PSWindows>. See [TROUBLESHOOTING.md §1](TROUBLESHOOTING.md#1-powershell-version-too-old). |
| `3` | Launcher (pre-broker) | Workspace selection was cancelled. The chef closed the workspace picker without choosing a folder, or the picker returned no result. The bootstrap pointer is left untouched. | Re-launch and complete the picker. See [TROUBLESHOOTING.md §1a](TROUBLESHOOTING.md#1a-workspace-selection-cancelled--exit-3). |
| `4` | Broker (`EXIT_E_WORKSPACE_LOCKED`) | Another Cookbook broker is already running against this workspace (after the four-step liveness probe confirmed it). | Close the other Cookbook (or wait for it to exit), then relaunch. See [TROUBLESHOOTING.md §2](TROUBLESHOOTING.md#2-broker-exit-code-4--workspace-locked). |
| `5` | Launcher (pre-broker) **or** Broker (`EXIT_E_SQLITE_DLL_INTEGRITY`) | The install tree is incomplete or corrupt. The launcher emits `5` when it cannot locate the broker entrypoint on disk. The broker emits `5` when the vendored SQLite native DLL fails its SHA-256 integrity check. Both have the same remediation. | Re-run `Install-PAXCookbook.ps1 install` to restore the canonical install tree. See [TROUBLESHOOTING.md §3](TROUBLESHOOTING.md#3-broker-exit-code-5--sqlite-dll-integrity-failure). |
| `6` | Launcher (pre-broker) **or** Broker (`EXIT_E_PAX_SCRIPT_INTEGRITY`) | The bundled PAX script on disk does not match its expected SHA-256 (the value pinned in `app/VERSION.json -> paxScript.sha256`), or `VERSION.json` itself is missing / unreadable. The launcher checks this once before invoking the broker; the broker re-checks before every bake spawn. Under Cookbook v1 (§4a), the broker also reports a structured `acquisition_required` response on cook routes when the engine has not yet been acquired (`install-state.json -> paxAcquisition.pending = true`); that path does not exit the process — it keeps the broker running so the SPA can drive the acquisition dialog. Exit code `6` is reserved for the post-acquisition state where bytes are present but corrupt. | Re-run `Install-PAXCookbook.ps1 install` to restore the canonical install tree (and, if needed, re-drive acquisition from **Settings → PAX engine**). See [TROUBLESHOOTING.md §4](TROUBLESHOOTING.md#4-broker-exit-code-6--pax-script-integrity-failure) and [§17e](TROUBLESHOOTING.md#17e-pax-engine-acquisition-errors). |
| `7` | Launcher (reuse path) | A live broker for this workspace was detected, but the launcher failed to open the chef's default browser at the broker's runtime URL. The broker keeps running; the launcher simply could not hand off. | Open the URL printed by the launcher manually in any browser. See [TROUBLESHOOTING.md §1b](TROUBLESHOOTING.md#1b-live-broker-found-but-default-browser-failed-to-open--exit-7). |
| `8` | Broker (`EXIT_E_OBSERVATION_TRIGGER_INTEGRITY`) | Either the append-only triggers on `update_request_observations` (`trg_uro_block_update`, `trg_uro_block_delete`) were not present in the live database when the broker started, or one of the trigger bodies in the live database no longer matches the canonical DDL the broker installs at schema bootstrap (a SHA-256 over the catalog `sql` text, after canonical whitespace normalization, did not match the pinned canonical hash). The broker checks for both conditions once at boot, immediately after schema bootstrap and before entering the dispatch loop, and refuses to serve if either trigger is missing or its body has drifted. The most common causes are an external SQLite tool that issued `DROP TRIGGER`, a database file restored from a backup that predates the append-only triggers, or a database file replaced by hand. The broker does not attempt automatic recreation and has no fallback path. | Reinstall from a known-good release (`Install-PAXCookbook.ps1 install`); the live database has been mutated by an external tool. The broker will recreate the triggers idempotently on next start. See §17.13 below and [TROUBLESHOOTING.md §13u](TROUBLESHOOTING.md#13u-broker-refuses-to-start-with-exit_e_observation_trigger_integrity). |
| `9` | Broker (`EXIT_E_PACKAGE_TRUST_INTEGRITY`) | The at-launch package-trust evaluation refused the installed package's bytes against the system trust store. Immediately after the trigger-integrity gate and before the HTTP listener binds, the broker re-derives signature/hash trust from first principles for every currently-staged package in the workspace `Updates\` folder. If any package returns `hashMismatch` or `signatureInvalid`, the broker writes one append-only `package_trust_observations` row per package with `boundary='pre_run'`, records the refusal in the recent-errors ring, and exits. There is no cache, no remembered-signer flag, no carry-forward across restart -- every boot re-evaluates from scratch. The most common causes are staged bytes that drifted (operator manipulation, partial sync, external tooling), or a publisher whose certificate is no longer trusted by the system. | Re-stage and re-apply from a known-good release. Inspect the most recent `package_trust_observations` row with `boundary='pre_run'` and `outcome IN ('mismatch','unknown')` for the per-package expected/observed digests. See §17.16 below and [TROUBLESHOOTING.md §13x](TROUBLESHOOTING.md#13x-broker-refuses-to-start-with-exit_e_package_trust_integrity). |
| `10` | Broker (`EXIT_E_ENVIRONMENT_CONSTRAINED_LANGUAGE`) | The broker's own PowerShell engine is running in a language mode other than `FullLanguage` (typically `ConstrainedLanguage` enforced by AppLocker or WDAC). The probe runs once at startup, after the at-launch package-trust evaluation and before the HTTP listener binds, by reading `$ExecutionContext.SessionState.LanguageMode`. On detection, the broker writes one append-only `environment_observations` row with `condition='constrained_language'` / `outcome='detected'`, records the refusal in the recent-errors ring, and exits. The broker's contract (dot-sourced helpers, .NET interop, `Add-Type`-free SQLite access) assumes FullLanguage semantics and cannot be honored under a constrained engine; there is no fallback. | Adjust the enterprise PowerShell policy so the broker process runs in `FullLanguage`, then relaunch. See §17.18 below and [TROUBLESHOOTING.md §13z](TROUBLESHOOTING.md#13z-broker-refuses-to-start-with-exit_e_environment_constrained_language). |
| `11` | Broker (`EXIT_E_ENVIRONMENT_LOW_DISK`) | The workspace volume has fewer than 500 MB free at broker startup. The low-disk probe runs once at startup, after the ConstrainedLanguage gate and before the HTTP listener begins serving, by reading `[System.IO.DriveInfo]::new(GetPathRoot($WorkspacePath)).AvailableFreeSpace`. Below the 500 MB hard floor the broker writes one append-only `environment_observations` row tagged `condition='low_disk'` / `outcome='detected'` and exits. Between 500 MB and 2 GB the broker writes one row tagged `outcome='warning'` and continues startup; bakes may then be refused by the per-bake precheck. The probe targets the workspace root volume only and is unrelated to the per-bake `MinFreeDiskBytesForCook` threshold. | Free space on the volume that hosts the workspace (delete old `Cooks\` subtrees from the Bakes page once you have exported what you need; the broker never auto-prunes them) and relaunch. See §17.18 below and [TROUBLESHOOTING.md §13aa](TROUBLESHOOTING.md#13aa-broker-refuses-to-start-with-exit_e_environment_low_disk). |

In each integrity case (codes `5` and `6`) the broker or launcher prints the file path, the expected hash, and the actual hash. Save both before re-installing if you want to investigate.

### 11.2 Bootstrap-pointer corruption

If `%APPDATA%\PAXCookbook\cookbook.bootstrap.json` becomes unreadable (sync conflict, partial write, encoding drift), the launcher:

1. Renames the corrupt file to a sibling `cookbook.bootstrap.json.broken-<UTC>` (timestamp format `yyyyMMddTHHmmssZ`).
2. Continues as if no bootstrap existed (you will be re-prompted for a workspace).

The `.broken-<UTC>` file is preserved verbatim. You may open it in any text editor to see what the launcher actually observed. Cookbook never deletes these files automatically; they are chef-owned diagnostic artifacts.

### 11.3 Workspace lock stuck

If a previous Cookbook process crashed without releasing the workspace lock, the next launch will refuse to start (`EXIT_E_WORKSPACE_LOCKED = 4`). The broker performs a 4-step liveness probe before claiming the lock is stale:

1. Is the PID alive?
2. Is the PID's process image a `pwsh` executable?
3. Does the recorded TCP port respond within 500 ms?
4. Does `/api/v1/health` return within 2 seconds with a matching `workspaceFolderPath`?

If all four say "no other broker is alive," Cookbook claims the lock automatically. If any say "yes," Cookbook refuses to start. In that case, find and quit the other Cookbook (Task Manager → `pwsh.exe` running `Start-Broker.ps1`).

### 11.3a Installer maintenance preflight (Test-IsBrokerRunning)

The four-step probe in §11.3 is the broker's own check when it tries to claim a workspace at startup. A separate, narrower preflight runs inside `app\install\Install-PAXCookbook.ps1` (`Test-IsBrokerRunning`) whenever a destructive maintenance mode is invoked — update, repair, uninstall, or the uninstall confirmation prompt launched from the **Uninstall PAX Cookbook** Start Menu shortcut. The chef-facing rule is simple: the installer must refuse to clobber a Cookbook that is actually running, but it must **not** refuse merely because it sees a stale `workspace.lock` on disk or because it is itself a `pwsh.exe` process whose command line happens to live under the install tree.

`Test-IsBrokerRunning` therefore evaluates two independent branches:

**Branch 1 — authoritative (bootstrap pointer → workspace lock → broker PID).** The installer reads `%APPDATA%\PAXCookbook\cookbook.bootstrap.json`, follows the recorded `workspaceFolderPath` to `<Workspace>\Runtime\workspace.lock`, and pulls the `brokerProcessId` field out of that lock file. A broker is treated as running only if **all** of the following hold:

- the PID exists in the live process table,
- its process image name is `pwsh.exe`,
- the PID is not the installer's own `$PID`.

If the lock file is missing, malformed, has no `brokerProcessId`, or names a PID that is not alive (or no longer a `pwsh.exe`), Branch 1 returns "not running" — a stale `workspace.lock` with a dead PID no longer blocks Repair, Update, or Uninstall.

**Branch 2 — narrowed command-line heuristic (fallback).** If Branch 1 is inconclusive (no bootstrap pointer, unreadable lock, etc.), the installer enumerates live `pwsh.exe` processes and treats a process as a Cookbook broker only when its command line names one of the two launcher entry points that actually host the broker:

- `Start-PAXCookbook.ps1`
- `Start-PAXCookbookSupportMode.ps1`

Two explicit exclusions are applied so the heuristic does not false-positive on the installer itself:

- the installer entrypoint `Install-PAXCookbook.ps1` is excluded — the running uninstall/update process never counts as "broker alive,"
- the current process (`$PID`) is excluded for the same reason.

The two branches together preserve the doctrine: **a running launcher or broker still blocks destructive maintenance modes; nothing else does**. In particular, an installer that is itself a `pwsh.exe` running from inside `App\install\` no longer self-matches, so Repair PAX Cookbook Shortcuts and Uninstall PAX Cookbook can be launched safely from the Start Menu even though the shortcut hands control to a `pwsh.exe` process under the install tree.

**Operator guidance.** If a maintenance mode reports "Cookbook is running, refusing to proceed," do **not** try to clear the condition by deleting `workspace.lock` by hand. The supported sequence is:

1. Use the **Stop PAX Cookbook Server** Start Menu shortcut. That asks the running broker to exit cleanly, which both releases the OS lock handle on `workspace.lock.acquire` and updates the `workspace.lock` evidence files on the way out.
2. Wait until the Cookbook browser tab reports the broker is gone (or until `Get-Process -Name pwsh` no longer shows a `Start-PAXCookbook.ps1` / `Start-PAXCookbookSupportMode.ps1` command line).
3. Re-run the maintenance action.

Task Manager is a **fallback only** — appropriate when the broker is hung, unreachable, or **Stop PAX Cookbook Server** has already been clicked and ignored. Look for `pwsh.exe` processes whose command line includes `Start-PAXCookbook.ps1` or `Start-PAXCookbookSupportMode.ps1`. End those, then re-run maintenance.

Manually deleting `workspace.lock` is **not supported** and is not needed. The new preflight already disregards stale evidence: a dead `brokerProcessId` does not block maintenance, and the file is rewritten by the next clean broker start. Hand-editing or deleting the file removes evidence the broker uses to reconcile interrupted bakes (§7.2) without changing whether the preflight will let you proceed.

### 11.4 Reinstall from scratch

The supported clean reinstall is:

```powershell
# Quit Cookbook first.
pwsh -File <release-folder>\app\install\Install-PAXCookbook.ps1 uninstall -Purge
pwsh -File <release-folder>\app\install\Install-PAXCookbook.ps1 install
```

`uninstall -Purge` removes the entire `%LOCALAPPDATA%\PAXCookbook\` tree, including `Backups\` and `Updates\`. Your **workspace is not touched** — recipes and bake history live in your chef-owned workspace folder, not in the install root.

### 11.5 Integrity failures — what to inspect

When the broker exits on code 5 or 6, before reinstalling:

1. Note the file path, expected hash, and actual hash from the broker's exit message.
2. Compare the actual hash with the value in `<InstallRoot>\App\VERSION.json` (for `paxScript`) or with the per-file SHA-256 + Authenticode subject expectations recorded in `<InstallRoot>\App\lib\sqlite\lockfile.json` (for the vendored SQLite stack). The broker's integrity check (`Test-VendoredSqliteIntegrity` in `app/broker/Start-Broker.ps1`) compares the installed DLLs to this lockfile and prints both the expected and observed values on mismatch.
3. If you suspect tampering, copy the unexpected file out before reinstalling. The reinstall will replace it.

### 11.6 Durability model — what survives what

Cookbook's durability story is deliberately narrow and explicit. There are no background compaction daemons, no silent retention trimming, no hidden "repair" passes. The chef owns the workspace; Cookbook only writes the evidence and lets the chef decide what to keep.

| Failure | What survives | How |
| --- | --- | --- |
| Power loss mid-write to a bake-folder file (`started.json`, `finished.json`, `interrupted.json`, `recipe-snapshot.json`, `cook-context.json`, `command.txt`, `command-argv.json`, `cook.log`) | The pre-write file is either fully intact or fully replaced. Observers never see a torn file. | Every broker- and supervisor-side write to these files is `<file>.tmp` followed by `File.Move(overwrite=true)`. The rename is the only file-list-mutating step, and it is atomic on NTFS. |
| Power loss mid-transaction to `cookbook.sqlite` | The pre-transaction state is preserved. Partially-written transactions are dropped on next open. The most recent un-checkpointed transaction may be lost. | SQLite is opened with `journal_mode=WAL` and `synchronous=NORMAL`. NORMAL is an *intentional* WAL-paired tradeoff: it gives "no corruption on power loss" at the cost of possibly losing only the very last in-flight commit. `synchronous=FULL` would fsync on every commit (overkill for an appliance workload). Cookbook does **not** use `synchronous=OFF`. |
| Power loss mid bake-terminal write (between updating `cooks.status` and inserting the `cook_artifacts` row) | Either both the status update and the artifact insert are visible, or neither is. The bake stays `running` if neither, and is resolved on next broker startup via sentinel reconciliation reading the on-disk `finished.json` / `interrupted.json`. | The supervisor writes the terminal pair inside a single `BEGIN IMMEDIATE` / `COMMIT` transaction with `busy_timeout=5000` and `foreign_keys=ON` applied per connection. |
| Broker process crash | The OS releases the workspace lock handle on process exit. On next start the lock acquires cleanly. SQLite WAL is replayed; partial `cooks` rows are reconciled from disk sentinels. | OS-managed `FileShare.None` lock on `workspace.lock.acquire`; sentinel reconciliation classifies every `running` row from its sentinel files. |
| Workspace moved to a new path (with Cookbook quit) | Recipes, bake history, and all bake-folder contents continue to resolve. | All bake-folder paths inside the database are stored workspace-relative and resolved at read time using the current workspace root. See §5.1. |

### 11.7 Retention — chef-owned, not Cookbook-owned

Cookbook **never** prunes bake history, **never** compacts the database in the background, **never** rotates `cook.log`, and **never** moves old bake folders out of `Cooks\`. There is no built-in retention policy because there is no built-in opinion about how long evidence should live. If you want to enforce a retention policy:

1. Quit Cookbook.
2. Delete the relevant `<Workspace>\Cooks\<recipeId>\<cookId>\` directories and `DELETE FROM cooks WHERE created_at < ...` against `<Workspace>\Database\cookbook.sqlite` (any standard SQLite tool will do; `cook_artifacts` rows cascade automatically via foreign keys).
3. Relaunch.

The doctrine here is intentional: an appliance that silently deletes its own evidence is an appliance whose evidence cannot be trusted.

### 11.8 Observability — what the broker actually exposes

The broker exposes only **local, inspectable** evidence. There is no telemetry, no cloud upload, no remote diagnostics endpoint, no background health-probe thread, no support-bundle export, and no embedded analytics. Three HTTP surfaces are the entire runtime observability contract; every field on every surface is classified so the chef can tell what it really means.

| Surface | Auth | Purpose |
| --- | --- | --- |
| `GET /api/v1/health` | unauthenticated | Liveness probe + recent-error queue + workspace identity. Built on demand from in-memory state and one disk sample. |
| `GET /api/v1/runtime/version` | bearer token | Startup-frozen identity (Cookbook version, bundled PAX version, integrity verdict). Does not change after the broker binds the port. |
| `GET /api/v1/updates/state` | bearer token | Manifest configuration, last-check / last-download timestamps, staged-package inventory, and trust-allowlist readiness. |

Every field on every surface is one of the following classifications. The `/api/v1/health` response carries this classification inline in its `evidenceClassification` block so a chef can see at the point of consumption what kind of value each field is.

| Classification | Meaning | What survives broker restart |
| --- | --- | --- |
| **authoritative** | Read directly from the on-disk source of truth (workspace path, database file, lock file). The broker is reporting what *is*, not what it *thinks*. | Yes — the source of truth itself survives. |
| **sampled** | Read once at request time from a file the broker doesn't own exclusively (e.g. `dbSizeBytes` reads the SQLite file length). May be `null` if the sample failed at that moment. | Source survives; the specific sample does not. |
| **derived** | Computed at request time from other fields on the same response. `status` is the only derived field today. | Yes — the inputs survive in the same way they did before. |
| **runtime-only** | Lives only inside this broker process. Cleared on broker exit; not persisted; never sent anywhere. The `recentErrors` queue and the `recentErrorOverflowCount` counter are both runtime-only. | **No.** Restart = clean slate. |
| **configuration** | Set at broker startup from `VERSION.json` or from a compile-time constant; immutable for the life of the process. | The configuration source survives; the runtime echo is rebuilt next start. |

#### `/api/v1/health` health-status semantics

The `status` field is **derived**, not claimed. The only states the broker will ever report are:

- `ok` — broker is up, the `recentErrors` queue is empty, and `recentErrorOverflowCount` is zero.
- `degraded` — broker is up and serving, but at least one error has been recorded this session (either still visible in `recentErrors`, or displaced into `recentErrorOverflowCount`). The chef can continue operating; something has been recorded as wrong, and the `recentErrors` array itself enumerates what is currently visible.

Cookbook does **not** synthesize states it cannot truthfully verify — there is no `corrupted`, `recovering`, `unsupported`, or `unknown` value. When in doubt, the broker reports `degraded` and surfaces the underlying error messages; the chef inspects the cause, the broker does not invent certainty. If the broker is so broken that it cannot even build this payload, the request fails — there is no synthetic green-light path.

#### Recent-error queue contract

- The queue is **runtime-only**. Entries are lost on broker restart.
- Capacity is bounded (`recentErrorCapacity`, currently `10`). When the queue fills, the oldest entry is displaced and `recentErrorOverflowCount` is incremented — the chef can see, in the same response, that evidence WAS dropped rather than silently disappearing.
- Each entry is a hashtable with `timestampUtc`, `message`, and an optional `source` tag (e.g. `sqlite_startup`, `migration`, `sentinel_reconciliation`). When the originating subsystem chose not to tag itself, `source` is `null` — never fabricated.
- The queue is **never forwarded** off the appliance. There is no upload step, no aggregator endpoint, no analytics shim. The queue is read only by `GET /api/v1/health`.
- Errors that need to outlive the broker session (e.g. bake-folder migration warnings, sentinel reconciliation diagnostics) also appear on disk as part of the artifact they describe — see §14 (*Where evidence lives*) for the durable trail.

#### `/api/v1/runtime/version` and `/api/v1/updates/state`

These two surfaces predate the `evidenceClassification` block but follow the same doctrine:

- `runtime/version` is frozen at startup; every field is either **configuration** (`cookbookVersion`, `bundledPaxVersion`, `releaseChannel`) or **runtime-only** (`integrity` verdict, `host`). The broker refuses to bind the port at all if integrity verification fails — so a successful response is itself the integrity attestation.
- `updates/state` is **sampled**: it reads the current state of `<InstallRoot>\Updates\`, the manifest-URL configuration, and the trust allowlist. The values can change between calls; the broker never caches them.

#### What this contract does **not** include

By design, the observability surface does not include:

- No `GET /api/v1/diagnostics`, `/api/v1/support-bundle`, `/api/v1/logs`, or similar aggregation endpoint.
- No background probe that calls a health URL on a timer.
- No persistent error log. The recent-error ring is in-memory only.
- No central logging client (no OpenTelemetry, no Application Insights SDK, no Datadog agent, no analytics POST).
- No "fleet view" or remote dashboard. Each appliance is observed only by the chef sitting in front of it.

If a future need arises that genuinely requires more visibility, the answer is a new local file or a new local route — never a remote sink.

#### Time-anomaly surface — dual-clock evidence preserved without smoothing

`/api/v1/health` carries three fields that describe the appliance's own runtime continuity: `uptimeSeconds`, `uptimeIsAnomalous`, and `timeAnomaly`. The contract here is deliberately split across two non-interchangeable clocks:

- **Wall clock** — `[datetime]::UtcNow`. The append-only evidence authority. `startedAtUtc` is the wall-clock moment the broker came up, expressed as a UTC ISO-8601 string. It does not change after startup and is recorded for evidence purposes; persisted timestamps elsewhere in the appliance (bake `started_at`, log entries, lifecycle stamps) also live in the wall-clock domain.
- **Monotonic clock** — `[System.Diagnostics.Stopwatch]::GetTimestamp()`. The elapsed-runtime operational authority. `uptimeSeconds` is computed from a monotonic anchor captured at broker start, floored at zero. Monotonic time generally does not advance during S4 hibernate, modern-standby suspend, or VM pause; it is meaningless across process restart.

The appliance never conflates these domains. `uptimeSeconds` reflects how long the broker process has actually been running; `startedAtUtc` reflects the wall-clock moment it started. When the two clocks disagree past a small threshold (currently 60 seconds), the appliance reports the discrepancy rather than smoothing it.

`uptimeIsAnomalous` is `true` when the most recent skew computation classified an anomaly; `false` otherwise. `timeAnomaly` is `null` when no anomaly is active, and otherwise a structured payload:

```json
{
  "kind": "sleep_or_pause_gap",
  "wallElapsedSec": 14400.0,
  "monoElapsedSec": 0.123,
  "skewSec": 14399.877,
  "anomalyThresholdSec": 60
}
```

`timeAnomaly.kind` is drawn from a frozen three-value vocabulary. The appliance refuses to extend this vocabulary, refuses to introduce heuristic aliases, and refuses to make claims it cannot justify from the wall-vs-monotonic comparison alone:

| `kind` | Wall delta | Monotonic delta | Plain reading |
| --- | --- | --- | --- |
| `wall_clock_rollback` | materially negative | any | The wall clock moved backward since the reference anchor. Consistent with an NTP step-back, a DST adjustment, or a manual clock change. |
| `sleep_or_pause_gap` | materially positive | effectively stalled (under one second) | Monotonic time barely advanced while the wall clock progressed. Consistent with the host being suspended (S4 hibernate, modern standby, VM pause). |
| `wall_clock_forward_jump` | materially positive | also advanced | The wall clock advanced significantly faster than monotonic. Consistent with an NTP forward step, or a manual clock change while the host was running. |

`timeAnomaly` describes **runtime continuity ambiguity**, not at-record-time timestamp invalidity. The `cooks` rows, log entries, and lifecycle stamps the appliance has already recorded remain truthful — `timeAnomaly` only says "between the reference anchor and now, the two clocks disagreed in a way the appliance cannot reconcile." It does not say a bake is corrupt, a row is wrong, or a log is invalid.

The appliance intentionally preserves temporal ambiguity, recorded evidence, and anomalous runtime observations rather than smoothing, rewriting, or hiding those conditions. There is no field that silences the anomaly surface, no path that auto-corrects skew, and no operator-facing affordance to mark a classified anomaly as resolved without re-authenticating. The chef sees what the appliance saw.

#### Bake-age anomaly fields — `ageSeconds`, `ageIsAnomalous`, `ageAnomalyReason`

When the broker reports active or recent bakes — for example on `/api/v1/updates/apply` refusal payloads (§8) and on bake-listing surfaces — each row also carries three age fields:

- `ageSeconds` is computed as `nowUtc - cook.started_at` and is **not clamped at zero**. A negative value is truthful evidence that the wall clock has rolled back since the bake started; the broker neither hides nor adjusts it.
- `ageIsAnomalous` is `true` when the appliance classified the age into one of the values below; `false` otherwise.
- `ageAnomalyReason` is `null` on the normal path, and otherwise a value drawn from a frozen two-value vocabulary that is **separate** from `timeAnomaly.kind`:

| `ageAnomalyReason` | Meaning |
| --- | --- |
| `negative_age` | `ageSeconds < 0`. The wall clock moved backward between the bake's `started_at` and the present. Recorded evidence is preserved verbatim; only the operational interpretation is anomalous. |
| `absurdly_old` | `ageSeconds` exceeds seven days (604,800 seconds). Operationally unusual for an active running bake. The flag exists to draw the chef's attention to a long-lived row; it does **not** claim the bake is corrupt, invalid, broken, or garbage. Recorded evidence is preserved even when the operational interpretation becomes anomalous. |

The broker never auto-rounds, auto-clamps, or auto-suppresses these fields. The chef inspects the bake view and decides; the appliance reports.

### 11.9 SPA operational doctrine — what the in-browser app does, does not, and cannot claim

The PAX Cookbook user interface is a vanilla-JavaScript single-page application served by the broker on the same port. It has no service worker, no offline mode, no client-side persistence beyond `sessionStorage` for the per-tab session token, no IndexedDB, and no React/Vue/Angular reactive store. It re-fetches every list and every detail on demand. This section enumerates what the SPA *is*, what it *is not*, and what each on-screen state actually means.

#### State authority

The broker is the only source of truth. The SPA is a thin cached projection of the broker's HTTP responses for the current page only.

- The SPA never invents state the broker did not return.
- The SPA never optimistically renders a save, a bake, a stop, or a materialize as "done" before the broker has acknowledged it.
- The SPA never retains broker state across page navigations. Open the Recipes page, navigate to Settings, come back: every row is re-fetched.
- The SPA never reconciles contradictory views by guessing. If a list-page request and a detail-page request disagree, the most recent successful broker response wins; the older one is discarded.
- The SPA never persists user data to the browser. The only thing in `sessionStorage` is `cookbook.sessionToken`, captured once from the URL fragment delivered by the launcher.

#### State vocabulary — what the topbar Status pill means

The topbar Status pill is a passive observer of HTTP traffic, not a polling probe. It updates only when a real request settles. Its label is one of:

| Pill label | What it means | What it does **not** mean |
| --- | --- | --- |
| `unknown` | No request to the broker has settled yet in this tab, or the tab was hidden for more than five minutes and the last successful fetch is now considered stale. | Does **not** mean the broker is down. It means the SPA has no recent evidence either way. |
| `ok` | The most recent HTTP request to the broker round-tripped successfully, and the boot-time `/api/v1/health` probe returned `status: 'ok'`. | Does **not** mean every endpoint is healthy. Each endpoint is verified only when it is actually called. |
| `degraded` | The boot-time `/api/v1/health` probe (or a subsequent call) returned `status: 'degraded'`. The broker is reachable; the broker itself is reporting that it has recorded at least one error this session. | Does **not** mean a specific bake or recipe has failed. Open the broker's `recentErrors` queue (see §11.8) for the actual list. |
| `unreachable` | The most recent request to the broker failed with a network-level error (DNS, TCP, TLS, or a hard timeout from the SPA's bounded fetch). | Does **not** mean the broker is necessarily dead. It means *this* request did not complete. Refresh the page to retry. |
| `unauthorized` | The broker rejected at least one request with HTTP 401 since the most recent successful response. The session token in this tab is no longer accepted. | Does **not** mean the broker is broken. It means this tab needs to be relaunched from the launcher to mint a fresh session token. |

The pill **steps down** to `unknown` on its own when the tab has been hidden for more than five minutes — the SPA has no fresh evidence after that interval and refuses to keep claiming `ok`. The pill is **never** promoted to `ok` by the passage of time. Promotion requires a live successful request.

#### Multi-tab semantics

The session token lives in `sessionStorage`, which is **tab-local** by browser design. Consequences:

- Each new tab opened from the launcher gets its own session token from the URL fragment (`#t=<token>`). Two launcher-opened tabs work independently.
- A tab cloned via Ctrl-T or "Duplicate tab" inherits the parent's `sessionStorage` and works. A tab opened from history without the launcher fragment will not have a token and will display the no-token error.
- Closing a tab discards that tab's session token. A future tab needs the launcher again.
- The SPA does **not** broadcast state changes across tabs. Two tabs viewing the same recipe will not see each other's edits until each tab reloads.

#### Browser refresh semantics

A full page refresh (F5 / Ctrl-R) reloads `index.html` and re-runs boot. `sessionStorage` survives the refresh, so the session token persists. Every page module re-mounts with fresh state; in-flight requests from before the refresh are abandoned by the browser. The boot health probe re-runs and the Status pill is reset to `unknown` until the probe settles.

#### Live-tail close labels (bake view)

The bake log live-tail is a WebSocket connection that the broker closes when the bake reaches a terminal state. The bake view distinguishes four close conditions and labels them truthfully:

| Close code | Operator sees | Meaning |
| --- | --- | --- |
| `1000` | *(bake reached terminal state — live tail closed)* | Clean server-side close. The bake is done; the hydrated console is complete. |
| `1006`, after subscription | *(live tail interrupted — broker stream ended without terminal marker)* | The stream died without a Close frame. The bake itself may or may not still be running. Reload the page to re-check status. |
| Any other non-1000 code, after subscription | *(live tail disconnected — abnormal close code N)* | Subscription succeeded then the socket dropped with a specific code. Navigate away and back to reattempt. |
| Failure before subscription | *(could not open live tail — close code N)* | The socket failed before `onopen` fired. The hydrated log, if any, is still authoritative. |

The bake view **never** reconnects automatically. There is no retry, no backoff, no fallback poll. If the live tail dies, the operator navigates away and back.

#### Save / bake / stop / materialize network-failure semantics

State-mutating calls (`POST /api/v1/recipes/*`, `PUT /api/v1/recipes/*`, `POST .../cook`, `POST .../stop`, `POST .../materialize`) are not idempotent from the client's perspective. When a network-level error occurs, the SPA does **not** claim the operation failed and does **not** claim it succeeded. Every such banner instead says some variant of:

> Could not reach broker: `<reason>`. Your save / bake / stop / materialize may or may not have been recorded on the server. Reload / open the Bakes page / refresh the bake to verify before retrying.

The doctrine: a bounded network failure is *evidence of uncertainty*, not evidence of failure. Retrying blind can produce duplicates (two bakes, two materialized recipes). The truthful path is to reload the authoritative view first.

#### Pending-request cancellation

Every page module owns one `AbortController` per mount. When the operator navigates away from a page, the page's `teardown()` aborts that controller, which signals every in-flight `fetch` to release its browser-side resources. This is a *cleanup*, not a correctness mechanism — epoch-based stale-drop in each `.then` handler is the authoritative response-discard signal. The `AbortController` exists so the browser does not keep dozens of zombie sockets open as the operator clicks through pages.

#### Bounded fetch timeout

Every HTTP request issued by the SPA has a 30-second default timeout enforced client-side via `AbortController`. A request that exceeds this bound fails with a timeout and the Status pill steps to `unreachable`. The SPA does not retry, does not back off, and does not extend the timeout. Long-running operations (bake runs, archive exports) are not exposed as long-running HTTP requests; the broker accepts the request, returns a `cookId`, and the operator observes progress through subsequent reads of `/api/v1/cooks/<id>` and the WebSocket live tail.

#### Giant-log boundedness

The bake log live-tail appends incoming WebSocket frames as new text nodes into a single `<pre>` element. There is no virtualization, no windowing, no truncation. A bake that emits hundreds of megabytes of stdout will produce a `<pre>` of the same size and the browser tab will eventually become unresponsive. The doctrine here is intentional: the SPA does not silently drop log output. If a bake produces unbounded output, the operator either lets the tab freeze or kills the bake from the bake view itself.

#### What the SPA deliberately does **not** do

- No background polling. The Status pill listens to settled requests; it does not generate them.
- No service worker, no manifest, no offline mode.
- No client-side analytics, telemetry, error reporting, or feature flags.
- No optimistic UI. Every render reflects a broker response that already arrived.
- No reactive store, no shared state between pages.
- No automatic reconnect on the live-tail WebSocket.
- No client-side recipe validation that overrides the server. AJV runs on the client only to give early field-level feedback; the broker re-validates every payload and the server's verdict is authoritative.

---

### 11.10 Broker shutdown classification and active-bake annotation

The broker records one row per process lifetime in the `broker_sessions` table, and additively annotates any bake still in `status='running'` at shutdown with `closure_reason = 'broker_shutdown_with_active_cook'`. This section explains what those annotations mean, what they do not mean, and the boundaries the appliance refuses to cross.

#### `broker_sessions.stop_class` vocabulary

| Value | What it means | What it does **not** mean |
| --- | --- | --- |
| `clean` | The broker reached `Invoke-Shutdown`, executed its finally-block ordering (annotate active bakes, then `Stop-BrokerSession` writes `stopped_at`, `stop_reason`, `stop_class='clean'`), and closed the SQLite connection. The row was committed by the broker that owned it, while that broker was alive. | Does **not** mean every bake the broker started reached a clean terminal state. Bake lifecycle and broker lifecycle are independent — a `clean` broker shutdown can co-exist with bakes that were still running, that were force-killed by the operator, that produced no output, or that the supervisor terminated. `'clean'` is a positive assertion the broker made about itself, nothing more. |
| `no_orderly_stop_record` | The NEXT broker observed a prior `broker_sessions` row with `stopped_at IS NULL`. Forensic; written retroactively by `Invoke-ClassifyPriorBrokerSessions`. It states only that no orderly stop record was committed by the broker that owned the row. | Does **not** name a cause. Six distinct causes can each produce an absent stop record: `TerminateProcess` from outside, a native crash inside the broker, an OS-level kill, a disk-full failure during the final UPDATE, an exception mid-shutdown after `Invoke-ActiveCookShutdownSweep` and before `Stop-BrokerSession`, or an exception after the orderly latch has run. The label names *what was observed*, not *what happened*. |

The doctrine in source (`Start-Broker.ps1`) is verbatim:

> A clean shutdown record is a positive assertion the broker made about itself. Its absence is a forensic observation, not a causal conclusion.

`'clean' does NOT mean all cooks finished`, and `'no_orderly_stop_record' does not mean the broker was killed`. The appliance never collapses these into a single "succeeded / failed" boolean.

#### Active-bake annotation: `broker_shutdown_with_active_cook`

When the broker's shutdown path runs, `Invoke-ActiveCookShutdownSweep` enumerates `$Script:CookRegistry` and, for each bake still in `status='running'`, calls `Set-CookBrokerShutdownAnnotation` to stamp three columns:

- `closure_reason = COALESCE(closure_reason, 'broker_shutdown_with_active_cook')`
- `abnormal_close_recorded_utc = COALESCE(abnormal_close_recorded_utc, <now>)`
- `broker_session_id_at_shutdown = COALESCE(broker_session_id_at_shutdown, <this broker's session_id>)`

Each column uses `COALESCE` so the **earliest writer wins forever**. If the supervisor already recorded a terminal closure_reason for the bake, that record is preserved; the broker never overwrites it. If a later broker (after restart) finds the same bake still in `status='running'` and annotates it again, the original session_id linkage is preserved.

The bake's `status` is **not** flipped to `'interrupted'` by this annotation. Doctrine: broker-driven interruption and orphan-side reconciliation are distinct paths. The status flip belongs to the NEXT broker's reconciliation, which also probes the orphan PID and records `orphan_probe_verdict`. Both facts coexist on the row.

#### What `broker_shutdown_with_active_cook` is and is not

| | |
| --- | --- |
| **Is** | Recorded evidence that, at the moment the broker began its shutdown, this `cooks` row was still `running` in the broker's view of the world. |
| **Is** | The cooks-side correlate of a `broker_sessions` row — combined with `broker_session_id_at_shutdown`, an operator can locate the exact broker process lifetime that recorded the annotation. |
| **Is not** | A lifecycle terminal state for the bake. The bake's own supervisor may have written a real terminal state (e.g. `succeeded`, `failed`, `cancelled`) before or after the broker shutdown. The earliest-writer-wins guarantee preserves the supervisor's record when it got there first. |
| **Is not** | An assertion that the broker killed the bake. The broker neither terminates the bake process nor commits the orphan probe — both happen on the reconciliation path of the NEXT broker. |
| **Is not** | A claim of continuity. After restart, the NEW broker has no authority over the old bake process and does not "resume" anything. The annotation is `evidence-additive, not lifecycle-finality`. |

#### `broker_session_id_at_shutdown` — the evidence linkage column

`cooks.broker_session_id_at_shutdown` is a nullable `TEXT` column. It holds the `broker_sessions.session_id` of the broker that recorded the active-bake annotation for this row. It is:

- Stamped only by `Set-CookBrokerShutdownAnnotation`, only via COALESCE, only on `status='running'` rows.
- Never backfilled. Pre-existing rows surface `NULL` and stay `NULL` forever.
- Never overwritten. A later broker that reannotates the same bake preserves the earliest stamped session_id.
- Never required for read paths. Bake display, bake listing, and the live tail do not depend on it.
- Possibly dangling. Operators MAY purge `broker_sessions` rows for disk pressure; the stamped session_id may then refer to a row that no longer exists, which the appliance treats as truthfully ambiguous — the evidence was recorded; the referenced session was later purged.

#### Continuity boundaries the appliance does not cross

`Runtime continuity is not append-only-evidence continuity`. The appliance keeps these distinct:

- **Broker process continuity.** Each broker process lifetime is one `broker_sessions` row. A new broker is a new row with a new `session_id`. Authority does not flow from the old broker to the new one — the new broker holds the broker-lock, the new broker mints session tokens, the new broker resolves the workspace.
- **Bake process continuity.** A bake's child process is owned by the PAX supervisor for that bake, not by the broker. The broker dying does not kill the bake; the broker starting does not adopt the bake. The bake's PID either still exists on the host (orphan, to be probed by the NEXT broker's reconciliation) or it does not (the OS cleaned it up). Either fact is recorded as evidence.
- **Recorded-evidence continuity.** SQLite rows persist across restart. A `cooks` row recorded by broker A is still in the database when broker B starts. That is append-only evidence — the only continuity the appliance claims.
- **Authority continuity.** None. No authority survives broker restart. Session tokens, in-memory bake registry, and listener state are reconstructed from scratch by the new broker.

The appliance preserves these distinctions even when collapsing them would produce a cleaner-looking operator surface. Cleaner-looking would be less truthful.

### 11.11 Broker startup classification and authority boundary

Every broker process emits a **startup classification** at boot — an observational label naming the runtime state the broker observed when it began writing its own `broker_sessions` row. The label is computed BEFORE the broker mutates any prior row in `broker_sessions` or `cooks`, persisted on the broker's own row in `broker_sessions.startup_classification`, surfaced on the runtime payload at `GET /api/v1/runtime/version`, and printed truthfully on the broker's console at boot.

The label answers exactly one question: **"What did this broker observe about the runtime state it found at boot?"** It does not answer "what happened to the prior broker", does not authorize bake resumption, does not unlock the broker, and does not confer authority on any prior session token, WebSocket attachment, or stale browser tab.

#### `broker_sessions.startup_classification` vocabulary

Five values, frozen. Extending the set requires explicit doctrinal review and corresponding coverage in `verify_phase_ah.ps1`.

| Value | What was observed | What it does **not** mean |
| --- | --- | --- |
| `clean_start` | At boot, `broker_sessions` had zero prior rows AND `cooks` had zero rows in `status='running'`. | Does **not** claim "the workspace is new" — the database file may have been replaced, deleted, or freshly provisioned. The label states only that no prior runtime evidence was visible to this broker. |
| `restart_after_clean_shutdown` | The most-recent prior `broker_sessions` row has `stop_class='clean'`. The prior broker made a positive assertion about its own orderly stop. | Does **not** confer any authority on the new broker. The new broker still minted a fresh session token, started Locked, and cleared the WebSocket registry. Does **not** mean every bake the prior broker started finished cleanly — broker shutdown class and bake lifecycle are independent. |
| `restart_after_no_orderly_stop_record` | The most-recent prior `broker_sessions` row has `stop_class='no_orderly_stop_record'`, or has `stopped_at IS NULL` (this broker's classifier is about to stamp it). | Does **not** name a cause. The doctrine in §11.10 lists the six distinct causes that can produce an absent stop record. The label preserves truthful ambiguity. |
| `startup_after_interrupted_runtime` | At boot, AT LEAST ONE row in `cooks` was in `status='running'`. | Does **not** claim the prior broker crashed. An orderly broker shutdown CAN leave a bake in `running` if the supervisor terminal-write was racing the broker's own teardown. Does **not** mean the new broker is resuming the bake — the new broker does not resume bakes. Reconciliation will walk those rows against on-disk sentinels and record the truthful closure_reason for each (see §11.10). |
| `startup_after_unknown_runtime_state` | The most-recent prior `broker_sessions` row has a `stop_class` that is neither `clean` nor `no_orderly_stop_record` (e.g. a value the future may add, or the column read failed mid-classification). | Does **not** indicate appliance failure. The label exists specifically to preserve truthful ambiguity — guessing one of the more-specific labels would be less truthful. |

#### Decision tree (observe-then-label, no runtime action)

The classifier samples three inputs ONCE, before any startup-classification mutation has run:

1. `priorSessionCount` — `COUNT(broker_sessions)` before this broker's row INSERTs.
2. `priorRunningCookCount` — `COUNT(cooks WHERE status='running')` before reconciliation runs.
3. `mostRecentPrior` — the `broker_sessions` row with `MAX(started_at)`, or `NULL` when `priorSessionCount = 0`.

The label is computed by the first matching branch:

1. `priorSessionCount = 0 AND priorRunningCookCount = 0` → `clean_start`
2. `priorRunningCookCount > 0` → `startup_after_interrupted_runtime`
3. `mostRecentPrior.stop_class = 'clean'` → `restart_after_clean_shutdown`
4. `mostRecentPrior.stop_class = 'no_orderly_stop_record'` OR `mostRecentPrior.stopped_at IS NULL` → `restart_after_no_orderly_stop_record`
5. fallback → `startup_after_unknown_runtime_state`

#### Evidence fields written to `broker_sessions` at INSERT

| Column | Type | What it records |
| --- | --- | --- |
| `startup_classification` | `TEXT` | The frozen-vocabulary label for THIS broker's boot context. |
| `startup_observed_prior_session_id` | `TEXT` | The `session_id` observed as the most-recent prior at THIS broker's boot, or `NULL` on `clean_start`. |
| `startup_prior_running_cook_count` | `INTEGER` | The count of rows in `cooks` with `status='running'` observed BEFORE reconciliation ran. Forensic only. |
| `startup_reconciled_cook_count` | `INTEGER` | The count returned by `Invoke-SentinelReconciliation` on this startup. Written AFTER reconciliation, via COALESCE so the first writer wins. |

All four columns are nullable. Rows from earlier schema versions that predate the columns surface as `NULL` and are never back-filled. Each column is set ONCE per session and never updated thereafter.

#### Authority boundary doctrine

`Authority continuity is NOT implied by persisted-evidence continuity.` Even when a broker observes `restart_after_clean_shutdown`, the following invariants are appliance-internal and load-bearing:

1. **Bearer token resets.** `$Script:SessionToken` is re-minted by `New-SessionToken` at every broker process boot. It is NEVER persisted to disk and NEVER restored from a prior session. Any bearer token issued by a prior broker is opaque garbage to the new broker; `Test-BearerToken` returns `$false` for it. Stale browser tabs fail truthfully on the first protected-route request after restart.

2. **Broker lock starts Locked.** `$Script:BrokerLock` defaults to `'Locked'` at boot. The lock is cleared only by an explicit Windows Hello / PIN re-auth (see §13 Re-authentication doctrine). There is no "remembered authorization" surface; the lock state from a prior broker cannot leak forward.

3. **WebSocket registry is fresh.** `$Script:WsRegistry` is initialized as a fresh empty synchronized hashtable per process. Any WebSocket attachment held by a prior browser tab fails truthfully — the prior socket has been closed by the OS when the prior broker process exited, and the new broker's registry does not know that socket. There is no replay, no rejoin, no rebind.

4. **The startup_classification label authorizes nothing.** It is purely an observational summary of what was found at boot. It does not unlock the broker. It does not authorize a stale tab to continue acting. It does not bypass the locked-at-boot default. It is forensic operator copy, nothing else.

#### Restart-truth surfaces

The same evidence is exposed at every operator-visible surface:

- **Console banner.** The broker prints one truthful classification line at boot, in the form `Broker startup classification: <label> | observed prior broker session <sid> (<stop_class>) | bakes observed in 'running' status at startup: N | bakes reconciled to terminal status this startup: M`. Restorative verbs (`recovered`, `resumed`, `restored`, `healed`) are doctrinally forbidden in operator-facing output.
- **`GET /api/v1/runtime/version`.** The response carries a `brokerSession` sub-object with `sessionId`, `startupClassification`, `observedPriorSessionId`, `observedPriorSessionStopClass`, `observedPriorSessionStoppedAtUtc`, `priorRunningCookCountAtStartup`, `reconciledCookCountAtStartup`, and an `evidenceClassification` map naming each field as `'runtime-only'` or `'observational'`.
- **`broker_sessions` row.** The same evidence persists in the broker's own row for forensic post-mortem.

#### Forbidden vocabulary in operator-facing copy

The following words MUST NOT appear in operator-facing UI text, console banners, runtime payload string values, websocket event strings, or log lines, in any case form, in any locale:

`recovered` · `resumed` · `restored` · `healed` · `reconnected seamlessly` · `reconnect complete` · `continuity maintained` · `continuity preserved` · `session resumed` · `session restored` · `seamless recovery` · `automatic restoration` · `recovered successfully` · `resumed operation` · `services restored` · `runtime stabilized` · `healthy again` · `all healthy` · `all services healthy` · `all systems operational` · `all clear`

These words and phrases are permitted only inside doctrine-negation comment blocks in source files and inside doctrine-negation prose blocks in these guides (where they are listed to forbid their use, identified by the surrounding "MUST NOT appear" / "frozen forbidden" / "forbidden pattern" markers). The reassurance-drift smoke enforces this distinction by parsing the broker with the PowerShell AST and scanning only `StringConstantExpressionAst` values for the single-word subset, and by scanning doc prose with a paragraph-window doctrine-negation allowlist for the multi-word phrase subset — comments and listing paragraphs are exempt.

#### Truthful ambiguity vs synthetic confidence

`startup_after_unknown_runtime_state` is a legitimate outcome. The appliance refuses to smooth ambiguous observations into one of the more-specific labels by guessing. If the operator sees `startup_after_unknown_runtime_state`, the truthful operator response is "inspect `broker_sessions` and recent error logs to understand what state was observed", not "treat this as recovery succeeded".

### 11.12 Stale-authority truthfulness, payload taxonomy, and restart-boundary visibility

This section names the doctrine that governs how the appliance answers a single class of question: **"What is true about the broker right now, and what is true about whatever evidence the SPA / operator is still holding?"** The boundary is load-bearing: Cookbook MUST tell the operator the truth, including when the truth is ambiguous, and MUST NOT smooth a stale picture into a reassuring one.

#### Seven continuity types — distinct, never collapsed

Cookbook recognizes seven independent continuity types. None of them implies any of the others. The appliance, the broker, the SPA, the documentation, and the smoke tests all maintain this separation.

| Continuity type | What it means | What it does **not** mean |
| --- | --- | --- |
| Runtime continuity | The current broker process has been up continuously since `startedAtUtc`. | Does not mean any bake is continuing, any token is honored, or any prior broker's state is still valid. |
| Persisted-evidence continuity | Prior rows in `broker_sessions` and `cooks` are still on disk and readable. | Does not mean the prior runtime is still valid. Persisted evidence is forensic only. |
| Authority continuity | The bearer token / broker lock state / per-op approval the SPA holds is still honored by the current broker. | Does not survive broker restart. A new broker re-mints the session token, starts Locked, and demands fresh re-auth for every sensitive operation. |
| WebSocket continuity | A live WebSocket attachment from a specific browser tab is still open against the current broker. | Does not survive broker restart. The OS closes prior sockets when the prior broker process exits; the new broker's registry does not know them. |
| Evidence continuity | A specific evidence field (e.g. `recentErrors`, `dbSizeBytes`) is still being sampled and reported. | The classification of that field (`runtime-only`, `sampled`, `authoritative`, `configuration`, `derived`, `observational`) names what kind of evidence it is — see §11.8 and the field tables below. |
| Broker continuity | The same broker process the SPA / launcher started against is still the broker handling this request. | Does not mean bake continuity, authority continuity, or runtime continuity. Detected by comparing `brokerSession.sessionId` across `/api/v1/health` snapshots. |
| Bake continuity | A specific bake is still in `status='running'` under the current broker's supervision. | Does not mean the broker that started the bake is still up. A reconciled bake that was orphaned by a prior broker is observable as recorded evidence; the new broker does not resume it. |

The forbidden pattern, in any operator-visible surface: implying that one continuity type proves another. Most common drift: a green "all healthy" pill (runtime continuity) silently implying authority continuity. Cookbook refuses to do this.

#### Stale-authority response shape

When the broker rejects a bearer token, the 401 body carries:

```json
{ "error": "unauthorized", "reason": "session_token_not_recognized" }
```

The `reason` field is drawn from `$Script:BrokerUnauthorizedReasons`, a frozen vocabulary. Current contents:

| Reason | Truthful meaning | Why this exact wording |
| --- | --- | --- |
| `session_token_not_recognized` | The bearer header attached to this request does not match `$Script:SessionToken` of the broker that received the request. | The broker cannot truthfully say *why* the token is unrecognized — it could be a token from a prior broker, a tampered token, an absent token, or a token from a different launcher session. All four shapes produce identical on-wire evidence; the vocabulary names only what is observable. |

Vocabulary the doctrine **explicitly rejects**:

- `token_expired` — implies a TTL the broker does not run.
- `session_ended` — implies a session continuity boundary the broker cannot observe.
- `broker_restarted` — would be a causal claim that the 401 alone cannot support; the same 401 fires for a tampered token from the same broker.
- `reconnect_required` — implies a restoration semantic.
- `reauthenticate` — implies a restoration semantic.

The same vocabulary is forwarded by the SPA to `broker-status.js`, which surfaces it in the topbar pill title. The pill never invents a reason it did not receive.

#### Payload taxonomy: structured operational evidence, not narrative

Every operator-facing payload that summarizes broker state MUST classify each field by the kind of evidence it represents. The classification vocabulary, in canonical order:

| Class | When to use it | Example fields |
| --- | --- | --- |
| `authoritative` | The value is the canonical source of truth that other surfaces derive from. | `workspaceFolderPath`. |
| `runtime-only` | The value is meaningful only for the lifetime of THIS broker process and is regenerated on every restart. | `brokerSession.sessionId`, `startedAtUtc`, `uptimeSeconds`, `activeCooks`, `recentErrors`. |
| `sampled` | The value was read at request time from a mutable source (disk, OS, network) and may be `null` if the sample failed. | `dbSizeBytes`. |
| `configuration` | The value is a static appliance constant set at broker boot. | `recentErrorCapacity`. |
| `derived` | The value is computed from other fields in this same payload (no new evidence). | `status` (derived from `recentErrors`, `recentErrorOverflowCount`). |
| `observational` | The value is a label this broker assigned by inspecting prior persisted evidence. It is forensic, not authoritative. | `brokerSession.startupClassification`. |

The taxonomy is exposed inline on every health and runtime payload as an `evidenceClassification` sub-object whose shape mirrors the payload itself. Operators and the SPA never have to guess whether a field is live, sampled, persisted, or interpreted — the payload says so explicitly.

Forbidden taxonomy collapses:

- Putting a runtime-only field next to an authoritative one without naming the difference.
- Synthesizing a `derived` field that silently combines `authoritative` + `runtime-only` + `observational` evidence into one boolean (e.g. `allHealthy: true`).
- Re-classifying a field after-the-fact under a different label to make the picture look better.

#### Restart-boundary visibility

A broker restart is a real lifecycle boundary. The appliance does NOT hide it. The truthful evidence is surfaced on three distinct planes, each with its own classification:

1. **`GET /api/v1/health`** (unauthenticated by design — see §11.8). The response body carries a `brokerSession` sub-object containing the current broker's `sessionId` (`runtime-only`), `startedAtUtc` (`runtime-only`), and `startupClassification` (`observational`). The SPA's boot-time health probe captures these values once; they NEVER change for the lifetime of a given browser tab because the SPA does not re-probe. A subsequent stale-tab fetch will receive a 401 (the bearer is no longer recognized) before it ever re-observes broker session evidence.

2. **`GET /api/v1/runtime/version`** (bearer-gated). The response carries a richer `brokerSession` sub-object — see §11.11 — including the prior-session-id, prior-session stop-class, prior running-bake count, reconciled bake count, and the same `startupClassification`. Every field is tagged `runtime-only` or `observational` in the inline `evidenceClassification` map.

3. **Console banner**. Truthful classification line printed once at broker boot — see §11.11.

4. **Topbar pill**. When the SPA receives a 401, `broker-status.js` surfaces the bounded reason vocabulary from §11.12 ("Broker rejected the session token (HTTP 401). Reason: session_token_not_recognized. SPA bootstrapped against broker session `<sid>`. Relaunch PAX Cookbook from the launcher to obtain a fresh token."). The pill never auto-clears, never auto-retries, never re-acquires authority.

#### What the appliance MUST NOT do

These behaviors are doctrinally forbidden. The smoke tests scan for them as permanent regression gates:

1. **Auto-reconnect.** No `setTimeout` / `setInterval` / WebSocket-onclose retry that silently re-establishes a connection. A closed socket stays closed until the chef relaunches.
2. **Auto-reauth.** No background token refresh, no "remember me" toggle, no re-acquisition of authority from a persisted location.
3. **Token-from-disk.** No file-backed session token. The token lives only in `$Script:SessionToken` for the lifetime of the broker process.
4. **Continuity synthesis.** No "looks healthy → must be the same broker" inference, no "no errors in the queue → must be safe to mutate" inference.
5. **Restorative wording.** The frozen forbidden-word list (single words `recovered`, `resumed`, `restored`, `healed`; multi-word phrases `reconnected seamlessly`, `reconnect complete`, `continuity maintained`, `continuity preserved`, `session resumed`, `session restored`, `seamless recovery`, `automatic restoration`, `recovered successfully`, `resumed operation`, `services restored`, `runtime stabilized`, `healthy again`, `all healthy`, `all services healthy`, `all systems operational`, `all clear`) is enforced by the reassurance-drift smoke against the broker's `StringConstantExpressionAst` values, the SPA's source, and operator-facing documentation prose. Doctrine-negation listings (paragraphs that enumerate the forbidden vocabulary in order to forbid it) are explicitly exempt.
6. **Silent failure smoothing.** The topbar pill never falls back from `unauthorized` to `ok` on an unrelated 2xx fetch. Sticky negative-evidence is the design.
7. **Collapsing the seven continuity types.** Every doctrinal surface keeps them distinct.

#### Truthful operator instruction

When the operator sees the pill flip to `unauthorized`, the truthful instruction is exactly one sentence: **"Relaunch PAX Cookbook from the launcher to obtain a fresh token."** Cookbook does not offer an in-browser "Reconnect" button, a "Retry" button, or a token-refresh flow. There is no path back to authority that does not pass through a real launcher session.

---

## 12. Chef's Keys

A **Chef's Key** is a named binding between an Entra app registration and a workload credential that lives in Windows Credential Manager under the current Windows account. The on-disk and database identifier for a Chef's Key is `authProfileId` (table `auth_profiles`); this guide uses *Chef's Key* in prose and the literal identifiers when referring to storage. A recipe references a Chef's Key by its `authProfileId`; the credential bytes themselves never appear in the recipe, in the bake's `cook.log`, in command-line arguments, or anywhere on disk that Cookbook owns.

### 12.1 Chef's Key shape

Chef's Keys live in `<Workspace>\Database\cookbook.sqlite` (`auth_profiles` table). Each row carries:

| Column | Meaning |
| --- | --- |
| `authProfileId` | Server-stamped GUID. Immutable. |
| `name` | Chef-supplied display name. Must be unique. |
| `mode` | `AppRegistrationSecret` or `AppRegistrationCertificate`. **Immutable after creation.** |
| `tenantId` | Entra tenant GUID. |
| `clientId` | App-registration client (application) GUID. |
| `credManTarget` | The Windows Credential Manager target name (Secret mode only). Set by the broker when the secret is bound. |
| `certThumbprint` | SHA-1 thumbprint of the cert in the local certificate store (Certificate mode only). |
| `certStore` | Store path, e.g. `LocalMachine\My` (Certificate mode only). |
| `description` | Optional free-form text. |
| `lastVerifiedAt` / `lastVerifiedResult` | Outcome of the most recent **structural** test. Never a permission claim. |
| `createdAt` / `updatedAt` | Broker-stamped UTC timestamps. |

`WebLogin`, `DeviceCode`, and `ManagedIdentity` are recipe-level auth modes — they are **not** persisted as Chef's Keys, because they carry no chef-owned credential.

### 12.2 Lifecycle (UI)

Open the **Chef's Keys** page from the nav rail.

1. **Create.** Click **New Chef's Key**. Choose `AppRegistrationSecret` or `AppRegistrationCertificate`, fill in tenant + client GUIDs (and, for Certificate mode, thumbprint + store), then Save. The row appears with `bound: false` / `thumbprint set` accordingly.
2. **Bind a secret** (Secret mode only). Click **Bind / Replace secret** on the row. Paste the app-registration secret value into the modal and Save. The broker writes the value to Windows Credential Manager under your Windows account. The textarea is cleared the moment the request is submitted; Cookbook never displays, copies, or exports the secret again.
3. **Edit.** Click **Edit**. Name, tenant, client, certificate fields, and description are editable. **Mode is immutable** — the broker refuses any update that changes `mode`, and the UI renders it as a read-only row.
4. **Test.** Click **Test**. The broker performs a *structural* check only: for Secret mode it confirms the Credential Manager entry exists and is readable; for Certificate mode it confirms the cert is present in the named store and has a private key. `lastVerifiedAt` / `lastVerifiedResult` are stamped from the outcome.
5. **Remove secret** (Secret mode only). Click **Remove secret**. The broker deletes the Credential Manager entry but keeps the Chef's Key row.
6. **Delete.** Click **Delete**. The broker removes both the Chef's Key row and (for Secret mode) the bound Credential Manager entry. Recipes that reference the deleted Chef's Key begin to fail at bake time with `unknown_authProfileId`; rebind them to a valid Chef's Key.

### 12.3 What the test endpoint does **not** do

- **No** Entra token request. The broker never trades the secret for a token during a Chef's Key test.
- **No** Microsoft Graph or PAX call.
- **No** permission inference. A passing test means the bytes are present and readable; it does **not** mean the workload has Graph permissions or that PAX will succeed.

This boundary is intentional. Permission-bearing tests would require a Graph call, which would consume a permission grant Cookbook has not been given. The first real authorization check happens when PAX itself runs.

### 12.4 What a recipe sees

A recipe with `auth.mode = "AppRegistrationSecret"` and `auth.authProfileId = "<id>"` is persisted exactly that way — no secret, no cert blob, no Credential Manager target string. At bake time the broker:

1. Resolves the Chef's Key row by `authProfileId`.
2. Reads the secret from Credential Manager (Secret mode) or stamps `-ClientCertificateThumbprint` (Certificate mode).
3. For Secret mode: sets `GRAPH_CLIENT_SECRET` on the **child process's environment block** before spawning PAX. The secret never appears on argv, in `cooks.command_argv_redacted`, or in any bake-folder file.
4. Spawns PAX with the redacted argv recorded in the `cooks` row.

### 12.5 ManagedIdentity is host-dependent

`ManagedIdentity` is selectable in the recipe editor but is only usable when the recipe runs on a host that exposes a workload-identity token (Fabric, Azure VM with a system-assigned MI, Azure Container Apps, etc.). Cookbook **does not emulate Managed Identity locally**. A `ManagedIdentity` recipe with `executionMode = "local-manual"` will fail when PAX tries to fetch a token from a metadata endpoint that does not exist.

### 12.6 Execution mode

Every recipe carries a top-level `executionMode`:

| Value | Where the bake is expected to run | Interactive auth allowed? |
| --- | --- | --- |
| `local-manual` | This Cookbook UI, chef-triggered. Also the only saved value compatible with the Schedule card (§7.3) — a recipe is registered with Windows Task Scheduler **without** changing its saved executionMode. | Yes (WebLogin / DeviceCode) for manual bakes; the Schedule card itself refuses to register WebLogin / DeviceCode recipes. |
| `local-scheduled` | Internal projection mode used by the scheduled-task wrapper at fire-time so the projection hash captures the scheduled-spawn context. Not selectable as a saved recipe value via the standard authoring path; the broker spawns local bakes for either `local-manual` or `local-scheduled` projections. | **No** — interactive prompts hang scheduled tasks. Use a profile or MI. |
| `fabric-hosted` | Microsoft Fabric workspace. | **No.** |
| `azure-hosted` | Azure compute. | **No.** |

The broker refuses to manually spawn any bake whose `executionMode` is not `local-manual`. The UI's **Bake Recipe** button is disabled for non-local recipes; those recipes are expected to run from their host environment, where Cookbook is not present.

---

## 13. Re-authentication doctrine

Cookbook operates two **distinct gates** that are easy to confuse but are not the same thing:

1. **The broker lock.** Process-wide, broker-scoped. Cleared by a Windows Hello / PIN prompt. Governs whether *any* mutating route is allowed to run.
2. **Per-operation approval.** Per-request, op-class-scoped. Cleared by a fresh Windows local-user verification at the moment the operator takes a sensitive action. Governs whether *this specific operation* is allowed to proceed.

A locked broker rejects every mutating route with HTTP 423 until you unlock it. An unlocked broker still demands a fresh per-op verification for every sensitive request — unlocking once does **not** unlock everything for the session.

### 13.1 What forces a fresh re-auth

The broker requires a fresh Windows local-user verification at each of the following moments:

- The app is launched (broker-lock acquisition).
- Every **manual bake** of every recipe (no "bake again without prompt" path).
- Every **Chef's Key mutation**: create, update, delete.
- Every **secret bind or replace**.
- Every **secret removal**.
- Every **scheduled-task config** mutation — `PUT` or `DELETE` on `/api/v1/recipes/<id>/scheduled-task`.
- Every **update apply** (reserved; the live apply path is not yet implemented).

Approval of operation A never carries over to operation B. The window is closed the moment the request settles.

### 13.2 What this is **not**

- **Not a password collection.** Cookbook never reads, hashes, compares, proxies, or stores your Windows password. The verification is delegated to Windows itself (Windows Hello / PIN / biometric, falling back to the local-user password prompt that Windows owns).
- **Not workload authorization.** A successful Windows verification proves *you* are at the keyboard. It does **not** prove the Entra workload has Graph permissions, nor that PAX will succeed.
- **Not a session.** There is no "remember me" toggle. Each sensitive op is its own gate.
- **Not browser-scoped.** A second browser tab on the same broker sees the same lock state because the gate lives in the broker process.

### 13.3 What the SPA shows you

When the broker is locked, every mutating fetch returns HTTP 423 with body `{ "code": "brokerLocked", ... }`. The SPA's lock overlay catches that event and presents a single action: **Unlock with Windows Hello**. There is no dismiss button. The overlay disappears only when the broker confirms `state: "Unlocked"`.

When an unlocked broker rejects a specific op with HTTP 401 `{ "code": "reAuthRequired", ... }`, a transient overlay surfaces the op class and the verification verdict (e.g. `Cancelled`, `Failed`, `Timeout`). Acknowledging the overlay closes it; the operation is **not** retried automatically — the chef re-issues the request, which re-prompts.

### 13.4 Forensic record

Every successful and failed verification is recorded in `cookbook.sqlite` (`auth_events` table) with op class, verdict, and UTC timestamp. The body of the verification is never recorded; only the verdict.

### 13.5 Time-anomaly aware re-locking

The broker re-locks itself on prolonged idle (§13.1) and on **observed runtime-continuity discontinuity**. The two conditions are evaluated together by the same lazy sweep that runs whenever the broker handles a `/api/v1/broker/lock-state` request or any mutating route.

The sweep compares two anchors captured at the last successful operator activity: a wall-clock UTC stamp and a monotonic stopwatch tick. If the wall delta and the monotonic delta disagree past the threshold described in §11.8 (*Time-anomaly surface*), the sweep classifies the discrepancy with one of the three frozen `timeAnomaly.kind` values and transitions the broker to `Locked` regardless of how recently the operator was active. The classification reports what the appliance observed about the clocks, not what physically happened to the host — `sleep_or_pause_gap` names the observed pattern of monotonic stall plus wall progression, *consistent with* sleep or VM pause; it does not assert that the host was definitely asleep.

The doctrine: if runtime continuity becomes ambiguous, auth freshness becomes ambiguous. Cookbook prefers **truthful re-lock over optimistic continuity forgiveness**. A laptop that has been suspended for four hours, an NTP step that adjusted the wall clock, or a VM that was paused mid-session all produce the same response — a fresh Windows Hello / PIN verification is required to re-establish the broker's runtime trust.

`GET /api/v1/broker/lock-state` carries the classification on its `timeAnomaly` field when the most recent sweep re-locked due to an observed discontinuity. The shape mirrors `/api/v1/health`'s `timeAnomaly`. When the sweep re-locked for prolonged idle alone (no clock discontinuity observed), `timeAnomaly` is `null` — a normal timeout is not an anomaly.

Successful re-auth clears the recorded anomaly. A fresh verification proves the operator is present right now, so whatever the clocks did during the locked interval is no longer operationally relevant; the broker bumps both clock anchors together and `timeAnomaly` returns to `null` on the next poll.

The appliance does **not** expose a way to suppress, smooth, or bypass the anomaly check. There is no field, no header, no query parameter, and no affordance that permits a sensitive operation to proceed while a classified discontinuity is active. The chef re-authenticates normally — the same gesture that clears a prolonged-idle lock clears an anomaly-triggered lock.

---

## 14. Clean uninstall

The simplest path is to use the **Uninstall PAX Cookbook** Start Menu shortcut. Open the Start Menu, expand the **PAX Cookbook** folder, and click **Uninstall PAX Cookbook**. A confirmation dialog appears that spells out exactly what will and will not be removed; the default focus is **No**, so accidentally pressing Enter cancels safely. Clicking **Yes** removes the installed appliance (App/, Backups/, Updates/) and all PAX Cookbook shortcuts. Workspace folders, scheduled tasks, and any external export folders are preserved.

### 14.1 Verifying the confirmation dialog without uninstalling

If you only want to verify that the **Uninstall PAX Cookbook** shortcut surfaces the confirmation dialog correctly — for example as part of a visual review of a freshly installed appliance — do **not** click **Yes** on a working install. The supported safe check is:

1. Open the Start Menu, expand the **PAX Cookbook** folder, click **Uninstall PAX Cookbook**.
2. Confirm the warning dialog appears.
3. Confirm the default focus / default button is **No**.
4. Read the dialog text and confirm it names what uninstall removes (the installed appliance — App/, Backups/, Updates/ — and the PAX Cookbook shortcuts) and what it preserves (workspace folders, scheduled tasks, external export folders).
5. Click **No** (or press Enter, which selects the default).
6. Confirm PAX Cookbook remains installed: the Start Menu **PAX Cookbook** folder is still present with its five shortcuts, and **PAX Cookbook** still launches normally.

That sequence exercises the shortcut, the relocated installer copy in `%TEMP%\PAXCookbookUninstall-<UTC-timestamp>-<8-hex>\`, and the confirmation prompt without removing the install tree.

### 14.2 Destructive uninstall verification (disposable install only)

Verifying that **Yes** actually removes everything is a destructive test. Run it only against a disposable install / disposable workspace — never against the install you are about to use for real work:

1. Use a disposable install on a disposable workspace.
2. Export or back up anything you would want to keep first; the workspace itself survives, but any in-flight bake artifacts and locally exported data should be copied off the machine before you proceed.
3. Open the Start Menu, expand the **PAX Cookbook** folder, click **Uninstall PAX Cookbook**.
4. In the confirmation dialog, click **Yes** (this is the destructive step — only do it in the disposable test).
5. Confirm `%LOCALAPPDATA%\PAXCookbook\App\`, `%LOCALAPPDATA%\PAXCookbook\Backups\`, and `%LOCALAPPDATA%\PAXCookbook\Updates\` are gone, and that the Start Menu **PAX Cookbook** folder is gone.
6. Confirm the disposable workspace folder is still present on disk (Cookbook does not delete chef-owned workspace contents).
7. Confirm any data the appliance had exported outside the workspace (Purview / Entra / Microsoft 365 exports to external folders) is also still present.

Both verifications use the same shortcut. The only difference is which button you click; the install on which you click it is the difference between a check and a wipe.

### 14.3 Command-line uninstall

If you prefer the command line:

```powershell
pwsh -File <release-folder>\app\install\Install-PAXCookbook.ps1 uninstall
```

Default uninstall removes `<InstallRoot>\App\`. `Backups\` and `Updates\` are preserved. The shortcuts (Start Menu folder and optional Desktop shortcut) are removed.

To remove everything under `%LOCALAPPDATA%\PAXCookbook\`, add `-Purge`. The Start Menu **Uninstall PAX Cookbook** shortcut always invokes the equivalent of `-Purge`:

```powershell
pwsh -File <release-folder>\app\install\Install-PAXCookbook.ps1 uninstall -Purge
```

The uninstall **does not** delete:

- your **workspace folder** (chef-owned). Workspaces live at user-chosen paths the installer does not track. To remove a workspace, delete its folder by hand.
- any external folder where PAX Cookbook previously exported data (Purview / Entra / Microsoft 365 exports outside a workspace).
- Purview, Entra, or Microsoft 365 data in your tenant.

If you want a truly empty machine afterward, delete those by hand. The uninstaller refuses to touch them because they may carry chef data or chef-managed evidence.

The uninstaller never edits the registry and never disables a service.

**One small leftover artifact.** When uninstall is launched via the Start Menu **Uninstall PAX Cookbook** shortcut, the running PowerShell process holds a handle on the installer file inside `<InstallRoot>\App\install\`. To free that handle, the shortcut copies the installer to a fresh folder named `%TEMP%\PAXCookbookUninstall-<UTC-timestamp>-<8-hex>\` and runs the relocated copy, which then removes the install tree. That `%TEMP%` folder is the one acceptable leftover; Windows cleans `%TEMP%` over time, but you can also delete the folder by hand any time after uninstall completes. Command-line uninstall (`-Mode uninstall -Yes -Purge`) does not relocate and does not produce this artifact.

**Scheduled tasks.** If you registered any recipes with Windows Task Scheduler via the Schedule card (§7.3), those tasks live under `\PAXCookbook\<recipeId>\` in Windows Task Scheduler and **survive a Cookbook uninstall** — they are chef-authored evidence and the uninstaller does not touch them. Before uninstalling, open each scheduled recipe in Cookbook and click **Delete schedule**, or remove the `\PAXCookbook\` folder from Task Scheduler (Computer Management → Task Scheduler → Task Scheduler Library → PAXCookbook). If you leave them in place, the task actions will point at a wrapper script under the (now-removed) `<InstallRoot>\launcher\` directory; Windows will run the action, the wrapper will not be there, and the run will fail until you delete the task by hand.

---

## 15. What Cookbook never does

This is the short form of the forbidden-actions contract codified in [`temp/PAX_Cookbook_Planning/CLEAN_MACHINE_FIRSTRUN_HARDENING.md`](../temp/PAX_Cookbook_Planning/CLEAN_MACHINE_FIRSTRUN_HARDENING.md). **The guarantees below apply to Cookbook itself — its installer, launcher, broker, and the SPA the broker serves — as part of Cookbook's own installation, launch, and runtime lifecycle. They do not constrain the bundled PAX script when invoked by a recipe. PAX-owned runtime behavior (notably PAX's per-user Python bootstrap for rollup workflows) is described in §3.1 and in [docs/RELEASE_PACKAGE.md §11a](RELEASE_PACKAGE.md#11a-bundled-workflow-runtime-behavior).**

As part of its own lifecycle, Cookbook does not perform:

- No elevation, no `RunAs`, no UAC prompt.
- No registry writes (HKLM or HKCU).
- No services. No autoruns.
- **No scheduled tasks except the ones the chef explicitly creates via the Schedule card.** Cookbook installs, updates, and uninstalls without registering, mutating, or removing a single Windows Scheduled Task on its own. The only path that touches Task Scheduler is `app/install/Register-PAXScheduledRecipe.ps1`, called by the broker exclusively in response to a chef-initiated `PUT` or `DELETE` on `/api/v1/recipes/<id>/scheduled-task`. The installer creates no tasks; the launcher creates no tasks; the broker has no background loop that creates tasks. See §7.3.
- No PATH or environment-variable mutation (Machine or User scope).
- No `ExecutionPolicy`, Defender, firewall, or Authenticode-trust-store mutation.
- No silent dependency auto-install (`winget`, `Install-Module`, `Install-Package`, `choco`, `pip`, `npm`, `dotnet tool install`).
- No outbound network calls from the installer or the launcher. The broker only fetches the update manifest and update packages, and only when the manifest URL is configured.
- No telemetry. No support-ticket integration. No remote diagnostics.
- No SaaS surface. No cloud onboarding. No embedded AI assistant.

These guarantees are enforced statically by `_temp/phase_w_verification/verify_phase_w.ps1` against Cookbook's own source tree (installer, launcher, broker, SPA). The harness deliberately does not scan `app/resources/pax/` because PAX is bundled-but-separate content with its own ownership chain. If you find code in Cookbook that violates any of the guarantees above, please file an issue.

---

## 16. Where evidence lives

When something goes wrong, the following are the chef-owned files to consult:

| Symptom | Evidence file |
| --- | --- |
| Install / update problems | `<InstallRoot>\install.log` |
| Bake ran but failed | `<Workspace>\Cooks\<cookId>\` (logs + sentinels) |
| Bake history queries | `<Workspace>\Database\cookbook.sqlite` (any SQLite client) |
| Workspace-lock disputes | `<Workspace>\Runtime\workspace.lock`, `workspace.lock.acquire` |
| Bootstrap corruption | `%APPDATA%\PAXCookbook\cookbook.bootstrap.json.broken-<UTC>` |
| Update staging | `<InstallRoot>\Updates\<version>\` (ZIP, `.sha256`, `.metadata.json`, optional `.sig`, `.signer.json`) |
| Release identity | `<InstallRoot>\App\VERSION.json` |

Every one of these is plain text, plain JSON, or SQLite. There is no proprietary binary log format. No file is encrypted by Cookbook.

---

## 17. Operational survivability doctrine

This section names the doctrine the appliance applies to procedures that span more than the broker's own process lifetime — primarily update apply, rollback, scheduler operations, package trust, and recovery flows. It exists so that operator-facing prose, log strings, and payload fields cannot drift toward the language of restoration platforms (see §17.5 for the enumerated forbidden phrases) in places where the appliance is in fact NOT making such a claim. The survivability vocabulary, the evidence classes, the four-step lifecycle, and the forbidden-phrase list are declared in [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1) and are consumed by the update-apply, packaging, scheduler, diagnostics, and recovery surfaces named throughout this section.

> *Slice-label note. Some sections below reference internal slice labels such as `AI.C1`, `AI.C2.3`, or `AI.C3.2`. Those labels appear only when adjacent to a concrete code anchor — a file path under `app/broker/Survivability/`, a smoke script under `_temp/phase_ai_verification/`, an HTML comment used by smokes to bound enumeration regions, or a PowerShell `$Script:` identifier. They are code references, not a development timeline. The behavior described in this section is the v1 contract.*

### 17.0 Inheritance from §11 Recovery flows

§11 already establishes the appliance's posture on runtime continuity ("Runtime continuity is not persisted-evidence continuity"; "Authority does not flow from the old broker to the new one"; "broker-process continuity, bake-process continuity, persisted-evidence continuity, and authority continuity are kept distinct"). §17 extends that posture from the runtime plane to the survivability plane — to procedures that explicitly mutate on-disk state (update apply, rollback, package discard) or operate across broker restarts (scheduler, recovery). The §11 invariants continue to apply unchanged; §17 names a strictly larger surface.

### 17.1 What §17 covers

- Update apply (operator-driven; not auto-polled, not auto-installed).
- Rollback of a partially-applied update.
- Discard of a staged-but-not-applied update package.
- Survivability-plane payload fields and SQLite columns added by the update-apply surface and later.
- Operator-facing prose in [OPERATOR_GUIDE.md](OPERATOR_GUIDE.md), [TROUBLESHOOTING.md](TROUBLESHOOTING.md), and the SPA, where survivability-plane state is described.

### 17.2 What §17 does NOT cover

- §17 does NOT auto-introduce any runtime behavior on its own. The vocabulary, evidence classes, four-step lifecycle, and forbidden-phrase list declared here are doctrine; runtime behavior lives in the surfaces that consume them (update apply, staged-package discard, scheduler, diagnostics, recovery). §17 does NOT modify `Invoke-UpdateMode`, the install/update apply path, the rollback procedure, the scheduler, or any payload field except through those consuming surfaces.
- §17 does NOT promise that an interrupted update will resume itself. The four-step lifecycle is operator-driven; the broker never auto-initiates an apply, a rollback, or a discard.
- §17 does NOT alter §11. Bake-process continuity, broker-process continuity, persisted-evidence continuity, and authority continuity remain as defined in §11.
- §17 does NOT introduce a doctrine-enforcement runtime. Discipline is gated at smoke time by `_temp/phase_ai_verification/smoke_ai_c1.ps1`, not at request time.

### 17.3 The five evidence classes

Every survivability-plane payload field MUST be tagged with EXACTLY ONE of these five classes. The runtime broker-session block already uses two of them (`runtime-only`, `observational`). The full enumeration is declared at [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1) in `$Script:SurvivabilityEvidenceClasses`.

- **`runtime-only`** — Field is computed at process boot and lost on broker exit. Survives no restart boundary. Example: `brokerSession.sessionId`.
- **`observational`** — Field is a first-order observation of state at the moment of capture. Carries no authority. May be stale by the time a reader observes it. Example: `brokerSession.startupClassification`.
- **`authoritative`** — Field is the result of a verification step that directly inspected the authoritative target state. Read by operators and downstream code as ground truth. Example: `installState.lastUpdateApplyVerification`.
- **`configuration`** — Field reflects on-disk configuration the broker read at startup. Operator-controlled, not broker-controlled. Example: `trust.allowlistPresent`.
- **`historical`** — Field is an append-only record of a prior event. Forensic. Never overwritten. Example: `installState.lastUpdateApplyObservation`.

Rejected alternatives: `inferred`, `reconstructed`, `synthesized`, `confirmed`. The first three would imply unbounded recursion / replay reconstruction / synthetic confidence; the last collapses observation and verification into a single claim. The appliance keeps those distinct.

### 17.4 The four-step survivability lifecycle

A single mutation in the survivability plane is observed across FOUR distinct moments, each with its own evidence class. The full vocabulary is declared at [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1) in `$Script:UpdateLifecyclePhases`, `$Script:RollbackLifecyclePhases`, and `$Script:StagedPackageDiscardPhases`.

1. **`*_requested`** — The operator's intent has been observed by the broker. Not proof anything has happened. Evidence class: observational.
2. **`*_started`** (or `*_attempted` for discard) — The procedure has begun executing. Not proof of completion. Evidence class: observational.
3. **`*_observed_<partial|failed|complete>`** — A first-order observation of what actually happened on the filesystem / process / SQLite. This is NEVER a success claim. `*_observed_complete` means the procedure ran to its end; it does NOT mean the procedure achieved its goal. Evidence class: observational.
4. **`*_verification_<passed|failed>`** — A SEPARATE check performed AFTER the observed step that directly inspects the authoritative target state (file hashes, package presence, schema rows). This is the only step entitled to make a truth claim about outcome. Evidence class: authoritative.

The staged-package-discard vocabulary uses a slightly different fourth step — `package_absence_verified` — because the discard procedure is AGAINST a target: the verification is that the package is GONE, not that the discard "succeeded".

Forbidden lifecycle suffixes (rejected by survivability doctrine, gated by smoke subtest 11): `*_succeeded`, `*_completed`, `*_good`, `*_done`, `*_ok`. Each of those fuses observed and verified into one terminal-success claim. The appliance does not make terminal-success claims at the lifecycle layer.

### 17.5 Forbidden survivability-language phrases

These 17 phrases are anti-restoration drift words specific to the survivability plane. They EXTEND the runtime-plane reassurance-drift list without modifying it. The single source of truth is `$Script:SurvivabilityForbiddenPhrases` at [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1); the survivability smoke parses it from there and uses it to scan PowerShell string constants (comments exempt), JS sources (with comments stripped), and the non-enumeration regions of this guide and [TROUBLESHOOTING.md](TROUBLESHOOTING.md). Doctrine-affirming negations in code comments — where the comment explicitly says the appliance does NOT do the forbidden thing — remain permitted; the smoke does not see them.

<!-- AI.C1: forbidden-phrase enumeration BEGIN -- smoke_ai_c1.ps1 excludes this region -->

- `auto-repair`
- `self-heal`
- `self-healed`
- `repair completed`
- `successfully recovered`
- `fully restored`
- `automatically recovered`
- `recovered from corruption`
- `silent recovery`
- `transparent recovery`
- `seamlessly resumed`
- `update applied automatically`
- `rolled back successfully`
- `scheduler recovered`
- `task auto-renewed`
- `credential refreshed automatically`
- `package auto-trusted`

<!-- AI.C1: forbidden-phrase enumeration END -->

The list is deliberately narrow. Each phrase names a specific class of restoration-platform language — phrasing that would imply the appliance silently restored, healed, or refreshed state without operator action. The appliance does none of those things; operator-facing surfaces must not say it does.

### 17.6 Deferred runtime consumers

The vocabulary is a planning anchor. It declares names; the consuming surfaces wire them into live runtime payloads. The runtime-consumer surface evolved across several builds: live 501 refusal (§17.7), dryRun preview (§17.8), append-only request observations (§17.9), refusal-branch observation coverage (§17.10), and the C3 packaging-trust surfaces (§17.15–§17.17). The arrays that are not yet consumed — `$Script:RollbackLifecyclePhases`, `$Script:StagedPackageDiscardPhases`, the unconsumed indexes of `$Script:UpdateLifecyclePhases` and `$Script:SurvivabilityEvidenceClasses`, and `$Script:SurvivabilityForbiddenPhrases` outside of guard scans — remain a contract for later survivability surfaces (rollback, discard, scheduler-lifecycle, diagnostics, recovery).

The live consumer summary:
- §17.7 wires `$Script:UpdateLifecyclePhases[0]` and `$Script:SurvivabilityEvidenceClasses[1]` into the live 501 path of `POST /api/v1/updates/apply`.
- §17.8 introduces `$Script:UpdateEvaluationPhases` (categorically distinct from the four-step lifecycle arrays — see §17.8 for the categorical-split rationale) and wires its single value into the dryRun preview path of the same route.
- §17.10 wires `$Script:UpdateRefusalPhases` into the three pre-execution refusal branches (401, 409, 503).

### 17.7 Update apply request visibility

The 501 response from `POST /api/v1/updates/apply` (which already returned `error = apply_not_yet_implemented` when the active-bake precondition would pass) carries three additive fields wired from the survivability vocabulary:

- **`lifecycle_phase`** — set to `update_apply_requested`, sourced from `$Script:UpdateLifecyclePhases[0]` in [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1). The value is read by index from the frozen array; it is never independently hard-coded in [Routes/Updates.ps1](../app/broker/Routes/Updates.ps1).
- **`lifecycle_phase_source`** — set to the literal string `UpdateLifecyclePhases[0]`. An audit field naming the array and index the `lifecycle_phase` value was read from. Operators and downstream tooling can use it to confirm the value's provenance without re-deriving it.
- **`evidence_classification`** — set to `observational`, sourced from `$Script:SurvivabilityEvidenceClasses[1]` in the same vocabulary file. The `*_requested` step is observational by §17.4's definition: it confirms that the broker observed the chef's POST, nothing more.

What these fields explicitly do NOT mean. The 501 status code and the `apply_not_yet_implemented` error wording are unchanged. The `lifecycle_phase = update_apply_requested` field does NOT mean apply has been accepted for execution, the chef's POST has graduated to `*_started`, the staged package was modified, the installer was launched, the install tree was touched, the Backups\ tree was touched, the scheduler was notified, rollback became available, or any verification of the target state occurred. None of those things happen on this path.

Why this path leaves apply machinery unimplemented. Wiring the first survivability vocabulary into a live wire-format payload, without simultaneously introducing apply behavior, is the narrowest possible proof that the survivability vocabulary works end-to-end. It makes the contract visible to operators (and to SPA developers consuming the response) before any mutation logic exists. The remaining `*_started`, `*_observed_<partial|failed|complete>`, and `*_verification_<passed|failed>` phases are reserved for later surfaces — see §17.6 for the deferred-work list.

Smoke surface. The smoke at `_temp/phase_ai_verification/smoke_ai_c2_1.ps1` gates: (a) the 501 status and the `apply_not_yet_implemented` error remain intact; (b) `lifecycle_phase` is sourced via an index expression on `$Script:UpdateLifecyclePhases`, not a hard-coded string; (c) the value resolves to exactly `update_apply_requested`; (d) the 501 response body carries no other lifecycle phase string and no terminal-success suffix; (e) `Invoke-UpdatesApply` introduces no install/update mutation primitive; (f) `Invoke-UpdateMode` is still defined in [app/install/Install-PAXCookbook.ps1](../app/install/Install-PAXCookbook.ps1) and is NOT called from [Routes/Updates.ps1](../app/broker/Routes/Updates.ps1); (g) no SQLite schema change appears in `Routes/Updates.ps1`; (h) no runtime/health endpoint paths are newly referenced from `Routes/Updates.ps1`; (i) the survivability-vocabulary smoke still passes against the modified surface.

### 17.8 DryRun evaluation request visibility

The dryRun preview path of `POST /api/v1/updates/apply?dryRun=true` wires a separate survivability vocabulary array. The existing 200 response (which already returned `dryRun`, `wouldRefuse`, `reason`, `detail`, `activeCookCount`, `activeCooks`, `stagedPackages`, `snapshotError` for SPA preview convenience) also carries three additive fields:

- **`lifecycle_phase`** — set to `update_apply_evaluation_requested`, sourced from `$Script:UpdateEvaluationPhases[0]` in [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1). The value is read by index from the frozen array; it is never independently hard-coded in [Routes/Updates.ps1](../app/broker/Routes/Updates.ps1).
- **`lifecycle_phase_source`** — set to the literal string `UpdateEvaluationPhases[0]`. An audit field naming the array and index the `lifecycle_phase` value was read from.
- **`evidence_classification`** — set to `observational`, sourced from `$Script:SurvivabilityEvidenceClasses[1]`. The chef's dryRun request has been observed by the broker, nothing more.

The semantic decision: a dryRun is NOT an apply request. When the chef passes `?dryRun=true`, they explicitly signal "tell me about applying" — they did NOT signal "apply." Conflating the two intents under a single lifecycle phase would be a wire-format lie and would inflate any audit query that counts apply attempts. The dryRun path therefore uses a separate vocabulary array `$Script:UpdateEvaluationPhases` rather than reusing `$Script:UpdateLifecyclePhases[0]` (which is the live apply path's phase). The two arrays are categorically distinct: lifecycle arrays describe the four-step progression of a mutation event (§17.4); the evaluation array describes a single observation point with no progression.

What this path explicitly does NOT mean. The 200 status code and the eight pre-existing dryRun body fields are unchanged. The `lifecycle_phase = update_apply_evaluation_requested` field does NOT mean apply has been accepted, the chef has committed to applying, the staged package was inspected for execution readiness, the installer was launched, the install tree was touched, the `Backups\` tree was touched, the scheduler was notified, rollback became available, or the broker is preparing to restart. None of those things happen on the dryRun path. The dryRun path remains pure-read, side-effect-free, and deliberately bypasses re-auth so a chef can preview without a Windows Hello / PIN gate.

What an evaluation request is NOT. The categorical distinction between evaluation and apply intent matters most when an operator (or downstream tooling) reads the dryRun response weeks or months later in an audit log. To prevent semantic-convenience drift, the evaluation phase is explicitly and only an observation of the chef's evaluation request:

- It is **not** a queued operation. The broker is not holding any deferred work behind this lifecycle phase. There is no queue.
- It is **not** a pending install. There is no pending state to inspect, resume, cancel, or expire.
- It is **not** a deferred apply. The broker has no future-intent record tied to this request and will not, on its own initiative, transition this phase to any `update_apply_*` phase.
- It is **not** resumable state. The lifecycle phase is born and complete the moment it is recorded in the response — there is nothing to come back to and nothing for a subsequent request to continue.
- It is **not** broker-owned future intent. The broker has not committed itself to any subsequent action based on this request. If the chef wants to apply, they must issue a separate, distinct POST without `dryRun=true`.

The broker is ONLY saying: the chef requested an evaluation-oriented route. Nothing more. Any future surface that wants to attach scheduler, persistence, or orchestration semantics to apply or evaluation events MUST do so through a new, doctrinally-named vocabulary array; quietly re-purposing `$Script:UpdateEvaluationPhases` to mean "queued apply" or "pending install" would collapse the very category boundary the separate array was created to establish.

Why a new vocabulary array rather than extending the existing lifecycle array. The four-step survivability lifecycle (§17.4) describes a single mutation event observed across `_requested → _started → _observed_<…> → _verification_<…>`. A dryRun has no `_started`, no `_observed_<…>`, no `_verification_<…>` — it is a single observation point with no progression. Forcing `update_apply_evaluation_requested` into `$Script:UpdateLifecyclePhases` would either break the 4-step pattern or imply a 5-step pattern that does not exist; either choice would corrupt the doctrine that the survivability-vocabulary smoke verifies. Declaring evaluation events as a separate vocabulary array preserves the lifecycle doctrine intact and gives the dryRun path a phase that cannot accidentally graduate into apply progression.

Relationship to the live apply path. The live (non-dryRun) path of the same route returns HTTP 501 with `lifecycle_phase = update_apply_requested` (sourced from `$Script:UpdateLifecyclePhases[0]`, see §17.7). The dryRun path returns HTTP 200 with `lifecycle_phase = update_apply_evaluation_requested` (sourced from `$Script:UpdateEvaluationPhases[0]`). Each path carries a distinct HTTP status and a distinct lifecycle phase — both truthfully reflecting what the broker observed. The shared `evidence_classification = observational` value reflects that both are operator-intent observations, just of categorically different intents.

Smoke surface. The smoke at `_temp/phase_ai_verification/smoke_ai_c2_2.ps1` gates: (a) the dryRun 200 response carries the three new fields and the eight pre-existing fields remain intact; (b) `lifecycle_phase` is sourced via an index expression on `$Script:UpdateEvaluationPhases`, not a hard-coded string; (c) the value resolves to exactly `update_apply_evaluation_requested`; (d) the live 501 path is untouched and still sources from `$Script:UpdateLifecyclePhases[0]`; (e) the dryRun body carries no `update_apply_requested` string literal (no leak of live-path phase into evaluation context); (f) `Invoke-UpdatesApply` introduces no install/update mutation primitive; (g) `Invoke-UpdateMode` is still not called from `Routes/Updates.ps1`; (h) no SQLite schema, runtime, health, rollback, scheduler, or diagnostics surface is touched; (i) the survivability-vocabulary smoke (now 16 subtests, including the new `$Script:UpdateEvaluationPhases` gate) and the prior-section smoke both still pass against the modified surface.

### 17.9 Update request observations (bounded persistence)

The broker introduces a SQLite-persistent surface tied to the `POST /api/v1/updates/apply` route. Every time the route reaches one of the two lifecycle-phase-emitting branches — the dryRun 200 preview (§17.8) and the live 501 refusal (§17.7) — the broker writes ONE append-only row to a table named `update_request_observations` and returns the row's GUID to the chef as an additional response field `observation_id`. The row is durable observation evidence — it captures the fact that the broker received and processed the request — and nothing more.

The schema. `update_request_observations` carries exactly ten columns, all NOT NULL: `observation_id` (TEXT, PRIMARY KEY, server-generated GUID), `observed_at_utc` (TEXT, ISO-8601 with milliseconds, from `Get-UtcNowIso`), `request_kind` (TEXT, one of two values from the new vocabulary array `$Script:UpdateRequestKinds` — see below), `lifecycle_phase` (TEXT, mirrors what the response body carries), `lifecycle_phase_source` (TEXT, mirrors the response body's audit field), `evidence_classification` (TEXT, mirrors the response body), `route` (TEXT, always the literal `POST /api/v1/updates/apply`), `http_status` (INTEGER, 200 for dryRun, 501 for live), `broker_pid` (INTEGER, the broker process id at write time), and `workspace_path` (TEXT, the broker's resolved workspace root). No foreign keys reference any other table; no indexes are declared. The intentional decoupling from `broker_sessions` lets operators purge session history under disk pressure without erasing observation evidence.

The vocabulary. A frozen array `$Script:UpdateRequestKinds` is declared in [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1) with exactly two values in canonical index order: `update_apply_request` (index 0, used by the live 501 branch) and `update_apply_evaluation_request` (index 1, used by the dryRun 200 branch). Each `request_kind` value is the categorical label of the wire surface that was observed — it is NOT a lifecycle phase, NOT a status, NOT an outcome, and NOT an intent. Future request surfaces (rollback request, discard request, scheduler enrolment, etc.) MUST add their own kind string AND their own observation table; this array MUST NOT be reused as a catch-all bucket.

What is durably recorded. The ten columns above. That is the entire persistence surface. Reading the row at any later time — including across broker restart — yields the same forensic truth: the chef issued this request, the broker observed it at this UTC instant, and the route responded with this HTTP status carrying this lifecycle phase.

What is NOT durably recorded. The broker does NOT durably record: that apply was accepted; that apply was queued; that apply was deferred; that apply was scheduled for later execution; that any state machine was advanced; that the broker owes the chef any subsequent action; that this observation links to a prior observation or will link to a later one; that the staged-package directory was inspected; that the installer was launched; that the install tree was touched; that the `Backups\` tree was touched; that the broker plans to do anything in response to this request. None of those things happen on the apply observation path.

What survives restart. The row survives restart, because it is persisted in the WAL-backed SQLite database. That is its entire restart-time meaning. When the broker process exits and starts again, the row is still there as an append-only record of the prior observation. Nothing else happens. The broker does NOT, at startup, read this table. The broker does NOT scan for in-flight requests. The broker does NOT resume any operation. The broker does NOT reconstruct any apply-related state from the observation rows. The table is write-only from the broker's perspective during normal runtime; the rows exist solely for out-of-band auditing (e.g. a future export route, an operator running a SQLite shell, or forensic incident review).

What an observation row is NOT. To prevent semantic-convenience drift, an observation row is explicitly and only a record that the broker observed the chef's apply or evaluation request:

- It is **not** a queued operation. The broker is not holding any deferred work behind this row. There is no queue.
- It is **not** a pending install. There is no pending state to inspect, resume, cancel, or expire.
- It is **not** a deferred apply. The broker has no future-intent record tied to this row and will not, on its own initiative, transition any state because of it.
- It is **not** resumable state. The row is born and complete at INSERT time — there is nothing to come back to and nothing for a subsequent request to continue.
- It is **not** broker-owned future intent. The broker has not committed itself to any subsequent action by writing this row. If the chef wants to apply, they must issue a separate, distinct POST.
- It is **not** a state-machine breadcrumb. The broker has no apply state machine in v1 (apply machinery itself remains unimplemented per §17.7). The row is independent forensic evidence, not a step in any progression.
- It is **not** read by the broker. Not at startup, not during runtime, not as part of any future-decision input. The append-only contract is a one-way pipe from request observation into durable storage; the broker never reads what it has written.

Why no FOREIGN KEY to `broker_sessions`. Coupling observation rows to a session-row lifetime would conflate two categorically distinct evidentiary streams: session evidence (the broker process was alive between time X and time Y) and observation evidence (the broker received and processed a wire request at time Z). The `broker_pid` and `observed_at_utc` columns already give forensic linkage by value; declaring the FK would let session-row deletion cascade into observation-row deletion, which would silently destroy evidence under disk pressure. Truthful messiness — orphaned observation rows whose `broker_pid` no longer maps to a session row — is preferable to silent evidence erasure.

Why no INDEX. Observation rows have no broker-side read surface. Declaring an index would be premature optimization for a read pattern that does not exist; the index would simply add write overhead and a doctrine-shaped commitment to a future export query whose shape has not been designed. When a future surface adds a read path (e.g. a `GET /api/v1/updates/observations` route, or a forensic-export CLI), that surface will be responsible for declaring whatever indexes it needs.

Failure mode. If the INSERT fails for any reason (SQLite I/O error, database locked beyond the busy timeout, an unexpected schema mismatch), the writer function `Add-UpdateRequestObservation` in [Routes/Updates.ps1](../app/broker/Routes/Updates.ps1) catches the exception, logs the failure via `Add-RecentError`, and returns `$null`. The HTTP response is still emitted with the same status code and lifecycle fields, but `observation_id` is `null`. The chef receives a truthful answer: "your request was received, the broker chose to respond with this status, and the broker could not record evidence of the receipt." Refusing the response on observation-write failure would conflate evidence-recording authority with execution authority — the broker has neither for apply, and inventing one to mask the other is the kind of contamination the bounded-observation contract was built to prevent. Truthful messiness is preferable to fabricated cleanliness.

Future-state tensions to watch. The writer is deliberately invoked only on lifecycle-phase-emitting branches (dryRun 200 and live 501; see §17.10 for the refusal-branch extension). A future surface that adds lifecycle phases to additional refusal paths SHOULD also extend the writer call sites, but it MUST do so as a deliberate per-branch decision — never as an "always-write" reflex — because each branch represents a different chef intent. Similarly, a future read surface MUST treat the table as append-only when running queries: no UPDATE, no DELETE, no schema mutation. The smoke at `_temp/phase_ai_verification/smoke_ai_c2_3.ps1` gates these invariants and will refuse to pass if any future surface attempts to mutate or read the table outside the bounded contract.

Smoke surface. The smoke at `_temp/phase_ai_verification/smoke_ai_c2_3.ps1` (31 subtests) gates, across seven sections: (A) vocabulary declaration — `$Script:UpdateRequestKinds` is declared exactly once, has exactly two frozen values in canonical order, and is referenced only in `Vocabulary.ps1` and `Routes/Updates.ps1`; (B) DDL — the literal `update_request_observations` appears in exactly two string constants (the CREATE TABLE and the INSERT INTO), the DDL lives inside `Apply-M1Schema`, declares all ten expected columns with their expected SQL types, contains no orchestration vocabulary tokens, declares no FOREIGN KEY, and declares no INDEX; (C) writer function — `Add-UpdateRequestObservation` is defined exactly once with the expected five mandatory parameters, contains exactly one parameterized INSERT (all values are `$`-prefixed parameter names), and wraps the INSERT in a try/catch; (D) route integration — the dryRun branch passes `-RequestKind = $Script:UpdateRequestKinds[1]`, the live 501 branch passes `-RequestKind = $Script:UpdateRequestKinds[0]`, both response bodies include `observation_id` bound to a variable (not a literal), and both call sites pass `-LifecyclePhase` and `-EvidenceClassification` via index expressions; (E) append-only invariants — no UPDATE, no DELETE, no SELECT, and no ALTER TABLE targets `update_request_observations` anywhere in `app/broker/**`, and no HTTP route exposes the table; (F) semantic firewall — the writer contains no orchestration vocabulary tokens, `Routes/Updates.ps1` contains no hard-coded `update_apply_request` or `update_apply_evaluation_request` string literals (canonical sourcing only), the writer makes no install/process/package-mutation calls (20-cmd forbid list), and the writer contains no SELECT on its own table; (G) regression — the survivability-vocabulary smoke and the two prior route-surface smokes all still pass against the modified surface.

### 17.10 Apply refusal observation coverage

The bounded-persistence contract from §17.9 extends from the two lifecycle-phase-emitting branches (dryRun 200 preview, live 501 refusal) to the three pre-execution refusal branches of `POST /api/v1/updates/apply`. Each refusal branch writes ONE append-only row to `update_request_observations` and returns the row's GUID to the chef as the same `observation_id` response field introduced in §17.9. The refusal branches covered are:

- HTTP **401** `reAuthRequired` — the chef has not completed the appliance's re-authentication step required for live apply requests.
- HTTP **503** `active_cook_snapshot_failed` — the broker could not enumerate active bakes to verify the precondition that no active bake is in progress.
- HTTP **409** `update_refused_active_cooks` — the broker successfully enumerated active cooks and found at least one in progress.

Coverage matrix for `POST /api/v1/updates/apply`:

| HTTP | Branch | Lifecycle phase source | Observation written |
| --- | --- | --- | --- |
| 200 | dryRun preview | `$Script:UpdateEvaluationPhases[0]` | yes |
| 501 | live apply (machinery unimplemented) | `$Script:UpdateLifecyclePhases[0]` | yes |
| 401 | reAuthRequired | `$Script:UpdateRefusalPhases[0]` | yes |
| 503 | active_cook_snapshot_failed | `$Script:UpdateRefusalPhases[2]` | yes |
| 409 | update_refused_active_cooks | `$Script:UpdateRefusalPhases[1]` | yes |
| 423 | brokerLocked (workspace-lock middleware) | — | no (middleware-enforced; out of scope) | — |

The 423 `brokerLocked` verdict is intentionally out of scope: it is enforced by the workspace-lock middleware BEFORE `Invoke-UpdatesApply` is entered, so the apply route never observes the request. Folding 423 into refusal-observation coverage would require relocating the brokerLocked check downstream of route entry — a structural change the appliance does not make.

The refusal vocabulary. A third frozen array, `$Script:UpdateRefusalPhases`, is declared in [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1) with exactly three values in canonical index order:

- `[0]` — `update_apply_refused_reauth_required` (used by the 401 branch)
- `[1]` — `update_apply_refused_active_cooks_present` (used by the 409 branch)
- `[2]` — `update_apply_refused_active_cook_snapshot_failed` (used by the 503 branch)

Why a separate vocabulary. A refusal is categorically not a mutation progression. The four-step lifecycle (§17.4) describes a single mutation event progressing through `_requested → _started → _observed_<…> → _verification_<…>`. A refusal has no `_started`, no `_observed_<…>`, no `_verification_<…>` — apply NEVER began. Inflating `$Script:UpdateLifecyclePhases` with refusal values would either (a) break the four-step pattern, or (b) imply a parallel progression that does not exist. Either choice would corrupt the survivability-vocabulary doctrine. The categorical split is enforced by the smoke for this section: `$Script:UpdateRefusalPhases` values MUST NOT appear in `$Script:UpdateLifecyclePhases` (subtest 4) and MUST NOT appear in `$Script:UpdateEvaluationPhases` (subtest 5). All three vocabularies are disjoint by construction.

What a refusal observation row records. Identical schema and semantics to §17.9: the same ten columns of `update_request_observations`, the same append-only writer (`Add-UpdateRequestObservation`), the same try/catch failure handling that returns `$null` rather than fabricating success. The only differences are: (a) `lifecycle_phase` is sourced from `$Script:UpdateRefusalPhases` instead of `$Script:UpdateLifecyclePhases` or `$Script:UpdateEvaluationPhases`; (b) `http_status` is 401, 503, or 409 instead of 200 or 501; (c) `request_kind` is always `$Script:UpdateRequestKinds[0]` (`update_apply_request`) — even the 401 and 503 refusals are observations of an apply request, because the chef issued the live path; evaluation requests have no refusal branch.

What a refusal observation row is NOT. The constraints from §17.9 carry forward without exception: a refusal observation row is not a queued operation, not a pending install, not a deferred apply, not resumable state, not broker-owned future intent, and not a state-machine breadcrumb. The additional categorical constraints introduced by refusal are stronger still:

- The broker did NOT begin to apply anything. The refusal happened BEFORE any apply machinery could be reached, in every case.
- The broker did NOT schedule a retry. There is no retry, no backoff, no automatic reissue. The chef must issue a new POST.
- The broker did NOT record an intent to apply later. The refusal terminates the request; there is no future-intent record tied to the row.
- The broker did NOT enter a "wait for re-auth" or "wait for cooks to finish" state. There is no waiting state. The refusal is final for this request.
- A restart does NOT convert a refused request into an active request. Refusal rows are not consulted at startup; the broker does not "pick back up" any refused apply.
- A refusal row does NOT obligate the chef to issue a follow-up request. The chef may issue a new POST when the precondition is satisfied (re-auth completed, snapshot succeeds, cooks finish); the broker has no record-of-debt model.

Response body shape. All three refusal branches return the same four additive transparency fields used by the live 501 and dryRun 200 paths: `lifecycle_phase`, `lifecycle_phase_source`, `evidence_classification` (always `observational`), and `observation_id`. The pre-existing per-branch fields are preserved without modification:

- 401: the body returned by `New-BrokerLockReAuthRequiredResponse` is preserved verbatim — including its existing `error`, `reauth`, and challenge fields — and the four additive fields are added by hash-table key assignment. The response `-Status` is preserved as `$resp.status` (the helper's chosen status, not a hard-coded 401), so any future change to the BrokerLock helper's status convention propagates without code duplication.
- 503: `error = 'active_cook_snapshot_failed'`, `reason`, `detail`, `snapshotError`, plus the four additive fields.
- 409: `error = 'update_refused_active_cooks'`, `reason`, `detail`, `activeCookCount`, `activeCooks`, plus the four additive fields.

In every case `observation_id` is GUID-or-`null`. A `null` value means the broker observed and refused the request as before, but could not durably record the row; the refusal itself is unaffected by the observation-write failure. The bounded-persistence contract continues to refuse to elevate evidence-recording authority into execution authority — failing to write a refusal row does NOT cause the broker to suddenly accept the apply.

Smoke surface. The smoke at `_temp/phase_ai_verification/smoke_ai_c2_4.ps1` (25 subtests) gates, across seven sections: (A) new vocabulary declaration — `$Script:UpdateRefusalPhases` is declared exactly once in `Vocabulary.ps1`, has exactly three frozen values in canonical order, is referenced only in `Vocabulary.ps1` and `Routes/Updates.ps1`, and its values are disjoint from both `$Script:UpdateLifecyclePhases` and `$Script:UpdateEvaluationPhases`; (B) refusal branch wiring — `Invoke-UpdatesApply` contains exactly 3 refusal `Add-UpdateRequestObservation` call sites plus the 2 mutation call sites for the dryRun-and-501 paths (5 total), each refusal branch uses the correct `$Script:UpdateRefusalPhases` index for its HTTP status, and all three refusal calls pass `-RequestKind = $Script:UpdateRequestKinds[0]` and `-EvidenceClassification = $Script:SurvivabilityEvidenceClasses[1]`; (C) refusal response body integrity — the 401 branch preserves `-Status $resp.status` and mutates `$body` with `observation_id` and `lifecycle_phase` via variable assignments (not literals), and the 503 / 409 branches return hash-table bodies with the correct integer status, the correct `error` value, `lifecycle_phase` sourced from the correct `$Script:UpdateRefusalPhases` index, and `observation_id` bound to a variable expression; (D) semantic firewall — `Routes/Updates.ps1` contains no hard-coded refusal-phase string literals (canonical indexed sourcing only) and the refusal-phase values themselves contain no forbidden orchestration / continuation tokens (queued, pending, deferred, retry, continuation, replay, resume, scheduled, active_transition, desired_state); (E) append-only + no-read-surface invariants — no SELECT / UPDATE / DELETE / ALTER TABLE targets `update_request_observations` anywhere in `app/broker/**`, and no HTTP route exposes the table; (F) boundary — `Invoke-UpdatesApply` makes no install / process / package-mutation calls (20-cmd forbid list) and `Routes/Updates.ps1` introduces no rollback / scheduler / diagnostics / export function; (G) regression — the survivability-vocabulary smoke and the three prior route-surface smokes all still pass against the modified surface.

---

### 17.11 Update request observation model

This section is the canonical reconciliation of the update-request observation model. It introduces no new runtime behavior, no new vocabularies, no new wire fields, no new table columns, no new writer call sites, no new routes, and no read path. Its purpose is to consolidate §17.7–§17.10 into a single authoritative classification matrix and to inventory the deferred decisions that any future read surface will need to confront.

**Observation model — coverage matrix.** The following table is the authoritative description of every branch of `POST /api/v1/updates/apply` that participates in the bounded observational persistence contract. The matrix MUST stay in lockstep with the wiring in [app/broker/Routes/Updates.ps1](../app/broker/Routes/Updates.ps1) and the vocabularies in [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1); the smoke at `_temp/phase_ai_verification/smoke_ai_c2_5.ps1` gates the matrix against drift.

| HTTP | Branch | `error` / `reason` | `request_kind` | Phase vocabulary | `lifecycle_phase_source` | `evidence_classification` | Observation row written | Broker reads row |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 200 | dryRun evaluation preview | `wouldRefuse` body field (200 OK) | `update_apply_evaluation_request` | `$Script:UpdateEvaluationPhases` | `UpdateEvaluationPhases[0]` | `observational` | yes | no |
| 501 | live apply (machinery unimplemented) | `apply_not_yet_implemented` | `update_apply_request` | `$Script:UpdateLifecyclePhases` | `UpdateLifecyclePhases[0]` | `observational` | yes | no |
| 401 | reAuthRequired | `reAuthRequired` | `update_apply_request` | `$Script:UpdateRefusalPhases` | `UpdateRefusalPhases[0]` | `observational` | yes | no |
| 503 | active_cook_snapshot_failed | `active_cook_snapshot_failed` | `update_apply_request` | `$Script:UpdateRefusalPhases` | `UpdateRefusalPhases[2]` | `observational` | yes | no |
| 409 | update_refused_active_cooks | `update_refused_active_cooks` | `update_apply_request` | `$Script:UpdateRefusalPhases` | `UpdateRefusalPhases[1]` | `observational` | yes | no |
| 423 | brokerLocked (workspace-lock middleware) | `brokerLocked` | n/a — middleware-enforced before route entry | n/a | n/a | n/a | no — middleware / out of scope | n/a |

**Reading the matrix.** A row is the broker's simultaneous promise about (a) what the wire response looks like AND (b) what the persisted SQLite row looks like for that branch. Both descriptions are simultaneously true: the response body and the persisted row carry the same lifecycle-phase string, the same lifecycle-phase source, the same evidence classification, and (for branches that write a row) the same `observation_id` GUID. The matrix is intentionally narrow — apply route only, update-request vocabularies only. Future routes that gain observation evidence MUST be added as a separate matrix in their own doctrine section; this matrix MUST NOT be expanded as a generic catch-all.

**Observation rows are recorded evidence, not active state.** Every row in `update_request_observations` is a forensic record of a single request that the broker observed at a single UTC instant. A row is NEVER a representation of active work, in-progress operations, queued requests, deferred apply, pending re-auth, scheduled retries, broker-owned intent, or any other forward-looking state. The broker has no concept of "outstanding observations" because observations are not outstanding — they are complete the moment they are recorded. Restart, runtime polling, status-pill computation, `/health` payload assembly, and update-route response generation all proceed without consulting the table.

**Restart survival is not restart continuity.** The `update_request_observations` table is WAL-backed and durable; rows survive broker restart, workspace move, machine reboot, and OS upgrade as long as the SQLite file itself survives. That survival is the table's only restart-time meaning. A surviving row does NOT cause the broker to pick up where the prior process left off — the prior process did not, in any covered branch, "leave off" anything: every covered branch is a complete request/response transaction whose lifecycle is fully discharged by the time the row is committed. After restart the broker neither scans the table, nor reconstructs state from it, nor treats prior rows as work-items, nor honors any implicit "the chef issued this earlier so the broker still owes them something" contract.

**Why read surfaces are intentionally deferred.** The update-request observation contract deliberately introduces ONLY the WRITE path. There is no broker-internal read, no HTTP read, no diagnostic export, no `/health` aggregation, no console listing, and no operator workflow that consumes the rows. The deferral is intentional: a read surface — even a read-only one — introduces ordering semantics (LIMIT N, ORDER BY observed_at_utc), retention expectations, index pressure, filtering semantics, and operator-interpretation hazards (the chef may treat the listing as a queue of outstanding requests). Until those semantics have been resolved by a future surface with its own contract and its own smoke, the broker MUST NOT expose any read path.

**Why `/health` does not surface observation rows.** `/health` is the appliance's primary operational status surface; chefs read it to determine whether the broker is alive, whether the workspace is unlocked, whether re-auth is pending, whether any active bakes are running. Adding an observation-row panel to `/health` would conflate two categories the appliance keeps strictly apart: live operational state (what is true RIGHT NOW about the broker process and the workspace) versus an append-only record (past requests the broker observed). A chef glancing at `/health` and seeing an `update_request_observations` panel could reasonably mistake it for an active queue, an active retry list, or an in-progress apply. To avoid that contamination, the `/health` payload deliberately omits the table.

**Why no index exists on `update_request_observations`.** The table is declared in [app/broker/Start-Broker.ps1](../app/broker/Start-Broker.ps1) as a 10-column `CREATE TABLE IF NOT EXISTS` with no `CREATE INDEX` statement. This is deliberate. An index is meaningful only when there is a query that would benefit from it — and the v1 contract has no query. Adding an index speculatively against the eventual shape of the first query would (a) lock the broker into a particular access pattern before that pattern has been deliberated, (b) impose write-amplification on every observation write, and (c) signal to future readers of the schema that some access pattern is "blessed". When a future surface introduces the first read path, the surface that introduces the query also introduces the index that satisfies it, in the same atomic doctrine change.

**423 `brokerLocked` middleware carve-out.** The 423 verdict is enforced by the workspace-lock middleware BEFORE `Invoke-UpdatesApply` is entered, so the apply route never observes the request and never writes an observation row. A 423 response will not carry `lifecycle_phase`, `lifecycle_phase_source`, `evidence_classification`, or `observation_id`. A 423 response means the entire workspace is locked (typically because another broker process is using it), which is a different category of refusal than "apply route observed the request and chose not to proceed". Any future surface that wants observation-coverage parity for 423 must explicitly take on that structural relocation as its primary goal.

**Summary.** Every semantic rule established by §17.7–§17.10 is preserved by this reconciliation without modification: observed request is not accepted work; persisted observation is not active operation; refusal observation is not queued work; evaluation observation is not apply intent; restart survival is not restart continuity; observation rows are an append-only record, not broker runtime state; the broker does not read observation rows; `/health` does not consume observation rows; no query, list, or read route exposes observation rows; no index exists on the table. The smoke at `_temp/phase_ai_verification/smoke_ai_c2_5.ps1` gates each of these rules and gates the matrix above against drift.

### 17.12 Append-only enforcement (structural)

§17.11 declares `update_request_observations` append-only as doctrine. The broker makes it mechanically true at the storage layer rather than relying on convention.

1. **Structural enforcement.** The append-only property is enforced by a pair of SQLite triggers (`trg_uro_block_update` and `trg_uro_block_delete`) created idempotently alongside the table DDL inside `Apply-M1Schema`. The append-only invariant is not carried by convention alone; the storage layer itself rejects any row mutation against the table.
2. **Storage-layer raise.** Any UPDATE or DELETE against `update_request_observations` — whether issued from a future broker code path, an unintended regression, or a chef-side direct-SQLite probe — raises at the storage layer with `RAISE(ABORT, 'append-only enforcement (AI.C2.6)')` and is rejected before any row is touched. The literal raise text is preserved verbatim because it is the catalog string that smokes and operators alike grep against; the parenthetical is a code anchor, not a phase reference.
3. **Internal to the broker process.** This enforcement lives entirely inside the broker's SQLite schema-bootstrap step. It does not introduce any read surface, query route, list route, GET endpoint, `/health` panel, index, retention behavior, aggregation, dashboard, export, diagnostics-bundle inclusion, or scheduler hook. The writer `Add-UpdateRequestObservation` is unchanged in signature and behavior.
4. **No change to §17.11.** Nothing about the reconciliation matrix, deferred-read-surface doctrine, or restart-survival semantics is altered. Observation rows remain an append-only record, not broker runtime state. The broker still does not read observation rows. `/health` still does not consume observation rows. No index exists on the table.
5. **No change to the 423 carve-out.** The brokerLocked middleware path still does not enter `Invoke-UpdatesApply` and still does not write an observation row. Append-only enforcement does not relocate the lock check, does not add observation coverage for 423, and does not change any other refusal-branch behavior.

The smoke at `_temp/phase_ai_verification/smoke_ai_c2_6.ps1` gates trigger presence, trigger-body raise text, idempotent creation, the empty SELECT/UPDATE/DELETE/INDEX site inventory in `app/broker/**`, the unchanged writer signature, the absence of new vocabulary or phase values, and exact-content equality of §17.11 and TROUBLESHOOTING §13s against their pinned canonical hashes.

### 17.13 Boot-time append-only trigger integrity verification

§17.12 makes the append-only property of `update_request_observations` structural in the schema text. At every broker boot the broker verifies that the triggers are actually installed in the live database, not just present in DDL text on disk. The same gate additionally verifies that each trigger BODY matches the canonical DDL the broker installed, via a SHA-256 over the catalog-stored `sql` after canonical whitespace normalization.

1. **Single sqlite_master read.** The verification is one read of the SQLite catalog (`sqlite_master`) scoped to triggers attached to `update_request_observations`, fetching both the trigger name and the `sql` column. It does not read, write, update, or delete any row of `update_request_observations` itself.
2. **Refuses to serve on missing triggers.** If either expected trigger (`trg_uro_block_update`, `trg_uro_block_delete`) is missing from the live catalog, the broker emits a console message naming the missing trigger(s), records a recent-error entry, and exits with `EXIT_E_OBSERVATION_TRIGGER_INTEGRITY` (§11.1, code `8`). The check runs once at boot, after `Apply-M1Schema` returns and before the dispatch loop is entered.
3. **Refuses to serve on trigger body drift.** If either expected trigger is present but its catalog-stored `sql` text, after canonical whitespace normalization, does not hash to the pinned canonical SHA-256 value the broker installed at schema bootstrap, the broker emits a console message naming the affected trigger and the expected/observed hashes, records a recent-error entry with `Source = 'observation_trigger_integrity'`, and exits with the SAME `EXIT_E_OBSERVATION_TRIGGER_INTEGRITY` (§11.1, code `8`). Body drift is most commonly produced by an external SQLite tool that ran `DROP TRIGGER` + `CREATE TRIGGER ...` with a weakened body (for example, `RAISE(IGNORE, ...)` instead of `RAISE(ABORT, ...)`, or a guard expression that always evaluates false). The pinned canonical hashes live in the broker source.
4. **No automatic recreation.** The broker does not attempt to recreate the triggers, fall back to a convention-only mode, or proceed without enforcement. Either failure (presence OR body drift) is terminal for that boot; the canonical recovery is to reinstall from a known-good release or restore the database from a known-good backup.
### 17.14 Observation-write attempt and failure counters

The broker exposes TWO process-lifetime monotonic integer counters on `/api/v1/health`, intended to be read together:

- `updateRequestObservationWriteAttemptCount` increments by exactly 1 at the start of every call to the writer for `update_request_observations`, before the INSERT is attempted. Both successful writes AND failed writes count toward the attempt total.
- `updateRequestObservationWriteFailureCount` increments by exactly 1 each time that writer's `catch` block fires (the same block that records a recent-error ring entry).

1. **Both keys.** Both counters appear on every `/health` response, including when either value is `0`. Both also appear in the response's inline `evidenceClassification` block tagged `runtime-only`, matching the taxonomy convention from §11.8 / §11.12.
2. **Invariant: `attempts >= failures >= 0`.** Every failure is also an attempt; not every attempt is a failure. The broker never decrements either counter and never lets a writer call skip the attempt increment. The counters are independent variables incremented at independent sites; the attempt counter is not derived from the failure counter, and the failure counter is not derived from the attempt counter.
3. **Chef-side interpretation.** A chef reads `attempts = N, failures = M` and infers a failure rate of `M / N` FOR THIS BROKER SESSION. The broker itself does not compute, expose, or act on the ratio. A ratio approaching `1.0` suggests a persistent driver (e.g. the broker's database file is unwritable). A small ratio over a large attempt count suggests transient noise (e.g. lock contention during heavy concurrent activity). The interpretation is the chef's; the broker emits raw counts only.
4. **Runtime-only.** Both counters live only in the current broker process and reset to `0` on every broker restart. Neither is persisted to disk, neither is written to `update_request_observations`, neither is sent off the appliance, and neither is derived from a read of the table. The §17.11 deferred-read-surface doctrine is unchanged.
5. **Informational, not status-driving.** Neither counter affects the `/health` `status` derivation (`ok` vs `degraded`). That derivation continues to depend only on the existing recent-errors ring and its overflow count. The counters are aggregate signals that complement the bounded ring when the ring has displaced entries; neither promotes nor demotes status on its own.
6. **What a non-zero failure count means.** A non-zero `updateRequestObservationWriteFailureCount` indicates one or more observation writes failed in this broker session. The chef does NOT need to take action against `update_request_observations` itself; the corrective action targets the underlying cause surfaced in the recent-errors ring (typically a SQLite I/O or locking issue against the broker's database file). The attempt counter is informational context for triage, not a corrective-action target on its own. See TROUBLESHOOTING.md §13v.
7. **No new read surface.** The two counters are the only new fields on `/health`. There is no new query, list, GET, or read route against `update_request_observations`. The §17.12 append-only structural doctrine, the §17.13 boot-time trigger integrity verification, and the 423 `brokerLocked` middleware carve-out are all unchanged.

---

### 17.15 Package-trust observations and at-staging SHA-256 verification

A second observation table, `package_trust_observations`, governs the integrity of update-package bytes at named verification boundaries. The table sits alongside `update_request_observations` (§17.11) and adopts the same forensic-evidence posture: append-only at the storage layer, never read by the broker at runtime, never used to drive any control-flow decision. It is paired with a building-block verification function, `Invoke-PackageStagingVerification`.

1. **Three boundaries.** The table's `boundary` column accepts three literals: `staging` (at-rest bytes immediately after a package has been written to the workspace `Updates\` folder), `pre_apply` (re-computation immediately before any apply step consumes the bytes), and `pre_run` (re-computation immediately before the broker hands the bytes off to an installer or extracted-script runner). All three literals are accepted by the CHECK constraint; each boundary writes from its own call site (§17.16).
2. **Fresh computation per observation, no caching.** Every call to `Invoke-PackageStagingVerification` computes `Get-FileHash -Algorithm SHA256` over the on-disk bytes at the supplied path. The broker NEVER reuses a prior observation row's `observed_sha256` as the answer for a later boundary; each boundary's row is independent evidence of what the bytes hashed to AT THAT BOUNDARY. This closes entry-gate G7 for the `staging` boundary.
3. **Outcome literals.** The `outcome` column carries one of three literals: `match` (the computed digest equals the caller-supplied `expected_sha256`, case-insensitive hex compare), `mismatch` (computed digest differs), or `unknown` (the file is missing, the read failed, or `Get-FileHash` threw). On `unknown` the row records the empty string in `observed_sha256` so the column never carries a fabricated digest.
4. **Structural append-only enforcement.** Two SQLite triggers, `trg_pto_block_update` and `trg_pto_block_delete`, are created idempotently alongside the table DDL inside `Apply-M1Schema`. Any UPDATE or DELETE against `package_trust_observations` — whether from a future broker code path, an unintended regression, or a chef-side direct-SQLite probe — raises at the storage layer with `RAISE(ABORT, 'append-only enforcement (AI.C3.1)')` and is rejected before any row is touched. The literal raise text is preserved verbatim because it is the catalog string that smokes and operators alike grep against; the parenthetical is a code anchor, not a phase reference. The structural posture mirrors §17.12 exactly.
5. **Paired runtime-only counters on `/health`.** The broker exposes two new integer fields on `/api/v1/health`, read together: `packageTrustStagingVerificationAttemptCount` increments by exactly 1 at the start of every call to `Invoke-PackageStagingVerification`, BEFORE the hash compute is attempted. `packageTrustStagingVerificationFailureCount` increments by exactly 1 each time that call's outcome is anything other than `match` (i.e. `mismatch` or `unknown`) OR an unexpected exception is caught. Both counters appear on every `/health` response (including when either value is `0`), both appear in the inline `evidenceClassification` block tagged `runtime-only`, both live only in the current broker process, and both reset to `0` on every broker restart. Neither is persisted, neither is written to `package_trust_observations`, neither is sent off the appliance, and neither is derived from a read of the table. The invariant is the same as §17.14: `attempts >= failures >= 0`.
6. **Informational, not status-driving.** Neither counter affects the `/health` `status` derivation (`ok` vs `degraded`). That derivation continues to depend only on the existing recent-errors ring and its overflow count. The package-trust counters are aggregate signals that complement the bounded ring when the ring has displaced entries; neither promotes nor demotes status on its own.
7. **What a non-zero failure count means.** A non-zero `packageTrustStagingVerificationFailureCount` indicates one or more at-staging verification calls in this broker session returned `mismatch` or `unknown`. The chef does NOT need to take action against `package_trust_observations` itself; the corrective action targets the package bytes (re-download from a known-good source, verify the manifest digest, audit the staging directory for tampering). The attempt counter is informational context for triage. See TROUBLESHOOTING.md §13w.
8. **No read surface for observation rows.** The two new counters are the only new fields on `/health`. There is no new query, list, GET, export, diagnostics-bundle inclusion, or scheduler hook against `package_trust_observations`. The §17.11 deferred-read-surface doctrine, §17.12 append-only structural doctrine, §17.13 boot-time trigger integrity verification, §17.14 write-failure counter doctrine, and the 423 `brokerLocked` middleware carve-out are all unchanged.

---

### 17.16 At-apply and at-launch trust verification + no-cache invariant

The three package-trust boundaries declared in §17.15 are operationalized by wiring the `Invoke-PackageStagingVerification` building block into the existing staging flow and by adding two parallel verification entry points: `Invoke-PackageApplyVerification` (boundary `pre_apply`) and `Invoke-PackageLaunchVerification` (boundary `pre_run`). All three boundaries share one structural invariant: **every observation is computed from scratch from the on-disk bytes (and, at `pre_run`, from the live system trust store) at the moment of the observation**. The broker NEVER carries trust forward across a restart, across a request, or across an observation row.

1. **At-staging (G1) — wired into `POST /api/v1/updates/download`.** When `Save-UpdatePackage` returns success (either freshly-downloaded `staged` or the idempotent `alreadyStaged` fast-path), `Invoke-UpdatesDownload` calls `Invoke-PackageStagingVerification` against the at-rest bytes and the manifest-pinned digest. The call is informational: it writes one `package_trust_observations` row (`boundary='staging'`) and increments the at-staging counters; it does NOT mutate the HTTP response. The bytes still get returned to the chef in the same shape as the unverified path. The verifier is invoked on the `alreadyStaged` path as well as the fresh-download path because the at-rest bytes can drift between downloads (operator manipulation, external tooling). A `mismatch` outcome at this boundary will surface in `/health` counters and the append-only table; the apply and launch boundaries will catch it again before any state change.

2. **At-apply (G2) — wired into `POST /api/v1/updates/apply`.** After every existing precondition refusal branch (reauth, snapshot failure, active bakes) has already short-circuited, `Invoke-UpdatesApply` iterates the preconditions snapshot's `stagedPackages`. For each package that has a `.metadata.json` sidecar carrying a `sha256` field, the broker calls `Invoke-PackageApplyVerification` — which re-hashes the on-disk bytes from scratch and compares against the sidecar digest. On ANY `mismatch` the apply request is REFUSED with HTTP 409 and a structured error body containing:
   - `error = 'package_trust_apply_mismatch'`
   - `mismatchedPackages` — the file paths whose bytes drifted
   - `packageTrustObservationIds` — the IDs of every `pre_apply` row written this call
   - `packageTrustObservationsByPath` — per-package outcome + expected/observed digest + row ID
   - `lifecycle_phase = $Script:UpdateRefusalPhases[3] = 'update_apply_refused_package_trust_mismatch'`
   - `lifecycle_phase_source = 'UpdateRefusalPhases[3]'`
   - `evidence_classification = 'observational'`
   - `observation_id` — the corresponding new `update_request_observations` row
   The refusal is recorded in BOTH append-only tables: one row per package in `package_trust_observations` (`boundary='pre_apply'`) and one row in `update_request_observations` (HTTP status 409, lifecycle phase `update_apply_refused_package_trust_mismatch`). The chef can cross-walk the two tables by `observation_id` and `packageTrustObservationIds`. The verifier is called with NO source other than the on-disk bytes; it never reads any prior staging-boundary row to short-circuit the re-computation.

3. **At-launch (G3) — wired into broker startup, exit code 9.** Immediately after the trigger-integrity gate and BEFORE the HTTP listener binds, the broker calls `Invoke-PackageLaunchVerification`. The function enumerates every currently-staged package via `Get-StagedPackageInventory` and, for each, calls the same `Get-PackageTrustState` reader the `/api/v1/updates/state` surface uses. `Get-PackageTrustState` re-derives trust from the live SYSTEM trust store on every call — there is no cache, no remembered-signer flag, no carry-forward `$Script:` variable, and no read of any prior `pre_run` row. The function maps the resulting `overallStatus` to an outcome literal:
   - `hashMismatch`, `signatureInvalid` -> `mismatch`
   - `missing`, `hashUnknown` -> `unknown`
   - `verified`, `unsigned`, `signaturePresentNotVerified`, `signerUnknown` -> `match`

   One `package_trust_observations` row is written per staged package with `boundary='pre_run'`. On ANY `mismatch` the broker writes an entry to the recent-errors ring, calls `Invoke-Shutdown`, and exits with the constant `EXIT_E_PACKAGE_TRUST_INTEGRITY = 9` (see §11.1). `unknown` is observational — recorded but NOT a refusal trigger; the trigger-integrity doctrine is the doctrine here, missing positive evidence does not refuse the boot. If no packages are staged, the function still runs once (incrementing the attempt counter) and produces no per-package rows.

4. **Four new paired runtime-only counters on `/health`.** The broker exposes four additional integer fields on `/api/v1/health`, all tagged `runtime-only` in the inline `evidenceClassification` block:
   - `packageTrustApplyVerificationAttemptCount`
   - `packageTrustApplyVerificationFailureCount`
   - `packageTrustLaunchVerificationAttemptCount`
   - `packageTrustLaunchVerificationFailureCount`

   Each attempt counter increments by exactly 1 at the start of its verification function, BEFORE the hash is computed (apply) or before the inventory is enumerated (launch), so the attempt is recorded even if the function throws downstream. Each failure counter increments by exactly 1 if and only if the call's verdict is a refusal (apply: any `mismatch` outcome; launch: at least one package mapped to `mismatch`). Both invariants from §17.15 carry over unchanged: `attempts >= failures >= 0`, runtime-only, reset on every broker restart, never persisted, never sent off the appliance, never derived from a read of the table. Because the at-launch check runs once per boot, the steady-state value of `packageTrustLaunchVerificationAttemptCount` on a serving broker is `1` and `packageTrustLaunchVerificationFailureCount` is `0` (if the launch check had refused, the broker would have exited instead of serving `/health`).

5. **No new column, no new table, no schema migration.** The `package_trust_observations` `boundary` CHECK constraint already accepted the literals `'staging'`, `'pre_apply'`, and `'pre_run'` from the staging-table DDL (§17.15). The at-apply and at-launch wiring described here only starts writing rows tagged `'pre_apply'` and `'pre_run'`; the structural append-only triggers (`trg_pto_block_update`, `trg_pto_block_delete`) cover them automatically. No schema migration was performed.

6. **Status derivation unchanged.** None of the four new counters contribute to the `/health` `status` derivation (`ok` vs `degraded`). Status continues to depend only on the existing recent-errors ring and its overflow count. The counters are aggregate signals that complement the bounded ring; neither promotes nor demotes status on its own.

7. **No-cache structural invariant (G4).** A structural smoke check scans every `.ps1` and `.psm1` file under `app/broker/`, `app/install/`, and `app/launcher/`, strips PowerShell comments via the parser (so doctrine commentary describing the forbidden patterns is excluded), and asserts that none of the following identifier substrings appear in code: `_trust_cached`, `_publisher_remembered`, `_signature_pinned`, `_trusted_at`, `cachedTrust`, `rememberedSigner`, `pinnedTrust`. These names would imply a remembered-trust mechanism, which is forbidden — trust is always re-derived from first principles at each boundary. The check is part of `smoke_ai_c3_2.ps1` and gates the entry-gate G4 closure for at-apply and at-launch.

8. **No new HTTP read route against `package_trust_observations`.** The four new counters are the only new fields on `/health`. There is no new query, list, GET, export, diagnostics-bundle inclusion, or scheduler hook against the table. The §17.11 deferred-read-surface doctrine, §17.12 append-only structural doctrine, §17.13 boot-time trigger integrity verification, §17.14 write-failure counter doctrine, §17.15 staging-counter doctrine, and the 423 `brokerLocked` middleware carve-out are all unchanged.

9. **What a non-zero failure count means.** A non-zero `packageTrustApplyVerificationFailureCount` indicates one or more in-process apply requests were refused at the at-apply trust boundary in this broker session. A non-zero `packageTrustLaunchVerificationFailureCount` (only observable in a post-mortem direct-SQLite or log inspection, since the broker exits on launch refusal) indicates the most recent broker boot refused to start because at least one staged package failed re-verification against the system trust store. In both cases the chef's corrective action targets the package bytes (re-download from a known-good source, verify the manifest digest, audit the staging directory for tampering), not the observation table. See TROUBLESHOOTING.md §13x.

### 17.17 Customer-verifiable package SHA-256 verification and signature subject narrowness

This section documents (a) how a chef independently verifies a downloaded package's SHA-256 against the canonical published reference BEFORE staging it through the broker, and (b) the signature-subject narrowness invariant the at-launch trust evaluator depends on. Both are doctrine — the procedure is operational, the narrowness invariant is structural and smoke-gated by `smoke_ai_c3_3.ps1`.

1. **Where the canonical reference SHA-256 lives.** Each broker workspace's `app/VERSION.json` carries one field, `updateManifestUrl`, that names the HTTPS URL of the canonical update manifest for that workspace's release channel. The broker fetches and schema-validates that manifest on `POST /api/v1/updates/check`; the chef can fetch it manually from the same URL. The manifest's `latestCookbook` object carries two fields the chef needs:
   - `latestCookbook.packageUrl` — the absolute HTTPS URL of the package archive (`.zip`).
   - `latestCookbook.sha256` — the canonical SHA-256 of those bytes, as a 64-character lowercase or uppercase hex string.

   The manifest schema (`app/broker/Update/Manifest.psm1`) hard-rejects any other top-level or `latestCookbook` field. If a manifest is served with extra fields, the broker refuses to act on it; the chef should treat that manifest as malformed and obtain a fresh one from the publisher.

2. **The verification procedure (verbatim).** Open an elevated PowerShell 7 session on the chef's workstation. The four commands below are the entire procedure. They use only built-in PowerShell cmdlets — no Cookbook-internal commands are required, and the workstation does NOT need a running broker.

   ```powershell
   # 1. Fetch the canonical manifest and capture the published digest.
   $manifestUrl   = '<paste VERSION.json.updateManifestUrl here>'
   $manifest      = Invoke-RestMethod -Uri $manifestUrl -UseBasicParsing
   $publishedHash = $manifest.latestCookbook.sha256

   # 2. Download the package archive named by the same manifest.
   $packageUrl    = $manifest.latestCookbook.packageUrl
   $localPath     = Join-Path $env:TEMP (Split-Path -Leaf $packageUrl)
   Invoke-WebRequest -Uri $packageUrl -OutFile $localPath -UseBasicParsing

   # 3. Recompute the SHA-256 of the bytes that landed on disk.
   $computedHash  = (Get-FileHash -Algorithm SHA256 -LiteralPath $localPath).Hash

   # 4. Compare case-insensitively. Anything other than $true is a refusal.
   $computedHash -ieq $publishedHash
   ```

   The expected output of step 3 is a `Microsoft.Powershell.Commands.FileHashInfo` record whose `Hash` property is a 64-character uppercase hex string. The expected output of step 4 is the literal Boolean `True`. Any other result — `False`, an exception during fetch/download, a non-200 HTTP response, a manifest that fails schema validation — is a verification refusal.

3. **What a `False` (or any non-`True`) result means.** The bytes the chef just downloaded do NOT match what the publisher claims to have signed at this version. Do NOT stage the package through the broker. Do NOT attempt to repair the file in place. Re-fetch from the canonical `latestCookbook.packageUrl` over a fresh connection; if a second download produces the same mismatched hash, the discrepancy is upstream — escalate to the publisher and keep the failing file on disk for evidence. See TROUBLESHOOTING.md §13y for the structured Symptom / Why / Evidence / Action shape.

4. **Tenant- and workspace-agnostic.** The procedure refers to no specific tenant, no specific workspace path, no specific package version, and no Cookbook-internal cmdlet. It depends only on the chef having (a) read access to `VERSION.json`'s manifest URL field, (b) outbound HTTPS to the publisher, and (c) `Get-FileHash` (built into Windows PowerShell 5+ and PowerShell 7+). It is intentionally independent of any broker process state — the chef can run it on a workstation that has never installed the Cookbook.

5. **Signature subject narrowness invariant (G6).** `Invoke-PackageLaunchVerification` delegates per-package trust evaluation to `Get-PackageTrustState` in `app/broker/Update/Trust.psm1`. The identity-matching step in that function is **thumbprint-pinned**, not subject-string-matched: a staged package is accepted as `signerKnown = $true` if and only if the cryptographic verifier (`Test-PackageSignature` in `app/broker/Update/Signature.psm1`) returned a 40-character SHA-1 hex thumbprint that appears verbatim in the operator-supplied `Trust/trusted-signers.json` allowlist. The comparison is exact uppercase-hex equality (see `Format-Thumbprint`). The cert's distinguished-name string (`certSubject`) is recorded on the verifier result for informational display and audit but is NEVER consulted in the trust decision.

6. **Why thumbprint pinning is narrower than any subject pattern.** A subject-string pattern — even one as specific as `Subject -like 'CN=<vendor>, O=<vendor>, *'` — accepts any current OR future certificate the vendor's PKI issues against that subject template. Issuing certs against a stable subject is part of routine PKI hygiene, so a subject pattern grants implicit forward trust to every renewal and every sibling cert under that name. A thumbprint pin trusts exactly one certificate: the one whose 40-character SHA-1 the operator typed into `Trust/trusted-signers.json`. Renewal requires an operator-visible allowlist edit; impersonation requires either a SHA-1 thumbprint collision or compromise of the same private key the operator already chose to trust. The narrowness gap matters most against an over-broad CA (e.g. any cert issued by a public commercial CA the OS already trusts).

7. **What the signature-narrowness smoke pins.** `smoke_ai_c3_3.ps1` (G6 subtests) asserts that `Trust.psm1` continues to use `certThumbprint` equality as the identity-matching predicate in `Get-PackageTrustState`, that `certSubject` appears only as an informational field on the verifier result and never on the left-hand side of a `-like`, `-match`, `-eq`, or `-ieq` comparison anywhere under `app/broker/Update/`, and that no `CN=`-prefixed literal appears in any matching predicate. If a future edit introduces subject-string matching — broad or narrow — the smoke fails. Widening the trust surface from thumbprint-pinned to subject-pattern-matched requires an explicit doctrine amendment and a corresponding update to this section.

8. **What this section deliberately does NOT add.** No new HTTP route. No new broker-side query, list, GET, or diagnostic surface against `package_trust_observations` or `update_request_observations` — the §17.11 deferred-read-surface doctrine remains in force, and the canonical out-of-band evidence-inspection path is still §13s. No new exit code, no new `$Script:` counter, no new vocabulary array, no new verification boundary. The customer-verification procedure in §17.17.2 is intentionally external to the broker; it does NOT consult, drive, or contribute observations to the broker's append-only tables. The broker-internal three-boundary verification — at-staging (§17.15), at-apply (§17.16), and at-launch (§17.16) — remains the authoritative on-appliance trust evaluation; the §17.17.2 procedure is the chef's independent upstream check against transport- and workstation-side mishaps before the bytes ever reach the broker.

### 17.18 Environment observations and ConstrainedLanguage detection

1. **What `environment_observations` is.** A third append-only SQLite table on the broker workspace database, alongside `update_request_observations` (§17.11) and `package_trust_observations` (§17.15). One row per OBSERVED hostile-environment detection event at broker startup. The row records the UTC instant, the named condition, the outcome, and the evidence classification. Nothing more. A row does NOT say the broker repaired the condition, degraded around it, queued any future action, or carries state forward across restart. The broker observes; enterprise IT remediates. Same deferred-read-surface doctrine as §17.11: the broker does NOT read this table at any point during request handling or startup; rows are forensic evidence for operators, surfaced via direct out-of-band SQLite read per §13s. The table carries the same append-only structural enforcement as the observation-table triggers used elsewhere (`trg_eo_block_update`, `trg_eo_block_delete`), each raising `ABORT` on any attempt to UPDATE or DELETE a row. The schema's `condition` column is `CHECK`-constrained at the storage layer; the CHECK enumerates the canonical hostile-condition vocabulary (`constrained_language`, `low_disk`, `workspace_path_forbidden`). The `outcome` column is `CHECK`-constrained to `detected` or `warning` (low-disk soft-warn).

2. **What the ConstrainedLanguage detection (G1) does.** Once per broker boot, after the trigger-integrity gate and the at-launch package-trust evaluation, and BEFORE the HTTP listener binds, the broker reads `$ExecutionContext.SessionState.LanguageMode`. If the value is anything other than the literal `FullLanguage`, the broker writes ONE row into `environment_observations` tagged `condition='constrained_language'` / `outcome='detected'` / `evidence_classification='observational'` and exits with `EXIT_E_ENVIRONMENT_CONSTRAINED_LANGUAGE` (= 10). The probe is structural: it reads the engine's own language mode, not a configuration file or environment variable, so it cannot be spoofed by anything short of patching the running PowerShell host. There is no fallback, no auto-elevation, no "try to run anyway" path — the broker's contract (function imports, dot-sourced helpers, `Invoke-Expression`-free script blocks, .NET interop via `[System.Security.Cryptography.SHA256]::Create()` and the SQLite `Microsoft.Data.Sqlite` types, `Add-Type`-free runtime) assumes FullLanguage semantics and a `ConstrainedLanguage` / `RestrictedLanguage` / `NoLanguage` engine cannot honor that contract. The observation row is evidence for the operator; the exit code is the verdict.

3. **Why a fatal exit is the right response.** Running the broker in a non-FullLanguage mode would produce a long tail of nondeterministic, condition-dependent failures spread across every subsystem (SQLite, signature verification, recipe validation, WebSocket dispatch) — each of which would look like a different bug to a debugging operator. A single fatal exit at startup, with a single named environment-observation row and a single named exit code, collapses that tail into one fact the operator can act on: "the appliance was launched in a constrained PowerShell environment; the enterprise PowerShell policy must be adjusted before the broker can serve." See TROUBLESHOOTING.md §13z for the structured Symptom / Why / Evidence / Action shape.

4. **What this section deliberately does NOT add.** No `/health` counter (the broker exits before `/health` exists). No HTTP route. No periodic re-detection (detection fires at startup; runtime drift is impossible because the language mode is fixed for the engine's lifetime). No PowerShell-side `EnvironmentConditions` vocabulary array (the SQLite `CHECK` constraint on `condition` is the canonical pin per §3). No read/query/GET/list surface over `environment_observations`. No auto-remediation (the broker cannot change its own engine's language mode after the engine has started). No "best-effort run anyway" fallback.

5. **What the low-disk detection (G2) does.** Once per broker boot, immediately after the ConstrainedLanguage gate and before the workspace-path forbidden-form gate, the broker probes the AvailableFreeSpace of the volume that hosts the workspace via `[System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($WorkspacePath))`. Two thresholds, both inlined in the broker source as literal expressions (`500MB`, `2GB`) per the hostile-environment vocabulary doctrine: below 500 MB free the broker writes ONE row tagged `condition='low_disk'` / `outcome='detected'` / `evidence_classification='observational'` and exits with `EXIT_E_ENVIRONMENT_LOW_DISK` (= 11); between 500 MB and 2 GB the broker writes ONE row tagged `condition='low_disk'` / `outcome='warning'` and continues startup. The warning is observational only — it does NOT block, refuse, or enter any periodic re-check loop. The probe is unrelated to the per-bake precheck against `$Script:MinFreeDiskBytesForCook`; that is a separate threshold for a separate code path. If the probe itself throws (drive not ready, transient I/O), the broker continues — a single probe failure must not block service; the exception is surfaced via the recent-errors ring.

6. **What the workspace-path forbidden-form gate does.** Once per broker boot, immediately after the low-disk probe and before the broker-session lifecycle begins, the broker reuses the existing `Get-WorkspacePathDiagnostic` classifier (no detection logic is reimplemented) and promotes the subset of classifications that are fundamentally incompatible with the broker's filesystem contract (SQLite WAL atomic-rename, FileShare.None sentinel semantics, MAX_PATH budget for bake folders) from "warning" to a refusal. The promoted subset is `isUnc`, `isReparsePoint` (junction / symlink / mount point / OneDrive placeholder root), `exceedsClassicLimit`, and `driveType` in {`Removable`, `Network`, `CDRom`, `Ram`, `NoRootDirectory`}. On refusal the broker writes ONE row tagged `condition='workspace_path_forbidden'` / `outcome='detected'` / `evidence_classification='observational'` and exits with the EXISTING `EXIT_E_WORKSPACE_LOCKED` (= 4) — no new exit constant is introduced for the workspace-refusal class, which is unified under the existing code. The other workspace-path diagnostic warnings (`workspace_canonical_differs_from_display`, `workspace_uses_long_path_prefix`) remain observational and continue to surface via the existing post-listener diagnostic block.

---

### 17.19 Hostile environment matrix and no-silent-catch structural gate

1. **Purpose.** This section adds no new runtime detection beyond the gates documented in §17.18. It does two things only: (G4) documents every hostile-environment condition the appliance is known to encounter in enterprise Windows fleets, separating those the broker can self-detect from those that are fundamentally an enterprise-IT responsibility; and (G5) installs one structural smoke-time gate against silent-catch regressions across `app\broker\**`.

2. **The hostile environment matrix (G4).** The matrix below is the canonical inventory of conditions the appliance has encountered in the field. Each row states whether the broker can detect the condition at startup, the broker's response, what evidence the operator can inspect, and where remediation lives. Conditions marked **NOT self-detectable by broker** are an **enterprise-IT responsibility** — the broker does not, and per the hostile-environment doctrine **will not**, probe for them: detection would require code-integrity-bypassing introspection the appliance is forbidden from performing, would produce nondeterministic results across antivirus / EDR / VDI vendors, and would generate false-positive evidence rows that obscure the genuine forensic record.

   | Condition | Detectable by broker | Response | Evidence | Remediation |
   |---|---|---|---|---|
   | ConstrainedLanguage (G1) | **Yes** — `$ExecutionContext.SessionState.LanguageMode` at startup | Hard exit `EXIT_E_ENVIRONMENT_CONSTRAINED_LANGUAGE` (= 10) | `environment_observations` row `condition='constrained_language' / outcome='detected'`; console line; recent-errors entry `Source='environment_constrained_language'` | TROUBLESHOOTING §13z; AppLocker / WDAC / execution-policy adjustment by enterprise IT |
   | Low disk (G2) | **Yes** — `AvailableFreeSpace` of workspace volume at startup | Hard exit `EXIT_E_ENVIRONMENT_LOW_DISK` (= 11) below 500 MB; observational `outcome='warning'` row between 500 MB and 2 GB; no action above 2 GB | `environment_observations` row `condition='low_disk' / outcome='detected'` or `'warning'`; console line; recent-errors entry `Source='environment_low_disk'` | TROUBLESHOOTING §13aa; free workspace volume or relocate via launcher workspace picker |
   | Workspace path forbidden form (G3) | **Yes** — reuses `Get-WorkspacePathDiagnostic`; promotes `isUnc`, `isReparsePoint`, `exceedsClassicLimit`, `driveType` ∈ {Removable, Network, CDRom, Ram, NoRootDirectory} | Hard exit using existing `EXIT_E_WORKSPACE_LOCKED` (= 4) | `environment_observations` row `condition='workspace_path_forbidden' / outcome='detected'`; console line naming the classification subset that triggered refusal | OPERATOR_GUIDE §17.18 paragraph 6; relocate workspace to a local fixed-drive NTFS path of supported length |
   | AppLocker (publisher / path / hash allow-list enforcement) | **NOT self-detectable by broker** — enterprise IT responsibility. AppLocker enforcement surfaces indirectly as the ConstrainedLanguage condition (G1) when the launched engine is downgraded; the broker treats that as the operative signal | Indirect: the G1 exit fires and the operator follows TROUBLESHOOTING §13z | The G1 `environment_observations` row plus `Get-AppLockerPolicy -Effective` run by enterprise IT outside the broker | Enterprise IT adjusts AppLocker rule sets so the broker's install location runs in `FullLanguage` (see TROUBLESHOOTING §13z item 1) |
   | WDAC (code integrity policy) | **NOT self-detectable by broker** — enterprise IT responsibility. WDAC enforcement also surfaces indirectly as G1 | Indirect via G1 | The G1 row plus `Get-CimInstance Win32_DeviceGuard` run by enterprise IT | Enterprise IT publishes a WDAC policy that allows the broker's signed scripts and SQLite native DLL |
   | Microsoft Defender quarantine (or third-party AV quarantine) of broker files | **NOT self-detectable by broker** — by the time a Cookbook file is quarantined the broker either cannot launch (DLL missing — exit 5) or hits a launch-time SHA-256 mismatch (`EXIT_E_PACKAGE_TRUST_INTEGRITY` = 9 via launch-trust verification). The broker does not enumerate Defender quarantine state | Existing exit 5 (SQLite DLL missing) or exit 9 (launch trust mismatch); not a new environment-observation path | TROUBLESHOOTING §14 (Defender / AV quarantine triage); enterprise-IT AV console quarantine list and event log | TROUBLESHOOTING §14 (restore false positives only; add install tree to AV exclusion if policy permits; re-run install from a known-good release) |
   | SmartScreen (MOTW / mark-of-the-web download blocking) | **NOT self-detectable by broker** — SmartScreen acts on launcher download / extract, before the broker process exists | Outside the broker's lifecycle entirely | Windows event log; SmartScreen popup at launcher download time | Enterprise IT unblocks the launcher / installer via SmartScreen reputation review or via Group Policy SmartScreen carve-out |
   | TLS interception (MITM proxy / SSL inspection on enterprise network) | **NOT self-detectable by broker** — the broker does not initiate any outbound TLS connection during bake execution; PAX uses pre-loaded credentials. TLS interception only affects the launcher's release-feed fetch and operator-side Microsoft Graph traffic, both outside the broker process | None at the broker level | Launcher release-feed fetch failures (separate code path); operator's own browser / Graph SDK telemetry | Enterprise IT adds the relevant Microsoft Graph / GitHub release hostnames to the TLS-inspection bypass list |
   | VDI / non-persistent session host | **NOT self-detectable by broker** — Citrix / AVD / Horizon environments are configuration choices that don't expose a deterministic local API. The broker treats them as ordinary workstations; the workspace must be on persistent storage or evidence rows are lost at logoff | None at the broker level (the workspace-path G3 gate refuses `Network` driveType, which catches the worst VDI failure mode — workspace on a roaming network share) | Operator observes vanishing workspace contents across sessions | Enterprise IT pins the workspace to persistent profile storage (FSLogix profile container, persistent-disk pool, etc.) |
   | EDR (CrowdStrike / SentinelOne / Defender for Endpoint, etc.) script-inspection hooks | **NOT self-detectable by broker** — EDR product detection would require fingerprinting vendor-specific DLLs injected into PowerShell, which is exactly the kind of opaque introspection the hostile-environment doctrine forbids. EDR may slow startup, intercept `Add-Type`, or trigger SmartScreen-adjacent alerts | None at the broker level; symptoms surface as bake-launch latency, signature-verification failures, or G1 (if EDR forces ConstrainedLanguage) | EDR vendor console alert log; enterprise-IT-side event correlation | Enterprise IT allow-lists the signed PAX broker entry points in the EDR console |
   | Browser policy (download blocking, certificate pinning, extension control) | **NOT self-detectable by broker** — browser-side policy never reaches the broker process | None | Operator's browser developer-tools console; enterprise IT's browser-policy console | Enterprise IT carves out the SPA origin in browser policy as needed |
   | Roaming user profiles | **NOT self-detectable by broker** — the broker cannot reliably distinguish a roaming profile from a local profile at the API level. Damage surfaces as workspace data loss at logoff, identical in shape to the VDI case | None at the broker level (the G3 gate catches the common "workspace on `\\server\share`" misconfiguration via `isUnc` refusal) | Operator observes workspace contents missing or stale across logons | Pin workspace to a non-roaming local path; coordinate with enterprise IT to exclude the workspace directory from profile roaming |
   | Long path (> MAX_PATH = 260) | **Partially** — the G3 gate's reuse of `Get-WorkspacePathDiagnostic` includes `exceedsClassicLimit`, which is **promoted to refusal** at the workspace root. Long paths **inside** the workspace tree (recipes, bake output) are not pre-checked; they may fail individual file operations | Hard exit at startup via G3 if the WORKSPACE ROOT itself exceeds the classic limit. Per-file long-path failures inside the tree surface as ordinary bake-time `IOException`s in the recent-errors ring | G3: `environment_observations` row `condition='workspace_path_forbidden' / outcome='detected'`. Per-file: bake stderr and recent-errors entries | Move workspace closer to drive root; enable per-machine NTFS long-path support via Group Policy (`Computer Configuration / Administrative Templates / System / Filesystem / Enable Win32 long paths`) |
   | Offline host (no network connectivity at startup) | **NOT self-detectable by broker** — and intentionally so: the broker is designed to operate fully offline. The Microsoft Graph permission probe is operator-initiated and lives entirely in the SPA; the broker has no startup network dependency | None — broker starts normally | Operator sees Microsoft Graph permission errors only when they next exercise that feature | Reconnect host / coordinate with enterprise IT for proxy configuration; the broker's local-only data path is unaffected |

3. **The no-silent-catch structural gate (G5).** A smoke entry in `_temp\phase_ai_verification\smoke_ai_c5_3.ps1` parses every `*.ps1` under `app\broker\**` with the PowerShell AST and inspects every `CatchClauseAst`. A catch passes the gate when it does **any** of the following: (a) **records or surfaces the error** — calls a logging cmdlet (`Write-Host` / `Write-Warning` / `Write-Error` / `Write-Verbose` / `Write-Information` / `& $logAppend`), an evidence writer (`Add-RecentError` / `Add-EnvironmentObservation`), an HTTP/WS response writer (`Write-JsonResponse` / `Write-StaticErrorResponse`), a recovery writer (`Set-CookInterrupted` / `Stop-BrokerSession` / `& $sqlExec`), or rethrows / exits / restarts (`throw` / `exit` / `Invoke-Shutdown`); (b) **defaults to a safe value** — body is purely a `return [literal]`, an assignment to a literal (`$x = $null` / `$x = -1` / `$x = @()` / `$x = 'unavailable'`), a `break` / `continue`, or any combination thereof; or (c) **is a recognized best-effort cleanup wrapper** — body is empty or `$null`-only AND the matching try body is a single method-invocation (`$x.Close()` / `$x.Dispose()` / `$x.Stop()` / `$x.Kill()` / `$x.Rollback()` / `[void](...)` / `[System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR(...)`), an `Unregister-Event` call, a SQLite cleanup chain (CreateCommand / Execute* / Dispose), or the explicit `Invoke-Shutdown` orchestration idiom (a try body composed solely of nested `try { cleanup } catch {}` clauses guarded by `if (...)`). Any catch that is empty and is paired with a try body the gate does not recognize as one of those cleanup wrappers is a **VIOLATION** and fails the smoke. The gate enforces forward progress: future code added to the broker cannot regress on silent operational catches. The smoke introduces no new exit constant, no new schema, no new counter, no new `/health` key, and no new module file. Existing best-effort-cleanup catches inside `Invoke-Shutdown` and the various Dispose-chain teardown paths remain unchanged because they implement the explicit best-effort-on-shutdown doctrine documented in the comment block above `Invoke-Shutdown` itself.

---

### 17.20 Diagnostics bundle export

When an operator needs to share forensic evidence with enterprise IT, Microsoft Support, or a Cookbook developer, they invoke a single PowerShell function inside the running broker process to produce a self-contained bundle. The bundle is **raw evidence**: it is not summarized, not interpreted, and not filtered. The operator analyzes it externally using standard tools (`sqlite3`, DB Browser for SQLite, a text editor for JSON).

1. **The command.** From a PowerShell session connected to the broker script scope (for example, a developer-attached `pwsh` host that has loaded `app\broker\Start-Broker.ps1`), invoke:

   ```powershell
   Export-DiagnosticsBundle                       # folder bundle, default location
   Export-DiagnosticsBundle -OutputPath C:\temp\bundle1   # folder bundle, explicit path
   Export-DiagnosticsBundle -AsZip                # zip bundle, default location
   Export-DiagnosticsBundle -OutputPath C:\temp\bundle1.zip -AsZip   # zip bundle, explicit path
   ```

   With no arguments, the bundle is written to `<workspace>\Diagnostics\bundle_<utc-stamp>\`. The function returns a `[pscustomobject]` with `BundlePath`, `Format` (`folder` or `zip`), and a `Contents` list naming each file in the bundle.

2. **What the bundle contains.** Exactly four kinds of files; nothing else.

   | File                                         | Source                                                                                                    | What the operator does with it                                                                       |
   |----------------------------------------------|-----------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------|
   | `cookbook.sqlite` (+ `-wal` / `-shm` if present) | Filesystem-level copy of the live database file. The function opens **no** SQLite connection.            | Inspect with `sqlite3` / DB Browser. Includes `update_request_observations`, `package_trust_observations`, `environment_observations`, plus all operational tables. |
   | `recent_errors.json`                         | Serialization of the broker's in-memory `$Script:RecentErrors` ring, plus the overflow counter.           | Read directly; the entries are the same shape that already appears at `/health.recentErrors`.        |
   | `VERSION.json`                               | Verbatim copy of `app\VERSION.json` as it was at bundle time.                                             | Read directly to confirm cookbook version, channel, and bundled PAX script identity.                 |
   | `metadata.json`                              | Small descriptor written by the function: `createdAtUtc`, `brokerCookbookVersion`, `brokerPaxScriptVersion`, `brokerReleaseChannel`, source paths. | Read directly to confirm when and from where the bundle was produced.                                |

   The bundle contains no recipes, no bake output, no chef-supplied data, and no secrets beyond what is already inside the database file. It contains no broker source code.

3. **What the function does NOT do.**

   - It does **not** open the SQLite database (no connection, no `SELECT` / `INSERT` / `UPDATE` / `DELETE` / `ALTER`).
   - It does **not** modify the database file, the WAL file, the SHM file, the workspace, or `VERSION.json`. The only writes occur inside the destination bundle directory.
   - It does **not** filter, reshape, summarize, or rank rows. The `.sqlite` file is an unmodified copy of the live file at copy time -- the broker does not transform it on the way out. (The database itself is reconstructible from the source-of-truth files under the workspace, so equivalence across environments is logical, not binary.)
   - It does **not** run on a schedule, register a background timer, or upload the bundle anywhere. Export is operator-initiated and pull-only.
   - It does **not** expose an HTTP route, a WebSocket message, or any other remote-callable surface. There is no `/diagnostics/query`, no `/observations/list`, no `/health/history`. The broker exposes the bundle on disk; the operator reads it on disk.

4. **How the operator interprets the bundle (high level).**

   - `recent_errors.json` and `cookbook.sqlite` together cover **what happened recently** (the ring buffer) and **what was observed over time** (the append-only observation tables in the database).
   - `VERSION.json` and `metadata.json` together cover **which build of the broker produced the evidence** so that any cross-version interpretation (for example, comparing two bundles from two upgrade attempts) is unambiguous.
   - Deeper interpretation is intentionally out of scope for this guide. Per §17.1, §17.11, §17.15, and §17.18, the broker observes; the operator (or enterprise IT, or a Cookbook developer) interprets.

5. **Completeness guarantee (G2) and read-only guarantee (G3).** The function fails fast — before writing any file — if any required input is missing: the cookbook database file, `VERSION.json`, or the in-memory recent-errors ring. A partial bundle is never produced. If the operator sees a `BundlePath` returned, every required evidence file is present in it. The function remains read-only with respect to the database (no connection opened, no SQL keywords used), the workspace, and `VERSION.json`; all writes occur strictly inside the destination bundle directory.

6. **How to use the bundle (G4).** Once the bundle is on disk, **no broker involvement is required to read it**. Open `cookbook.sqlite` with `sqlite3` or DB Browser for SQLite and inspect the observation tables (`update_request_observations`, `package_trust_observations`, `environment_observations`) plus any operational tables of interest. Open `recent_errors.json`, `VERSION.json`, and `metadata.json` in any text editor or JSON viewer — no schema is hidden, no field is encrypted, no transformation has been applied. To attach the bundle to a support case, zip the folder (or pass `-AsZip` at export time) and attach the single `.zip`; the recipient needs only standard SQLite and JSON tools to interpret it.

---

## 18. Where this guide stops

This guide does not cover:

- The PAX script's own command-line surface — see the [PAX repository](https://github.com/Microsoft/PAX).
- Authoring Power BI templates against PAX output.
- Release-engineering for distributors — see [RELEASE_PACKAGE.md](RELEASE_PACKAGE.md).
- Symptom-by-symptom triage — see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

If you are looking for a roadmap, marketing material, or feature pitch, this is not the right document. This guide describes the appliance as it is.
