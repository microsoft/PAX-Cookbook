# PAX Cookbook — Troubleshooting

This guide covers operational triage. Every entry follows the same shape:

> **Symptom** — what you see.
> **Why** — what is actually happening.
> **Evidence to inspect** — chef-owned files you can look at right now.
> **Corrective action** — one specific, named action. No vague boilerplate, no magic incantations, no referral to a phantom help desk.

If you encounter a symptom that is not in this guide, do **not** invent a procedure. Capture the evidence, open an issue, and reference this guide.

---

## 1. PowerShell version too old

> **Symptom** — Launcher exits with code `1` immediately, with a message including `PowerShell 7.4 or newer is required` and a link to `https://aka.ms/PSWindows`.
>
> **Why** — Cookbook hard-gates PowerShell 7.4+. Windows PowerShell 5.1 and PowerShell 7.0–7.3 are not supported. The gate runs before any other launcher logic.
>
> **Evidence to inspect** — Run `$PSVersionTable.PSVersion` in the same prompt you launched from. If it shows `5.x` you launched from Windows PowerShell. If it shows `7.0`–`7.3` you launched from an outdated PowerShell 7.
>
> **Corrective action** — Install PowerShell 7.4 or newer from <https://aka.ms/PSWindows>. Launch Cookbook from the new `pwsh.exe` (the Start Menu shortcut uses `pwsh.exe` automatically; if you are running from a custom prompt, confirm `pwsh` resolves to the new version).

---

## 1a. Workspace selection cancelled — exit 3

> **Symptom** — Launcher prints `No workspace selected. Aborting.` and exits with code `3`. The browser does not open.
>
> **Why** — The launcher reached the workspace picker (because no valid `cookbook.bootstrap.json` was present, or the previously recorded workspace path no longer exists on disk), the picker opened, and the chef closed it without choosing a folder. The bootstrap pointer is left untouched. Cookbook never guesses a workspace and never falls back to a default location.
>
> **Evidence to inspect** —
> - The launcher's last line — it will name the cancellation, not a corruption.
> - `%APPDATA%\PAXCookbook\cookbook.bootstrap.json` — if the file already existed, the launcher did not modify it. If it did not exist, no file was created.
>
> **Corrective action** — Re-launch Cookbook (the Start Menu shortcut or `launcher\Start-PAXCookbook.ps1`) and complete the workspace picker. If you want to abort intentionally, exit `3` is the expected return code; no further action is required.

---

## 1b. Live broker found, but default browser failed to open — exit 7

> **Symptom** — Launcher prints `Live broker detected for this workspace`, names the broker's PID and port, and then prints `Failed to open default browser: <reason>`. The launcher exits with code `7`. The broker is **still running** — only the browser hand-off failed.
>
> **Why** — The launcher's runtime-reuse path tries to open your Windows default browser at the broker's authoritative URL. That call can fail when: no default browser is registered for `http:`, the default browser association is broken or points at an uninstalled application, or Windows refuses the file association call for any other environmental reason. The launcher does not retry, does not try to pick a different browser, and does not change your file associations.
>
> **Evidence to inspect** —
> - The exact error message printed after `ERROR   Failed to open default browser:` — this is the underlying Windows / .NET exception.
> - The URL the launcher printed on the preceding line. That URL is the authoritative entry point for the already-running broker. It is loopback-only and carries a per-session token; it is valid only for as long as the broker stays up.
> - `Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice'` — what Windows currently records as your default `http:` handler.
>
> **Corrective action** — Copy the printed URL and paste it into the browser of your choice. The broker continues to serve that workspace until you close the browser tab (or kill the broker process). If you want the launcher to open the browser automatically next time, fix the default-browser association in Windows Settings → Apps → Default apps. Cookbook does not modify file associations on your behalf.

---

## 1c. Reopening Cookbook after closing the browser tab

> **Symptom** — You closed the Cookbook browser tab or window. The broker (the PowerShell console window) is still open. You want the SPA back.
>
> **What to do** — If you close the browser while Cookbook is still running, open PAX Cookbook from the Start Menu or Desktop shortcut again. Cookbook will reconnect to the running local broker and reopen the browser. You do not need to kill the broker just to reopen the page.
>
> **Why this works** — Each shortcut launches the same launcher. The launcher first checks whether a healthy broker is already serving this workspace (PID alive, recorded loopback port responds, `/api/v1/health` matches the workspace path). If yes, the launcher reuses the existing broker, reads the broker's session-token sidecar, composes the loopback URL with `#t=<token>`, and asks Windows to open it in your default browser. No second broker is started. The token is redacted in the launcher console output.
>
> **If the broker is no longer healthy** — The launcher falls through to a normal fresh start: it starts a new broker, waits for `/api/v1/health`, waits for the broker-token sidecar to appear, and opens the browser with the new token fragment.
>
> **Related** — Use **Repair PAX Cookbook Shortcuts** in the Start Menu to refresh the Start Menu and (when present) Desktop `.lnk` files against the current install. It does not delete workspace data, the install tree, or any bake outputs.

---

## 2. Broker exit code 4 — workspace locked

> **Symptom** — Launcher prints `Broker exited 4` and the SPA does not open. The named exit constant is `EXIT_E_WORKSPACE_LOCKED`.
>
> **Why** — Another Cookbook broker is already running against the same workspace, **or** a previous broker crashed without releasing the lock. The new broker performs a four-step liveness probe (PID alive → image is `pwsh` → recorded TCP port responds within 500 ms → `/api/v1/health` returns within 2 s with matching workspace path). If the probe says another broker is alive, the lock is honored.
>
> **Evidence to inspect** —
> - `<Workspace>\Runtime\workspace.lock` — contains the PID, port, and workspace path of the broker holding the lock.
> - `<Workspace>\Runtime\workspace.lock.acquire` — acquire-time sentinel.
> - Task Manager → look for a `pwsh.exe` running `Start-Broker.ps1`.
>
> **Corrective action** — Find the other Cookbook in your browser tabs and close it, **or** end the `pwsh.exe` running `Start-Broker.ps1` in Task Manager. Then relaunch. Do not delete `workspace.lock` by hand; the next broker will reclaim it automatically when the four-step probe confirms the previous broker is gone.

---

## 3. Broker exit code 5 — SQLite DLL integrity failure

> **Symptom** — Launcher prints `Broker exited 5` and a message naming the failing path and expected vs actual SHA-256. The named exit constant is `EXIT_E_SQLITE_DLL_INTEGRITY`.
>
> **Why** — The vendored SQLite native DLL inside the install tree does not match its pinned SHA-256. This typically means: an antivirus product quarantined or rewrote the DLL, the install tree was partially overwritten, or the install was extracted from a corrupt ZIP.
>
> **Evidence to inspect** —
> - `<InstallRoot>\install.log` — the most recent install or update operation.
> - The expected and actual hash values printed by the broker. Save these before reinstalling.
> - Your antivirus product's quarantine / event log. Check for entries naming any file under `<InstallRoot>\App\lib\`.
>
> **Corrective action** —
>
> 1. Quit Cookbook (if anything is running).
> 2. Re-run the installer from a known-good extracted release: `pwsh -File <release-folder>\app\install\Install-PAXCookbook.ps1 install`.
> 3. The installer replaces `App\` and preserves the prior copy under `Backups\`.
> 4. If the failure recurs, the cause is environmental (antivirus, disk error, sync agent) — not Cookbook. Resolve the environmental cause before reinstalling again.

---

## 4. Broker exit code 6 — PAX script integrity failure

> **Symptom** — Launcher prints `Broker exited 6` and a message naming the bundled PAX script path and expected vs actual SHA-256. The named exit constant is `EXIT_E_PAX_SCRIPT_INTEGRITY`.
>
> **Why** — The bundled PAX script (`app/resources/pax/PAX_Purview_Audit_Log_Processor.ps1`) on disk does not match the SHA-256 pinned in `app/VERSION.json -> paxScript.sha256`. Causes are the same as exit code 5 (AV, partial install, corrupt extraction).
>
> **Not the same as `acquisition_required`** — Under Cookbook v1 (see [OPERATOR_GUIDE.md §4a](OPERATOR_GUIDE.md#4a-pax-engine-acquisition)), a fresh install does not yet have the PAX engine on disk. That state is reported as a structured `acquisition_required` response on cook routes and **does not** exit the broker process. The SPA renders the acquisition dialog while the broker keeps running. Exit code `6` is reserved for the post-acquisition state where the script bytes are present but corrupt. Seeing exit `6` on a brand-new install you have never run before is unexpected under Cookbook v1 — a never-acquired install reports `acquisition_required`, not exit `6`. Treat exit `6` as on-disk corruption of an engine that was previously acquired, and follow the diagnosis below.
>
> **Evidence to inspect** —
> - The expected and actual hash values printed by the broker.
> - `<InstallRoot>\App\VERSION.json` — confirms the expected hash. Also check `paxScript.acquisitionPolicy`: `"embedded"` means the script is expected to be in the install tree; `"external"` means acquisition is still pending and you should be seeing `acquisition_required` from the broker, not exit `6`.
> - The file at `<InstallRoot>\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1`.
> - Your AV quarantine log, especially if the file is missing.
>
> **Corrective action** — Same procedure as exit code 5. Re-run `Install-PAXCookbook.ps1 install` from a known-good extracted release. Do **not** copy a PAX script from somewhere else into the install tree — Cookbook is asserting integrity against a specific bundled version, not "any PAX script." Do **not** edit the PAX script bytes at `<InstallRoot>\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1` to "make the hash match" — the pin in `VERSION.json.paxScript.sha256` is the expected hash of the **approved release**, not the hash of whatever is currently on disk, and editing the script (whitespace, line endings, encoding, comments, signing, wrapping — anything) cannot reconcile the mismatch. Do **not** edit `paxScript.sha256` in `VERSION.json` to bless the on-disk bytes — doing so disables the integrity check entirely. The corrective action is always to restore the script bytes from a known-good source: re-extract the release ZIP, re-run the installer, and for v1 (external acquisition) installs re-drive acquisition through **Settings → PAX engine** on next launch (see [§17e](#17e-pax-engine-acquisition-errors)).

---

## 5. Workspace folder rejected

> **Symptom** — During first launch (or after editing the bootstrap pointer), the workspace picker rejects your selection with a specific named reason, such as `Workspace must be on a local fixed drive`, `Workspace cannot be on OneDrive`, or `Workspace path must be writable`.
>
> **Why** — Workspaces have hard-requirement rules (see [OPERATOR_GUIDE.md §5.2](OPERATOR_GUIDE.md#52-workspace-hard-requirement-rules)). The rejection is by design and not a bug.
>
> **Evidence to inspect** — The specific rejection reason in the launcher's output. The launcher names exactly one reason — no generic "invalid path" message.
>
> **Corrective action** — Pick a local folder on a fixed drive that you own and can write to. `C:\PAXCookbookWorkspace` is a fine default. Do not symlink, junction, or `subst` your way around the rule — the launcher detects the underlying transport.

---

## 6. Corrupt bootstrap pointer

> **Symptom** — Launcher proceeds to the workspace picker on a machine where you previously chose a workspace. Or, the launcher mentions that the prior bootstrap was preserved as `cookbook.bootstrap.json.broken-<UTC>`.
>
> **Why** — The launcher could not parse `%APPDATA%\PAXCookbook\cookbook.bootstrap.json`. Causes include: a partial write (machine crashed during workspace selection), a sync-agent conflict on `%APPDATA%`, an encoding drift, or manual editing that produced invalid JSON.
>
> **Evidence to inspect** —
> - `%APPDATA%\PAXCookbook\cookbook.bootstrap.json.broken-<UTC>` — the launcher preserved the exact bytes it observed. Open in any text editor.
> - The UTC timestamp in the filename (format `yyyyMMddTHHmmssZ`) tells you when the launcher rotated it.
>
> **Corrective action** — Re-pick your workspace in the launcher's workspace picker. The launcher will write a fresh `cookbook.bootstrap.json`. The `.broken-<UTC>` file is preserved verbatim and is yours to inspect or delete. Cookbook never deletes it automatically.

---

## 7. Broker port already in use

> **Symptom** — Launcher reports a failure to bind a loopback port, or the broker exits during startup before printing a port number.
>
> **Why** — The broker binds an ephemeral loopback port. If your machine has very few free ephemeral ports (rare on a desktop, more common on a server), or another process is exhausting them, allocation can fail.
>
> **Evidence to inspect** —
> - `<InstallRoot>\install.log` (if startup logging is enabled).
> - `netstat -an | findstr 127.0.0.1` to see loopback bindings.
>
> **Corrective action** — Identify and quit the process consuming the ephemeral port range, then relaunch. Cookbook does not let you pick a fixed port — by design. The loopback port is randomized per launch.

---

## 8. Update check fails

> **Symptom** — `GET /api/v1/updates/check` returns an error in the SPA, or the Updates panel reports a manifest-fetch failure.
>
> **Why** — One of:
> 1. `<InstallRoot>\App\VERSION.json -> updateManifestUrl` is `null`. The broker does not check for updates in this case **by design** — it is the documented "I am offline / I will be updated manually" configuration.
> 2. The URL is set but unreachable (DNS, proxy, firewall).
> 3. The URL is reachable but the response is not a valid manifest (wrong content-type, malformed JSON, unknown `schemaVersion`).
>
> **Evidence to inspect** —
> - `<InstallRoot>\App\VERSION.json -> updateManifestUrl`. If null, no check is expected.
> - The exact error string from the broker. The broker is specific about which of the three failure modes it hit.
>
> **Corrective action** —
> - If `updateManifestUrl` is `null` and you want updates: edit `VERSION.json` to point at your distributor's manifest URL, **or** apply updates manually by extracting a new release and running `Install-PAXCookbook.ps1 update`.
> - If the URL is set but unreachable: fix the network path (proxy, firewall) and retry.
> - If the response is malformed: this is the distributor's bug, not yours. Open an issue with the distributor.

---

## 9. Update package downloads but refuses to apply

> **Symptom** — A staged package appears in `<InstallRoot>\Updates\<version>\` but the Apply button is disabled or the broker refuses to apply, reporting one of the trust states from `Get-PackageTrustState`.
>
> **Why** — Cookbook honors its trust model. A staged package will sit at one of these states:
>
> | Reported state | Meaning | Apply-able? |
> | --- | --- | --- |
> | `verified` | SHA-256 matches AND `.sig` cryptographically verifies AND signer is in your `trusted-signers.json`. | Yes. |
> | `unsigned` | SHA-256 matches; no `.sig` on disk. | Apply-able only if your policy permits unsigned packages. The broker does not silently auto-promote. |
> | `hashMismatch` | The downloaded ZIP's SHA-256 does not match the manifest. | **No.** Refuse. Delete the staged copy and re-stage. |
> | `signatureInvalid` | `.sig` is present but cryptographic verification failed. | **No.** Refuse. |
> | `signerUnknown` | `.sig` verified but the signer thumbprint is not in `trusted-signers.json`. | **No** until the chef adds the thumbprint. |
> | `signaturePresentNotVerified` | `.sig` on disk; verifier module did not run (partial install). | **No.** Repair the install (run `Install-PAXCookbook.ps1 install` from a known-good release). |
>
> **Evidence to inspect** —
> - `<InstallRoot>\Updates\<version>\*.metadata.json` — what the broker recorded at stage time.
> - `<InstallRoot>\Updates\<version>\*.zip.sha256` — the SHA-256 that was supplied alongside.
> - `<Workspace>\Trust\trusted-signers.json` — the chef-owned allowlist, if any.
>
> **Corrective action** — Match the corrective action to the state in the table above. Do not edit `metadata.json` to upgrade a trust state by hand — the broker recomputes its own evaluation, and tampering will be reported.

---

## 10. SPA loads but says "session token invalid"

> **Symptom** — The SPA loads in the browser but every API call returns 401 / "session token invalid".
>
> **Why** — The bearer token is regenerated on every broker launch. If you bookmark the SPA URL with an old token, the next launch will not accept it. The token is also bound to a single browser session — opening the SPA in a second browser without the new token will be rejected.
>
> **Evidence to inspect** — The SPA URL in the browser. It carries the per-session token in a query parameter or local-storage handoff. If the launcher just restarted the broker, the token has changed.
>
> **Corrective action** — Re-open the SPA via the launcher (Start Menu shortcut or `Start-PAXCookbook.ps1`). The launcher passes the current token to your browser. Do not bookmark Cookbook URLs.

---

## 11. Bake process exits with a non-zero code

> **Symptom** — In the SPA's terminal view, the bake ends and the Bakes page shows it as `failed` with a non-zero exit code from PAX.
>
> **Why** — The bundled PAX script returned a non-zero exit code. This is **PAX's** decision, not Cookbook's. Cookbook does not interpret PAX exit codes — it records them and stops.
>
> **Evidence to inspect** —
> - `<Workspace>\Cooks\<cookId>\` — full stdout / stderr captured by the broker.
> - The expanded PAX command line shown in the Preview view of the recipe.
> - The PAX repository's documentation for the specific exit code.
>
> **Corrective action** — Consult PAX documentation for the exit code. Cookbook will not retry the bake for you, will not "fix" the PAX invocation, and will not modify the recipe to work around a PAX error.

---

## 11a. PAX rollup reports Python is unavailable

> **Symptom** — A rollup-based bake (recipes built from the **M365 Usage Analytics Dashboard** or **AI-in-One Dashboard** templates, or any recipe with `processing.rollup = "Rollup"` / `"RollupPlusRaw"`) prints, in the embedded terminal, a PAX-emitted message indicating that Python is not on `PATH`, that the Python interpreter cannot be located, that `winget` is unavailable, or that the python.org installer fallback failed.
>
> **Why** — PAX's rollup post-processor is implemented in Python (3.10 or newer). PAX itself attempts to locate a compatible interpreter on `PATH`, and if none is found, PAX attempts a per-user silent install (`winget Python.Python.3.13` → python.org installer fallback). If both paths fail — for example, `winget` is not present on the host, the python.org download is blocked by a proxy or firewall, or the host has no network egress — PAX cannot complete the rollup post-processing step. **This is a PAX-owned behavior. Cookbook does not detect, install, configure, or vendor Python.**
>
> **Evidence to inspect** —
> - `<Workspace>\Cooks\<cookId>\` — the full PAX stdout / stderr, including PAX's own Python-resolution messages (`Resolve-PythonExe`, `Rollup: installing Python 3.13 via winget...`, `Rollup: winget is not available on this host; falling back to python.org installer.`, etc.).
> - `Get-Command python; Get-Command python3; Get-Command py` from a fresh PowerShell prompt — confirms what the host currently exposes.
> - `Get-Command winget` — confirms whether the package manager PAX would use is available.
>
> **Corrective action** —
> 1. Provide a Python 3.10+ interpreter on `PATH` by whichever method your environment normally uses (your IT-managed Python image, an existing per-user install, a `winget install Python.Python.3.13` run manually, or a download from <https://www.python.org>). Cookbook does not specify or vendor a particular Python distribution.
> 2. Re-run the bake. PAX re-resolves the interpreter at the start of every rollup invocation; no Cookbook-side action is required.
> 3. If your environment forbids Python entirely, do not use rollup-based recipes. Non-rollup PAX invocations (raw / 1-1 mode) have no Python dependency. The other bundled templates and any chef-authored non-rollup recipe will run on PowerShell alone.
>
> Cookbook will **not** add a "Python check" panel, a "Repair Python" button, or a Python installer of its own. Python belongs to PAX. The PAX-owned `Resolve-PythonExe` path inside the bundled PAX script is the single source of truth for Python resolution.

---

## 12. Bake shows as `interrupted` after a reboot or crash

> **Symptom** — The Bakes page lists a bake with terminal state `interrupted`, not `succeeded` or `failed`.
>
> **Why** — On startup, the broker reconciles orphan bake sentinels — any bake whose row in `cookbook.sqlite` is still `running` but whose child process is no longer alive is marked `interrupted`. This is a recovery action, not a failure.
>
> **Evidence to inspect** —
> - `<Workspace>\Cooks\<cookId>\` — whatever PAX wrote before the interruption.
> - `<Workspace>\Database\cookbook.sqlite` — the row will show the reconciled state.
>
> **Corrective action** — Decide whether to re-run the recipe. Cookbook does not automatically restart interrupted bakes. The interrupted bake directory is left in place as evidence and can be deleted by hand when you no longer need it.

---

## 12a. I moved the workspace folder and one or more old bakes no longer open

> **Symptom** — You quit Cookbook, copied or renamed your workspace folder, relaunched against the new path, and most bakes are fine — but a small number of older bakes show a path that looks wrong, or fail to open their bake folder.
>
> **Why** — Cookbook stores bake folders workspace-relative so that moving the workspace "just works" for every bake created after that storage shape was introduced. The broker runs a one-shot, idempotent migration on every startup that rewrites old absolute paths to the relative form. That migration only rewrites rows whose absolute prefix matches the *current* workspace root. If a row's stored path points outside the current workspace — typically because you moved the workspace *before* the broker ever ran with workspace-relative paths — Cookbook preserves the row as-is rather than silently rewriting recorded evidence it cannot prove belongs to this workspace.
>
> **Evidence to inspect** —
> - `GET /api/v1/health` → the response `status` will be `degraded` (not `ok`) for as long as foreign-prefix rows are visible in this broker session. `recentErrors` will list each affected bake with the message `Cook <id> has cook_folder outside current workspace; preserved as-is: <path>`. If more than `recentErrorCapacity` (currently 10) such errors were recorded since broker startup, `recentErrorCount` plus `recentErrorOverflowCount` together tell you how many were observed in total — no evidence is silently discarded.
> - `<Workspace>\Database\cookbook.sqlite` → `SELECT cook_id, cook_folder FROM cooks` shows the stored path.
>
> **Corrective action** —
> - If the recorded bake folder still exists at the stored absolute path (e.g. on a drive you also moved), open it directly with Explorer — Cookbook does not need to resolve it for you to inspect its contents.
> - If you want the row to participate in normal Cookbook flows again, update `cook_folder` in `cookbook.sqlite` manually (with any standard SQLite tool) to point at the new location — workspace-relative form `Cooks\<recipeId>\<cookId>` is preferred. Cookbook will treat the row as already-migrated on the next startup.
> - New bakes are unaffected; this only impacts pre-existing rows whose absolute prefix was already foreign at the moment the workspace-relative migration first ran.

---

## 12b. Recipe load failures — three distinguishable error labels

> **Symptom** — Opening or saving a recipe in the SPA returns a 4xx error whose body mentions one of `recipe_file_missing`, `recipe_file_malformed`, or `recipe_unsupported_schema_version`.
>
> **Why** — Cookbook returns a discriminated load result for recipes. The broker tells you exactly which of three conditions it actually saw.
>
> **`recipe_file_missing` (404 on GET / preview, 422 on PUT)** —
> - **Meaning.** The SQLite metadata index has a row for this recipe, but `<Workspace>\Recipes\<id>.recipe.json` is not on disk.
> - **Likely cause.** The chef (or a sync agent) moved or deleted the file; the workspace was restored from a partial backup; the file was trashed manually but the row was not.
> - **Corrective action.** Restore the file from `<Workspace>\Recipes\_trash\` (if it was trashed) or from a backup. If the file is gone for good, accept the loss — the SQLite row by itself does not contain enough leaves to reconstruct the recipe. The chef may delete the orphan row with any standard SQLite tool, or just create a new recipe that supersedes it.
>
> **`recipe_file_malformed` (422)** —
> - **Meaning.** The file exists but is not parseable JSON, or it parses to something that is not a JSON object. The `detail` field of the response carries the parser's verbatim message (e.g. `json_parse_failed: Unexpected token … `).
> - **Likely cause.** The chef opened the `.recipe.json` in a text editor and saved a syntactically broken version; an incomplete write was interrupted by a crash or power loss (the broker's own writes are temp-and-rename atomic, but a chef-driven `>` redirect, an editor with a faulty save-on-quit, or external tooling can land a half-file on disk); a file-system or sync conflict produced a merge marker.
> - **Corrective action.** Open `<Workspace>\Recipes\<id>.recipe.json` in a text editor and fix the syntax yourself. The broker deliberately does NOT auto-heal — silently overwriting damaged content is exactly the failure mode the slice doctrine forbids. If you cannot recover the JSON, restore the file from a backup, or create a new recipe and delete the broken row.
>
> **`recipe_unsupported_schema_version` (422)** —
> - **Meaning.** The file parses cleanly but its `recipeSchemaVersion` is missing, non-numeric, or different from the version this broker supports. The response carries both `supportedSchemaVersion` (this broker) and `detail` (the observed value).
> - **Likely cause.** A future Cookbook broker wrote this file and you have downgraded; a chef hand-edited the version field to something out of range; a workspace was copied between machines running incompatible broker versions.
> - **Corrective action.** Move forward — install the newer broker version. The recipe file format is intentionally version-locked and has no auto-migration path; an older broker will not "best-effort" a newer recipe. If downgrade is mandatory, edit the file by hand to bring it back into the supported schema (and accept any feature loss that implies).
>
> **What stays the same across all three** —
> - **GET** of the recipe returns the error; the SPA's recipe editor shows the truthful label.
> - **PUT** of an update refuses to proceed; the prior on-disk state is not overwritten. This is the central doctrine: the broker will not silently coerce, auto-heal, or fabricate validity.
> - **Preview** of the stored recipe (`POST /api/v1/recipes/preview` with only `recipeId` in the body) returns the same labels, so the SPA's "show me the command" surface sees the same failure vocabulary.
> - **List** (GET /api/v1/recipes) reads only the SQLite metadata index and so is unaffected — a workspace can list cleanly even while one specific recipe file is damaged.

---

## 12c. The SPA Status pill — what each label means and what to do

> **Symptom** — The topbar pill labelled `Status` in the SPA topbar shows something other than `ok`, or it changes between page views.
>
> **Why** — The Status pill is a passive observer of every HTTP request the SPA issues, plus a one-shot probe of `/api/v1/health` at boot. It updates only when a real request settles; it does not poll. Each label corresponds to a specific kind of evidence.
>
> | Pill label | What it means | What to do |
> | --- | --- | --- |
> | `unknown` | The SPA has no settled HTTP evidence yet (just-loaded tab, or the tab was hidden more than five minutes and the prior evidence is now considered stale). | Click any page in the navigation. A successful fetch promotes the pill to `ok`. A failed fetch promotes it to `unreachable` or `unauthorized`. |
> | `ok` | The most recent request round-tripped and the boot `/api/v1/health` returned `status: 'ok'`. | Nothing. The broker is reachable and self-reports healthy. |
> | `degraded` | A `/api/v1/health` response carried `status: 'degraded'`. The broker is reachable; it is itself reporting one or more recorded errors this session. | Open the broker's `recentErrors` queue (`GET /api/v1/health`, see OPERATOR_GUIDE §11.8) for the actual list. The chef inspects the cause; the SPA does not interpret it. |
> | `unreachable` | The most recent request failed with a network-level error (DNS, TCP, TLS, or the SPA's bounded 30-second fetch timeout fired). | Refresh the page (F5). If the pill comes back to `unknown` and then `ok` after a successful fetch, the broker is reachable on the most recent attempt; if `unreachable` returns, check whether the broker process is still running (Task Manager, or relaunch from the launcher). |
> | `unauthorized` | At least one request returned HTTP 401 since the most recent successful response. The session token in this tab is no longer accepted. | Relaunch the appliance from the launcher. The launcher mints a fresh session token and opens a new tab with the token in the URL fragment. The old tab cannot be re-authorized in place. |
>
> **The pill does NOT auto-promote.** Time alone never moves the pill to `ok`. The pill steps **down** to `unknown` automatically only when the tab has been hidden for more than five minutes — the SPA refuses to keep claiming `ok` without fresh evidence.
>
> **The pill does NOT poll.** Background timers are deliberately absent. The pill reflects evidence the SPA already had reason to gather; it never invents new traffic just to keep itself green.

---

## 12d. Live-tail close labels in the bake view

> **Symptom** — The bake view's console section displays a parenthesised notice such as *(bake reached terminal state — live tail closed)* or *(live tail interrupted — broker stream ended without terminal marker; reload to re-check bake status)*.inal marker)*.
>
> **Why** — The bake log live-tail is a WebSocket. When the connection closes, the SPA records the close code and labels the notice truthfully so the chef can tell which of four conditions actually occurred.
>
> | Notice text | Meaning | What to do |
> | --- | --- | --- |
> | *(bake reached terminal state — live tail closed)* | The broker closed the socket cleanly with code 1000 because the bake reached a terminal state (succeeded, failed, or stopped). The hydrated console above the notice is complete. | Nothing. The output you see is the final output. The bake view's status row carries the terminal-state label. |
> | *(live tail interrupted — broker stream ended without terminal marker; reload to re-check bake status)* | The socket dropped with code 1006 after subscription succeeded. The stream died without a Close frame. The bake itself may or may not still be running. | Click Refresh on the bake view (or reload the page). The next `GET /api/v1/cooks/<id>` will report the bake's current truthful status. Do **not** assume the bake is dead. |
> | *(live tail disconnected — abnormal close code N; navigate away and back to reattempt)* | The socket dropped with some other non-1000 code after subscription succeeded. | Navigate to another page in the SPA, then back to the bake view. A fresh mount opens a new socket if the bake is still running. Look up the close code in the WebSocket RFC for the lower-level diagnosis. |
> | *(could not open live tail — close code N)* | The socket failed before `onopen` fired. Subscription never completed. | The hydrated log fetched via HTTP is still authoritative. Reload the bake view to retry the socket. If it consistently fails, check the Status pill: `unauthorized` means the session token expired; `unreachable` means the broker is not answering on this port. |
>
> The bake view **never reconnects automatically**. There is no retry, no backoff, no fallback poll. The doctrine: the chef chooses when to retry, because every retry costs a fresh WebSocket handshake against the broker.

---

## 12e. Save / bake / stop / materialize failed with "may or may not have been recorded"

> **Symptom** — The recipe editor (or the Recipes list, the bake view's Stop button, or the Pantry detail page) shows a banner saying *"Your save may or may not have been recorded on the server"* or *"A bake may or may not have been started on the server"* or similar.
>
> **Why** — State-mutating HTTP calls are not idempotent from the SPA's perspective. The SPA sent the request; the broker may have received and committed it before the socket dropped, or it may not. The SPA cannot tell. The truthful response is to refuse to claim either outcome and direct the operator to the authoritative view.
>
> **What to do — recover, do NOT retry blind:**
>
> | Operation that failed | Authoritative view | Why this order matters |
> | --- | --- | --- |
> | **Save recipe (POST /recipes or PUT /recipes/{id})** | Reload the recipe (use the back button or click the recipe again from the Recipes list). The reload re-fetches the on-disk recipe. Compare it to what you tried to save. | Retrying a save blindly can overwrite a save that *did* succeed with stale content, or can produce two recipes if the create call partially committed. |
> | **Trigger bake (POST /recipes/{id}/cook)** | Open the Runs list (`#/cooks`). A bake started in the last few seconds will appear there with status `running` or already in a terminal state. | Retrying a bake trigger blindly can produce two concurrent bakes, both writing to the same recipe's output path, both racing for the same sentinel files. The bake trigger is the **most dangerous** operation to retry. |
> | **Stop bake (POST /cooks/{id}/stop)** | Click Refresh on the bake view. The status row reflects the bake's current truthful state. | Retrying a stop blindly is safe but pointless — if the first stop landed, the bake is already in a terminal state. |
> | **Materialize template (POST /templates/{id}/materialize)** | Open the Recipes list. A recipe materialized in the last few seconds will appear there (server orders by `createdAt DESC`). | Retrying a materialize blindly can produce two near-identical recipes from one operator click. |
>
> **The general rule:** every banner that says *"may or may not have been recorded"* is a directive to **read the authoritative view first**, decide whether the operation actually happened, and only then retry the operation if it truly did not.

---

## 12f. A scheduled-task run did not appear in the Runs list

> **Symptom** — A Windows scheduled task fired at the expected time (you can see the run in Task Scheduler's history pane, or you saw a transient `pwsh.exe` process via Task Manager / Process Explorer) but the corresponding row is missing from Cookbook's Runs list.

> **What this means** — Cookbook does not poll for scheduled-task runs in real time. The wrapper that Task Scheduler invokes writes its evidence into `<Workspace>\Cooks\<cookId>\` (the same flat layout used by manual bakes) and Cookbook imports that evidence lazily, on two occasions: at broker startup, and immediately before serving `GET /api/v1/cooks` (which is what the Runs page calls). A run that fired since the last Runs-page load is therefore expected to appear the next time the Runs page is loaded or refreshed, not the moment the wrapper finishes.

> **What to check, in order:**
>
> 1. **Reload the Runs page.** Browser refresh (Ctrl+R) or click the navigation entry again. This re-issues `GET /api/v1/cooks`, which re-runs the importer. The row should appear if the wrapper has written `wrapper-finished.json` (or `wrapper-refused.json`).
> 2. **Confirm the wrapper folder exists.** Open `<Workspace>\Cooks\` and look for a folder whose name is a 26-character Crockford ULID (the scheduled-task bake IDs are not the 36-character hyphenated GUIDs that manual bakes carry). The folder should contain `wrapper-started.json` and either `wrapper-finished.json` or `wrapper-refused.json`. If only `wrapper-started.json` exists and the run was more than a few seconds ago, the wrapper crashed before finishing — see §12g.
> 3. **Check broker stderr for an importer error.** The broker's startup log line `Scheduled-task reconciliation: X imported, Y unchanged, Z skipped-malformed, W errors.` is the importer's audit summary. If `W > 0`, an import failed and the corresponding folder will not be visible until the underlying issue is fixed. The recent-errors panel on the Settings page surfaces the same errors.
> 4. **Confirm the recipe still exists in the database.** The reconciler refuses to import a wrapper folder whose `recipeId` no longer resolves to a row in the `recipes` table (the folder is left in place and counted as `skippedMalformed`). If the recipe was deleted between fire time and the next reconciliation pass, the run will not appear in Cookbook even though Task Scheduler ran it.

> **What this guide will NOT recommend** — Adding a poller. The lazy-import design is deliberate. The reconciler is bounded (at most 256 new folders per call) and idempotent (re-running it produces the same database state). Refreshing the Runs page is the supported way to make recent runs visible.

## 12g. A scheduled bake shows `status = 'interrupted'` with `error_class = 'wrapper_orphan_classified'`

> **Symptom** — A scheduled-task run is visible in the Runs list with status `interrupted`. Opening the bake detail shows `error_class = wrapper_orphan_classified` and the Scheduled-run card shows only the `wrapper-started.json` fields; the wrapper-finished/refused fields are em-dashed.

> **What this means** — The wrapper wrote `wrapper-started.json` (so the importer knows a run was launched), but never wrote `wrapper-finished.json` or `wrapper-refused.json`. The wrapper-recorded PID is no longer alive on this host, and a grace window (12 hours by default) has passed since the wrapper recorded its `startedAt`. The importer therefore concluded the wrapper process did not survive long enough to write a finish envelope, and classified the run as interrupted. Common reasons: the host was rebooted while the wrapper was running; the wrapper's parent session was terminated; the wrapper process was killed externally; PAX itself hung and was force-killed by an operator.

> **What to do:**
>
> 1. **Read the bake folder.** The bake's PAX log (`Purview_Audit*.log`) may still be in the bake folder and may indicate how far PAX progressed before being killed. The reconciler links the log into `cook_artifacts` and the bake detail surfaces it.
> 2. **Decide whether to re-run the bake.** Scheduled-task runs are not resumable in V1; the chef cannot click Resume on a `wrapper_orphan_classified` bake. The chef can either let Task Scheduler fire the recipe again on its next cadence, or bake the recipe manually from the Recipes page to pick up immediately.
> 3. **Do NOT manually edit the bake row.** The interrupted state is the truthful classification of what the wrapper folder evidences. Editing the row to `completed` would lose forensic value and produce a divergence between the database and the on-disk envelopes.

## 12h. A scheduled bake shows `wrapperOutcome = pax_nonzero`, `wrapperOutcome = spawn_failed`, or `wrapperOutcome = wrapper_internal`

> **Symptom** — A scheduled bake's Scheduled-run card shows a non-`pax_ok` outcome.

> **What this means** — The wrapper completed (it wrote `wrapper-finished.json`), but the underlying PAX invocation did not succeed cleanly. The three distinct outcomes mean three different things:

> - **`pax_nonzero`** — The wrapper spawned PAX, PAX ran, and PAX exited with a non-zero exit code. The bake row's `exit_code` column carries the PAX exit code unchanged. Read the bake's PAX log to determine why PAX failed. The bake is classified `status = failed`.

> - **`spawn_failed`** — The wrapper attempted to spawn PAX (`pwsh.exe -File <PAX script>`) and the spawn itself failed before any PAX code ran. The most common causes are: the bundled PAX script file is missing or unreadable; the PowerShell host (`pwsh.exe`) is missing from the PATH the scheduled task ran under; the wrapper-resolved PAX path failed an integrity probe. The wrapper's `wrapperReason` carries the specific cause. The bake is classified `status = failed`.

> - **`wrapper_internal`** — The wrapper itself raised an unhandled exception after writing `wrapper-started.json` but before completing the PAX spawn cleanly. `wrapperReason` carries the exception message. The bake is classified `status = failed`. This outcome is rare and indicates a defect in the wrapper code path; please copy the wrapper's `wrapper-finished.json` into a support bundle (see §18).

> **What to do** — Address the root cause indicated by `wrapperReason` and re-run the bake (either by letting Task Scheduler fire again on its next cadence or by running the recipe manually). Do NOT edit the bake row.

## 12i. A scheduled bake shows up as `refused` with `reason = refused_stale_projection`

> **Symptom** — A scheduled bake's Scheduled-run card shows the refusal envelope (`refusedAt`, `reason = refused_stale_projection`). The bake is classified `status = refused`.

> **What this means** — At fire time the wrapper recomputed the redacted projection hash (PAX script version + redacted argv) and found it differed from the hash stored in the `scheduled_tasks` row at registration time. Rather than invoking PAX with a projection the chef has not confirmed, the wrapper wrote `wrapper-refused.json` and exited with code 30. No PAX invocation took place; no audit log was emitted. The most common causes are: the chef edited the recipe (changing argv) after registering the schedule; the bundled PAX script was upgraded (changing `paxScriptVersion`); the Chef's Key bound to the schedule was rotated.

> **What to do** — Open the recipe in the Recipe editor. The Schedule card will report the schedule as **stale**. Review the change, then click **Save schedule** to re-confirm the projection (re-entering the client secret if the recipe uses `AppRegistrationSecret`). Task Scheduler's next fire will use the refreshed projection. There is intentionally no auto-clear of staleness; re-saving is the explicit confirmation that the new projection is what the chef wants Task Scheduler to fire.

---

## 12j. Recipe validator failures — run shape and mutex rules

> **Symptom** — Save or Bake Recipe refuses with one or more validator errors that name a `query.mode`, `destinations.fact`, `destinations.userInfo`, `query.activityTypes`, `query.agentFilter`, `ingredients.m365Usage`, or `advanced.extraArguments` path. The recipe file is not written.

> **What this means** — The validator enforces the run-shape contract before any bake is spawned. The most common failure modes:

| Validator keyword | Cause | Remedy |
| --- | --- | --- |
| `factOutputMutex` | `destinations.fact` carries both `path` and `appendFile`. | Pick one mode. OutputPath and AppendFile are mutually exclusive on the fact destination. |
| `userInfoOutputMutex` | `destinations.userInfo` carries both `path` and `appendFile`. | Pick one mode. OutputPathUserInfo and AppendUserInfo are mutually exclusive on the user-info destination. |
| `userInfoOnlyForbidsAuditDates` / `userInfoOnlyForbidsFactDestination` / `userInfoOnlyForbidsRollup` / `userInfoOnlyForbidsActivityTypes` / `userInfoOnlyForbidsUserIds` / `userInfoOnlyForbidsGroupNames` / `userInfoOnlyForbidsAgentFilter` / `userInfoOnlyForbidsPromptFilter` / `userInfoOnlyForbidsM365Usage` | `query.mode = 'userInfoOnly'` was selected but the recipe still carries audit-shape fields. | Remove the named field. UserInfoOnly is a separate run shape — it skips the audit query. See OPERATOR_GUIDE §6.11. |
| `userInfoOnlyRequiresUserInfoDestination` | `query.mode = 'userInfoOnly'` was selected but no `destinations.userInfo` is configured. | Add a user-info output destination (OutputPathUserInfo or AppendUserInfo). |
| `agentFilterMutex` | `query.agentFilter` carries more than one of `agentIds` / `agentsOnly` / `excludeAgents`. | Pick one filter mode. The three agent filters are mutually exclusive. |
| `rollupExcludeCopilotRequiresM365Usage` | The trailer carries `-ExcludeCopilotInteraction` (or `includeCopilotInteraction = false`) without `includeM365Usage = true`. | Either enable the M365 usage bundle, or remove the exclusion. ExcludeCopilotInteraction is only valid inside the M365 usage bundle. |

> **What to do** — Read the validator's `instancePath` to find which leaf of the recipe is wrong, fix that leaf, and Save again. The validator names every failing path; do not guess.

## 12k. Recipe validator failures — unsupported / blocked switches

> **Symptom** — Save refuses with a validator error that names an `advanced.extraArguments` entry such as `-RecordTypes`, `-ServiceTypes`, `-IncludeAgent365Info`, `-OnlyAgent365Info`, `-OutputPathAgent365Info`, `-AppendAgent365Info`, `-UseEOM`, `-ExportWorkbook`, `-ExplodeArrays`, `-ExplodeDeep`, or `-RAWInputCSV`.

> **What this means** — The verbatim trailer (`advanced.extraArguments`) is scanned at validate time. Cookbook deliberately blocks these switches:

- `RecordTypes` / `ServiceTypes` — Cookbook only supports the constrained `ActivityTypes = [CopilotInteraction]` shape. The broader PAX activity-type surface is not exposed.
- `IncludeAgent365Info` / `OnlyAgent365Info` / `OutputPathAgent365Info` / `AppendAgent365Info` — the Agent365Info catalog/export surface remains disabled / unsupported in this Cookbook version. The active agent filters (AgentId / AgentsOnly / ExcludeAgents) are a separate surface and are configured via `query.agentFilter`, not the trailer.
- `UseEOM` / `ExportWorkbook` / `ExplodeArrays` / `ExplodeDeep` / `RAWInputCSV` — removed or rollup-incompatible PAX switches.

> **What to do** — Remove the blocked switch from `advanced.extraArguments`. If the underlying need is an agent filter, configure `query.agentFilter` instead. If the underlying need is the M365 usage bundle, enable `ingredients.m365Usage.includeM365Usage` instead. There is no override or compatibility shim.

## 12l. Bake refuses with a ClientCertificatePath / certificate-password limitation

> **Symptom** — A bake configured for `auth.mode = 'AppRegistrationCertificate'` refuses to start, or the recipe editor refuses to accept a certificate path that requires a password.

> **What this means** — Cookbook surfaces `auth.clientCertificatePath` only for the **passwordless** PFX case (a PFX with no protecting password). Password-protected PFX files require future secure secret storage; Cookbook does not yet have a UI for that and deliberately refuses any path that would put a certificate password into the recipe JSON or onto the bake's argv.

> **What to do** — Use the thumbprint / certificate-store auth mode for password-protected certificates today. Do not embed a certificate password in `advanced.extraArguments` or anywhere else in the recipe — Cookbook does not redact recipe fields, and embedded passwords would be persisted in plain text. See OPERATOR_GUIDE §6.16.

---

## 12m. The `Notifications\` folder or today's `.jsonl` file is missing

> **Symptom** — You expected a durable notification log under `<Workspace>\Notifications\` and the folder, or today's `<YYYY-MM-DD>.jsonl` file, is not there.

> **Why** — The dated file is created the first time a bake reaches a terminal status *on that calendar day* with at least one matching per-status switch turned on. A workspace whose first bake of the day has not finished yet, or one where all three per-status switches (completed / errored / interrupted) are off, has nothing to write. The folder rolls to a new file by date, so an empty `Notifications\` folder simply means no qualifying bake has finished today.

> **Evidence to inspect** — Open Settings and confirm at least one of "Notify on completed / errored / interrupted bakes" is on. Run a short bake to completion and re-check `<Workspace>\Notifications\<today>.jsonl`. Yesterday's file, if present, confirms the path and permissions are correct.

> **Corrective action** — Turn on the per-status switch for the outcomes you care about, then finish a bake. If the folder still does not appear after a finished bake with a switch on, confirm the workspace path is writable (the same condition that would block cook history). A missing notification file never means a bake was lost — the bake's terminal status in cook history is the system of record.

---

## 12n. A Windows (Action Center) toast did not appear

> **Symptom** — A bake finished but no native Windows toast was shown, even though in-app notifications or the JSONL log recorded the event.

> **Why** — Windows toasts are best-effort and the most commonly suppressed surface. Likely causes: Windows notifications for the bake are **off** (the "Windows notifications" setting is off by default unless you enabled it); **Focus Assist / Do Not Disturb** is on, so Windows is holding or dropping toasts; notifications are turned off for the app in **Windows Settings → System → Notifications**; or the session cannot raise an Action Center toast (a limitation that can apply when running from a source tree or in certain elevated / service-style sessions).

> **Evidence to inspect** — Open today's `<Workspace>\Notifications\<YYYY-MM-DD>.jsonl` line for the bake. If the line shows the toast surface was attempted and skipped or failed with a bounded reason while the durable line itself was written, the event was emitted correctly and only the OS surface did not display. Check Windows Focus Assist state and the per-app notification toggle in Windows Settings.

> **Corrective action** — Turn on Windows notifications in Cookbook Settings, turn off Focus Assist (or allow Cookbook through it), and enable notifications for the app in Windows Settings. A toast that never appears does **not** mean the bake failed or that other surfaces were skipped — the durable JSONL line and any enabled in-app / webhook surface are independent.

---

## 12o. The outbound webhook did not fire

> **Symptom** — You configured a webhook but your endpoint received nothing after a bake finished.

> **Why** — The webhook is **disabled by default** and is only attempted when the master "Webhook enabled" switch is on, a valid `https://` endpoint URL is set, *and* the finished bake's per-status switch is on. It is also a single best-effort attempt with a short timeout and **no retry queue** — a slow or failing endpoint is reported once and dropped. The endpoint is rejected before any network call if it is not `https`, if it points at a loopback / link-local / private / cloud-metadata host, or if it embeds credentials.

> **Evidence to inspect** — Open the bake's line in today's `.jsonl`. If the webhook surface was not attempted, the master switch, the URL, or the per-status switch is off / unset. If it was attempted and failed, the line carries a bounded reason (for example a rejected URL, a non-HTTPS scheme, a blocked host, or an HTTP / timeout failure). The reason never includes the response body or the endpoint URL.

> **Corrective action** — Confirm in Settings that the webhook is enabled, the URL is a public `https://` endpoint (not `http`, not `localhost`, not a private IP), and the per-status switch for that outcome is on. Test with an endpoint you control. Because there is no retry queue, a transient endpoint outage is not re-sent — finish another bake to retry. A webhook failure never fails a bake.

---

## 12p. The Teams webhook preset posts nothing, or the endpoint rejects the body

> **Symptom** — You selected the **Teams** webhook format and your Teams channel / Power Automate flow shows nothing, or the endpoint returns an error.

> **Why** — The Teams preset is a **webhook** integration: it POSTs a MessageCard to a Teams *incoming webhook* or a Power Automate "When an HTTP request is received" URL. It is **not** a Microsoft Graph integration — there is no sign-in, no token, and no Graph call — so it only works against an endpoint that accepts an inbound MessageCard POST. Pointing the Teams format at anything other than such a webhook URL (for example a Graph endpoint, or a flow expecting a different schema) will be rejected by the receiver.

> **Evidence to inspect** — Confirm the URL is a Teams incoming-webhook / Power Automate HTTP-trigger URL (an `https://...webhook.office.com/...` or your flow's trigger URL). Check the bake's `.jsonl` line for a bounded webhook reason. Verify the format setting is `teams` and the receiver expects a MessageCard.

> **Corrective action** — Use a genuine incoming-webhook / Power Automate URL for the Teams format, or switch to the `generic` format for a flat-JSON endpoint you control. Direct, Graph-authenticated Teams posting is not part of this release.

---

## 12q. A notification did not arrive — was the bake lost?

> **Symptom** — A toast, an in-app banner, the JSONL line, or the webhook did not show up, and you are unsure whether the bake itself succeeded.

> **Why** — Notifications are a best-effort convenience layer attempted *after* a bake has already reached its terminal status and been written to cook history. Any surface can be off, suppressed by the OS, or failing without affecting the bake. A missing notification is never evidence of a failed or lost bake.

> **Evidence to inspect** — Open **Cook History** and the bake's detail page. The terminal status there (and the PAX log inside it) is authoritative. The durable `<Workspace>\Notifications\<YYYY-MM-DD>.jsonl` line, written before any other surface, shows which surfaces were attempted, which succeeded, and a bounded reason for any that did not.

> **Corrective action** — Trust cook history and the JSONL log over any single delivery surface. If a specific surface is consistently missing, use §12m–§12p to diagnose that surface; the bake outcome itself is already recorded.

---

## 13. Chef's Keys and the broker lock

> The symptoms grouped under §13 cover Chef's Key CRUD, secret bind / replace / remove, and the broker-scoped Windows Hello / PIN re-auth gate. The doctrine that backs these symptoms lives in [OPERATOR_GUIDE.md §12](OPERATOR_GUIDE.md#12-chefs-keys) and [§13](OPERATOR_GUIDE.md#13-re-authentication-doctrine).

## 13a. Every mutating route returns HTTP 423 `brokerLocked`

> **Symptom** — Save, Bake Recipe, Chef's Key CRUD, secret bind — every mutating action fails with `423 brokerLocked` and the SPA shows a full-viewport "PAX Cookbook is locked" overlay.
>
> **Why** — The broker maintains a single process-wide lock. It enters the locked state on startup and after any operation that closes the lock window (a planned idle-timeout policy will also close the lock on prolonged idle). Allow-listed routes (`/api/v1/health`, `/api/v1/broker/lock-state`) still return 200 while locked, so the Status pill correctly reports `locked` rather than `unreachable`.
>
> **Evidence to inspect** —
> - The `cookbook:brokerLocked` event in the browser console (logged by `lock-overlay.js`).
> - `GET /api/v1/broker/lock-state` — returns `{ "state": "Locked" }` with no body fields exposing why.
> - The broker's stderr (when launched from the launcher console) — shows the verification verdict if a recent unlock failed.
>
> **Corrective action** — Click **Unlock with Windows Hello** on the overlay. Approve the prompt that Windows owns. The overlay closes the moment the broker confirms `state: "Unlocked"`. If the prompt is dismissed, the overlay stays up — there is no other way out (close the browser tab, but the next launch will land you on the same overlay).

---

## 13b. Windows Hello unlock failed — prompt missing, on wrong monitor, or completed but returned `ComInteropFailure`

> **Symptom** — One of:
>
> 1. Clicking **Unlock with Windows Hello** does not produce a prompt at all. The overlay's status line reports a verdict such as `DeviceNotPresent`, `NotConfiguredForUser`, `DisabledByPolicy`, or `DeviceBusy`.
> 2. The prompt appears, but on a different monitor than the browser tab.
> 3. The prompt appears and you complete biometric / PIN successfully (you see the green check or the OK button), but the overlay then reports `(verdict: ComInteropFailure)` with the message "Windows verification surface is unavailable. Restart the appliance and try again; if the problem persists, see TROUBLESHOOTING §13b."
>
> **Why** — The broker delegates verification to Windows by calling `Windows.Security.Credentials.UI.UserConsentVerifier` via raw WinRT/COM interop. The native dialog is rendered by Windows and anchored to the **broker's** console window (`GetConsoleWindow()`), not to the browser. This produces three categories of failure:
>
> | Category | Verdicts | Where the failure happens |
> |---|---|---|
> | OS / policy state | `DeviceNotPresent`, `NotConfiguredForUser`, `DisabledByPolicy`, `DeviceBusy` | Returned by Windows before the prompt is shown |
> | Operator action | `Canceled`, `RetriesExhausted` | Returned by Windows after operator dismissal / failed attempts |
> | Cookbook interop layer | `ComInteropFailure`, `Unknown` | The broker's WinRT/COM call into UserConsentVerifier failed |
>
> The full enum is `Verified` | `DeviceNotPresent` | `NotConfiguredForUser` | `DisabledByPolicy` | `DeviceBusy` | `RetriesExhausted` | `Canceled` | `ComInteropFailure` | `Unknown`. Cookbook-private values (`ComInteropFailure`, `Unknown`) never come from the OS; they signal that the broker's native call did not produce a meaningful OS verdict.
>
> **Monitor placement is not adjustable from Cookbook.** The Hello dialog anchors to the HWND that the broker hands the OS. The broker's HWND is its own console window; the browser is a separate process with its own HWND. There is no API surface to re-parent the Windows Hello dialog to the browser tab. If the prompt lands on the wrong monitor, move the broker console window (drag it to the monitor with the browser before clicking Unlock) and Windows will follow.
>
> **The post-biometric OK button is rendered by Windows**, not by Cookbook. Some Windows versions / policies require a confirmation tap after the biometric succeeds; the appliance has no API to skip it.
>
> **Evidence to inspect for `ComInteropFailure`** — The broker records a diagnostic tag identifying which of the seven WinRT interop paths failed. The tag is appended to `/api/v1/health.recentErrors[]` under `source: 'windows_hello_interop'` in the form `detail=<code>:<hr_or_status>`:
>
> | Code | Meaning |
> |---|---|
> | `classname_hcs:<hr>` | `WindowsCreateString` failed building the activation-factory class name |
> | `roget_factory:<hr>` | `RoGetActivationFactory` could not return `IUserConsentVerifierInterop` |
> | `message_hcs:<hr>` | `WindowsCreateString` failed building the prompt message HSTRING |
> | `reqverif:<hr>` | `RequestVerificationForWindowAsync` returned a failing HRESULT |
> | `poll_qi:<hr>` | The returned async op could not be QI'd to `IAsyncInfo` |
> | `poll_status:<hr>` | `IAsyncInfo::get_Status` failed mid-poll |
> | `poll_error:<status>` | The async op finished with status `Error(3)` (biometric flow itself faulted) |
> | `poll_timeout:<elapsed_ms>` | The async op was still `Started` after the 60 s default timeout |
> | `getresults:<hr>` | `IAsyncOperation<T>::GetResults` failed after `Completed` status |
> | `native_exception:<type>:<msg>` | An unmanaged exception escaped the C# Verify call entirely |
>
> If you completed Hello successfully and still got `ComInteropFailure`, the tag is almost certainly `poll_error:3`, `getresults:<hr>`, or `native_exception:*`. The first two indicate that Windows reported the operation completed but the result object was unusable; the third indicates a hosting / runtime problem in the broker process (rare).
>
> For `Canceled`, `DeviceNotPresent`, `NotConfiguredForUser`, `DisabledByPolicy`, `DeviceBusy`, `RetriesExhausted`: no detail tag is recorded — these are direct OS verdicts.
>
> **Corrective action** —
>
> - **`DeviceNotPresent`, `NotConfiguredForUser`, `DisabledByPolicy`** — Configure Windows Hello (PIN at minimum) for the current account in Windows Settings → Accounts → Sign-in options. Cookbook **cannot** fall back to its own password prompt; the verification contract is delegated by design.
> - **`DeviceBusy`** — Another caller is using the biometric sensor. Wait a few seconds and try again.
> - **`Canceled`, `RetriesExhausted`** — Try again. After repeated `RetriesExhausted`, Windows may temporarily lock biometric for a cooldown; PIN remains available.
> - **`ComInteropFailure` with detail `roget_factory:*`** — The WinRT activation factory could not be resolved. Confirm you are running on Windows 10 1607 or later (UserConsentVerifierInterop was added in `RS1`). Restart the broker.
> - **`ComInteropFailure` with detail `poll_error:3` or `getresults:*`** — Biometric flow faulted inside Windows after the prompt was shown. Capture the broker's most recent `/api/v1/health.recentErrors[]` entry with `source: 'windows_hello_interop'` and report it — this is a Windows-level fault, not a Cookbook policy refusal.
> - **`ComInteropFailure` with detail `native_exception:*`** — The C# native call threw. Capture the exception type and message from the detail tag and report it.
> - **`Unknown`** — Generic fail-closed. Restart the broker. If reproducible, capture `recentErrors[]` and report it.

---

## 13c. Chef's Key **Test** passed but PAX still fails with `unauthorized`

> **Symptom** — The Chef's Keys page reports `Verified @ <utc>` for a Chef's Key. A bake using that Chef's Key fails with PAX printing `unauthorized` or `Authorization_RequestDenied`.
>
> **Why** — **This is by design.** The Chef's Key test endpoint performs a *structural* check only:
> - **Secret mode** — confirms the Credential Manager entry exists and is readable.
> - **Certificate mode** — confirms the cert is in the named store and has a private key.
>
> It does **not** perform a token-exchange call to Entra, and it does **not** call Microsoft Graph. Cookbook deliberately avoids burning a permission grant during a structural test. The first real authorization check happens when PAX runs.
>
> A passing test therefore proves the credential bytes are present and readable. It does **not** prove the workload has Graph permissions, that the app registration's `Reports.Read.All` (or whichever permission the recipe requires) is granted, or that admin consent has been given. The Entra portal is the authoritative view of permission state.
>
> **Evidence to inspect** —
> - The PAX bake's stderr for the precise Graph error (`AccessDenied`, `Authorization_RequestDenied`, `consent_required`).
> - The Entra portal: **App registrations → <your app> → API permissions**. Confirm that the workload has the permission scopes PAX needs **and** that admin consent has been granted for the tenant.
> - The Entra portal: **Sign-in logs**. A workload sign-in failure carries a correlation ID and a concrete failure reason.
>
> **Corrective action** — Grant and admin-consent the required Graph application permissions for the app registration. The Cookbook Chef's Key **never** needs to change for a permission-only fix. Re-run the bake — no re-bind, no re-test, no re-create.

---

## 13d. Bake fails with `unknown_authProfileId` or `auth_profile_required`

> **Symptom** — A bake fails immediately (before PAX is even spawned) with `unknown_authProfileId: <id>` or `auth_profile_required`.
>
> **Why** —
> - `unknown_authProfileId` — the recipe references an `authProfileId` that no longer exists in this workspace's `auth_profiles` table. The Chef's Key was deleted, or the workspace was cloned without the bound credentials.
> - `auth_profile_required` — the recipe's `auth.mode` is `AppRegistrationSecret` or `AppRegistrationCertificate` but `auth.authProfileId` is empty.
>
> **Evidence to inspect** —
> - The Chef's Keys page — does the referenced id appear?
> - The recipe JSON (open the recipe in the editor and inspect `auth.authProfileId`).
> - For Secret mode: the bound state column on the Chef's Keys page (a Chef's Key may exist but be unbound — `secret_not_bound` is reported separately).
>
> **Corrective action** —
> - If the `authProfileId` is missing: re-create the Chef's Key (Mode is immutable, so re-create with the same mode) and re-point the recipe at the new id.
> - If the recipe lacks an `authProfileId`: open the recipe editor, pick a Chef's Key from the **Chef's Key** dropdown, and save. The dropdown is filtered by mode.

---

## 13e. Bound a secret, but the next bake says `secret_not_bound`

> **Symptom** — You used **Bind / Replace secret** and the page showed `bound`. Within minutes, a bake fails with `secret_not_bound`.
>
> **Why** — One of:
> 1. The Credential Manager entry lives under the **Windows account that bound it**. If the broker is later run under a different Windows account (Run As, a different scheduled-task principal, or a different desktop session), the bound entry is not visible.
> 2. A separate Windows process (or roaming-profile policy) deleted the entry between bind and bake.
> 3. The certificate-mode Chef's Key was bound and tested, then the cert's private key was revoked or the cert was removed from the store. Re-binding a *secret* on a *certificate-mode* Chef's Key is not possible; the modal is hidden for certificate Chef's Keys.
>
> **Evidence to inspect** —
> - **Control Panel → Credential Manager → Windows Credentials**. Look for an entry named `cookbook/auth-profile/<authProfileId>`. If it is missing, the broker cannot read it.
> - `whoami` in a terminal — confirm the broker is running under the same Windows account that bound the secret.
> - For certificate mode: `Get-Item Cert:\<store>\<thumbprint> | Format-List *`. Confirm `HasPrivateKey`.
>
> **Corrective action** —
> - Re-bind the secret while signed in as the Windows account that will run the broker.
> - If the bake is going to be triggered by Task Scheduler, schedule it under the same Windows account.

---

## 13f. The recipe editor's **Chef's Key** dropdown is empty or wrong

> **Symptom** — You picked `AppRegistrationSecret` or `AppRegistrationCertificate` as the recipe's auth mode, but the **Chef's Key** dropdown shows only `(none selected)` — or it shows Chef's Keys in the wrong mode.
>
> **Why** —
> - The dropdown is filtered **client-side by mode** — only Chef's Keys whose `mode` matches the recipe's currently-selected `auth.mode` are listed. A Chef's Key in `AppRegistrationCertificate` mode is intentionally invisible while the recipe is set to `AppRegistrationSecret` and vice versa.
> - If the dropdown is entirely empty, you have not created any Chef's Key in that mode yet.
> - If the dropdown reports a field-level error like `Could not load auth profiles: HTTP 423`, the broker is locked — the **lock-overlay** owns that error; the inline editor message is suppressed for 423 / 401.
>
> **Evidence to inspect** —
> - The Chef's Keys page — count of Chef's Keys per mode.
> - The browser console for any inline error rendered into `#fld-authProfileId-error`.
> - The Status pill — `locked` will hide the editor-level message in favor of the overlay.
>
> **Corrective action** —
> - If the dropdown is empty: open **Chef's Keys** and create a Chef's Key in the matching mode. Return to the recipe editor; the dropdown refreshes automatically the next time you change the `Auth mode` field.
> - If the broker is locked: unlock from the overlay first.

---

## 13g. The broker re-locked even though I was active a moment ago — after a laptop sleep, VM pause, NTP step, or wall-clock change

> **Symptom** — You step away from the laptop briefly, or the host suspends, or the wall clock changes (NTP correction, DST adjustment, manual change). When you return, the Status pill shows `locked` and the lock overlay is back even though the previous 15-minute idle window had not yet elapsed in your perception. `GET /api/v1/broker/lock-state` carries a non-null `timeAnomaly` object.
>
> **Why** — The broker maintains two clocks for two non-interchangeable purposes: a wall clock for recorded evidence, and a monotonic clock for elapsed-runtime operations. The lazy inactivity sweep (which runs next time the SPA polls `/broker/lock-state` or fires a mutating request) compared the two clocks against the activity anchors and observed a discontinuity it cannot reconcile. The sweep responded by transitioning the broker to `Locked`. This is intentional: when runtime continuity becomes ambiguous, auth freshness becomes ambiguous, and the appliance prefers truthful re-lock over optimistic continuity forgiveness. See [OPERATOR_GUIDE.md §13.5](OPERATOR_GUIDE.md#135-time-anomaly-aware-re-locking) for the full doctrine.
>
> **Evidence to inspect** —
> - `GET /api/v1/broker/lock-state` — `timeAnomaly.kind` carries one of three frozen values: `sleep_or_pause_gap`, `wall_clock_rollback`, or `wall_clock_forward_jump`. `timeAnomaly.skewSec` reports the magnitude of the observed discrepancy.
> - `GET /api/v1/health` — `uptimeIsAnomalous` and `timeAnomaly` report the same family of events at broker-startup scope.
> - The browser console's `cookbook:brokerLocked` event (logged by `lock-overlay.js`).
>
> **What the anomaly does and does not mean** —
> - It means: between the moment of your last activity and the moment the sweep ran, the wall and monotonic clocks disagreed past the threshold. The appliance cannot confirm that it was running continuously, or that the wall clock remained continuous, across that interval.
> - It does **not** mean: a bake is corrupted, a row is invalid, a log entry is wrong, or any recorded evidence is unsound. `timeAnomaly` describes runtime-continuity ambiguity only; persisted timestamps remain truthful as recorded.
> - It does **not** mean: the host was definitely asleep. The classification names the observed clock-delta pattern, not the physical cause. `sleep_or_pause_gap` is the appliance's read of the evidence — consistent with sleep, consistent with VM pause, consistent with any other condition that stalls monotonic time while the wall clock keeps moving.
>
> **Corrective action** — Re-authenticate from the lock overlay. A successful Windows Hello / PIN verification clears the lock and resets both clock anchors together; the `timeAnomaly` field returns to `null` on the next poll. Resume your work normally.
>
> **What this guide deliberately does not advise** — Cookbook does not provide an option to suppress the anomaly surface, and this guide will not recommend that you change Windows sleep settings, disable hibernation, alter your NTP configuration, modify scheduled-task wake behavior, or otherwise reshape the host to make the appliance look unbothered. The appliance is reporting truthfully; the response is to re-authenticate.

---

## 13h. A bake's age looks wrong — negative, or many days old

> **Symptom** — A bake row exposes `ageSeconds` with an unexpected value. Either the value is negative (a future-dated `started_at` relative to the current wall clock), or it is much larger than the elapsed time you remember. The row also carries `ageIsAnomalous: true` and an `ageAnomalyReason` of `negative_age` or `absurdly_old`.
>
> **Why** —
> - `negative_age` — the current wall clock is earlier than `cook.started_at`. The most common cause is a wall-clock rollback (NTP step-back, DST adjustment, or a manual clock change) at some point since the bake was recorded. The bake's `started_at` is the truthful evidence of *when the broker stamped the row*; the negative age is the truthful evidence of *what the wall clock has done since*. Neither value is smoothed.
> - `absurdly_old` — the row has been alive for more than seven days. This threshold exists so the chef notices a long-lived or stuck row, not because the row is presumed to be wrong. Long-running cooks are a legitimate operational state; the flag asks for inspection, not deletion.
>
> **Evidence to inspect** —
> - The bake's `started_at` (UTC ISO-8601). This value is recorded evidence and is never rewritten by the broker.
> - The current wall-clock time on the host (e.g. `Get-Date -Format o`). If the host's wall clock is itself wrong, the comparison is misleading; the row is still truthful.
> - `GET /api/v1/health` `timeAnomaly` and `GET /api/v1/broker/lock-state` `timeAnomaly` — if the appliance has also observed a recent runtime-continuity anomaly, the broker has already reported it.
> - The bake view (live tail and folder contents) — confirm whether the bake is still actually running.
>
> **What the anomaly does and does not mean** —
> - It means: the operational interpretation of how old this bake is has become anomalous, in the specific sense documented in [OPERATOR_GUIDE.md §11.8](OPERATOR_GUIDE.md#118-observability--what-the-broker-actually-exposes).
> - It does **not** mean: the bake row is corrupt, the bake's evidence is invalid, the bake's outputs are broken, or the appliance is in a bad state. Recorded evidence is preserved verbatim even when the operational interpretation becomes anomalous.
>
> **Corrective action** —
> - For `negative_age`: investigate why the host's wall clock has moved backward. Cookbook does not adjust, correct, or hide the rollback; the chef confirms host time and lets subsequent cooks accumulate ages against the corrected clock.
> - For `absurdly_old`: open the bake view. If the bake is genuinely still running, that is the operationally truthful state and the flag is informational. If the bake is not actually still running but the row still says `running`, see §12 (bake interrupted after reboot or crash) for the reconciliation path.

---

## 13i. A bake is marked `broker_shutdown_with_active_cook` after a broker restart

> **Symptom** — A bake row exposes `closure_reason = 'broker_shutdown_with_active_cook'`, `abnormal_close_recorded_utc` is populated, and `broker_session_id_at_shutdown` references a `broker_sessions.session_id`. The bake itself may be in `status='running'` (not yet reconciled), in `status='interrupted'` with a populated `orphan_probe_verdict` (reconciled by the next broker), or in a normal terminal state (`succeeded`, `failed`, `cancelled` — the supervisor's record won the COALESCE race).
>
> **Why** — At the moment the broker began its shutdown path, this bake row was still `running` in the broker's view. The broker's shutdown sweep (`Invoke-ActiveCookShutdownSweep`) annotated the row with three columns via `COALESCE`, then stopped. The annotation is **forensic** evidence of what the broker observed, not a lifecycle terminal state for the bake. The actual bake child process is owned by its PAX supervisor, not by the broker; the broker dying does not kill the bake, and the next broker starting does not adopt it.
>
> **What the annotation means** —
> - `closure_reason = 'broker_shutdown_with_active_cook'` — the broker recorded that, at shutdown, this bake had not yet committed a terminal state through the supervisor's path. The supervisor may still produce one if its own process survived (e.g. PAX wrote `finished.json` and the next broker reads it on startup); when that happens, the supervisor's record is preserved because COALESCE keeps the earliest writer. Some rows will therefore carry `closure_reason = 'broker_shutdown_with_active_cook'` even though the bake later finished cleanly — the annotation is **evidence-additive**, not exclusive.
> - `abnormal_close_recorded_utc` — the UTC timestamp at which the shutdown sweep ran. It is the wall-clock moment the broker observed the row was still `running`, not the bake's actual end time.
> - `broker_session_id_at_shutdown` — the `broker_sessions.session_id` of the broker that recorded the annotation. This makes the cooks-to-broker-sessions join explicit instead of implicit-temporal, so the operator can correlate the annotation to a specific broker process lifetime.
>
> **What the annotation does not mean** —
> - It does **not** mean the bake was killed by the broker. The broker does not terminate bake processes during shutdown; it only annotates the row. Termination, if it happens, is the OS reclaiming the orphan or the next broker's reconciliation deciding the orphan is gone.
> - It does **not** mean the bake is resumed by the next broker. The next broker is a new process; it has no in-memory continuity with the prior broker's `$Script:CookRegistry`, no claim on the bake's child PID, and no authority to restart anything. The next broker reads the row, sees the annotation, may probe the orphan PID, and records whatever evidence it observes — but the bake itself is not resumed.
> - It does **not** mean the bake's evidence is invalid. Bake artifacts, stdout, exit code (if recorded), and any partial outputs are all preserved on disk and remain readable. The annotation is additive metadata, not corruption.
> - It does **not** mean the broker shutdown was abnormal. A broker can `stop_class = 'clean'` and still leave one or more bakes with `broker_shutdown_with_active_cook` — the two records describe two different processes (the broker and the bake). A clean broker shutdown with active bakes is a normal, expected state when the operator stops the appliance while bakes are running.
>
> **The companion `broker_sessions.stop_class` to inspect** —
> - `clean` — the prior broker committed an orderly stop record. The active-bake annotation was written before SQLite closed. This is the most common case.
> - `no_orderly_stop_record` — the prior broker did not commit a stop record. The next broker observed `stopped_at IS NULL` on the prior `broker_sessions` row and stamped `stop_class = 'no_orderly_stop_record'` retroactively. This is a **forensic observation**, not a causal conclusion — the absent record could reflect a force-kill, a native crash, an OS-level termination, a disk-full failure during the final UPDATE, or an exception mid-shutdown. Cookbook does not synthesize a cause it cannot prove.
>
> **Evidence to inspect** —
> - The bake row's `closure_reason`, `abnormal_close_recorded_utc`, `broker_session_id_at_shutdown`, `status`, `orphan_pid`, `orphan_probe_verdict`, and `recovery_run_id`. These columns together describe what was observed across the broker-restart boundary.
> - The referenced `broker_sessions` row, located via the stamped `session_id`. Inspect `started_at`, `stopped_at`, `stop_reason`, `stop_class`, `classified_at`, and `classified_by_session`. The stamped session_id may refer to a row that has since been purged by an operator-driven cleanup, in which case the annotation is preserved but the linkage no longer resolves — this is the appliance preserving ambiguity rather than smoothing it.
> - The bake view itself. If the bake is genuinely still on disk and the supervisor produced a `finished.json` after the broker came back, the supervisor's terminal state will be reflected in the bake view alongside the broker-shutdown annotation. Both facts coexist.
>
> **Corrective action** —
> - None is required. The annotation is informational metadata. The operator does not delete it, does not "clear" it, and does not retroactively re-classify it. Subsequent cooks accumulate their own records; this row's evidence is preserved as-is.
> - If the bake is still in `status='running'` and the next broker has not yet reconciled it, the next broker's reconciliation pass will probe the orphan PID on startup and record the result. The chef does not manually flip the row.
> - If `broker_session_id_at_shutdown` no longer resolves to a `broker_sessions` row, that is the truthful result of an earlier operator-driven purge. Cookbook does not refuse to display the dangling reference, and does not invent a fallback session_id.
>
> **What this section deliberately does not advise** — Cookbook does not provide an option to clear, hide, or rewrite `broker_shutdown_with_active_cook` annotations. The annotation is evidence; rewriting it would falsify history. See [OPERATOR_GUIDE.md §11.10](OPERATOR_GUIDE.md#1110-broker-shutdown-classification-and-active-cook-annotation) for the doctrine in full.

---

## 13j. The startup banner reports `startup_after_interrupted_runtime` — does that mean my bake is being resumed?

> **Symptom** — At broker boot the console prints a line of the form:
>
> ```
> Broker startup classification: startup_after_interrupted_runtime | observed prior broker session 1a2b3c... (no orderly stop record committed) | cooks observed in 'running' status at startup: 2 | cooks reconciled to terminal status this startup: 2
> ```
>
> The runtime payload at `GET /api/v1/runtime/version` exposes the same evidence under `brokerSession.startupClassification = 'startup_after_interrupted_runtime'`, and the `broker_sessions` row for THIS broker carries the same label in `startup_classification`.
>
> **What this means** — At THIS broker's boot, the broker observed at least one bake row in `status='running'`. The label is **purely observational**: it captures what the new broker found in the database, not anything the new broker is doing about it.
>
> **What this does not mean** —
> - It does **not** mean the new broker is resuming or restarting the bake. Cookbook does not resume bakes across broker restart. The new broker has no in-memory continuity with the prior broker's `$Script:CookRegistry`, no claim on the bake's child PID, and no authority to restart anything.
> - It does **not** mean the new broker recovered the prior broker's authority. The new broker minted a fresh session token, started Locked, and cleared the WebSocket registry. Any browser tab from before the restart will fail truthfully on the first protected-route request and prompt for re-auth.
> - It does **not** assert the prior broker crashed. An orderly broker shutdown CAN leave a bake in `status='running'` if the supervisor's terminal-write was racing the broker's teardown. Use `broker_sessions.stop_class` and the §13i evidence to determine the prior broker's shutdown classification separately.
>
> **What the new broker actually did** — `Invoke-SentinelReconciliation` walked each bake in `status='running'` and recorded the truthful closure_reason for each, sourced from on-disk sentinels:
> - `finished.json` present → the supervisor finished writing a terminal record before exiting; the bake is reconciled to that terminal state.
> - `interrupted.json` present → the supervisor recorded an interruption sentinel; the bake is reconciled to `status='interrupted'` with `closure_reason='broker_restart_interrupted_sentinel'`.
> - Neither sentinel present → the orphan PID is probed for liveness; the bake is reconciled to `status='interrupted'` with `closure_reason='broker_restart_orphan_alive' | 'broker_restart_orphan_dead' | 'broker_restart_orphan_unknown'` based on the verdict.
>
> The reconciled count is recorded on the new broker's `broker_sessions.startup_reconciled_cook_count` column for forensic auditability.
>
> **Evidence to inspect** —
> - `broker_sessions.startup_classification` for this broker (the label you saw at boot) and `broker_sessions.startup_reconciled_cook_count` (how many cooks were reconciled in this startup).
> - The reconciled cooks themselves: their `closure_reason`, `orphan_probe_verdict`, `orphan_pid`, and `status` columns describe what was observed about each.
> - The prior broker's `broker_sessions` row, located via `broker_sessions.startup_observed_prior_session_id`. Inspect `stop_class`, `stopped_at`, `classified_at`, `classified_by_session` to understand how the prior broker exited (or didn't).
>
> **Corrective action** — None required. The classification is informational. The new broker has completed its boot and is serving normally regardless of the label.

## 13k. The startup banner reports `restart_after_no_orderly_stop_record` — was the prior broker killed?

> **Symptom** — At broker boot the console prints `Broker startup classification: restart_after_no_orderly_stop_record | observed prior broker session <sid> (no orderly stop record committed)`. The runtime payload at `GET /api/v1/runtime/version` carries the same `startupClassification` on `brokerSession`.
>
> **What this means** — The most-recent prior `broker_sessions` row had `stopped_at IS NULL` at the moment THIS broker booted, and this broker's classifier either stamped or just observed `stop_class='no_orderly_stop_record'` on it. The label is a **forensic observation** — it states what was visible in the prior row, not why.
>
> **What this does not mean** — It does **not** name a cause. Multiple distinct causes can each leave an empty stop record on a prior broker's row:
> - `TerminateProcess` fired by an external tool (Task Manager kill, kill-by-pid script).
> - A native crash inside the broker bypassed the PowerShell `finally` block.
> - The OS killed the process without grace (out-of-memory, shutdown without notice).
> - The disk filled before the final `UPDATE broker_sessions SET stopped_at = ...` could commit.
> - The broker was still mid-`Invoke-Shutdown` when the SQLite connection closed (e.g. shutdown sweep took longer than the OS gave it).
> - An unhandled exception threw AFTER the orderly latch flipped but BEFORE the final UPDATE returned.
>
> Cookbook makes **no inference** between these. Synthesizing a cause from "no record was written" is the exact drift the appliance refuses.
>
> **What this does not mean (continuity)** — Same as §13j: the new broker did NOT inherit any authority from the prior broker. Bearer tokens, the broker lock, and WebSocket attachments were all reset to default-at-boot.
>
> **Evidence to inspect** —
> - The prior `broker_sessions` row (located via `broker_sessions.startup_observed_prior_session_id` on the new broker's row): `started_at`, `pid`, `stop_reason`, `stop_class`, `classified_at`, `classified_by_session`.
> - The Windows Event Log around the prior broker's `started_at` and the gap until the new broker's `started_at`. Application + System logs may show the actual termination event.
> - The cooks rows that may have been active at prior-broker shutdown (joined by `broker_session_id_at_shutdown = <prior session_id>` per §13i).
>
> **Corrective action** — None directly. If the label appears repeatedly across multiple broker boots, investigate the host-level cause (AV, OS shutdowns, OOM, manual kills) using the evidence above. Cookbook does not change behavior based on the label; the truthful operator response is investigation, not configuration.

## 13l. The startup banner reports `startup_after_unknown_runtime_state`

> **Symptom** — At broker boot the console prints `Broker startup classification: startup_after_unknown_runtime_state`. The runtime payload carries the same label on `brokerSession.startupClassification`.
>
> **What this means** — The classifier observed prior `broker_sessions` rows but the most-recent prior's `stop_class` is neither `'clean'` nor `'no_orderly_stop_record'`. The label preserves **truthful ambiguity** rather than guessing one of the more-specific labels.
>
> **Common causes** —
> - A schema migration recorded a `stop_class` value the current code does not recognize (forward/backward compat surface).
> - The PRAGMA / SELECT against `broker_sessions` failed mid-classification (transient SQLite error). In this case the classifier returned the fallback label without raising, and the error itself was recorded via `Add-RecentError`.
> - The database was edited by an external tool that left an inconsistent `stop_class` value.
>
> **Evidence to inspect** —
> - `broker_sessions` rows for any `stop_class` values outside the frozen vocabulary (`'clean'`, `'no_orderly_stop_record'`).
> - `/api/v1/health` recent errors for any `Get-StartupClassification` or `Apply-M1Schema` errors recorded in the last boot.
> - The classifier function itself in `app/broker/Start-Broker.ps1` (`Get-StartupClassification`) — the decision-tree comments name every input the label is derived from.
>
> **Corrective action** — None automatic. The appliance does not silently overwrite `stop_class` values it does not recognize, and does not collapse the label into a neighbour by guessing. If the operator can explain the unrecognized value (e.g. a planned schema upgrade), the row stands. If the operator cannot, this is a signal to investigate, not to suppress.

---

## 13m. The broker-status pill says `unauthorized` — what does that mean and how do I get back in?

> **Symptom** — The topbar broker-status pill has flipped to `unauthorized`, the token-status pill has flipped to `rejected`, and every mutating action returns HTTP 401. The pill's title hint may read `Broker rejected the session token (HTTP 401). Reason: session_token_not_recognized. SPA bootstrapped against broker session <sid>. Relaunch PAX Cookbook from the launcher to obtain a fresh token.`
>
> **What this means** — The broker received a bearer token it does not recognize. The 401 response body carries a bounded reason field drawn from `$Script:BrokerUnauthorizedReasons`; the only value currently in that vocabulary is `session_token_not_recognized`. The wording is **intentionally truthful and ambiguous** — the same 401 fires for every distinguishable cause, and the broker refuses to synthesize a more specific narrative it cannot prove.
>
> **Common causes** —
> - The broker was restarted while the SPA was open (the new broker minted a fresh session token; the old one is no longer recognized). This is the most common cause and is observable on `/api/v1/health` as a new `brokerSession.sessionId`.
> - The browser tab was opened in a previous Cookbook session and has been sitting idle while a different launcher session came and went.
> - The token in `sessionStorage` was tampered with by another script or extension.
> - The launcher URL was pasted into a tab that was already past its bootstrap (the SPA captured a stale or absent token at boot).
>
> **What the broker is NOT saying** —
> - It is **not** saying the broker restarted (it cannot prove that from the 401 alone).
> - It is **not** saying your session expired (Cookbook does not run TTLs on session tokens).
> - It is **not** suggesting you reconnect, retry, or re-authenticate in the browser. There is no in-browser path to re-acquire authority.
> - It does **not** auto-reconnect, auto-retry, or auto-restore. By design.
>
> **Evidence to inspect** —
> - The pill title hint: it surfaces the broker-supplied reason verbatim and (when the SPA's boot health probe succeeded) the broker session ID the SPA bootstrapped against.
> - `GET /api/v1/health` (unauthenticated by design): the `brokerSession.sessionId` and `brokerSession.startupClassification` fields name the **current** broker. Compare to what the SPA was bootstrapped against to confirm a restart-boundary.
> - `broker_sessions` table: the most-recent row's `startup_classification` describes the boot context.
>
> **Corrective action** — Relaunch PAX Cookbook from the launcher. The launcher mints a one-shot URL containing the fresh session token, opens it in a new tab, and the new tab inherits authority from the new launcher session. There is no "Reconnect" button, no "Retry" button, no token-refresh flow. This is intentional: every path back to authority passes through a real launcher session, so the operator always knows when authority changed hands.
>
> **Related doctrine** — See OPERATOR_GUIDE §11.11 (broker startup classification + authority boundary), §11.12 (stale-authority truthfulness, payload taxonomy, restart-boundary visibility), and §13 (re-authentication doctrine).

---

## 13n. Survivability-language reference — what the appliance does and does not say about apply, rollback, scheduler, and recovery

> **Symptom** — You are reading a Cookbook log line, a payload field, an SPA toast, or an operator-guide passage about an update apply, a rollback, a staged-package discard, a scheduler operation, a diagnostic action, or a recovery flow, and the language sounds clinical or even understated where you might have expected words like "completed", "succeeded", or "restored". You want to confirm this is intentional and find out what the underlying observation actually was.
>
> **What this means** — The appliance follows a four-step survivability lifecycle (`*_requested` → `*_started` → `*_observed_<partial|failed|complete>` → `*_verification_<passed|failed>`) and a five-class evidence taxonomy (`runtime-only`, `observational`, `authoritative`, `configuration`, `historical`). Each payload field, log string, and operator-facing message in the survivability plane is tagged with exactly one evidence class. The appliance deliberately refuses to make terminal-success claims at the lifecycle layer — `*_observed_complete` means the procedure ran to its end; the matching `*_verification_passed` is the only field entitled to claim the procedure achieved its goal. The discipline is doctrinal, not stylistic.
>
> **Forbidden language** — The survivability vocabulary declares 17 forbidden phrases in `$Script:SurvivabilityForbiddenPhrases` at [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1). The single source of truth is that file; the operator-facing enumeration lives at OPERATOR_GUIDE §17.5. If you see any of these in a Cookbook surface, that is a defect — please file an issue.
>
<!-- forbidden-phrase enumeration BEGIN -- excluded from smoke scans -->
> The enumerated phrases (for searchability when triaging without internet access): `auto-repair`, `self-heal`, `self-healed`, `repair completed`, `successfully recovered`, `fully restored`, `automatically recovered`, `recovered from corruption`, `silent recovery`, `transparent recovery`, `seamlessly resumed`, `update applied automatically`, `rolled back successfully`, `scheduler recovered`, `task auto-renewed`, `credential refreshed automatically`, `package auto-trusted`.
<!-- forbidden-phrase enumeration END -->
>
> **Where to find the full reference** — See OPERATOR_GUIDE §17 (Operational survivability doctrine):
> - §17.0 — inheritance from §11 Recovery flows.
> - §17.1 — what the doctrine covers.
> - §17.2 — what the doctrine deliberately does NOT cover (the vocabulary itself adds no runtime behavior).
> - §17.3 — the five evidence classes, verbatim.
> - §17.4 — the four-step lifecycle, verbatim.
> - §17.5 — the 17 forbidden phrases, verbatim.
> - §17.6 — deferred runtime consumers.
>
> **Vocabulary contract** — The five `$Script:*` arrays declared in [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1) are the single source of truth for survivability-plane wording. The vocabulary file itself does not change any payload field, SQLite column, or log string; downstream consumers (apply route, dryRun preview, refusal observations, append-only enforcement, and so on) read by index into these arrays so the wording flows from one location.
>
> **Corrective action** — Read OPERATOR_GUIDE §17 for the doctrinal framing. If you spot a Cookbook surface that uses one of the 17 forbidden phrases (or a `*_succeeded` / `*_completed` / `*_good` / `*_done` / `*_ok` lifecycle suffix), the survivability-language smoke at `_temp/phase_ai_verification/smoke_ai_c1.ps1` should already be failing — re-run it locally to confirm the regression, then file the defect with the smoke output attached.
>
> **Related doctrine** — OPERATOR_GUIDE §11 (Recovery flows — the runtime-plane baseline §17 extends), §17 (this section's full reference), and the reassurance-drift smoke at `_temp/phase_ah_verification/smoke_ah_c3.ps1` (runtime-plane reassurance-drift gate, which §17 extends without modifying).

---

## 13o. The apply response returns HTTP 202 with applyStatus=restart_initiated — what does that mean?

> **Symptom** — A POST to `/api/v1/updates/apply` returns HTTP 202 with a JSON body that includes `applyStatus = restart_initiated`, `handoffPath`, `stagedExtractedPath`, `selectedPackagePath`, `selectedPackageSha256`, `version`, `expectedBehavior`, `lifecycle_phase = update_apply_started`, `lifecycle_phase_source = UpdateLifecyclePhases[1]`, `evidence_classification = observational`, and `observation_id`. The broker process then exits within a few seconds; the launcher detects the apply-exit and spawns a short-lived detached orchestrator that runs the staged installer once and relaunches Cookbook once on success.
>
> **What this means** — The chef's POST reached the route, every precondition passed (re-auth verdict was Verified, no active cooks, at-apply SHA-256 re-verification matched the manifest-pinned digest, a verified staged package was selected), and the broker performed the work it owns inside its own process: it extracted the staged `.zip` into `<Workspace>\Updates\<version>\extracted\`, confirmed the extracted tree contains `app\VERSION.json`, and wrote `<Workspace>\Updates\handoff.json` naming the extracted `app\` directory as `stagedExtractedPath`. The broker then wrote one append-only observation row with `lifecycle_phase = update_apply_started` and HTTP 202, flushed the 202 to the chef's browser, set its shutdown reason to `operator_update_apply`, and exited. The file swap of `<InstallRoot>\App\`, the preservation of the previous tree under `<InstallRoot>\Backups\App-<timestamp>\`, the installer single-attempt, and the Cookbook relaunch are owned by the detached orchestrator that the launcher spawns AFTER the broker exits — not by the broker itself.
>
> **Why the lifecycle phase is `update_apply_started`, not `*_completed` or `*_verification_passed`** — The survivability vocabulary declares the seven-step survivability lifecycle (see OPERATOR_GUIDE §17.4). The 202 response is emitted at the moment the broker has BEGUN the apply — extraction succeeded, handoff JSON is on disk, broker is about to exit — but the file swap, the installer run, and the relaunch have NOT happened yet. `*_started` is the **observational** evidence class: it confirms that apply began, NOT that apply completed and NOT that the new tree is verified. Each later step (`*_observed_*`, `*_verification_*`) requires its own independent evidence; the broker that emitted the 202 will never write those later phases because it is the process that is about to exit. Later phases, if and when they are wired, will be observed and persisted by a future broker generation that boots against the new `App\` tree.
>
> **Where the values come from** — `lifecycle_phase` is read by index from `$Script:UpdateLifecyclePhases[1]` in [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1); it is never independently hard-coded in the route handler. `lifecycle_phase_source` is the literal string `UpdateLifecyclePhases[1]`, which names the survivability vocabulary array and index the value was read from — an audit field for downstream tooling. `evidence_classification` is read by index from `$Script:SurvivabilityEvidenceClasses[1]` in the same vocabulary file. The `observation_id` is the GUID primary key of the row written to `update_request_observations` for this 202 emission.
>
> **What the broker is NOT saying** —
> - It is **not** saying the apply has COMPLETED. Apply began; the installer has not run yet.
> - It is **not** saying the file swap of `App\` has happened.
> - It is **not** saying `Backups\App-<timestamp>\` has been created yet.
> - It is **not** saying the new tree has been verified against `app\VERSION.json` at runtime; the installer will perform its own verification, and a future broker generation booting against the new tree may emit additional lifecycle phases — that is the installer's contract, not the route's.
> - It is **not** saying Cookbook has relaunched. The 202 is emitted BEFORE broker exit; relaunch is a downstream orchestrator action.
> - It is **not** saying the previous browser tab will continue to work. The broker exits and the new broker generation will bind a fresh loopback ephemeral port (broker port selection is OS-assigned; see [app/broker/Start-Broker.ps1](../app/broker/Start-Broker.ps1) `Start-LoopbackListener`); the previous tab points to the OLD port and will show Cookbook offline once the old broker has exited. The launcher's normal startup flow opens a fresh browser tab against the new port; close the old tab manually.
> - It does **not** auto-retry, auto-resume, or auto-apply. The detached orchestrator makes ONE installer attempt; on installer non-zero exit the orchestrator does NOT retry and Cookbook does NOT relaunch — the chef investigates `<Workspace>\Updates\handoff.json` plus `<InstallRoot>\install.log` and decides whether to retry by hand (OPERATOR_GUIDE §8.4 recovery path).
>
> **What the broker could ALSO return instead of 202** — The apply route has additional non-202 branches:
> - **HTTP 500** with `error = update_apply_extraction_failed`, `update_apply_extracted_tree_malformed`, or `update_apply_handoff_write_failed` — the broker reached the extraction or handoff-write step and the operation failed. The broker stays alive; the chef can inspect the staged tree and retry. These three 500 modes are broker-internal apply failures and do NOT write observation rows; the wire status code, the structured `error`/`reason`/`detail`, and the `Add-RecentError` entry tagged `update_apply` are the authoritative evidence.
> - **HTTP 409** with `error = no_verified_staged_package` — every precondition passed but the at-apply trust loop did not produce any package with a verified outcome and a sidecar-pinned SHA-256. The chef must run Check and Download to stage a verified package first. This 409 branch does NOT write an observation row (the broker reached this branch AFTER the AI.C2.4-covered refusal branches but BEFORE the 202 success branch; persistence coverage for this case is not yet wired — see §13s coverage matrix).
> - **HTTP 401 / 409 / 503** — the four AI.C2.4-covered refusal branches (`reAuthRequired`, `update_refused_active_cooks`, `package_trust_apply_mismatch`, `active_cook_snapshot_failed`); see §13r for the chef-facing narrative.
>
> The previous HTTP 501 / `apply_not_yet_implemented` response — described in earlier builds and still referenced by the AI.C2.1 doctrine narrative at OPERATOR_GUIDE §17.7 — is NO LONGER EMITTED by the live apply path. §17.7 documents the AI.C2.1 wire-format wiring as it existed when apply machinery was deferred; that 501 branch has been replaced by the 202 success path plus the four new failure branches enumerated above. The §17.7 narrative remains correct as the recorded AI.C2.1 doctrine; the live broker no longer matches it.
>
> **Evidence to inspect** —
> - The 202 response body itself: `applyStatus`, `handoffPath`, `stagedExtractedPath`, `lifecycle_phase`, `lifecycle_phase_source`, `evidence_classification`, `observation_id`.
> - `<Workspace>\Updates\handoff.json` — the file the broker wrote before exiting. Contains `stagedExtractedPath`, `version`, `packageSha256`, `createdAtUtc`, and `brokerPid` (the PID of the broker that wrote the handoff; not the PID of the orchestrator or installer).
> - `<Workspace>\Updates\<version>\extracted\app\VERSION.json` — present iff extraction succeeded; the installer reads `cookbookVersion` from here to confirm what it is about to install.
> - `<InstallRoot>\install.log` — written by the staged installer when the detached orchestrator runs it; this is the operator-facing evidence of the file swap and the relaunch attempt.
> - [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1): the single source of truth for `$Script:UpdateLifecyclePhases` and `$Script:SurvivabilityEvidenceClasses`.
> - [app/broker/Routes/Updates.ps1](../app/broker/Routes/Updates.ps1): the apply route's HTTP 202 emission, the inline extraction/handoff/observation sequence, and the controlled-shutdown signal.
> - [app/broker/Start-Broker.ps1](../app/broker/Start-Broker.ps1): the `operator_update_apply` shutdown reason and the exit-code mapping the launcher reads when deciding whether to spawn the orchestrator.
> - [launcher/Start-PAXCookbook.ps1](../launcher/Start-PAXCookbook.ps1): the inline detached-orchestrator payload that waits for the launcher to exit, runs the installer once with `-Mode update -Handoff <jsonPath>`, and relaunches Cookbook once on installer success.
> - OPERATOR_GUIDE §8 for the chef-facing apply / recovery narrative; §17.7 for the recorded AI.C2.1 wire-format doctrine.
>
> **Corrective action** — None on the 202 success path; the orchestrator carries the work to completion or surfaces a failure that the chef can act on by hand. If the chef sees Cookbook fail to relaunch after a 202, the recovery path documented at OPERATOR_GUIDE §8.4 is the supported manual finish: `pwsh.exe -ExecutionPolicy Bypass -File <stagedExtractedPath>\app\install\Install-PAXCookbook.ps1 -Mode update -Handoff <Workspace>\Updates\handoff.json`. If a 500-class error came back instead, the chef investigates `Add-RecentError` on `/health` for the `update_apply` entry and the staged extraction directory.
>
> **Related doctrine** — OPERATOR_GUIDE §17 (Operational survivability doctrine, full reference), §17.4 (the seven-step lifecycle), §17.7 (AI.C2.1 first-consumer note — describes the original 501 wiring, not the current 202 emission), §8 (chef-facing apply narrative), §13n above (Survivability-language reference), §13r below (refusal branches), §13s below (coverage matrix).

---

## 13p. The dryRun apply response includes a lifecycle_phase — what does that mean?

> **Symptom** — A POST to `/api/v1/updates/apply?dryRun=true` returns HTTP 200 with a JSON body that includes `lifecycle_phase = update_apply_evaluation_requested`, `lifecycle_phase_source = UpdateEvaluationPhases[0]`, and `evidence_classification = observational` alongside the existing `dryRun = true`, `wouldRefuse`, `reason`, `detail`, `activeCookCount`, `activeCooks`, `stagedPackages`, and `snapshotError` fields. The lifecycle phase says "evaluation requested," not "apply requested."
>
> **What this means** — The chef's `?dryRun=true` POST reached the route and was observed by the broker as an EVALUATION request, not an apply request. The broker is recording that the chef asked "could this apply succeed?" — they did NOT ask "please apply." No apply behavior has been initiated; no update process was started; no staged package was inspected for execution readiness; no installer was launched; no re-authentication was prompted (dryRun deliberately bypasses re-auth so a chef can preview without a Windows Hello / PIN gate). The HTTP 200 status code and the eight pre-existing dryRun body fields are unchanged from earlier builds — the new three fields are additive transparency, not a change of behavior.
>
> **Why the lifecycle phase differs from the live (non-dryRun) path** — A dryRun is categorically not an apply request. When you preview, you express EVALUATION intent. When you POST without `dryRun=true`, you express APPLY intent. These are doctrinally distinct operator intentions, and the broker records them with distinct lifecycle phases so that audit queries, future rate-limit policy, and the SPA's UI labels can read the lifecycle phase as a single-field signal of which kind of request occurred. Conflating the two under a single phase would be a wire-format lie: a chef previewing the apply path would see the broker record an "apply request" even though they explicitly signaled "do not apply."
>
> **Where the values come from** — `lifecycle_phase` is read by index from `$Script:UpdateEvaluationPhases[0]` in [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1); it is never independently hard-coded in the route handler. `lifecycle_phase_source` is the literal string `UpdateEvaluationPhases[0]`, which names the survivability vocabulary array and index the value was read from — an audit field for downstream tooling. `evidence_classification` is read by index from `$Script:SurvivabilityEvidenceClasses[1]` in the same vocabulary file. The `$Script:UpdateEvaluationPhases` array is a SEPARATE category from `$Script:UpdateLifecyclePhases` because evaluation events are categorically not 4-step mutation lifecycles — see OPERATOR_GUIDE §17.8 for the categorical-split rationale.
>
> **What the broker is NOT saying** —
> - It is **not** saying you committed to applying. You previewed.
> - It is **not** saying the installer was launched, will be launched, or is queued.
> - It is **not** saying the staged package was modified, copied, moved, deleted, extracted, verified, or trust-evaluated.
> - It is **not** saying the install tree (`%LOCALAPPDATA%\PAXCookbook\App\`) or the `Backups\` tree was touched.
> - It is **not** saying the scheduler was notified, that rollback became available, or that the broker is preparing to restart.
> - It is **not** the same lifecycle phase that the live (non-dryRun) path returns. On success the live path returns HTTP 202 with `lifecycle_phase = update_apply_started` (sourced from `UpdateLifecyclePhases[1]`); on refusal it returns one of HTTP 401 / 409 / 503 with a `UpdateRefusalPhases[*]` lifecycle phase — see §13o above and §13r below.
> - It is **not** a queued operation. The broker is not holding any deferred work behind this lifecycle phase. There is no queue.
> - It is **not** a pending install. There is no pending state to inspect, resume, cancel, or expire.
> - It is **not** a deferred apply. The broker has no future-intent record tied to this request and will not, on its own initiative, transition this phase to any `update_apply_*` phase.
> - It is **not** resumable state. The lifecycle phase is born and complete the moment it is recorded in the response — there is nothing to come back to and nothing for a subsequent request to continue.
> - It is **not** broker-owned future intent. The broker has not committed itself to any subsequent action based on this request. If you want to apply, you must issue a separate, distinct POST without `dryRun=true`.
>
> The broker is ONLY saying: the chef requested an evaluation-oriented route. Nothing more.
>
> **Evidence to inspect** —
> - The dryRun 200 response body itself: the three new fields name the AI.C1 vocabulary that gates the wording.
> - [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1): the single source of truth for `$Script:UpdateEvaluationPhases`, `$Script:UpdateLifecyclePhases`, and `$Script:SurvivabilityEvidenceClasses`.
> - The `wouldRefuse` and `reason` fields on the same response: they carry the truthful precondition verdict from `Test-UpdateApplyPreconditions` — either `active_cooks_present` (one or more cooks are running) or `active_cook_snapshot_failed` (the broker could not enumerate active cooks). DryRun does NOT exercise re-auth (`reAuthRequired`) or at-apply package-trust re-verification (`package_trust_apply_mismatch`); those gates only fire on the live path.
> - OPERATOR_GUIDE §17.8 for the AI.C2.2 semantic-decision narrative; OPERATOR_GUIDE §17.7 for the recorded AI.C2.1 wire-format doctrine (the live path's 501 wiring as it existed when apply machinery was deferred — see §13o for the current 202 emission).
>
> **Corrective action** — None inside the appliance. DryRun is a deliberate read-only preview surface; it is not a defect. If you intended to actually apply an update, drop the `?dryRun=true` query parameter and POST without it — the live path will then return HTTP 202 with `applyStatus = restart_initiated` on success (§13o), or one of the refusal / failure branches (HTTP 401 / 409 / 500 / 503 — see §13o and §13r). If a 500-class failure happens AFTER the broker has emitted 202 and exited (i.e. the detached orchestrator's installer attempt returned non-zero), the chef can finish the install by hand using the recovery path at OPERATOR_GUIDE §8.4.
>
> **Related doctrine** — OPERATOR_GUIDE §17 (Operational survivability doctrine, full reference), §17.4 (the four-step mutation lifecycle), §17.7 (AI.C2.1 live-path consumer note — describes the original 501 wiring; the live broker now emits 202 instead), §17.8 (AI.C2.2 dryRun-path consumer note, including the categorical-split rationale), §13n above (Survivability-language reference), §13o above (the live 202 success path's lifecycle phase).

---

## 13q. The apply response includes an observation_id — what is the broker persisting, and does that mean my update is queued?

> **Symptom** — Either the live response or the dryRun 200 response from `/api/v1/updates/apply` includes a field `observation_id` whose value is a 36-character GUID string (for example, `"f3a1c4e2-9b78-4d56-b2e1-0c8f7a5d3b91"`). Sometimes the field is present with a GUID; sometimes the field is present with `null`.
>
> **What this means** — Starting with the AI.C2.3 slice, the broker writes ONE durable row to its SQLite database every time the apply route reaches a lifecycle-phase-emitting branch — the dryRun 200 preview (§13p), the live 202 success path (§13o), or one of the AI.C2.4-covered refusal branches (§13r). The row records ten facts: the GUID itself (`observation_id`), the UTC instant the request was observed, the `request_kind` (`update_apply_request` or `update_apply_evaluation_request`), the `lifecycle_phase` and its `lifecycle_phase_source` audit field, the `evidence_classification` (always `observational`), the route literal (`POST /api/v1/updates/apply`), the HTTP status the broker chose (200, 202, 401, 409, or 503), the broker's process id, and the broker's workspace path. The `observation_id` returned in the response body is the primary-key GUID of that row. The row's only purpose is to be durable forensic evidence that the request was received and processed.
>
> **What this does NOT mean — does not mean your update is queued, pending, deferred, or scheduled** — Persisting a row in `update_request_observations` does NOT mean the broker has committed to applying your update later. There is no queue, no pending-install state, no deferred apply, no resumable workflow, no broker-owned future intent, and no state machine. The row is the moral equivalent of a server access-log line, not a work-item ticket. The broker does NOT scan this table at startup. The broker does NOT read this table during runtime. The broker does NOT, on its own initiative, ever transition any apply phase based on the existence of a row. If you POSTed `?dryRun=true`, the broker observed a preview request and that is the only thing the row attests to; the chef has not committed to applying anything. If you POSTed without `dryRun=true`, the row attests to the OBSERVATION the broker made of the request — that the request was received and that the route reached one of the lifecycle-phase-emitting branches (success at 202 with `update_apply_started`, or one of the AI.C2.4-covered refusal branches). The row does NOT, by itself, advance any apply phase, schedule any retry, or imply that the broker owes the chef any subsequent action.
>
> **What survives a broker restart, and what does NOT** — The row survives, because the SQLite database is WAL-backed and durable. When the broker process exits and is started again, the row is still there as append-only evidence of the earlier observation. That is its entire restart-time meaning. The broker does NOT, at startup, scan the table for "in-flight" requests. The broker does NOT resume any apply or evaluation operation. The broker does NOT reconstruct any state from the rows. The route handler does NOT consult the table when responding to subsequent requests. The chef does NOT need to "drain" or "acknowledge" rows.
>
> **Why observation_id can be `null`** — If SQLite is busy beyond its 5-second `busy_timeout`, or the database has been moved/locked by an external tool, or the broker workspace is on a path that briefly lost write permission, the INSERT can fail. In that case the writer catches the exception, logs an entry via `Add-RecentError` (which the chef can see on `/health`), and returns `$null` as the observation_id. The HTTP response is still emitted with the same status code and the same `lifecycle_phase` / `lifecycle_phase_source` / `evidence_classification` fields — the chef receives a truthful answer that the request was received, but the broker could not record evidence of the receipt. **A `null` observation_id does NOT mean your apply failed.** The apply machinery has not run, would not have run, and was not blocked by the observation-write failure; the broker's apply behavior is unchanged when the row write fails. The only thing that is lost is the forensic record of THIS specific request having been observed.
>
> **Can I query past observations? Is there a list-observations route?** — No. There is no `GET /api/v1/updates/observations` route, no `GET /observations`, no `GET /update_request_observations`, and no other broker-side read surface for the table. The persistence is intentionally write-only from the broker's perspective. If you need to inspect rows for an out-of-band audit, attach a SQLite shell directly to the broker's database file (the broker advertises the database path in OPERATOR_GUIDE §3) and run an interactive `SELECT` from outside the broker process. The decision to make the table broker-write-only is deliberate: exposing a read surface would invite operators (and future code) to treat observation rows as a state input — exactly the contamination the bounded-persistence contract was designed to prevent.
>
> **What the broker is NOT saying** —
> - It is **not** saying apply was accepted.
> - It is **not** saying apply was queued.
> - It is **not** saying apply was deferred.
> - It is **not** saying apply was scheduled for later execution.
> - It is **not** saying the broker plans to act on the recorded row.
> - It is **not** saying the row links to any prior or subsequent observation.
> - It is **not** saying the broker will resume any operation on restart.
> - It is **not** saying the staged package, the install tree, or the `Backups\` tree was touched.
> - It is **not** saying the installer was launched.
> - It is **not** saying the chef now owes the broker any follow-up action.
> - It is **not** saying the broker now owes the chef any follow-up action.
> - It is **not** saying the row is a state-machine breadcrumb. There is no state machine.
> - It is **not** saying the row is read by the broker. Not at startup, not during runtime, not anywhere.
>
> The broker is ONLY saying: at this UTC instant, the broker observed an apply or evaluation request on this route and chose to respond with this HTTP status carrying this lifecycle phase. Nothing more.
>
> **Evidence to inspect** —
> - The HTTP response body itself: `observation_id` (GUID or null), alongside `lifecycle_phase`, `lifecycle_phase_source`, and `evidence_classification`.
> - The `/health` endpoint's recent-errors panel: if `observation_id` came back `null`, an `Add-RecentError` entry tagged `update_request_observation` will be present, naming the SQLite error message.
> - [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1): the single source of truth for `$Script:UpdateRequestKinds` (introduced in AI.C2.3), `$Script:UpdateLifecyclePhases`, `$Script:UpdateEvaluationPhases`, and `$Script:SurvivabilityEvidenceClasses`.
> - The broker's SQLite database file: a `SELECT * FROM update_request_observations` from an external SQLite shell will list all rows ever written.
> - OPERATOR_GUIDE §17.9 for the AI.C2.3 bounded-persistence narrative; §17.7 / §17.8 for the contrasting AI.C2.1 / AI.C2.2 wire-only consumer notes.
>
> **Corrective action** — None inside the appliance. `observation_id` is an additive transparency field, not a defect signal. Treat it as a forensic reference you can cite back to support when reporting an apply-route issue. If the field came back `null` and you want to know why, inspect `/health` for the `update_request_observation` recent-errors entry. Otherwise the row is invisible to your day-to-day baking workflow and requires no chef action.
>
> **Related doctrine** — OPERATOR_GUIDE §17 (Operational survivability doctrine, full reference), §17.7 (the recorded AI.C2.1 wire-format doctrine — describes the original 501 wiring; the live broker now emits 202 instead, see §13o for the current narrative), §17.8 (AI.C2.2 dryRun 200 lifecycle wiring), §17.9 (AI.C2.3 bounded-persistence narrative — the authoritative explanation of what an observation row is and what it is NOT), §13n above (Survivability-language reference), §13o above (the live 202 success lifecycle phase), §13p above (the dryRun 200 lifecycle phase).

---

## 13r. The apply route returned a refusal (401, 409, or 503) and the body now carries lifecycle_phase / observation_id — does that mean the broker is holding the request to retry later?

> **Symptom** — A `POST /api/v1/updates/apply` request comes back with one of four refusal statuses, and the response body carries the same four transparency fields that appear on the dryRun 200 preview (§13p) and the live 202 success path (§13o): `lifecycle_phase`, `lifecycle_phase_source`, `evidence_classification`, and `observation_id`. The four refusal cases the chef may see:
>
> - **HTTP 401** with `error: "reAuthRequired"` and `lifecycle_phase: "update_apply_refused_reauth_required"` — the chef has not completed the appliance's re-authentication step (Windows Hello / PIN) required for live apply requests.
> - **HTTP 503** with `error: "active_cook_snapshot_failed"` and `lifecycle_phase: "update_apply_refused_active_cook_snapshot_failed"` — the broker could not enumerate active bakes to verify the no-active-bake precondition.
> - **HTTP 409** with `error: "update_refused_active_cooks"` and `lifecycle_phase: "update_apply_refused_active_cooks_present"` — the broker successfully enumerated active cooks and found at least one in progress.
> - **HTTP 409** with `error: "package_trust_apply_mismatch"` and `lifecycle_phase: "update_apply_refused_package_trust_mismatch"` — the broker re-hashed the staged package bytes at apply time and found that the on-disk SHA-256 no longer matches the manifest-pinned digest captured at staging time. The response carries `mismatchedPackages` (an array of staged-package paths) and `packageTrustObservationIds` (the GUIDs of the per-package trust observations the verifier wrote to `package_trust_observations`).
>
> **What this means** — Starting with the AI.C2.4 slice, every pre-mutation refusal branch wired by AI.C2.4 writes ONE durable row to `update_request_observations` (the same SQLite table introduced in AI.C2.3 — §13q above) and returns the row's GUID as the response `observation_id`. AI.C3.2 G2 extended that coverage with a fourth refusal branch — `package_trust_apply_mismatch` (HTTP 409) — which uses the same writer with `lifecycle_phase = update_apply_refused_package_trust_mismatch` sourced from `$Script:UpdateRefusalPhases[3]`. The row's only purpose is to be durable forensic evidence that the request was received and refused. The `lifecycle_phase` value is drawn from a frozen AI.C1 array, `$Script:UpdateRefusalPhases`, which is categorically separate from the seven-step mutation lifecycle array (§13n / §13o) and the dryRun evaluation array (§13p). The categorical split is deliberate: a refusal is NOT a mutation progression — apply NEVER began. Two non-AI.C2.4 wire branches do NOT write observation rows and are documented at §13s: the HTTP 409 `no_verified_staged_package` carve-out (reached only after all four AI.C2.4 refusals would have passed but no trust-verified package was selectable) and the three HTTP 500 broker-internal apply failures (`update_apply_extraction_failed`, `update_apply_extracted_tree_malformed`, `update_apply_handoff_write_failed`).
>
> **What this does NOT mean — does not mean the broker is retrying, queueing, deferring, or waiting on your behalf** — Writing a refusal observation row does NOT mean the broker has committed to retrying the request when the precondition clears. There is no retry, no backoff, no automatic reissue, no queue, no pending state, no deferred apply, no scheduler, no "waiting for re-auth" state, no "waiting for cooks to finish" state, and no broker-owned record of debt. The row is the moral equivalent of a server access-log line, not a work-item ticket. The broker does NOT scan this table at startup. The broker does NOT read this table during runtime. The broker does NOT, on its own initiative, ever transition any apply phase based on the existence of a refusal row. If the chef wants to apply once the precondition clears, the chef must issue a new, distinct POST — the broker will treat it as a fresh request with no relationship to any prior refusal row.
>
> **What survives a broker restart, and what does NOT** — The row survives, because the SQLite database is WAL-backed and durable. When the broker process exits and is started again, the refusal row is still there as append-only evidence of the earlier refusal. That is its entire restart-time meaning. The broker does NOT, at startup, scan refusal rows. The broker does NOT resume any apply or evaluation operation. The broker does NOT reconstruct any state from the rows. The broker does NOT, on restart, convert a refused request into an active or accepted request. The route handler does NOT consult the table when responding to subsequent requests. A refusal row is permanent forensic evidence and nothing more.
>
> **Why observation_id can be `null` on a refusal** — Identical reasoning to §13q: if SQLite is busy beyond its 5-second `busy_timeout`, or the database is externally locked, or the broker workspace briefly lost write permission, the INSERT can fail. In that case the writer catches the exception, logs an entry via `Add-RecentError` tagged `update_request_observation`, and returns `$null` as the observation_id. The HTTP response is still emitted with the same refusal status code and the same `lifecycle_phase` / `lifecycle_phase_source` / `evidence_classification` fields. **A `null` observation_id on a refusal does NOT mean the request was accepted instead of refused.** The refusal stands. The broker's apply behavior is unchanged when the row write fails; the only thing that is lost is the forensic record of THIS specific refusal having been observed.
>
> **What about HTTP 423 (brokerLocked)?** — The 423 `brokerLocked` verdict is enforced by the workspace-lock middleware BEFORE the apply route is entered, so the apply route never observes the request and never writes an observation row. A 423 response will NOT carry `lifecycle_phase` or `observation_id`. This is intentional: AI.C2.4 declined to relocate the brokerLocked check downstream of route entry. A 423 response means the entire workspace is locked (typically because another broker process is using it); the chef should consult the workspace-lock documentation rather than expect a refusal observation.
>
> **What about HTTP 500 broker-internal apply failures?** — Three HTTP 500 branches — `update_apply_extraction_failed`, `update_apply_extracted_tree_malformed`, and `update_apply_handoff_write_failed` — are reached AFTER every AI.C2.4 refusal would have passed but BEFORE the 202 success path. These three branches do NOT write observation rows; the wire status code, the structured `error` / `reason` / `detail`, and the corresponding `Add-RecentError` entry tagged `update_apply` are the only evidence. Persistence coverage for these three apply-side failure modes is not yet wired and is flagged in §13s for a possible future slice. The broker stays alive on a 500; no shutdown is signalled.
>
> **What about HTTP 409 `no_verified_staged_package`?** — A fifth 409 branch is reached AFTER the four AI.C2.4 refusals would have passed but BEFORE the broker selects a package to apply. This branch fires when every staged package was either missing from disk, missing a sidecar-pinned SHA-256, or already classified `mismatch` by the at-apply trust loop — there is simply no `verified` package left to feed the installer. This branch does NOT write an observation row (it sits outside the AI.C2.4 refusal-coverage set); the wire status code and structured `error` / `reason` / `detail` are the only evidence.
>
> **What the broker is NOT saying when it writes a refusal observation row** —
> - It is **not** saying the broker will retry the request.
> - It is **not** saying the broker has queued the request for later execution.
> - It is **not** saying the broker has deferred the apply.
> - It is **not** saying the broker is waiting for the precondition (re-auth, snapshot success, cooks finishing) to clear.
> - It is **not** saying the broker is holding any state on the chef's behalf between refusal and a future request.
> - It is **not** saying the chef now owes the broker any follow-up action by a deadline.
> - It is **not** saying the broker now owes the chef any follow-up action.
> - It is **not** saying the broker will, on restart, convert the refused request into an active one.
> - It is **not** saying the row links to any prior or subsequent observation.
> - It is **not** saying apply machinery was reached.
> - It is **not** saying the staged package, the install tree, or the `Backups\` tree was touched.
> - It is **not** saying the installer was launched.
> - It is **not** saying the row is a state-machine breadcrumb. There is no state machine.
>
> The broker is ONLY saying: at this UTC instant, the broker observed an apply request on this route and refused it with this HTTP status carrying this refusal lifecycle phase. Nothing more.
>
> **Evidence to inspect** —
> - The HTTP response body itself: `lifecycle_phase`, `lifecycle_phase_source`, `evidence_classification`, `observation_id` (GUID or null), alongside the per-branch fields (`error`, `reason`, `detail`, `snapshotError`, `activeCookCount`, `activeCooks`, or the BrokerLock helper's re-auth challenge payload).
> - The `/health` endpoint's recent-errors panel: if `observation_id` came back `null`, an `Add-RecentError` entry tagged `update_request_observation` will be present, naming the SQLite error message.
> - [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1): the single source of truth for `$Script:UpdateRefusalPhases` (introduced in AI.C2.4), `$Script:UpdateRequestKinds` (AI.C2.3), `$Script:UpdateLifecyclePhases`, `$Script:UpdateEvaluationPhases`, and `$Script:SurvivabilityEvidenceClasses`.
> - The broker's SQLite database file: a `SELECT * FROM update_request_observations WHERE http_status IN (401, 503, 409)` from an external SQLite shell will list all refusal observation rows.
> - OPERATOR_GUIDE §17.10 for the AI.C2.4 refusal-coverage narrative; §17.9 for the AI.C2.3 bounded-persistence contract that AI.C2.4 extends; §17.7 / §17.8 for the contrasting AI.C2.1 / AI.C2.2 wire-only consumer notes.
>
> **Corrective action** — Per-branch, NONE of these actions are taken by the appliance itself; each requires deliberate chef action against the actual precondition:
>
> - **401 `reAuthRequired`** — Complete the re-authentication step (Windows Hello / PIN) the broker is challenging for, then issue a new POST to `/api/v1/updates/apply`. The refusal row is not a defect signal; it records that re-auth was missing.
> - **503 `active_cook_snapshot_failed`** — Inspect `/health` for the underlying snapshot error (the row's `snapshotError` field also carries it). The broker could not enumerate active cooks; this is typically a SQLite I/O or locking issue. Resolve the underlying issue, then issue a new POST.
> - **409 `update_refused_active_cooks`** — Wait for the active bakes (listed by id in the `activeCooks` field) to complete, then issue a new POST. Do NOT cancel bakes to clear the precondition unless the bake is genuinely abandoned; cancelling produces a different evidentiary stream that the chef will need to defend later.
> - **409 `package_trust_apply_mismatch`** — The staged package bytes on disk no longer match the manifest-pinned digest captured at staging time. Run Check + Download again to stage a fresh package from a known-good source; the old staged file should be removed (the `mismatchedPackages` array names the paths). Do NOT "force apply" — there is no force flag, and a SHA mismatch at this boundary is exactly the contamination AI.C3.2 G2 was designed to refuse.
>
> In all four cases the refusal `observation_id` (when non-`null`) is a useful forensic reference the chef can cite back to support when reporting an apply-route issue. It is otherwise invisible to the day-to-day baking workflow and requires no chef action.
>
> **Related doctrine** — OPERATOR_GUIDE §17 (Operational survivability doctrine, full reference), §17.10 (AI.C2.4 refusal-coverage narrative — the authoritative explanation of what a refusal observation row is and what it is NOT, including the 423 brokerLocked carve-out), §17.9 (AI.C2.3 bounded-persistence narrative), §17.7 (the recorded AI.C2.1 wire-format doctrine — describes the original 501 wiring, not the current 202 success emission), §17.8 (AI.C2.2 dryRun 200 lifecycle wiring), §13n above (Survivability-language reference), §13o above (the live 202 success lifecycle phase), §13p above (the dryRun 200 lifecycle phase), §13q above (the AI.C2.3 `observation_id` field on dryRun 200 / live 202 / refusal responses).

---

## 13s. I need a single picture of what the update_request_observations table records, what it does NOT record, and where I can actually inspect it

> **Symptom** — The chef has read §13n through §13r and wants ONE consolidated reference of (a) every branch of `POST /api/v1/updates/apply` that writes an observation row, (b) what each row's values mean on the wire and in SQLite, (c) where the chef can inspect rows when needed, and (d) what corrective actions the appliance does and does NOT take based on observation rows. This entry is the chef-facing companion to OPERATOR_GUIDE §17.11 (AI.C2.5 reconciliation).
>
> **Coverage matrix — what the broker observes and persists.** Each branch of the apply route either writes ONE row to `update_request_observations` (the AI.C2.3 / AI.C2.4 / AI.C3.2 G2 covered branches plus the AI.C2 202 success path) or writes NO row (the 423 middleware carve-out, the 500-class apply failures, and the 409 `no_verified_staged_package` post-precondition branch). When a row is written, both the wire response body and the persisted row carry the same `lifecycle_phase`, `lifecycle_phase_source`, `evidence_classification`, and (when the INSERT succeeds) the same `observation_id` GUID.
>
> | HTTP | Branch | `request_kind` | `lifecycle_phase_source` | Observation row written |
> | --- | --- | --- | --- | --- |
> | 200 | dryRun evaluation preview | `update_apply_evaluation_request` | `UpdateEvaluationPhases[0]` | yes |
> | 202 | live apply (restart initiated) | `update_apply_request` | `UpdateLifecyclePhases[1]` | yes |
> | 401 | reAuthRequired | `update_apply_request` | `UpdateRefusalPhases[0]` | yes |
> | 409 | update_refused_active_cooks | `update_apply_request` | `UpdateRefusalPhases[1]` | yes |
> | 409 | package_trust_apply_mismatch | `update_apply_request` | `UpdateRefusalPhases[3]` | yes |
> | 409 | no_verified_staged_package | n/a | n/a | no — apply-side gap, see note below |
> | 500 | update_apply_extraction_failed | n/a | n/a | no — apply-side gap, see note below |
> | 500 | update_apply_extracted_tree_malformed | n/a | n/a | no — apply-side gap, see note below |
> | 500 | update_apply_handoff_write_failed | n/a | n/a | no — apply-side gap, see note below |
> | 503 | active_cook_snapshot_failed | `update_apply_request` | `UpdateRefusalPhases[2]` | yes |
> | 423 | brokerLocked (middleware) | n/a | n/a | no — middleware / out of scope |
>
> **Note on the four non-observation branches.** When the live 501 `apply_not_yet_implemented` branch was replaced by the 202 success path, four new wire branches landed AFTER the four AI.C2.4 refusals but BEFORE the 202 emission: one 409 (`no_verified_staged_package`) reached when no package survives the at-apply trust loop with a verified outcome, and three 500-class broker-internal failures (`update_apply_extraction_failed`, `update_apply_extracted_tree_malformed`, `update_apply_handoff_write_failed`) reached when the inline extraction or handoff-write step fails. None of these four branches currently writes a row to `update_request_observations`. For these branches the chef's only evidence is the wire status code, the structured `error` / `reason` / `detail` in the response body, and the corresponding `Add-RecentError` entry tagged `update_apply` visible on `/health`. Wiring observation-row persistence for these four branches is a candidate for a future slice; until that lands, the chef should treat these four responses as authoritative on their own terms without expecting a corresponding observation_id.
>
> **What `observation_id` is — a support / audit reference, nothing more.** The `observation_id` field returned by the apply route is a GUID that uniquely identifies one row in `update_request_observations`. Its ONLY purpose is to give the chef a stable forensic handle they can cite back to support when reporting an apply-route issue ("the apply call I made at 14:23 returned `observation_id` X, can you tell me what the broker recorded?"). It is NOT a job ticket, NOT a tracking number for a queued operation, NOT a `cookId`-equivalent, NOT something the broker reads at runtime, and NOT something the chef is expected to revisit. A `null` `observation_id` (rare — see §13q / §13r for failure semantics) means the broker observed and responded to the request exactly as before but could not durably record the row. The HTTP response status code and body fields are the authoritative outcome; the `observation_id` is supplementary forensic metadata only.
>
> **There is no visible history panel, and that is intentional.** Chefs accustomed to other appliances may expect a "recent update requests" list on `/health`, a "recent apply attempts" tab in a UI, an `Export-Observations` cmdlet in the launcher, or a `GET /api/v1/updates/observations` endpoint. **None of these exist, and none of them are scheduled to exist in AI.C2.** OPERATOR_GUIDE §17.11 documents WHY: a list-style read surface introduces ordering semantics, retention expectations, index pressure, and interpretation hazards (chef sees a list and assumes those rows are "outstanding" or "queued") that have not yet been deliberated. Until a future slice (AI.C2.6 or later) takes on the read-surface contract explicitly, the broker exposes NO observation listing. The absence of a visible history panel is the appliance behaving correctly, not a defect or a missing feature.
>
> **Direct SQLite inspection is possible, but it is outside broker behavior.** If support asks the chef to read rows directly (e.g. to investigate a specific `observation_id`), the chef can open the broker's SQLite database file in an external SQLite shell — the table is `update_request_observations`, the row schema is the 10 columns listed in [app/broker/Start-Broker.ps1](../app/broker/Start-Broker.ps1) `CREATE TABLE update_request_observations`, and a query like `SELECT * FROM update_request_observations WHERE observation_id = '<guid>'` will return the single row. **This is an out-of-broker, support-driven path.** The broker itself does NOT issue any SELECT, UPDATE, DELETE, or ALTER against the table at any time; opening the database externally for read does not change broker behavior. The chef should close the external SQLite session before issuing further apply requests to avoid lock contention against the broker's WAL writer.
>
> **No appliance corrective action exists for observation rows.** Unlike (say) a stuck bake (§13i) or a re-auth challenge (§13m), an observation row by itself NEVER warrants chef action. There is no "abandoned observation" state, no "stale observation" state, no "observation pending acknowledgement" state, and no broker-side cleanup the chef must trigger. Rows accumulate append-only and stay where they are. If the chef sees an unexpected pattern of observations (e.g. many 503 `active_cook_snapshot_failed` rows in succession), the corrective action targets the UNDERLYING condition that caused the refusals (the SQLite snapshot probe failing), not the observation rows themselves. The rows are passive evidence — fixing the underlying condition stops new rows from being written; old rows are forensic record and stay.
>
> **What survives broker restart, and what does NOT — restated.** Observation rows survive process exit, broker restart, machine reboot, workspace move, and OS upgrade, exactly as long as the SQLite database file itself survives. Survival is the table's only restart-time meaning. The broker does NOT, on restart, read the table; does NOT reconstruct any state from rows; does NOT convert a prior refusal into an active request; does NOT resume any apply or evaluation operation; and does NOT carry forward any obligation between rows. A fresh request issued after restart is a fresh observation, independent of every row that came before.
>
> **What the table is NOT, in one place.** Observation rows are NOT a queue, NOT a retry list, NOT a pending-work backlog, NOT a deferred-apply registry, NOT a scheduler input, NOT a state machine, NOT a job tracker, NOT a replay log, NOT a continuation cursor, NOT an active-operation snapshot, NOT a `/health` panel, NOT a `cooks` table for updates, and NOT a source of truth the broker consults at any time during normal operation. Every one of those concepts has been explicitly considered and explicitly rejected by AI.C2 doctrine. Future slices may introduce ONE of those concepts deliberately under its own contract — none of them are present today.
>
> **Evidence to inspect (consolidated).** When investigating an apply-route observation:
>
> - The HTTP response body of the apply call itself (`lifecycle_phase`, `lifecycle_phase_source`, `evidence_classification`, `observation_id`, plus the per-branch fields documented in §13o / §13p / §13q / §13r).
> - The `/health` recent-errors panel for any `Add-RecentError` entry tagged `update_request_observation` (these appear ONLY when the writer's INSERT failed and `observation_id` came back `null`).
> - [app/broker/Survivability/Vocabulary.ps1](../app/broker/Survivability/Vocabulary.ps1): the single source of truth for `$Script:UpdateLifecyclePhases`, `$Script:UpdateEvaluationPhases`, `$Script:UpdateRefusalPhases`, `$Script:UpdateRequestKinds`, and `$Script:SurvivabilityEvidenceClasses`.
> - [app/broker/Routes/Updates.ps1](../app/broker/Routes/Updates.ps1): the writer (`Add-UpdateRequestObservation`) and its current call sites — the dryRun preview branch and the live 202 success branch (mutation-lifecycle observations) plus the four AI.C2.4 / AI.C3.2 G2 refusal branches (refusal observations).
> - [app/broker/Start-Broker.ps1](../app/broker/Start-Broker.ps1): the `CREATE TABLE update_request_observations` statement with the canonical 10-column schema and no index.
> - If support requests direct row inspection: the broker's SQLite database file, queried from an external shell using a query like `SELECT observation_id, observed_at_utc, request_kind, lifecycle_phase, http_status FROM update_request_observations ORDER BY observed_at_utc DESC LIMIT 50;`.
>
> **Related doctrine** — OPERATOR_GUIDE §17.11 (AI.C2.5 reconciliation — the authoritative model matrix and the explicit list of deferred decisions), §17.10 (AI.C2.4 refusal-coverage), §17.9 (AI.C2.3 bounded-persistence), §17.8 (AI.C2.2 dryRun lifecycle), §17.7 (the recorded AI.C2.1 wire-format doctrine — describes the original 501 wiring; the live broker now emits 202 instead; see §13o for the current narrative), §17.4 (seven-step lifecycle), §13n above (survivability-language reference), §13o (live 202 success lifecycle), §13p (dryRun 200 lifecycle), §13q (`observation_id` on dryRun 200 / live 202), §13r (`observation_id` on 401 / 409 / 503 refusals).

---

## 13t. Why a direct UPDATE or DELETE against update_request_observations fails by design

> **Symptom** — A chef, support engineer, or external auditor opens the broker's SQLite database with an external tool and tries to issue an UPDATE or DELETE against `update_request_observations` (for example, to "correct" a misclassified row, to "purge" old evidence, or to test the schema). The statement fails with a SQLite error along the lines of `append-only enforcement (AI.C2.6)`.
>
> **Why** — This is intentional and structural, not a defect.
>
> 1. The append-only property of `update_request_observations` is enforced at the storage layer by a pair of SQLite triggers (`trg_uro_block_update` and `trg_uro_block_delete`) installed by the broker during schema bootstrap. Any UPDATE or DELETE against the table -- regardless of who issues it or how -- raises before any row is touched.
> 2. There is no broker-side corrective action and no appliance-level "unlock" or "force-write" mode. The enforcement is part of the schema; it does not have a runtime toggle, a feature flag, an operator override, or a chef-facing escape hatch.
> 3. Read access patterns are unchanged from §13s above. The broker still does not read observation rows. `/health` still does not consume observation rows. No query, list, or read route exposes the table. AI.C2.6 only adds write-side enforcement; it does not introduce any read surface or any new operator-visible behavior.
> 4. If a row has incorrect content (for example, a future code defect causes a misclassification), the correct response is to (a) fix the code path that produced the row, (b) accept that recorded evidence stays as-it-was-recorded, and (c) treat any subsequent corrected observation as a separate row in its own right. Rewriting recorded evidence is exactly what AI.C2's append-only doctrine refuses to permit.
>
> **What to tell the chef** — "This is supposed to fail. The Cookbook records request observations as append-only evidence and never modifies them after the fact. There is nothing to fix on the appliance side." If they need to inspect rows, the read patterns from §13s still apply (external SQLite SELECT against the broker's database file is fine; the triggers only block UPDATE and DELETE).
>
> **Related doctrine** — OPERATOR_GUIDE §17.12 (AI.C2.6 structural append-only enforcement), §17.11 (AI.C2.5 reconciliation), §13s above (observation model, chef-facing).

---

## 13u. Broker refuses to start with EXIT_E_OBSERVATION_TRIGGER_INTEGRITY

> **Symptom** — The launcher prints exit code `8` and a message of one of the following two forms:
>
> - `Append-only trigger integrity check failed: missing trigger(s) on update_request_observations: <trigger names>. Live database has been mutated by an external tool, overwritten with a pre-AI.C2.6 snapshot, or replaced by hand. The broker will not start.`
> - `Append-only trigger integrity check failed: trigger body drift on <trigger name>: expected <hash>, observed <hash>. The live catalog DDL for this trigger does not match the canonical DDL the broker installed at AI.C2.6. An external SQLite tool may have dropped and recreated the trigger with a weakened body. The broker will not start.`
>
> The broker process exits before any HTTP traffic is served.
>
> **Why** — One of two conditions caused the boot-time append-only trigger integrity check to fail:
>
> 1. **Presence failure (AI.C2.7).** One or both of the `update_request_observations` append-only triggers (`trg_uro_block_update`, `trg_uro_block_delete`) were not present in the live database. The most common causes are: an external SQLite tool that issued `DROP TRIGGER` against the broker's database file; a database restored from a backup taken before AI.C2.6 closed; or a database file replaced by hand from an unrelated source.
> 2. **Body drift (AI.C2.9).** Both triggers were present, but the catalog-stored `sql` text of one of them, after canonical whitespace normalization, did not hash to the pinned canonical SHA-256 the broker installed at AI.C2.6. The most common causes are: an external SQLite tool that ran `DROP TRIGGER` + `CREATE TRIGGER ...` with weakened conditions (for example, `RAISE(IGNORE, ...)` instead of `RAISE(ABORT, ...)`, or a guard expression that always evaluates false); or a manual DDL edit that changed whitespace-significant text.
>
> The broker checks for both conditions once at boot, immediately after schema bootstrap and before entering the dispatch loop, and refuses to serve if either check fails.
>
> **Evidence to inspect** —
>
> 1. The exact trigger name(s) and, for body drift, the expected and observed hashes the broker named in its exit message.
> 2. The broker's recent-error queue (entry with source `observation_trigger_integrity`), if `/health` was reachable on the previous run before the database was mutated.
> 3. An external SQLite query against the broker's database file: `SELECT name, sql FROM sqlite_master WHERE type='trigger' AND tbl_name='update_request_observations';`. A known-good database returns both `trg_uro_block_update` and `trg_uro_block_delete` with the canonical DDL text the broker installed.
>
> **Corrective action** —
>
> 1. Re-run `Install-PAXCookbook.ps1 install` from a known-good release, **or** restore the database from a backup taken after AI.C2.6 closed. The broker will recreate the triggers idempotently with the canonical DDL on the next start. This recovery is identical for the presence-failure case and the body-drift case.
> 2. Do NOT attempt to recreate the triggers by hand from an external SQLite shell unless instructed by support. The canonical DDL lives in the broker's schema-bootstrap code path; hand-edited triggers risk drifting from the canonical text and silently weakening the append-only invariant — which is exactly what the AI.C2.9 body-drift check is designed to catch.
> 3. If the database file was replaced or mutated by a process you control, retain the affected file as evidence before reinstalling or restoring — the appliance has no diagnostic bundle for this condition beyond the recent-error message and the exit code.
>
> **Read access patterns are unchanged from §13s.** The broker still does not read observation rows. `/health` still does not consume observation rows. No query, list, or read route exposes the table. AI.C2.7 and AI.C2.9 add boot-time integrity verification only; they do not introduce any read surface or any new operator-visible behavior beyond the exit code on missing-or-drifted triggers.
>
> **Related doctrine** — OPERATOR_GUIDE §17.13 (AI.C2.7 + AI.C2.9 boot-time trigger integrity verification), §17.12 (AI.C2.6 structural append-only enforcement), §17.14 (AI.C2.8 observation-write failure counter), §11.1 (exit code `8`), §13s and §13t above.

---

## 13v. The /health payload shows a non-zero observation-write failure count

> **Symptom** — A `GET /api/v1/health` response contains `updateRequestObservationWriteFailureCount` with an integer value greater than `0`. The same response also exposes `updateRequestObservationWriteAttemptCount`; the two counters are a paired runtime-only surface.
>
> **Why** — At least one observation-write INSERT against `update_request_observations` failed in this broker session. The writer's existing `catch` block incremented the failure counter and recorded a recent-error ring entry. The attempt counter incremented at the start of every writer call (both successful and failing), so it acts as the denominator for the failure count. If more than 10 errors of any kind accumulated this session, the corresponding ring entry may have been displaced; in that case `recentErrorOverflowCount` on the same response is also non-zero.
>
> **Evidence to inspect** —
>
> 1. The two counters together. Compare `updateRequestObservationWriteFailureCount` to `updateRequestObservationWriteAttemptCount` to gauge severity. A failure-to-attempt ratio close to `1.0` suggests a persistent driver (e.g. the database file is unwritable, or every INSERT is hitting a structural constraint); a small ratio over a large attempt count suggests transient noise (e.g. lock contention during heavy concurrent activity). The broker emits the raw counts; the chef computes the ratio.
> 2. The `recentErrors` array and `recentErrorOverflowCount` on the same `/health` response. Entries originating from this writer carry `source: 'update_request_observation'`.
> 3. The broker's stderr / launcher console for the recorded error lines (full exception messages live there, not on `/health`).
> 4. If direct SQLite inspection of the broker's database file is warranted, follow the out-of-broker read pattern documented in §13s. Neither counter is stored in the database file.
>
> **What this does NOT mean** —
>
> 1. It does NOT mean any update apply was lost, refused, or accepted differently. The observation row is forensic evidence only; failure to record it never changes the apply route's HTTP response.
> 2. It does NOT mean the database is corrupt. The most common cause is a transient SQLite lock contention or an I/O condition against the workspace database file.
> 3. It does NOT mean the append-only triggers are missing or drifted — if those were missing or had body drift the broker would have refused to start (§13u, exit code `8`).
> 4. The attempt counter being larger than the failure counter does NOT mean the broker is "absorbing" failures silently. Every failure is also an attempt; the difference is the number of successful writes plus any early-return paths (e.g. a null SQLite connection).
>
> **Corrective action** —
>
> 1. Address the underlying SQLite I/O or locking condition reported in `recentErrors`. Common drivers: an external process holding a long write lock on the database file, a file-share volume issuing intermittent I/O errors, or a disk approaching capacity. The attempt counter does NOT change the corrective action — it is informational context for triage, not a corrective-action target on its own.
> 2. Neither counter has a corrective action. Both reset to `0` on the next broker restart by design. Do not attempt to mutate either counter or the database file by hand.
>
> **Read access patterns are unchanged from §13s.** AI.C2.8 added a write-failure counter and AI.C2.10 added a paired write-attempt counter; neither slice introduces any read access pattern against `update_request_observations` itself. The broker still does not read observation rows. `/health` still exposes no rows from the table — only the two integer counters described in OPERATOR_GUIDE §17.14.
>
> **Related doctrine** — OPERATOR_GUIDE §17.14 (AI.C2.8 + AI.C2.10 observation-write attempt and failure counters), §17.13 (AI.C2.7 + AI.C2.9 boot-time trigger integrity verification), §17.12 (AI.C2.6 append-only enforcement), §17.11 (AI.C2.5 deferred-read-surface doctrine), §13s and §13u above.

---

## 13w. The /health payload shows a non-zero package-trust staging verification failure count

> **Symptom** — A `GET /api/v1/health` response contains `packageTrustStagingVerificationFailureCount` with an integer value greater than `0`. The same response also exposes `packageTrustStagingVerificationAttemptCount`; the two counters are a paired runtime-only surface.
>
> **Why** — At least one call to `Invoke-PackageStagingVerification` in this broker session returned an outcome other than `match` (i.e. `mismatch` or `unknown`), or threw an unexpected exception that was caught. Each failure path also wrote one row into `package_trust_observations` with `outcome = 'mismatch'` or `outcome = 'unknown'` and (when the writer succeeded) recorded a recent-error ring entry. The attempt counter incremented at the start of every verification call (both successful and failing), so it acts as the denominator for the failure count.
>
> **Evidence to inspect** —
>
> 1. The two counters together. Compare `packageTrustStagingVerificationFailureCount` to `packageTrustStagingVerificationAttemptCount` to gauge severity. A failure-to-attempt ratio close to `1.0` over a small number of attempts most commonly indicates a single tampered or truncated package; the same ratio over a large attempt count indicates a persistent driver (e.g. the staging directory is on a flaky volume, or the manifest's `expected_sha256` is itself wrong).
> 2. The `recentErrors` array and `recentErrorOverflowCount` on the same `/health` response. Entries originating from this code path carry `source: 'package_trust_observation'`.
> 3. If direct SQLite inspection of the broker's database file is warranted, follow the out-of-broker read pattern documented in §13s. Filter `package_trust_observations` by `outcome IN ('mismatch','unknown')` to enumerate the specific package paths and observed digests the broker recorded. Neither counter is stored in the database file.
>
> **What this does NOT mean** —
>
> 1. It does NOT mean any package was applied with mismatched bytes. AI.C3.1 implements only the at-staging observation surface; the broker does not yet wire the verification call into the apply path. The observation row is forensic evidence that a mismatch was OBSERVED, not that a tainted apply occurred.
> 2. It does NOT mean the database is corrupt. The most common cause is tampered, truncated, or partially-downloaded staged bytes — or an `expected_sha256` value the caller passed in that does not match the bytes on disk.
> 3. It does NOT mean the append-only triggers (on `update_request_observations` or `package_trust_observations`) are missing or drifted — if those were missing or had body drift the broker would have refused to start (§13u, exit code `8`).
> 4. The attempt counter being larger than the failure counter does NOT mean the broker is "absorbing" failures silently. Every failure is also an attempt; the difference is the number of `match` outcomes.
>
> **Corrective action** —
>
> 1. Identify the package(s) implicated by the recent-error entries and (if needed) the SQLite inspection in step 3 above. For each affected file under the workspace `Updates\` folder, re-download from a known-good source and re-stage. Do NOT mutate `package_trust_observations` by hand; the structural append-only triggers will reject the attempt anyway.
> 2. If the same package keeps producing `mismatch`, the manifest's `expected_sha256` value may be the one that is wrong. Cross-check the manifest against the release artifact source.
> 3. Neither counter has a corrective action. Both reset to `0` on the next broker restart by design.
>
> **Read access patterns are unchanged from §13s.** AI.C3.1 added the second observation table and the paired counter pair on `/health`; the slice introduces NO read access pattern against `package_trust_observations` from inside the broker itself. The broker still does not read observation rows. `/health` still exposes no rows from either observation table — only the four integer counters described in OPERATOR_GUIDE §17.14 and §17.15.
>
> **Related doctrine** — OPERATOR_GUIDE §17.15 (AI.C3.1 package-trust observations and at-staging verification), §17.14 (AI.C2.8 + AI.C2.10 observation-write counters), §17.13 (AI.C2.7 + AI.C2.9 boot-time trigger integrity verification), §17.12 (AI.C2.6 append-only enforcement), §17.11 (AI.C2.5 deferred-read-surface doctrine), §13s, §13u, and §13v above.

---

## 13x. Broker refuses to start with EXIT_E_PACKAGE_TRUST_INTEGRITY

> **Symptom** — The launcher prints exit code `9` and a message of the form:
>
> - `Broker: at-launch package-trust verification refused (<N> mismatched packages of <M> evaluated). See package_trust_observations rows <id,id,...> for evidence.`
>
> The broker process exits before the HTTP listener binds; `/health` is not reachable on this boot.
>
> **Why** — AI.C3.2 added a boot-time package-trust re-evaluation step (`Invoke-PackageLaunchVerification`) that runs immediately after the AI.C2.7 trigger-integrity gate and before the HTTP listener binds. For every currently-staged package under the workspace `Updates\` folder, the broker re-derives signature/hash trust from first principles against the live system trust store. There is no cache, no remembered-signer flag, no carry-forward across restart, and no read of any prior `pre_run` observation row. If any package returns `hashMismatch` or `signatureInvalid`, the broker writes one append-only `package_trust_observations` row per package with `boundary='pre_run'`, records the refusal in the recent-errors ring with `source: 'package_trust_launch_integrity'`, calls `Invoke-Shutdown`, and exits with `EXIT_E_PACKAGE_TRUST_INTEGRITY = 9`. The most common causes are staged bytes that drifted on disk (operator manipulation, partial sync, antivirus repair, external tooling) or a publisher whose certificate is no longer trusted by the system trust store.
>
> **Evidence to inspect** —
>
> 1. The exit message itself names the count of refused packages and the row IDs the broker wrote before exiting. Capture this verbatim before reinstalling.
> 2. Direct SQLite inspection of the broker's database file. Follow the out-of-broker read pattern documented in §13s. The relevant rows are the most recent inserts into `package_trust_observations` filtered by `boundary='pre_run'` and `outcome IN ('mismatch','unknown')`. Each row carries the per-package `expected_sha256`, `observed_sha256`, and `package_path`. A `mismatch` row tells you exactly which file's bytes drifted and what the system-trust-store re-evaluation observed. An `unknown` row by itself does NOT cause the refusal; the refusal is fired only on `mismatch`.
> 3. The `package_trust_observations` row with `boundary='staging'` for the same package path (written by AI.C3.1 when the package was originally downloaded) gives the recorded baseline of what the bytes hashed to at staging time. Cross-walking the staging row and the new `pre_run` row pinpoints when and how the bytes drifted.
>
> **What this does NOT mean** —
>
> 1. It does NOT mean the database is corrupt. The append-only invariant on `package_trust_observations` is enforced by the same trigger pattern as `update_request_observations`; if those triggers had drifted the broker would have refused to start with exit code `8` instead (§13u).
> 2. It does NOT mean a prior apply succeeded with bad bytes. AI.C3.2 also wires the same verifier into the apply path (HTTP 409 `package_trust_apply_mismatch`); a mismatch at apply time would have refused the apply request before any state change.
> 3. It does NOT mean the broker remembered a prior signature verdict. Every boot re-derives trust from the live system trust store — there is no persisted "we trusted this once" state on disk or in process memory.
>
> **Corrective action** —
>
> 1. Re-stage and re-apply from a known-good release. Remove the affected file(s) from the workspace `Updates\` folder and re-run the download flow against the canonical manifest. The next broker start will re-evaluate the freshly-staged bytes from scratch.
> 2. If the publisher's certificate genuinely failed signature trust (not a hash drift), the system trust store may need an operator-side update before re-staging. The broker does NOT install or trust any certificate on the chef's behalf.
> 3. Do NOT attempt to mutate `package_trust_observations` by hand. The append-only triggers will reject the attempt and the broker's AI.C2.7 trigger-integrity gate will refuse to start on the next boot if the triggers are tampered with.
>
> **Read access patterns are unchanged from §13s.** AI.C3.2 added the apply-path wiring, the launch-time evaluation, and four runtime-only counters on `/health`; the slice introduces NO new HTTP read route or query against `package_trust_observations`. The broker still does not read observation rows during request handling. `/health` still exposes no rows from either observation table — only the integer counters described in OPERATOR_GUIDE §17.14, §17.15, and §17.16.
>
> **Related doctrine** — OPERATOR_GUIDE §17.16 (AI.C3.2 at-apply and at-launch trust verification + no-cache invariant), §17.15 (AI.C3.1 package-trust observations and at-staging verification), §17.13 (AI.C2.7 + AI.C2.9 boot-time trigger integrity verification), §11.1 (exit code `9`), §13s, §13u, §13v, and §13w above.

---

## 13y. Customer SHA-256 verification mismatch

> **Symptom** — The chef is following the customer-verification procedure in OPERATOR_GUIDE §17.17.2 against a package downloaded from the canonical `latestCookbook.packageUrl`. Step 4 (`$computedHash -ieq $publishedHash`) returns `False`. The hash in `$computedHash` (recomputed locally with `Get-FileHash -Algorithm SHA256 -LiteralPath`) does not match `$publishedHash` (the `latestCookbook.sha256` value read from the manifest at `VERSION.json.updateManifestUrl`). This check runs on the chef's workstation and is independent of the broker — no `package_trust_observations` row is written, no `/health` counter increments, no broker process is consulted.
>
> **Why** — The bytes that landed on disk during step 2 (`Invoke-WebRequest`) do not match what the publisher claims to have signed at this version. Possible causes, in rough order of likelihood: (a) the download was truncated or corrupted in transit (proxy injection, partial connection, antivirus rewrite, OneDrive sync interference); (b) the file was modified after the download completed but before the chef ran `Get-FileHash` (text editor that re-encoded the bytes, archive tool that "repaired" the zip, drag-and-drop into a folder that triggered a sync rewrite); (c) the chef pasted the wrong `updateManifestUrl` and is comparing against an unrelated channel's manifest; (d) the chef is comparing the manifest's `latestCookbook.sha256` to a hash computed against an older locally-cached copy of the package; (e) the publisher's manifest itself is stale or the package URL is pointing at the wrong artifact. The customer-verification procedure is the chef's defense against (a)–(d) — it catches every transport- and workstation-side issue before any byte ever reaches the broker. Case (e) is upstream and requires publisher escalation.
>
> **Evidence to inspect** —
>
> 1. The literal `$computedHash` value and the literal `$publishedHash` value, captured verbatim from the PowerShell session. Both should be 64-character hex strings. A length other than 64 (or a non-hex character) in either side points at a manifest-parsing problem, not a hash mismatch — re-fetch the manifest with `Invoke-RestMethod` and confirm the `latestCookbook.sha256` field is present and well-formed.
> 2. The HTTP response code from step 2 (`Invoke-WebRequest`). If a proxy or captive portal returned a 200 with HTML error-page bytes instead of the package archive, `Get-FileHash` will produce a hash of the error page. Open `$localPath` in a hex viewer or run `Get-Item $localPath | Select-Object -Property Length,LastWriteTime`; the file should be the expected `.zip` size, not a few KB of HTML.
> 3. The `Length` of `$localPath` compared to whatever size the publisher documents for that version. A truncated download usually has a wildly different file size, not just a different hash.
> 4. Repeat steps 1–4 from a different network egress (e.g. mobile hotspot) and with antivirus real-time scanning temporarily disabled for the target folder. If the second download produces a matching hash, the original failure was workstation- or transport-side; if it produces the same mismatched hash, the discrepancy is upstream.
>
> **What this does NOT mean** —
>
> 1. It does NOT mean the broker is misbehaving. The customer-verification procedure runs entirely on the chef's workstation; the broker is not involved. No broker-side observation row is written by this procedure.
> 2. It does NOT mean any previously-staged package is suspect. The procedure is an upstream gate against bytes that have not yet been staged — its purpose is to catch problems BEFORE the file lands in the workspace `Updates\` folder. Packages already staged through the broker were independently verified at the staging, at-apply, and at-launch boundaries (§17.16) at the time they were processed.
> 3. It does NOT mean the publisher's certificate or signature is invalid. The customer-verification procedure is a SHA-256 byte check only — it does not exercise signature verification at all. The signature/identity chain is the broker's job (`Get-PackageTrustState`, see §17.17.5).
>
> **Corrective action** —
>
> 1. Do NOT stage the package through the broker. Do NOT move the failing file into the workspace `Updates\` folder. Do NOT attempt to repair the file in place (re-zip, re-name, edit in a text editor) — any modification will invalidate both the SHA-256 check and any signature on the bytes.
> 2. Delete `$localPath` and re-run steps 2–4 of the §17.17.2 procedure. A transient network-side issue typically does not survive a second download from a different connection.
> 3. If a second download from a different network egress produces the same mismatched hash, the discrepancy is upstream. Escalate to the publisher; include the verbatim `$publishedHash`, `$computedHash`, file `Length`, and the `latestCookbook.packageUrl` and `updateManifestUrl` values you used. Preserve the failing file on disk for the publisher's investigation; do NOT delete it before escalation.
> 4. Do NOT bypass the §17.17.2 procedure. The broker's three-boundary verification (§17.16) will catch a bad SHA-256 at staging and again at apply and again at launch, but the customer-verification procedure is the chef's only opportunity to catch a transport-side or workstation-side problem BEFORE the bytes are committed to the workspace.
>
> **Related doctrine** — OPERATOR_GUIDE §17.17 (customer-verifiable SHA-256 procedure and signature subject narrowness), §17.16 (three-boundary trust verification), §17.15 (at-staging verification primitive), §13x above (broker-side at-launch trust refusal).

---

## 13z. Broker refuses to start with `EXIT_E_ENVIRONMENT_CONSTRAINED_LANGUAGE`

> **Symptom** — The broker exits at startup with code `10` (`EXIT_E_ENVIRONMENT_CONSTRAINED_LANGUAGE`). The console prints a single line of the form `Broker: PowerShell language mode is <Mode>, not FullLanguage. The broker requires FullLanguage semantics and will not start. (environment_observations row <id>)`. The recent-errors ring records an entry with `Source='environment_constrained_language'` and the same message. No HTTP listener binds; no `/health` endpoint is reachable. The probe runs once at startup, immediately after the AI.C2.7 trigger-integrity gate and the AI.C3.2 at-launch package-trust evaluation, and before the HTTP listener bind.
>
> **Why** — The broker reads its own engine's `$ExecutionContext.SessionState.LanguageMode` at startup. If the value is anything other than the literal string `FullLanguage` (the typical alternative under enterprise policy is `ConstrainedLanguage`), the broker refuses to start. The broker's contract — dot-sourced PowerShell modules, .NET interop via `[System.Security.Cryptography.SHA256]::Create()`, the SQLite `Microsoft.Data.Sqlite` types, `Add-Type`-free runtime, `Invoke-Expression`-free script blocks — assumes FullLanguage semantics. A constrained engine cannot honor that contract; running anyway would produce a long tail of nondeterministic, condition-dependent failures spread across every subsystem, each of which would look like a different bug to a debugging operator. A single fatal exit at startup collapses that tail into one fact the operator can act on.
>
> **Evidence to inspect** —
>
> 1. The console line printed by the broker at exit, verbatim. The `<Mode>` substring names the detected language mode (`ConstrainedLanguage`, `RestrictedLanguage`, or `NoLanguage`); this identifies WHICH enterprise policy is constraining the engine.
> 2. The corresponding row in `environment_observations`. Open the workspace SQLite file (per §13s) and run `SELECT id, observed_at_utc, condition, outcome, evidence_classification FROM environment_observations ORDER BY id DESC LIMIT 5;`. The most recent row has `condition='constrained_language'` and `outcome='detected'`. If no row is present, the writer's INSERT failed (typically because the SQLite connection was already torn down by an upstream failure); inspect the recent-errors ring for an `Add-EnvironmentObservation INSERT failed:` entry.
> 3. The recent-errors entry with `Source='environment_constrained_language'`. The message text is identical to the console line.
> 4. Independently confirm the detected mode in a fresh PowerShell session launched the same way the broker was launched (same shortcut, same `pwsh.exe` path, same elevation): `Write-Host $ExecutionContext.SessionState.LanguageMode`. The value must match what the broker reported.
>
> **What this does NOT mean** —
>
> 1. It does NOT mean the broker is misconfigured or that the install tree is corrupt. The language mode is enforced by the host PowerShell engine (typically via AppLocker, WDAC, or a per-machine PowerShell execution policy), not by anything the broker controls.
> 2. It does NOT mean the package, the database, or the workspace is corrupt. The probe runs before any user-facing work and writes no state beyond the single observation row.
> 3. It does NOT mean restarting will fix it. The language mode is fixed for the engine's lifetime and is determined by enterprise policy at process-launch time. Successive launches under the same policy will produce the same exit.
>
> **Corrective action** —
>
> 1. Identify the policy enforcing the constraint. The most common sources are: AppLocker rules (Get-AppLockerPolicy), WDAC code-integrity policy (Get-CimInstance Win32_DeviceGuard), and machine-wide PowerShell execution policy (Get-ExecutionPolicy -List). The constraint typically applies to processes launched outside an allow-listed location; the concrete remediation for that case is step 2 below.
> 2. Coordinate with enterprise IT to either (a) allow-list the broker's install location so the launched engine runs in `FullLanguage`, or (b) re-install the Cookbook under a path that already qualifies. Do NOT attempt to bypass the constraint locally (e.g. by setting `$env:__PSLockdownPolicy`); enterprise lockdown bypass is out of scope for the appliance and the constraint is in place for organizational reasons.
> 3. Preserve the failing `environment_observations` row as evidence for any enterprise-IT ticket. Do NOT delete it; the append-only triggers (`trg_eo_block_update`, `trg_eo_block_delete`) will reject any attempt to UPDATE or DELETE the row anyway.
> 4. After the policy change, relaunch the broker. A successful start writes no new `environment_observations` row for the `constrained_language` condition (the probe writes only on detection).
>
> **Related doctrine** — OPERATOR_GUIDE §17.18 (AI.C5.1 environment observations and ConstrainedLanguage detection), §11.1 (exit code `10`), §13s (out-of-band SQLite evidence inspection), §13u, §13v, §13w, §13x, §13y above.

---

## 13aa. Broker refuses to start with `EXIT_E_ENVIRONMENT_LOW_DISK`

> **Symptom** — The broker exits with code `11` (`EXIT_E_ENVIRONMENT_LOW_DISK`) immediately after the AI.C5.1 ConstrainedLanguage gate and before the HTTP listener begins serving. The console line reads `Broker: workspace volume <drive> has <N> byte(s) free; the broker requires at least 500 MB to start.` and references an `environment_observations` row.
>
> **Why** — The low-disk probe runs once at broker startup. It reads `[System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($WorkspacePath)).AvailableFreeSpace` and compares against two inlined thresholds: a 500 MB hard floor and a 2 GB soft warn. Below 500 MB the broker refuses to start because bakes would be refused by the per-bake precheck anyway and the long tail of bake refusals carries no useful evidence. Between 500 MB and 2 GB the broker writes a `condition='low_disk'` / `outcome='warning'` row and continues — exit code `11` is NEVER emitted in the warn band.
>
> **What this exit means and does NOT mean** —
>
> 1. It DOES mean the volume that hosts the workspace had less than 500 MB free at the moment the probe ran.
> 2. It does NOT mean the workspace database, the install tree, or any package is corrupt — no state is mutated beyond the single observation row.
> 3. It does NOT mean restarting will fix it. The disk is what it is; re-launch will re-probe and re-fail until space is freed.
>
> **Evidence to inspect** —
>
> 1. The recent-errors entry (`Source='environment_low_disk'`) printed to the console names the drive, the observed free byte count, and the `environment_observations` row id.
> 2. Out-of-band SQLite read (see §13s): `SELECT observation_id, observed_at_utc, condition, outcome, evidence_classification FROM environment_observations WHERE condition='low_disk' ORDER BY observed_at_utc DESC LIMIT 5;` lists both refusal rows (`outcome='detected'`) and any prior warn rows.
>
> **Corrective action** —
>
> 1. Free space on the workspace volume. The largest reclaimable area is usually old per-bake subtrees under `<workspace>\Cooks\<recipeId>\<cookId>\`. Use the SPA's `Recent cooks` view to export anything you want to preserve, then delete the bake subtrees by hand — the broker NEVER auto-prunes them.
> 2. Relaunch. A successful start writes no new `environment_observations` row for `low_disk` (the probe writes only on detection or warning).
> 3. If the workspace volume is structurally undersized for evidence retention, move the workspace to a larger volume via the launcher's workspace picker. The bootstrap pointer is updated atomically.
>
> **Related doctrine** — OPERATOR_GUIDE §17.18 (AI.C5.2 low-disk detection and workspace-path forbidden-form gate), §11.1 (exit code `11`), §13s (out-of-band SQLite evidence inspection).

---

## 13ab. Broker fails due to environment restriction (general triage)

> **Symptom** — The broker either exits at startup with an `EXIT_E_ENVIRONMENT_*` code (10 ConstrainedLanguage, 11 low disk) or with `EXIT_E_WORKSPACE_LOCKED` (4) on a fresh workspace; OR cooks misbehave in vendor-specific ways (slow process launch, signature-verification failures with no Cookbook bug, surprise quarantines, vanishing workspace contents at logoff) that do not match any other §13 entry.
>
> **Why** — Enterprise Windows fleets carry environmental constraints (AppLocker, WDAC, Defender / EDR, SmartScreen, TLS interception, VDI session disposability, roaming profiles, browser policy, long-path enforcement) that the appliance is forbidden from probing for. Per OPERATOR_GUIDE §17.19, only ConstrainedLanguage (G1), low disk (G2), and the forbidden workspace-path form (G3) are self-detected by the broker. Every other condition surfaces indirectly or is invisible to the broker entirely.
>
> **Evidence to inspect** —
>
> 1. The exit code (see §11.1) is the first sort key. Codes 4, 10, and 11 are the AI.C5-covered environment exits; consult §13z / §13aa / OPERATOR_GUIDE §17.18 paragraph 6 directly.
> 2. The most recent rows of `environment_observations` via out-of-band SQLite read per §13s: `SELECT observation_id, observed_at_utc, condition, outcome, evidence_classification FROM environment_observations ORDER BY observed_at_utc DESC LIMIT 20;`. Rows are append-only — they are forensic evidence, not state.
> 3. Vendor-side enterprise-IT consoles for AppLocker, WDAC, Defender / EDR, SmartScreen, and browser policy. The broker has no visibility into these.
>
> **Corrective action** — Map the symptom to OPERATOR_GUIDE §17.19's matrix and follow its remediation column. Conditions marked **NOT self-detectable by broker** are an **enterprise-IT responsibility**: route the ticket to enterprise IT with the relevant `environment_observations` row (if any) and the exit code attached as evidence. Do NOT attempt local lockdown bypasses; the appliance contract assumes the host complies with the matrix.
>
> **Related doctrine** — OPERATOR_GUIDE §17.19 (hostile environment matrix), §17.18 (AI.C5.1 / AI.C5.2 environment observations), §11.1 (exit codes 4 / 10 / 11), §13s (out-of-band SQLite evidence inspection), §13z (ConstrainedLanguage), §13aa (low disk), §14 (AV quarantine).

---

## 13ac. Diagnostics bundle usage (AI.C6.3 G4)

> **When to export** — Whenever a problem needs to be escalated to enterprise IT, Microsoft Support, or a Cookbook developer and the operator wants to attach forensic evidence rather than describe symptoms. Typical triggers: a recurring `EXIT_E_*` exit code, a refusal whose `lifecycle_phase` does not match any §13 entry, repeated package-trust failures, or any condition mapped to the hostile environment matrix (OPERATOR_GUIDE §17.19).
>
> **How to export** — From a PowerShell session attached to the broker script scope, invoke `Export-DiagnosticsBundle` (folder default) or `Export-DiagnosticsBundle -AsZip` (single archive). Full reference: OPERATOR_GUIDE §17.20.
>
> **What the bundle contains** — Exactly four file kinds: `cookbook.sqlite` (+ `-wal` / `-shm` sidecars when present), `recent_errors.json`, `VERSION.json`, and `metadata.json`. No recipes, no bake outputs, no PAX logs.tput, no chef-supplied data, no broker source code. Presence-only completeness is guaranteed (AI.C6.2 G2): a returned `BundlePath` means every required file is present.
>
> **How to attach to a support case** — If you exported a folder bundle, zip it; otherwise attach the `.zip` produced by `-AsZip`. The single archive is the complete evidence package. No additional logs, transcripts, or screenshots are required for the broker to be diagnosable — though the operator may add their own.
>
> **What the broker does NOT do** — The broker does not interpret the bundle. There is no `/diagnostics/query`, no `/observations/list`, no `/health/history`, no scheduled export, no automatic upload. Interpretation is the operator's (or recipient's) responsibility, done out-of-band with standard SQLite and JSON tools.
>
> **Related doctrine** — OPERATOR_GUIDE §17.20 (full diagnostics bundle reference), §17.18 / §17.19 (environment evidence), §13s (out-of-band SQLite evidence inspection).

---

## 13u. Scheduling — Save schedule refuses with `auth_profile_secret_missing`

> **Symptom** — On the recipe editor's **Schedule** card you click **Save schedule** for an `AppRegistrationSecret` recipe and the broker returns `auth_profile_secret_missing` with detail "AppRegistrationSecret scheduled-task PUT requires 'clientSecret' in the request body (one-shot, not stored). Cookbook rebinds the secret to Windows Credential Manager on every PUT."
>
> **Why** — Every `PUT /api/v1/recipes/<id>/scheduled-task` for an `AppRegistrationSecret` recipe re-binds the client secret to Windows Credential Manager. There is no "secret already bound, skip prompt" path; create and update both require the chef to re-enter the plaintext on the form. Delete is unaffected.
>
> **Evidence to inspect** —
> - Look at the recipe's Chef's Key in the editor's Chef's Key card. If `mode` is `AppRegistrationSecret`, the Schedule card shows a client-secret field; that field is required on every save.
> - The broker's per-bake re-auth log entry will also fire on this PUT, which is expected.
>
> **Corrective action** —
> 1. Re-enter the AAD application's client secret in the Schedule card's **Client secret** field.
> 2. Click **Save schedule** again. The plaintext leaves the SPA on the wire once; the broker rebinds CredMan and discards the plaintext.
> 3. If you do not have the secret, you cannot register or update a scheduled task for that recipe. Either rotate the secret in AAD and bind the new value, or migrate the recipe to `AppRegistrationCertificate` auth (the preferred mode for unattended scheduling).

---

## 13v. Scheduling — Save schedule refuses with `auth_mode_not_permitted` or `execution_mode_not_local_manual`

> **Symptom** — The Schedule card is disabled with one of these messages:
> - "Scheduling requires executionMode = local-manual. Current: ... ."
> - "Scheduling requires AppRegistrationSecret or AppRegistrationCertificate auth. WebLogin / DeviceCode / ManagedIdentity cannot run unattended via Task Scheduler. Current: ... ."
>
> Or the broker returns `execution_mode_not_local_manual` / `auth_mode_not_permitted` on a direct PUT.
>
> **Why** — Unattended scheduling cannot host a Windows Hello / device-code prompt at 3 a.m., and the local broker can only spawn the wrapper for recipes whose saved `executionMode` is `local-manual`. Hosted modes (`fabric-hosted`, `azure-hosted`) ship bake execution out to a different runtime that Cookbook is not present in, so the local Task Scheduler hook would have nothing to call.
>
> **Evidence to inspect** — The recipe editor's Where card (executionMode) and Chef's Key card (auth mode + `authProfileId`).
>
> **Corrective action** —
> 1. Set the recipe's executionMode to `local-manual` and save.
> 2. Set the Chef's Key to `AppRegistrationSecret` or `AppRegistrationCertificate` and save.
> 3. Reload the Schedule card. The gate evaluates against the saved recipe; in-flight unsaved edits do not change it.

---

## 13w. Scheduling — the Schedule card shows "Stale" and the next fire fails with `refused_stale_projection`

> **Symptom** — The Schedule card shows a "Stale" status line ("Recipe or Chef's Key changed since registration." or "PAX engine version changed since registration."). If the task fires before the chef re-saves, the wrapper exits with code 30 and the bake is recorded with refusal name `refused_stale_projection`.
>
> **Why** — Cookbook stores a SHA-256 of the redacted projection (PAX script version + full argv, secret switches replaced by `<REDACTED>`) at registration time. The wrapper recomputes the hash at fire time. Any drift — recipe edits, Chef's Key change, PAX engine update — flips the schedule to stale and refuses the next fire. This is a deliberate fence: the chef must reaffirm the new projection.
>
> **Evidence to inspect** —
> - The bake row's `closure_reason` / refusal log shows `refused_stale_projection` plus `storedProjectionHash`, `currentProjectionHash`, `storedPaxScriptVersion`, `currentPaxScriptVersion` for diff inspection.
> - The Schedule card's stale line names which axis drifted (recipe vs. PAX engine).
>
> **Corrective action** —
> 1. Review the recipe — confirm the new projection is what you want fired unattended.
> 2. Open the Schedule card and click **Save schedule**. For `AppRegistrationSecret` recipes you must re-enter the client secret as part of the save (see §13u). The save re-registers the task with the live hash.
> 3. If you instead want the schedule gone, click **Delete schedule**.

---

## 13x. Scheduling — Windows Task Scheduler shows the task fired but no bake appears in Cookbook

> **Symptom** — Open Task Scheduler (taskschd.msc), navigate to `\PAXCookbook\<recipeId>\`, see a recent **Last Run Result** entry, but no matching bake in the recipe's Recent runs rail.
>
> **Why** — One of:
> 1. The wrapper refused before invoking PAX. Common refusal names: `refused_stale_projection` (§13w), `wrapper_internal` (exit 31), or an auth-profile not-found / secret-not-bound condition.
> 2. The wrapper invoked PAX but PAX did not write a bake directory (`<Workspace>\Cooks\<cookId>\`) before failing — there is nothing for Cookbook to import.
> 3. The bake is in the database but the SPA is showing a stale Recent runs payload (S6A is stale-on-mount; reload the recipe page).
>
> **Evidence to inspect** —
> - The wrapper writes its own `cook.log` under the bake folder for any bake it actually spawned, including refused ones. Check `<Workspace>\Cooks\` for a folder timestamped near the Task Scheduler fire time.
> - The wrapper exit codes: `0` = ok, `1+` = PAX forwarded, `30` = `refused_stale_projection`, `31` = `wrapper_internal`. Task Scheduler's "Last Run Result" column shows the same code.
> - The Recent runs rail is fetched once on mount; navigate away from the recipe and back to re-fetch.
>
> **Corrective action** —
> 1. If Task Scheduler shows exit `30`, re-save the schedule (§13w).
> 2. If exit `31`, inspect `cook.log` for the wrapper internal error — most often a missing Chef's Key, a missing recipe row, or a missing PAX script. Resolving each is the same workflow as a manual bake.
> 3. If PAX exit codes are non-zero, the bake is recorded normally; check the per-bake detail page.

---

## 13y. Scheduling — Delete schedule succeeds but the task is still in Task Scheduler

> **Symptom** — Cookbook reports the schedule deleted (the card shows "Not scheduled"), but the task is still listed in taskschd.msc under `\PAXCookbook\<recipeId>\`.
>
> **Why** — The registrar (`app/install/Register-PAXScheduledRecipe.ps1`) attempts `Unregister-ScheduledTask` on delete. If Task Scheduler returns an error (permissions, task in use, task name mismatch from out-of-band edits), the broker still removes the database row so the Schedule card reflects the chef's intent, but the OS-side task remains. The Cookbook database is the source of truth for *what the chef wants*; Task Scheduler is the source of truth for *what is registered with Windows*.
>
> **Evidence to inspect** — Run Task Scheduler MMC (taskschd.msc) and navigate to the `\PAXCookbook\<recipeId>\` folder. The task itself is the evidence.
>
> **Corrective action** —
> 1. Delete the task by hand in Task Scheduler (right-click → Delete).
> 2. The next time the chef clicks **Save schedule** on that recipe, the registrar will create a fresh task — there is no half-state to recover from on the Cookbook side.

---

## 13aa. Schedule card health pill — `Last run failed`, `Last run refused`, `Last run interrupted`

> **Symptom** — The Schedule card opens with a coloured pill that reads **Last run failed**, **Last run refused**, or **Last run interrupted**. Beneath the recurrence and last-imported lines, a "Last terminal scheduled run" line links to a specific bake.
>
> **Why** — The broker's health surface computes the pill from the most recent terminal scheduled bake on record (`status IN ('completed','failed','refused','interrupted')` for this recipe's `scheduled_task_id`, ordered by `COALESCE(finished_at, started_at) DESC`). The priority chain is deterministic: `refused` outranks `failed` outranks `interrupted`. The pill is a pointer to the run, not a diagnosis — it tells the chef *which* bake to open. The paired short message names the wrapper outcome class (`pax_nonzero_exit`, `wrapper_spawn_failed`, `wrapper_internal_error`, `refused_stale_projection`, `wrapper_orphan_classified`) when one is recorded.
>
> **Evidence to inspect** —
> - The bake detail page for the linked terminal run. The **Scheduled run** card shows the verbatim wrapper envelopes, including `wrapperOutcome`, `wrapperReason`, refusal `reason` / `detail`, and PAX exit code. The **PAX log** discovered in the bake's fact directory is the authoritative source for the exact failure inside PAX.
> - For `Last run refused` with `error_class = 'refused_stale_projection'`, cross-reference §13w (stale schedule). The wrapper recomputed the projection hash at fire time and saw drift.
> - For `Last run interrupted` with `error_class = 'wrapper_orphan_classified'`, the reconciler saw a `wrapper-started.json` whose PID is no longer alive after the grace window and never finished. The wrapper folder under `<Workspace>\Cooks\<cookId>\` retains the partial evidence.
>
> **What the pill does NOT mean** — The pill never auto-clears. It will read `Last run failed` (or refused / interrupted) until the *next* terminal scheduled bake posts a different outcome. The pill is not a notification badge; clearing it by hand is not a supported operation. The next scheduled fire updates the pill on the next GET of the Schedule card.
>
> **Corrective action** —
> 1. Open the linked terminal bake. Read the PAX log and the wrapper envelopes.
> 2. For `refused_stale_projection` — apply §13w (review the recipe, click **Save schedule** to re-confirm).
> 3. For `pax_nonzero_exit` — the failure is inside PAX; the same diagnostic flow as a manual bake applies.
> 4. For `wrapper_spawn_failed` / `wrapper_internal_error` — inspect `wrapper-finished.json` for the wrapper-side reason; the most common causes are a missing PAX script, a missing `pwsh.exe` on the scheduled-task PATH, or a wrapper-resolved PAX path that failed an integrity probe.
> 5. For `wrapper_orphan_classified` — the prior run did not finish gracefully (forced reboot during bake, broker killed mid-run, host hibernation while wrapper was waiting on PAX). Inspect the wrapper folder; the partial evidence usually identifies the cause.

---

## 13ab. Schedule card health pill — `Running`

> **Symptom** — The Schedule card opens with a **Running** pill. The card may otherwise look healthy (recurrence shown, "Last checked" timestamp current).
>
> **Why** — A scheduled bake for this recipe is currently in `status = 'running'`. The reconciler classifies a wrapper folder as `running` when the wrapper has written `wrapper-started.json` but neither `wrapper-finished.json` nor `wrapper-refused.json` has appeared yet, *and* the wrapper-recorded PID is still alive, *and* the grace window has not expired. This is the normal in-flight state.
>
> **Evidence to inspect** —
> - The **Runs** list (open the recipe's Recent runs rail or the global Runs page). The running bake is visible there with the same `scheduled` trigger chip.
> - The wrapper folder under `<Workspace>\Cooks\<cookId>\`: `wrapper-started.json` is present, `wrapper-finished.json` is absent.
> - The Schedule card's last-imported-run link points at the running bake.
>
> **What the pill does NOT mean** — The pill does not block any other operation. The chef can still re-save the schedule, save the recipe, or trigger manual bakes for other recipes. The broker does not stage operator-state changes around the **Running** pill — it is informational.
>
> **Corrective action** — Wait for the bake to finish. The pill transitions to `Current` (on success), `Last run failed` (on PAX non-zero exit), `Last run interrupted` (if the wrapper folder is orphaned past the grace window), or `Last run refused` (if a future fire is refused for staleness — though that would not be the *running* bake). No chef action is required.

---

## 13ac. Schedule card health pill — `Unknown`

> **Symptom** — The Schedule card opens with an **Unknown** pill. The paired message reads "Schedule registered, but staleness could not be determined. Check the recipe and re-save the schedule if needed."
>
> **Why** — The broker tried to recompute the live projection hash at GET time and failed. The most common cause is that the recipe file on disk is now unreadable, the Chef's Key referenced by the recipe was deleted, or the recipe's auth mode requires inputs (clientSecret, certificate, etc.) the broker cannot project from the current state. `Unknown` is the broker's honest "I do not have enough evidence to call this `current` or `stale`." It is NEVER the same thing as "the schedule is broken in Windows" — Cookbook owns nothing about whether Task Scheduler can fire the task, and `Unknown` does not imply anything about the OS-side state.
>
> **Evidence to inspect** —
> - The recipe itself in the Recipe editor: does it load? Does the Chef's Key referenced by the recipe still exist?
> - The broker's recent-error ring (`/health` payload) — a failed projection-hash recompute is logged there with the recipe ID and the error message.
>
> **Corrective action** —
> 1. Open the recipe and resolve the underlying issue — repair the file, restore the Chef's Key, or re-fill any required form field.
> 2. Re-save the recipe (if you edited it) and then **Save schedule** to refresh the registered hash.
> 3. The next GET of the Schedule card will recompute and the pill will transition to `Current` or `Stale` (or stay `Unknown` if the underlying issue persists).

---

## 13ad. Schedule card "Last checked" timestamp is old or stays blank

> **Symptom** — The Schedule card shows a `Last checked: <iso>` line whose timestamp is many days old, or no `Last checked` line at all.
>
> **Why** — The watermark is the broker's `scheduled_tasks.last_stale_check_at` column. It records the moment the broker last compared the live projection to the stored projection during a GET of the Schedule card. The watermark is refreshed every time the chef opens the Schedule card. A watermark that is many days old simply means the Schedule card has not been opened in many days — it is NOT a sign that the schedule is broken or that the wrapper stopped recomputing the hash at fire time (the wrapper does its own hash check independently of this watermark).
>
> A *blank* "Last checked" line means there is no `scheduled_tasks` row for this recipe (the pill would also read `Not registered` in that case), or the broker has never served a GET of this scheduled task since the row was inserted.
>
> **What the watermark does NOT mean** — The watermark is NEVER a substitute for the wrapper's at-fire-time stale check. Task Scheduler fires the wrapper, the wrapper recomputes the hash from first principles, and the wrapper refuses with exit 30 if the hash drifted — none of this code path consults `last_stale_check_at`. The watermark is operator-facing only.
>
> **Corrective action** — None required. Opening the Schedule card refreshes the watermark in the same GET. If the chef wants a current watermark, the act of viewing the card is sufficient.

---

## 14. Antivirus quarantines a Cookbook file

> **Symptom** — A previously-working install suddenly fails to start with exit code 5 (SQLite DLL) or 6 (PAX script). Files that were on disk are missing or zero-length.
>
> **Why** — Some antivirus products heuristically quarantine PowerShell scripts and native DLLs they have not seen before. This is environmental, not a Cookbook bug.
>
> **Evidence to inspect** —
> - Your antivirus product's quarantine list and event log.
> - The expected vs actual hashes printed by the broker on the integrity failure.
>
> **Corrective action** —
> 1. Restore the quarantined files **only if your AV reports a false positive** — never override AV blindly.
> 2. Add the install tree (`%LOCALAPPDATA%\PAXCookbook\App\`) to your AV's exclusion list if your organization's policy permits.
> 3. Re-run `Install-PAXCookbook.ps1 install` from a known-good release.

---

## 15. Bootstrap exists but the launcher still re-prompts for a workspace

> **Symptom** — The bootstrap pointer at `%APPDATA%\PAXCookbook\cookbook.bootstrap.json` exists, but every launch re-prompts you for a workspace folder.
>
> **Why** — One of:
> 1. The bootstrap is **corrupt** and the launcher rotated it to `.broken-<UTC>`. See §6.
> 2. The bootstrap names a workspace path that **no longer exists** (you moved or deleted the folder).
> 3. The bootstrap names a workspace path that **now fails workspace validation** (you moved it onto OneDrive, a network drive, etc.). See §5.
>
> **Evidence to inspect** —
> - The contents of `%APPDATA%\PAXCookbook\cookbook.bootstrap.json` (or its `.broken-<UTC>` rotation).
> - The named rejection reason in the launcher's output.
> - Whether the path in the bootstrap actually exists on disk.
>
> **Corrective action** — Re-pick a valid workspace in the launcher. The launcher writes a fresh bootstrap. If you want to recover an old workspace, move its contents to a valid location before re-pointing the bootstrap.

---

## 16. The Updates panel says my installed version is newer than the manifest

> **Symptom** — The Updates panel reports that the staged manifest advertises an **older** version than what is installed, and refuses to offer the package.
>
> **Why** — This is a defensive guard. Cookbook does not auto-downgrade. If the manifest is genuinely advertising an older version, either the distributor's manifest has rolled back (intentional) or someone pointed your `updateManifestUrl` at the wrong channel.
>
> **Evidence to inspect** —
> - `<InstallRoot>\App\VERSION.json -> cookbook.version` — what you have.
> - The manifest the broker fetched — visible in the Updates panel and saved as part of the staged metadata.
>
> **Corrective action** —
> - If the rollback is intentional and you want to downgrade: do it manually by uninstalling (`Install-PAXCookbook.ps1 uninstall`) and installing the older release ZIP. Cookbook will not perform downgrades through the update flow.
> - If the rollback is unintentional: fix the `updateManifestUrl` to point at the correct channel.

---

## 17. Everything looks fine but I want to confirm I am running a known-good install

> **Symptom** — Not a failure. Pre-flight inspection request.
>
> **Corrective action** — Run `pwsh -File <release-folder>\app\install\Install-PAXCookbook.ps1 check`. The installer's `check` mode reports installed version, expected vs actual integrity hash for the bundled PAX script, and the presence of the install root. It does not write anything.

---

## 17a. Recipe Takeout errors and surprises

Recipe Takeout is the chef-facing flow for moving a single recipe definition between Cookbook workspaces (`Export Recipe Takeout` on a saved recipe, `Import Recipe Takeout` on the Recipes page). This section covers the broker error codes the flow can surface and the chef-visible surprises that are not errors per se but commonly trip people up. The full operator-facing description of the feature lives in [OPERATOR_GUIDE.md §6a](OPERATOR_GUIDE.md#6a-recipe-takeout).

### 17a.1 Error codes the broker can return

The broker returns these errors as `{ "error": "<code>", "message": "<plain text>" }` and the wizard surfaces the `message` field. All codes below are **active in shipped Cookbook** — if you see one of these in the wizard, that is the contract.

> **`invalid_json`** — The body of the import request was not valid JSON, or the export sanitizer produced bytes that did not round-trip through JSON. Almost always means the file was edited by hand. Re-export from the source workspace and try again.

> **`payload_too_large`** — The import file is larger than Recipe Takeout's per-request body cap. Cookbook deliberately rejects oversized payloads to keep the broker process bounded. A normal recipe envelope is small (well under 256 KB). If you see this, the file is not a Recipe Takeout envelope.

> **`takeout_shape_invalid`** — The JSON parsed, but it is not the right shape (missing required fields, wrong top-level structure). Either the file is not a Recipe Takeout envelope, or it is from a future Cookbook release with new required fields this Cookbook does not understand. Re-export from a Cookbook at the same major version.

> **`takeout_unknown_field`** — The envelope contains a top-level field this Cookbook does not recognize. This catches hand-edits that try to smuggle new fields in. If you authored the file by hand: don't. Use Export Recipe Takeout.

> **`takeout_kind_invalid`** — The envelope's `kind` discriminator is not the expected value. This is the file-identity guard: it confirms the file is a recipe envelope and not, for example, a workspace export from a different tool. Re-export.

> **`takeout_schema_version_unsupported`** — The envelope's `schemaVersion` is older or newer than this Cookbook can process. Update Cookbook (if too new) or re-export from a current Cookbook (if too old). Cookbook does not silently upcast older envelopes; that is intentional.

> **`takeout_contains_forbidden_secret_field`** — The inbound envelope contains a field on the credential deny-list (token, secret, certificate, etc.). The broker refuses to import the file. Likely cause: someone hand-edited the envelope to add a credential. Re-export from the source workspace. The source export sanitizer fails closed on the same deny-list, so a clean export will not contain these fields.

> **`recipe_name_required`** — The import request did not include a `targetRecipeName`, or the field was an empty string. The wizard fills this field from the name input; the only way to see this directly is from a non-UI client. Provide a non-empty name.

> **`recipe_name_invalid`** — The supplied `targetRecipeName` is not valid as a recipe display name. Most commonly: contains characters Cookbook does not allow in recipe names, is whitespace-only, or exceeds the length limit. Pick a different name.

> **`recipe_name_conflict`** — The supplied `targetRecipeName` is already in use by another recipe in this workspace. The response body contains a `nextSuggestion` field with the next free `Name (1)`-style alternative. The wizard pre-fills the name input with this suggestion; you can accept it with one click or type something different. This error is **not** the same thing as the validate-time conflict warning — validate is advisory; this is the durable check that runs at the moment of disk write.

> **`takeout_persist_failed`** — The broker validated the envelope but could not write the new recipe to disk (database lock, antivirus quarantine, OneDrive sync conflict, disk full). The broker rolls back the partial write and the wizard surfaces the failure. Resolve the underlying disk condition and try again. Nothing on disk has been left in a half-written state.

> **`recipe_not_found`** — The chef asked to export Recipe Takeout for a recipe that no longer exists. Usually means the recipe was deleted in a second Cookbook window after the Export button rendered. Refresh the Recipes page.

> **`takeout_sanitization_failed`** — On the export side: the sanitizer refused to build the envelope. This is a defense-in-depth catch — it means the recipe being exported contained a value in a field the sanitizer was about to strip, and the sanitizer chose to fail rather than silently strip. Typical cause: a recipe authored by an older Cookbook that allowed a field which the current allow-list rejects. Re-save the recipe in the current Cookbook (which will normalize the in-memory shape), then re-try Export.

> **`takeout_secret_leak_detected`** — On the export side: the sanitizer found credential-shaped data in a field that is not supposed to carry credentials. This is the export-side counterpart to `takeout_contains_forbidden_secret_field`. The export is aborted; no file is written. Open a ticket — this is not a normal condition on the export side.

> **`takeout_envelope_invalid`** — A generic fallback when the envelope fails a structural check that does not match any more specific code above. Treat the same as `takeout_shape_invalid`: re-export from the source workspace.

### 17a.2 Imported recipe is in Needs Prep

> **Symptom** — You imported a `.paxrecipe.json`, opened the new recipe, and the Prep Station shows a **Needs Prep** banner and a disabled Bake button.
>
> **Why** — The source recipe used an app-registration Chef's Key. Recipe Takeout never moves Chef's Keys, secrets, or certificates between workspaces, so the imported recipe arrived with the credential slot empty. The `auth.mode` and tenant ID came across as advisory metadata so you know what kind of Chef's Key to bind.
>
> **Corrective action** — In the Prep Station's auth card, pick a local Chef's Key that matches the tenant the recipe expects. If no suitable Chef's Key exists yet, add one on the Chef's Keys page first, then return. Save the recipe — Needs Prep clears immediately. See [OPERATOR_GUIDE.md §6a.5](OPERATOR_GUIDE.md#6a5-chefs-keys-and-needs-prep) and [§12](OPERATOR_GUIDE.md#12-chefs-keys).

### 17a.3 Export Recipe Takeout button is greyed out

> **Symptom** — Open a recipe in the Prep Station, click around, and the **Export Recipe Takeout** button is disabled.
>
> **Why** — Either the recipe has unsaved changes (the Save button is highlighted), or the recipe has never been saved at all. Export is for the persisted recipe; it does not capture the unsaved editor state.
>
> **Corrective action** — Save the recipe. The export button enables.

### 17a.4 Import says my recipe name is already in use

> **Symptom** — In the Import Recipe Takeout wizard, the name input shows `Name (1)` (or `Name (2)`, etc.) when you expected the original name from the file.
>
> **Why** — The current workspace already has a recipe with the original name. Cookbook walks `Name`, `Name (1)`, `Name (2)`, … up to `Name (99)` and pre-fills the next free slot so you can accept it with one click. If `Name (99)` is also taken, the field is left empty and you must type a name.
>
> **Corrective action** — Either accept the suggested name or type a different one. Recipe Takeout treats the name in the input as authoritative; nothing else (filename, source identifier, envelope contents) overrides it.

### 17a.5 The downloaded filename does not match the recipe name

> **Symptom** — You exported a recipe called `Tenant audit weekly` and the browser saved a file like `tenant-audit-weekly-a3f1.paxrecipe.json`. The filename does not match the display name.
>
> **Why** — This is intentional. The filename is a deterministic slug of the recipe's display name plus a short identifier component, so two exports from the same machine do not clobber each other in the Downloads folder. The recipe name lives **inside** the envelope; the filename is convenience for the file picker.
>
> **Corrective action** — None needed. You can rename the file to anything you like (`my-recipe.paxrecipe.json`, `report-q4.json`, no extension at all); Recipe Takeout imports by envelope contents, not filename.

### 17a.6 The imported recipe still works after I deleted the .paxrecipe.json

> **Symptom** — You imported a recipe, then deleted the `.paxrecipe.json` file on disk. The imported recipe in Cookbook still works.
>
> **Why** — This is the intended model. Import copies the envelope contents into a new local recipe in the workspace (`cookbook.sqlite` plus the recipe JSON under `<Workspace>\Recipes\`). After import, Cookbook holds the recipe; there is no live dependency on the transport file. See [OPERATOR_GUIDE.md §6a.7](OPERATOR_GUIDE.md#6a7-file-independence-after-import).

### 17a.7 I imported the same takeout twice and got two recipes

> **Symptom** — You imported `my-recipe.paxrecipe.json` once, then imported the same file again. You now have two recipes in Cookbook.
>
> **Why** — This is intended. Recipe Takeout's duplicate detection is on the **recipe display name**, not on envelope contents. If you give the second import a different name (or accept the `Name (1)` suggestion), Cookbook creates a second, independent local recipe. Each import has its own fresh local recipe identifier; nothing links the two on disk.
>
> **Corrective action** — If you only wanted one copy, delete the duplicate on the Recipes page. If you wanted both, you already have what you want.

---

## 17b. Support Mode opens Cookbook with a visible broker window

> **Symptom** — You need to see the local broker messages while launching or using Cookbook, or a support instruction tells you to open Cookbook in **Support Mode**.
>
> **Why** — Support Mode uses the same local Cookbook app and the same workspace as a normal launch. The only difference is that the broker is started with its `pwsh.exe` console window visible, so broker startup, health, route, and error messages are visible in real time. Support Mode does not switch workspaces, does not change recipe or bake behavior, does not enable extra data collection, and does not change how authentication or workspace unlock works. Windows Hello and Cookbook's session unlock still run exactly as they would in a normal launch. The in-product help panel surfaces the same description under **Troubleshooting &rarr; Support Mode**.
>
> **Evidence to inspect** —
> - The visible `pwsh.exe` broker window opened by the **PAX Cookbook Support Mode** Start Menu shortcut.
> - The browser page that opens from that same launch.
> - Any specific error lines printed in the visible broker window.
> - The diagnostics bundle output described in [&sect;13ac](#13ac-diagnostics-bundle-usage-aic63-g4) if you want a file you can attach to an issue.
>
> **Corrective action** — Launch **PAX Cookbook Support Mode** from the Start Menu when you need to see broker messages. Reproduce the issue, then copy the visible error text out of the broker window or run the diagnostics bundle / copy diagnostics flow described in [&sect;13ac](#13ac-diagnostics-bundle-usage-aic63-g4). When you are finished, close the browser tab or use **Stop PAX Cookbook Server** (see [&sect;17c](#17c-stop-pax-cookbook-server-does-not-delete-your-workspace)) to stop the broker cleanly. Support Mode is the same Cookbook that the chef normally uses; the visible broker console is the only practical difference, and the shortcut is the only supported way to open it.

---

## 17c. Stop PAX Cookbook Server does not delete your workspace

> **Symptom** — You want to stop the running local Cookbook broker without hunting for the PowerShell process in Task Manager, or Cookbook appears to still be running after you closed the browser tab.
>
> **Why** — Closing the browser tab does not necessarily mean the local broker has already stopped. The launcher's runtime-reuse path deliberately keeps the broker alive after the tab closes so that the next launch is fast. The **Stop PAX Cookbook Server** Start Menu shortcut asks the running broker to shut down cleanly for the selected workspace. It is a controlled stop, not an uninstall, not a workspace delete, and not a recipe or bake cleanup operation. Stop PAX Cookbook Server does not delete workspace data, does not delete saved recipes, does not delete bakes or bake output files, does not remove Chef's Keys, and does not change anything inside the install tree. The in-product help panel surfaces the same description under **Troubleshooting &rarr; Stop PAX Cookbook Server**.
>
> **Evidence to inspect** —
> - The **Stop PAX Cookbook Server** Start Menu shortcut itself.
> - Task Manager before and after using it &mdash; the `pwsh.exe` process running `Start-Broker.ps1` for this workspace should be gone afterwards.
> - The visible Support Mode broker window, if you launched Cookbook in Support Mode and want to watch the clean-shutdown messages.
> - `<Workspace>\Runtime\workspace.lock` before and after shutdown, if you are investigating a stale lock; the next healthy launch will reclaim a stale lock automatically (see [&sect;2](#2-broker-exit-code-4--workspace-locked)).
>
> **Corrective action** — Use **Stop PAX Cookbook Server** from the Start Menu whenever you want to fully end a Cookbook session &mdash; for example, when the browser tab is closed but the broker is still serving, when you want to release the workspace lock before launching against the same workspace from another machine, or when a support step asks for a fresh broker start. After Stop PAX Cookbook Server completes, relaunch **PAX Cookbook** normally from the Start Menu. If the broker is no longer healthy and the stop request cannot reach it, end the `pwsh.exe` process running `Start-Broker.ps1` in Task Manager and then relaunch &mdash; the next healthy launch will reclaim a stale `workspace.lock` automatically. Do not edit or delete `workspace.lock` by hand; the broker manages it.

---

## 17d. Uninstall PAX Cookbook from the Start Menu

> **Symptom** — You want to remove PAX Cookbook from this Windows user profile and you want a clear, plain-language confirmation before any files are deleted.
>
> **Why** — The **Uninstall PAX Cookbook** Start Menu shortcut is the supported user-facing way to remove the appliance. It opens a confirmation dialog that spells out exactly what will and will NOT be deleted, defaults focus to **No** so accidentally pressing Enter cancels safely, and refuses to run while the broker is still up (it tells you to click **Stop PAX Cookbook Server** first). On confirmation, it removes `<InstallRoot>\App\`, `Backups\`, `Updates\`, the Start Menu folder, and the Desktop shortcut. It does not touch workspace folders (recipes, bake outputs, runtime state), external export folders, Purview / Entra / Microsoft 365 tenant data, or scheduled tasks you created with the Schedule card &mdash; those live at paths the installer does not track, so removing them would risk taking your data with the appliance. The in-product help panel surfaces the same description under **Troubleshooting &rarr; Uninstall PAX Cookbook**.
>
> **Evidence to inspect** —
> - The **Uninstall PAX Cookbook** Start Menu shortcut itself.
> - `<InstallRoot>\install.log` before and after &mdash; the relocation and the spawned uninstaller both write to it.
> - `%TEMP%\PAXCookbookUninstall-<UTC-timestamp>-<8-hex>\` &mdash; one acceptable leftover folder; see OPERATOR_GUIDE.md &sect;14.
> - The Start Menu folder `%APPDATA%\Microsoft\Windows\Start Menu\Programs\PAX Cookbook\` &mdash; gone after a successful uninstall.
>
> **Corrective action** — Open the Start Menu, expand **PAX Cookbook**, click **Uninstall PAX Cookbook**, read the dialog, click **Yes** to confirm. If the dialog refuses with "PAX Cookbook is currently running", click **Stop PAX Cookbook Server** first, wait for the broker window to close, and try again. If you want a fully scripted uninstall without the dialog, run `pwsh -File <release-folder>\app\install\Install-PAXCookbook.ps1 uninstall -Yes -Purge` from a fresh PowerShell window (not the one that the Start Menu shortcut launched). To remove a workspace folder or external export folder, delete it by hand &mdash; the uninstaller deliberately leaves chef-owned data alone.

---

## 17e. PAX engine acquisition errors

This section covers failure modes specific to the Cookbook v1 external PAX engine acquisition flow (see [OPERATOR_GUIDE.md §4a](OPERATOR_GUIDE.md#4a-pax-engine-acquisition) for the chef-facing description and [docs/CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md §14a](CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md#14a-external-pax-engine-acquisition-cookbook-v1) for the binding doctrine).

All seven failure codes below are structured-JSON responses from the broker. They are surfaced in the SPA acquisition dialog (**Settings → PAX engine**), echoed as `cook_runs` telemetry fields, and written to `install.log` when triggered during automation-fallback installs.

### 17e.1 `acquisition_required`

> **Symptom** — Every recipe / cook / scheduler / runtime request returns HTTP `409 acquisition_required` with a JSON body naming the expected engine version and the acquisition surface to drive. The SPA opens the acquisition dialog automatically and shows a persistent banner pointing at **Settings → PAX engine**.
>
> **Why** — This is the normal state on a fresh v1 install (or on any appliance where `install-state.json -> paxAcquisition.pending = true`). No PAX engine has been validated to disk yet. The broker refuses all cook-class operations until acquisition succeeds; this is by design, not a bug.
>
> **Evidence to inspect** —
> - `%LOCALAPPDATA%\PAXCookbook\install-state.json -> paxAcquisition.pending` — confirms acquisition has not completed.
> - `<InstallRoot>\App\VERSION.json -> paxScript.acquisitionPolicy` — should be `"external"` for v1 artifacts.
> - The structured JSON body of any blocked cook request — names the expected `version` and `engineManifestUrl`.
>
> **Corrective action** — Open the acquisition dialog (it should auto-open; if not, navigate to **Settings → PAX engine** and click **Acquire engine**). Choose **Download** (broker fetches from the manifest URL) or **Use a local script file** (offline path; see §4a.4). If neither is feasible, contact your distributor / IT for the air-gapped automation-fallback path.

### 17e.2 `manifest_fetch_failed`

> **Symptom** — Clicking **Download** in the acquisition dialog reports `manifest_fetch_failed` with an HTTP status (or transport error) sub-code.
>
> **Why** — The broker could not retrieve the signed approved-engine manifest from the URL pinned in `VERSION.json.paxScript.engineManifestUrl`. Causes are network-level: DNS failure, proxy refusal, firewall blocking the destination, TLS handshake failure, or the manifest endpoint returning a non-2xx response.
>
> **Evidence to inspect** —
> - The HTTP status / transport error in the broker's response.
> - `<InstallRoot>\App\VERSION.json -> paxScript.engineManifestUrl` — the URL the broker is trying to reach.
> - Your machine's outbound HTTPS connectivity to that exact URL (test with PowerShell: `Invoke-WebRequest -Uri '<url>' -UseBasicParsing`).
> - Any proxy / Zscaler / corporate firewall logs that may be filtering the destination.
>
> **Corrective action** — Fix the network path so the broker can reach the manifest URL. If outbound HTTPS to the engine-manifest URL is permanently blocked in your environment, use the offline path: have your IT contact obtain the signed manifest file out-of-band and pre-stage it with the `-PaxManifestPath` installer parameter (§4a.4 in OPERATOR_GUIDE.md), then drive **Use a local script file** in the acquisition dialog.

### 17e.3 `manifest_signature_invalid`

> **Symptom** — The acquisition dialog reports `manifest_signature_invalid`. The broker successfully downloaded the manifest but refused to trust it.
>
> **Why** — One of:
> 1. The detached `.sig` envelope did not cryptographically verify against the manifest bytes (RSA-PKCS1v15-SHA256 failure).
> 2. The signing certificate's SHA-1 thumbprint does not match the trust anchor pinned at `VERSION.json.paxScript.engineManifestTrustAnchorThumbprint`.
> 3. The signing certificate is expired or not yet valid.
> 4. The signature envelope's `schemaVersion` is unrecognized.
>
> The broker offers **no "use anyway" override**. The trust anchor is the entire basis on which Cookbook will execute the resulting script bytes; an invalid signature is a terminal refusal.
>
> **Internal-test builds.** In a build configured with `paxScript.manifestSignaturePolicy = "internal-test-bypass"` (see §17e.8), the broker does **not** perform detached-signature verification, so this code cannot fire. The bypass relaxes signature verification **only**: JSON-schema validation, approved-entry selection, the SHA-256 pin check (§17e.5), and exact-byte activation at the canonical install path all still run identically to a production build. `manifest_signature_invalid` applies to production builds where `manifestSignaturePolicy = "required"`.
>
> **Evidence to inspect** —
> - The specific sub-code in the broker's response (`signature_invalid` vs `thumbprint_mismatch` vs `cert_expired` vs `unknown_schema`).
> - The full manifest bytes and `.sig` envelope the broker received (the broker preserves these for forensic inspection at `%LOCALAPPDATA%\PAXCookbook\install-state.json -> paxAcquisition.lastAttemptError`).
> - The trust anchor thumbprint pinned in your Cookbook release: `<InstallRoot>\App\VERSION.json -> paxScript.engineManifestTrustAnchorThumbprint`.
>
> **Corrective action** — This is almost always a publisher-side issue (the PAX team's signing pipeline rolled a cert, the trust anchor in your Cookbook release is stale, or the manifest endpoint is serving a tampered file). Open an issue with your Cookbook distributor and attach the `paxAcquisition.lastAttemptError` JSON. Do **not** edit `engineManifestTrustAnchorThumbprint` by hand — it is a Cookbook-release-time pin and changing it bypasses the signed-manifest trust model. Do **not** edit the PAX script to make any of this go away — a manifest-signature failure means the engine release channel cannot currently be trusted, not that the script needs adjustment; mutating the script bytes, the `paxScript.sha256` pin, or any other trust pin to coerce a pass is a doctrine violation (see [`CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md` constitution rule 8](CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md#2-constitution-quick-reference)). The corrective action is to obtain a freshly-published, validly-signed manifest from the PAX team via your distributor.

### 17e.4 `no_compatible_engine`

> **Symptom** — The acquisition dialog reports `no_compatible_engine` after the manifest verifies successfully. The dialog lists the engine versions the manifest does advertise and tells you they are out of range for your running Cookbook.
>
> **Why** — The signed manifest does not contain an engine entry whose compatibility window (`minCookbookVersion` ≤ *your Cookbook* ≤ `maxCookbookVersion`) AND whose `version` matches the value pinned in `VERSION.json.paxScript.version`. The two halves of the compatibility check are independent: the manifest might advertise newer engine versions that require a newer Cookbook, or your Cookbook might be ahead of the most recent compatible engine entry.
>
> **Evidence to inspect** —
> - Your Cookbook version: `<InstallRoot>\App\VERSION.json -> cookbook.version`.
> - The expected engine version: `<InstallRoot>\App\VERSION.json -> paxScript.version`.
> - The manifest entries returned by the broker (visible in the acquisition dialog and recorded in `install-state.json -> paxAcquisition.lastAttemptError.manifestSnapshot`).
>
> **Corrective action** — Two paths, depending on which side is ahead:
> - **If your Cookbook is older than what the manifest advertises:** update Cookbook (download the latest release, re-run the installer in `update` mode). After update, re-drive acquisition from **Settings → PAX engine**.
> - **If your Cookbook is newer than what the manifest advertises:** the PAX team has not yet published a compatible engine. Either roll back to a Cookbook release that *is* compatible with the most recent published engine, or wait for the PAX team to publish a new engine compatible with your Cookbook. Contact your distributor for guidance.

### 17e.5 `script_hash_mismatch`

> **Symptom** — The acquisition dialog reports `script_hash_mismatch`. The manifest verified and named a compatible engine, but the downloaded script bytes do not hash to the expected SHA-256.
>
> **Why** — One of:
> 1. The script bytes were tampered with in transit (TLS-intercepting proxy that re-served different content; CDN cache poisoning).
> 2. The PAX team's published script bytes diverged from what their manifest advertises (publisher pipeline bug; should be reported).
> 3. (For the local-file path) The file you selected is not the engine the manifest pins.
>
> The broker refuses to write the bytes to disk; no partial state is left behind.
>
> **Evidence to inspect** —
> - Expected SHA-256: from the manifest entry (echoed in the broker's response and in `install-state.json -> paxAcquisition.lastAttemptError`).
> - Observed SHA-256: also in the broker's response.
> - For the local-file path: the file you selected. Recompute SHA-256 yourself: `Get-FileHash -Algorithm SHA256 -LiteralPath '<path>'`.
>
> **Corrective action** — For the **Download** path: clear any HTTPS-intercepting proxy from the broker's path to the engine endpoint and re-drive acquisition; if the failure repeats, file an issue with your distributor (this is publisher- or transport-side). For the **Use a local script file** path: confirm you selected the correct file; if so, obtain a fresh copy of the engine from your distributor. Do **not** edit the PAX script to make the hash match the manifest — even a single trailing-newline / line-ending / BOM change shifts the SHA-256 by design, and any such edit invalidates the trust model. Do **not** copy a random `.ps1` file into `<InstallRoot>\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1` to "unblock" the install — the broker re-hashes that path on every cook and will reject the substitute. Do **not** change `paxScript.sha256` (or `manifest.json.includedPaxScript.sha256`) in your Cookbook install to track whatever bytes you happen to have; the pin is the **approved** hash and is never adjusted to match a tampered or off-channel script. The corrective action is always to acquire the approved PAX release from the PAX team's distribution channel (see [`CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md` constitution rule 8](CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md#2-constitution-quick-reference)).

### 17e.6 `local_file_not_approved`

> **Symptom** — The acquisition dialog reports `local_file_not_approved` when you selected a `.ps1` file via **Use a local script file**. The manifest verified, but the script bytes you selected do not match any entry in the manifest.
>
> **Why** — The signed approved-engine manifest is the trust anchor for **every** acquisition path, including the offline local-file path. A file whose hash is not enumerated in the current manifest cannot be accepted, regardless of where it came from. This is the same trust check that prevents arbitrary scripts from being substituted in the **Download** path.
>
> **Evidence to inspect** —
> - The file you selected: its SHA-256 (computed by the broker) and its `version` string (if recoverable from the file's header).
> - The manifest entries the broker has loaded (visible in the dialog and recorded in `install-state.json`).
>
> **Corrective action** — Obtain the correct engine file from your distributor. The local-file path is an offline-distribution convenience, not a sideload-anything override. If you genuinely need to run an off-manifest engine for development purposes, that is a Cookbook-developer concern outside the chef-facing trust model and is not supported through the acquisition dialog. Do **not** edit the local `.ps1` you selected to make Cookbook accept it — the file you supplied is hashed as-is and validated against the signed approved-engine manifest, and any mutation (whitespace, line endings, encoding, comments, wrapping, signing) will continue to produce a hash the manifest does not list. Do **not** modify the script in Cookbook's managed location, do **not** change `paxScript.sha256` in `VERSION.json` to bless the off-manifest bytes, and do **not** copy a random `.ps1` into the install tree to bypass the dialog. Cookbook will not edit the PAX script on your behalf, and editing it yourself does not change the trust outcome; the only corrective action is to obtain the approved PAX release whose hash **is** listed in the current signed manifest (see [`CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md` constitution rule 8](CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md#2-constitution-quick-reference)).

### 17e.7 PAX-script export surface removed

> **Symptom** — A client (script, automation, browser bookmark, or legacy SPA tab) issues a request to a PAX-script export path such as `GET /api/v1/pax/download` or `GET /api/v1/runtime/pax-script/download` and receives the broker's standard 404 for an unknown route. The Settings page no longer shows the **Export bundled PAX script** button you may remember from older Cookbook builds.
>
> **Why** — Under Cookbook v1, Cookbook does not redistribute the PAX engine. The export surface was meaningful in the Phase 12 embedded-payload era because Cookbook owned the bytes it served; with external acquisition, the PAX team owns the engine release channel and Cookbook only orchestrates downloads through their signed manifest. Re-serving the bytes from Cookbook would amount to redistribution without the PAX team's release controls. The route, the SPA button, and the supporting CSS have all been removed from the source tree — absence is the contract, not a 403 short-circuit. There is no `pax_export_disabled` error code in v1.
>
> **Evidence to inspect** — The broker's route table (no PAX-script export entry exists) and the Settings page DOM (no Export button is rendered).
>
> **Corrective action** — Remove any client-side code that targets a PAX-script export path. If you need the exact engine bytes Cookbook is currently using (for offline backup, version-control, or audit), read them directly from `%LOCALAPPDATA%\PAXCookbook\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1` after acquisition has succeeded. For obtaining a fresh copy of the engine from the publisher, use the PAX team's distribution channel (URL pinned at `VERSION.json.paxScript.engineManifestUrl`).

### 17e.8 `internal-test-bypass` manifest-signature policy

> **Symptom** — `%LOCALAPPDATA%\PAXCookbook\App\VERSION.json` reports `paxScript.manifestSignaturePolicy = "internal-test-bypass"` and `paxScript.engineManifestTrustAnchorThumbprint = null`, and the acquisition dialog shows a banner titled **Internal-test build (not customer-facing)**.
>
> **Why** — This is an internal-test build. It is configured for internal testing only and is **not customer-facing**. The approved-engine manifest is fetched **without detached-signature verification**; this is the only check the policy relaxes. Every other acquisition check runs exactly as in a production build: JSON-schema validation of the manifest, approved-entry selection for the expected engine version, the SHA-256 pin check against the manifest (§17e.5), and exact-byte activation at the canonical install path. There is no "use anyway" override and no export route. A production release uses `manifestSignaturePolicy = "required"` with a populated trust anchor and a signed manifest.
>
> **Evidence to inspect** —
> - `<InstallRoot>\App\VERSION.json -> paxScript.manifestSignaturePolicy` — `"internal-test-bypass"` on an internal-test build, `"required"` on production.
> - `<InstallRoot>\App\VERSION.json -> paxScript.engineManifestTrustAnchorThumbprint` — `null` on an internal-test build.
> - The acquisition dialog banner (internal-test builds render the non-customer-facing banner; production builds do not).
>
> **Corrective action** — None required if you are knowingly running an internal-test build for validation. The bypass does **not** weaken the SHA-256 pin, approved-entry selection, or the no-edit guarantee on the PAX script, so the engine bytes you activate are still exactly the approved bytes. Do **not** publish an internal-test build to customers; production distribution requires `manifestSignaturePolicy = "required"`, a real trust anchor, and a signed approved-engine manifest. If you expected a production build but see `internal-test-bypass`, obtain the correct production artifact from your distributor.

---

## 18. When this guide is not enough

If your symptom is not here:

1. Capture the launcher's full output (copy from the console).
2. Capture `<InstallRoot>\install.log` (if relevant).
3. Capture the relevant `<Workspace>\Cooks\<cookId>\` directory (if a bake is involved).
4. Capture `<InstallRoot>\App\VERSION.json` so the version is unambiguous.
5. Open an issue with all of the above attached.

Do **not** invent a workaround procedure and add it to this guide unilaterally. Every entry here corresponds to behavior that is reproducible from the source tree. If you discover a missing entry that meets that bar, propose an addition that includes the same four-section shape (symptom / why / evidence / corrective action).
