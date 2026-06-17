# PAX Cookbook

**The Operational Experience Layer for PAX**

> **Status.** PAX Cookbook is a working local appliance under active development. The runtime, installer, launcher, broker, SPA, and update flow are all implemented. This README describes the operational model. See [docs/OPERATOR_GUIDE.md](docs/OPERATOR_GUIDE.md) for the canonical chef reference. *(In Cookbook's vocabulary, the **chef** is the person running the appliance — picking a workspace, editing recipes, and running bakes.)*

PAX Cookbook transforms the [PAX Purview Audit Log Processor PowerShell script](https://github.com/Microsoft/PAX) from a powerful command-line tool into a guided, repeatable, enterprise-friendly operational experience — without changing the underlying PAX Purview Audit Log Processor PowerShell script at all.

Built as a local-first orchestration shell around PAX, Cookbook simplifies complex data collection workflows through recipes, guided execution, operational visibility, and automation-ready workflows while preserving the full power and transparency of native PAX execution.

---

## Documentation

- **[Chef Guide](docs/OPERATOR_GUIDE.md)** — Install, launch, use, recover, uninstall. The canonical reference for chefs (end users running the appliance).
- **[Release Package Reference](docs/RELEASE_PACKAGE.md)** — Release ZIP structure, integrity model, signing model. For distributors who build or redistribute releases.
- **[Troubleshooting](docs/TROUBLESHOOTING.md)** — Symptom → why → evidence → corrective action. Operational triage only.

---

## Installation

**Recommended — the setup wizard.**

1. Download **`PAX_Cookbook_Setup.exe`** from the [latest Release](https://github.com/microsoft/PAX-Cookbook/releases/latest).
2. **Double-click** it to launch the setup wizard.
3. The wizard **checks for prerequisites** — PowerShell 7 and Python — and offers to **install them for you** if they are missing.
4. **Follow the wizard** to choose the install location and complete the installation.
5. Open **PAX Cookbook** from the **Start Menu**.

PAX Cookbook itself installs per-user under `%LOCALAPPDATA%\PAXCookbook\` and needs no administrator rights. If you let the wizard install PowerShell 7, Windows shows a one-time administrator (UAC) approval prompt for that step only — you can decline it and install PowerShell 7 yourself later; PAX Cookbook still installs. Python is installed per-user with no administrator prompt.

> The Setup executable is not yet code-signed, so Windows SmartScreen may show a "Windows protected your PC" prompt. Choose **More info → Run anyway** to proceed.

**Requirements.** Windows 11 and the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (preinstalled on current Windows 11). PowerShell 7 and Python are installed for you by the wizard when needed.

**Advanced / silent install (ZIP + script).** Distributors and advanced users can instead download the release ZIP, verify its `.sha256` sidecar, extract it, and run `install\Install-PAXCookbook.ps1 install` from PowerShell 7.4+. The Setup executable also accepts the verb-based CLI (`PAX_Cookbook_Setup.exe install`, `… uninstall --quiet`, `… version`) for scripted/unattended use. See [docs/OPERATOR_GUIDE.md §3](docs/OPERATOR_GUIDE.md#3-first-install).

---

## Features

- **Guided setup wizard** — a double-click installer that detects and (with your consent) installs the PowerShell 7 and Python prerequisites, then installs PAX Cookbook per-user.
- **Seven-step recipe builder** — Basics, Authentication, Date Range, Audit Operations, Output, Schedule, and Review + Save, each with inline guidance.
- **Dashboard presets** — start fast with **AI-in-One**, **AI Business Value**, **M365 Usage**, **Entra user info only**, or a fully **Custom** recipe.
- **Previous Day date mode** — one click scopes a run to the prior full UTC day, ideal for daily scheduled collection.
- **Background broker daemon** — runs quietly with a system-tray icon, auto-starts at login, and keeps serving after you close the window so scheduled work can fire.
- **Scheduled bakes** — schedule a recipe and PAX Cookbook registers a per-user Windows Task Scheduler task that delegates the run to the background daemon (no ghost runs, shared history).
- **Bake history** — every run (a "Bake") is recorded with its exact PAX command, output files, and log; open the output folder or the log directly from the app.
- **Pantry** — browse the PAX dashboard repositories in-app, preview or save files, and read project READMEs without leaving the window.
- **Check Readiness** — validate the engine, authentication, output path, and permissions before you run.
- **Chef's Keys** — manage the authentication credentials (app registration secret/certificate, device code, or interactive sign-in) your recipes use.
- **Close dialog** — choose **Minimize to tray** (keep the background broker and any active/scheduled bakes running) or **Exit** (stop the broker).
- **PAX engine v1.11.6** — Fabric OneLake GUID-form URLs, dual certificate-store search (CurrentUser + LocalMachine), and improved SharePoint uploads.

---

## Why PAX Cookbook?

PAX has evolved into an incredibly flexible enterprise data collection engine capable of:

- pulling Microsoft audit and telemetry sources at scale
- supporting rollup architectures
- supporting multiple output destinations
- powering advanced Power BI reporting solutions
- enabling operational automation scenarios

That flexibility also means:

- many switches
- many workflow combinations
- many storage and output paths
- increasing operational complexity

PAX Cookbook solves that problem.

---

## Recipe-Based Workflows

Save repeatable PAX workflows as reusable **Recipes**.

Recipes guide users through:

- what data to collect
- where outputs go
- which authentication method to use
- rollup configuration
- operational settings

Advanced users still retain:

- native command visibility
- raw argument passthrough
- the full flexibility of PAX

**Recipe Takeout.** Move a single recipe definition between Cookbook workspaces with **Export Recipe Takeout** and **Import Recipe Takeout**. The transport file is the recipe definition only — Chef's Keys, secrets, bakes, logs, and output data are excluded. See [docs/OPERATOR_GUIDE.md §6a](docs/OPERATOR_GUIDE.md#6a-recipe-takeout).

---

## Guided Operational Experience

Cookbook replaces long command examples and documentation-heavy workflows with a clean, guided seven-step recipe builder:

1. **Basics** — name the recipe and pick a dashboard preset (or start Custom)
2. **Authentication** — bind a Chef's Key (app secret/certificate, device code, or interactive sign-in)
3. **Date Range** — choose Previous Day or a custom window
4. **Audit Operations** — choose the data to collect
5. **Output** — choose where the results go
6. **Schedule** — optionally run the recipe on a recurring schedule
7. **Review + Save** — confirm the exact PAX command, then save

The result:

- faster onboarding
- fewer configuration mistakes
- repeatable operational consistency

---

## Native PAX Transparency

Cookbook does **not** replace PAX.

PAX remains:

- fully standalone
- fully script-driven
- fully executable outside Cookbook

Cookbook simply orchestrates native commands and provides:

- workflow simplification
- execution visibility
- a local record of every bake
- recipe management

Every bake shows the exact native PAX command being executed.

---

## Real-Time Bake Visibility

Watch bakes execute live through an embedded terminal experience.

Track:

- execution progress
- validation
- warnings
- failures
- runtime metrics
- operational logs

The Bakes page provides searchable operational visibility across past runs.

---

## Dashboard-Aligned Templates

Cookbook includes guided dashboard presets aligned to Microsoft reporting ecosystems powered by PAX:

- **AI-in-One Dashboard**
- **AI Business Value Dashboard**
- **M365 Usage Dashboard**
- **Entra user info only**
- **Custom audit export** — a fully hand-built recipe

Presets accelerate setup while still allowing full customization.

---

## Enterprise-Friendly by Design

Cookbook is intentionally designed to minimize security and deployment friction. **The guarantees in this section apply to Cookbook itself — its installer, launcher, broker, and the SPA the broker serves — as part of Cookbook's own installation and runtime lifecycle. They do not describe the runtime behavior of the bundled PAX script when invoked by a recipe; PAX-owned runtime behavior (including PAX's per-user Python bootstrap for rollup workflows) is described in the paragraphs below and in [docs/RELEASE_PACKAGE.md §11a](docs/RELEASE_PACKAGE.md#11a-bundled-workflow-runtime-behavior).**

Cookbook installs and runs entirely **per-user**. The installed application — its broker, launcher, and the SPA the broker serves — never requires administrator rights for normal use, and Cookbook itself does not perform:

- **HKLM (machine-wide) registry writes** — all registry use is per-user (HKCU) only
- **Windows services**
- **`PATH` or environment-variable mutation**
- **cloud-hosted infrastructure**
- **telemetry, remote diagnostics, or AI-assistant integrations**
- **browser extensions**

What Cookbook *does* do, all per-user and reversible by uninstall:

- **Per-user HKCU registry entries** — file associations for `.pax` / `.paxlite`, the `paxcookbook:` protocol handler, an Add/Remove Programs uninstall entry, and (when you enable Start-at-login) a `Run` value that auto-starts the background broker. No machine-wide (HKLM) keys are ever written.
- **Per-user scheduled tasks** — when you schedule a recipe, Cookbook registers a Windows Task Scheduler task under your account (no elevation) that delegates the run to the running background broker.
- **A background broker daemon** — a windowless, system-tray process that hosts the local broker so scheduled bakes can fire while no window is open. You start or stop it from the close dialog and Settings.

The **setup wizard** can optionally download and install two prerequisites for you, with your consent:

- **PowerShell 7** — downloaded from Microsoft's official GitHub releases and installed silently. This step shows a one-time Windows administrator (UAC) approval prompt; you can decline it and install PowerShell 7 yourself, and Cookbook still installs.
- **Python** — downloaded from python.org and installed per-user (no administrator prompt). PAX's rollup post-processor uses Python; pre-installing it lets rollup recipes run without a first-run delay.

Both prerequisite installs are optional and consent-based — nothing is installed silently behind your back. Certain bundled recipes — notably the **M365 Usage Analytics Dashboard** and **AI-in-One Dashboard** presets — invoke PAX in `Rollup` or `RollupPlusRaw` mode, whose post-processor is implemented in Python; if Python was not pre-installed by the wizard, PAX itself detects and installs it per-user at run time. See [docs/OPERATOR_GUIDE.md §3.1](docs/OPERATOR_GUIDE.md#31-prerequisites) for the full prerequisite chart.

The operational model is intentionally simple:

1. Download `PAX_Cookbook_Setup.exe` from the latest Release.
2. Double-click it and follow the wizard. It checks for PowerShell 7 and Python, offers to install them, then installs Cookbook per-user — no MSI, no MSIX, no machine-wide changes.
3. Launch Cookbook from the Start Menu shortcut it created.
4. Pick a local workspace folder and start baking.

The installer writes only to `%LOCALAPPDATA%\PAXCookbook\` and the per-user HKCU entries above, and uninstalling reverses them. See [docs/OPERATOR_GUIDE.md §3](docs/OPERATOR_GUIDE.md#3-first-install) for the full procedure.

---

## Architecture Philosophy

PAX Cookbook is intentionally:

- local-first
- lightweight
- transparent
- minimal-dependency
- operationally focused

It is **not**:

- a cloud platform
- a workflow engine
- a server product
- a replacement for Power BI
- a replacement for PAX

The goal is simple:

> Keep the orchestration layer thin.
> Keep PAX authoritative.
> Make complex workflows dramatically easier to operate.

---

## Designed for Real Operational Work

PAX Cookbook is built for organizations that need:

- repeatable audit collection workflows
- operational consistency
- easier onboarding
- simplified rollup workflows
- reduced CLI complexity
- visibility into past bakes
- automation-ready operational foundations

…without sacrificing the flexibility and power that made PAX valuable in the first place.

---

## Current Status

The core operational experience is implemented and operating end-to-end: the guided setup wizard with prerequisite auto-install, recipe authoring, guided bakes with live execution visibility, the Bakes page, scheduling via the background broker daemon, the update lifecycle, the trust / integrity model, and the dashboard presets. The project is being built in deliberate, well-scoped increments so each capability lands with full operational quality before the next is added.

Installation is via the **`PAX_Cookbook_Setup.exe`** setup wizard (a ZIP + per-user script path remains for advanced/silent use). It is per-user and reversible; only the optional PowerShell 7 prerequisite install requests a one-time administrator approval, which you can decline. The orchestration layer is intentionally thin and PAX remains authoritative.

---

## Usage

Launch PAX Cookbook from the Start Menu, pick a local workspace folder, and start authoring recipes. Use **Check Readiness** to validate your environment before a run, watch bakes execute live, and review past runs (with their output files and logs) on the **Bakes** page. Schedule a recipe and the background broker runs it on time even when no window is open; closing the window lets you **Minimize to tray** (keep the broker running) or **Exit** (stop it). Every screen has contextual guidance, and the in-app **Help** system (the **?** in the app header) is the canonical end-user reference for recipes, bakes, scheduling, Chef's Keys, and recovery. For the written operator reference, see the [Chef Guide](docs/OPERATOR_GUIDE.md).

---

## Building from Source

**Prerequisites**

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js 18+](https://nodejs.org/) (for the React product shell)
- PowerShell 7.4+
- Windows 11

**Build the native broker (C#)**

```powershell
dotnet build src\PAXCookbook.App\PAXCookbook.App.csproj -c Release
```

**Build the product shell (React)**

```powershell
cd app\web-react
npm ci
npm run build   # emits the static bundle into app\web\app
```

**Run the tests**

```powershell
dotnet test
```

**Build the distributable installer**

```powershell
pwsh -File tools\release\Build-Setup.ps1
```

This publishes the broker, builds the React shell, stages the appliance payload, and emits the single-file installer at `dist\setup\PAX_Cookbook_Setup.exe`. The bundled PAX engine script is copied byte-for-byte and its SHA-256 is verified during the build; it is never modified.

---

## License

This project is licensed under the [MIT License](LICENSE).

The bundled [PAX Purview Audit Log Processor](https://github.com/microsoft/PAX) script is a separate component with its own license and governance; PAX Cookbook orchestrates it without modification.

---

## Contributing

This project welcomes contributions and suggestions. See [SECURITY.md](SECURITY.md) for reporting vulnerabilities, [SUPPORT.md](SUPPORT.md) for help, and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for our community standards.

### Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft trademarks or logos is subject to and must follow [Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general). Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship. Any use of third-party trademarks or logos is subject to those third-party's policies.
