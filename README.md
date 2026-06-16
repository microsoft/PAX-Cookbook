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

**Recommended.** Download **`PAX_Cookbook_Setup.exe`** from the [latest Release](https://github.com/microsoft/PAX-Cookbook/releases/latest) and run it. The installer is per-user, writes only to `%LOCALAPPDATA%\PAXCookbook\`, and requires no administrator rights. When the installer finishes, open **PAX Cookbook** from the Start Menu.

> The Setup executable is not yet code-signed, so Windows SmartScreen may show a "Windows protected your PC" prompt. Choose **More info → Run anyway** to proceed.

**Alternative (ZIP + script).** Distributors and advanced users can instead download the release ZIP, verify its `.sha256` sidecar, extract it, and run `install\Install-PAXCookbook.ps1 install` from PowerShell 7.4+. See [docs/OPERATOR_GUIDE.md §3](docs/OPERATOR_GUIDE.md#3-first-install).

**Requirements.** Windows 11, and the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (preinstalled on current Windows 11). PowerShell 7.4+ is required only for the ZIP-based install path.

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

Cookbook replaces long command examples and documentation-heavy workflows with a clean guided experience built around five simple steps:

1. **What** — choose the data to collect
2. **When** — choose the time window
3. **Where** — choose the output destination
4. **Advanced** — tune optional behavior
5. **Bake** — run the recipe

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

Cookbook includes guided templates aligned to Microsoft reporting ecosystems powered by PAX.

Initial templates include:

- **M365 Usage Analytics Dashboard**
- **AI-in-One Dashboard**

Templates accelerate setup while still allowing full customization.

---

## Enterprise-Friendly by Design

Cookbook is intentionally designed to minimize security and deployment friction. **The guarantees in this section apply to Cookbook itself — its installer, launcher, broker, and the SPA the broker serves — as part of Cookbook's own installation and runtime lifecycle. They do not describe the runtime behavior of the bundled PAX script when invoked by a recipe; PAX-owned runtime behavior (including PAX's per-user Python bootstrap for rollup workflows) is described in the paragraphs below and in [docs/RELEASE_PACKAGE.md §11a](docs/RELEASE_PACKAGE.md#11a-bundled-workflow-runtime-behavior).**

As part of its own installation and runtime lifecycle, Cookbook itself does not perform:

- elevation (no admin rights at any point)
- services or scheduled tasks
- registry writes
- `PATH` or environment-variable mutation
- silent dependency installs (`winget`, `Install-Module`, `choco`, `pip`, `npm`)
- browser extensions
- cloud-hosted infrastructure
- telemetry, remote diagnostics, or AI-assistant integrations

Cookbook's own runtime footprint is intentionally small:

- PowerShell 7.4 or newer — required to install, launch, and operate Cookbook itself.
- A localhost browser experience served from the local broker.

That is the full **Cookbook-side** dependency footprint. Certain bundled recipes — notably the **M365 Usage Analytics Dashboard** and **AI-in-One Dashboard** templates — invoke PAX in `Rollup` or `RollupPlusRaw` mode. PAX's rollup post-processor is implemented in Python. PAX itself — not Cookbook — detects, installs, and manages Python when a rollup workflow is invoked. Cookbook does not own that dependency chain; it orchestrates the PAX invocation and surfaces PAX's output verbatim. See [docs/OPERATOR_GUIDE.md §3.1](docs/OPERATOR_GUIDE.md#31-prerequisites) for the full prerequisite chart.

The operational model is intentionally simple:

1. Download the release ZIP and verify its SHA-256 sidecar.
2. Extract it and run `install\Install-PAXCookbook.ps1 install` from PowerShell 7.4+. The installer is a per-user script — no MSI, no MSIX, no elevation.
3. Launch Cookbook from the Start Menu shortcut it created.
4. Pick a local workspace folder and start baking.

The installer is a script inside the ZIP, not a separate executable. It writes only to `%LOCALAPPDATA%\PAXCookbook\` and creates a per-user Start Menu shortcut. See [docs/OPERATOR_GUIDE.md §3](docs/OPERATOR_GUIDE.md#3-first-install) for the full procedure.

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

The core operational experience is implemented and operating end-to-end: recipe authoring, guided bakes with live execution visibility, the Bakes page, the update lifecycle, the trust / integrity model, and the foundational dashboard templates. The project is being built in deliberate, well-scoped increments so each capability lands with full operational quality before the next is added.

Installation is via the script `install\Install-PAXCookbook.ps1` inside the release ZIP. It is per-user, non-elevated, and reversible. Cookbook does not require any administrator action at any point. The orchestration layer is intentionally thin and PAX remains authoritative.

---

## Usage

Launch PAX Cookbook from the Start Menu, pick a local workspace folder, and start authoring recipes. Every screen has contextual guidance, and the in-app **Help** system (the **?** in the app header) is the canonical end-user reference for recipes, bakes, scheduling, Chef's Keys, and recovery. For the written operator reference, see the [Chef Guide](docs/OPERATOR_GUIDE.md).

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
