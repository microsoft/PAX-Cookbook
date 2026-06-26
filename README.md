<p align="center">
  <a href="https://microsoft.github.io/PAX-Cookbook">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="images/pax-cookbook-logo-readme-dark.png">
      <source media="(prefers-color-scheme: light)" srcset="images/pax-cookbook-logo-readme.png">
      <img alt="PAX Cookbook" src="images/pax-cookbook-logo-readme.png" width="400">
    </picture>
  </a>
</p>

<p align="center">
  <strong>Turn your Microsoft 365 Copilot audit data into actionable Power BI dashboards — no scripting required.</strong>
</p>

---

## What is PAX Cookbook?

**PAX Cookbook** is a Windows desktop app that collects Microsoft 365 Copilot adoption and usage data from your organization's Microsoft Purview audit logs and prepares it for Power BI dashboards — all through a friendly, guided experience.

It's built for the people who need to measure Copilot's impact: **analysts, IT admins, project managers, and business leaders** tracking adoption and return on investment. You don't write any code. You make a few choices in a step-by-step builder, and PAX Cookbook does the rest.

Under the hood, PAX Cookbook drives an open-source engine called **[PAX (the Purview Audit Log Processor)](https://github.com/microsoft/PAX)** — the tool that knows how to pull the right records from Microsoft 365 and shape them into clean output. PAX Cookbook gives you all of that power without the command line, and every run produces **structured data that's ready for Power BI** (or any reporting tool).

---

## What can you do with it?

- 📈 **Track daily Copilot adoption** — feed the **AI-in-One** dashboard with a fresh pull every day, automatically.
- 💰 **Measure Copilot ROI** — feed the **AI Business Value** dashboard with business-impact metrics.
- 📊 **Analyze broader M365 usage** — feed the **M365 Usage Analytics** dashboard beyond just Copilot.
- 👥 **Export your user directory** — pull Microsoft Entra user details to enrich your reports.
- 🧩 **Build custom collections** — configure exactly what you need for any reporting system.

---

## Dashboard templates

PAX Cookbook's output feeds the team's ready-made Power BI dashboard templates:

- **AI-in-One** — a comprehensive Copilot adoption view
- **AI Business Value** — ROI and business-impact metrics
- **M365 Usage Analytics** — broader Microsoft 365 usage

The output is structured and standard, so it also works with **any reporting tool** that can read data files — not just Power BI.

> 📸 **Screenshot:** A Power BI dashboard populated with PAX Cookbook data (or the PAX Cookbook home page if a dashboard image isn't available).

---

## Quick Start

1. **Download** `PAX_Cookbook_Setup.exe` from the [latest Release](https://github.com/microsoft/PAX-Cookbook/releases/latest).
2. **Double-click** it. The wizard installs the components it needs (.NET 8, PowerShell 7, Python) for you.
3. **Open** PAX Cookbook from the Start Menu.
4. Go to **Recipes → New Recipe**. Pick a name and the **AI-in-One** preset, choose **Device Code** sign-in and **Previous Day**, pick a local output folder, and **Save**.
5. Open the recipe and click **Bake**. Confirm with Windows Hello and watch it collect your data.
6. Open the output folder — your data is ready for Power BI.

That's it. For the full walkthrough, see the **[User Guide](docs/PAX-Cookbook-User-Guide.md)**.

> 📸 **Screenshot:** The Setup wizard Welcome screen.

> 📸 **Screenshot:** The recipe builder with the AI-in-One preset selected.

> 📸 **Screenshot:** A completed bake showing its output files.

---

## Documentation

- 📖 **[Full User Guide](docs/PAX-Cookbook-User-Guide.md)** — complete, step-by-step instructions for every feature.
- ⚙️ **[PAX engine](https://github.com/microsoft/PAX)** — the open-source tool that powers data collection.

---

## Installation

**Requires Windows 10 or 11 (64‑bit).** The wizard installs the other prerequisites (.NET 8, PowerShell 7, Python) for you.

1. Download **`PAX_Cookbook_Setup.exe`** from the [latest Release](https://github.com/microsoft/PAX-Cookbook/releases/latest).
2. Double-click to run the wizard. It checks for and installs the prerequisites (.NET 8, PowerShell 7, Python) automatically.
3. Follow the prompts to finish, then launch from the Start Menu.

> The installer isn't code-signed yet, so Windows SmartScreen may show "Windows protected your PC." Click **More info → Run anyway** to continue.

For detailed installation help — including system requirements, uninstalling, and updating — see the **[User Guide](docs/PAX-Cookbook-User-Guide.md#2-installation)**.

---

## Alternative installation (locked-down / managed PCs)

Some organizations run strict security policies (Microsoft Defender Application Control / WDAC, or similar) that **hard-block brand-new, not-yet-signed apps** — sometimes with no "Run anyway" option at all. During PAX Cookbook's preview period the `PAX_Cookbook_Setup.exe` installer **isn't code-signed yet**, so on those managed machines the normal Setup can be blocked.

If that's your situation, there's a fully supported **manual installation path** that avoids the Setup `.exe` entirely. PAX Cookbook runs on Microsoft's own signed `.NET` host, so you can install the free Microsoft prerequisites, download the app's data payload, and run a small setup script — no blocked program involved.

👉 **[Alternative installation instructions →](Alternative_Installation_Instructions/README.md)**

> **Why this exists (and for how long):** this manual path is a **temporary bridge** for locked-down environments while we're in testing and preview. Once code signing is in place — which we're targeting **before General Availability** — the standard signed installer will run everywhere and this alternative path will no longer be needed.

---

## Building from source

<details>
<summary>For contributors and developers</summary>

**Prerequisites**

- .NET 8 SDK
- Node.js 18+

**Build the app**

```powershell
# Build the .NET solution
dotnet build PAXCookbook.sln -c Release

# Build the web UI
cd app/web-react
npm ci
npm run build
```

**Build the installer**

```powershell
pwsh -File tools/release/Build-Setup.ps1
```

This produces the distributable `PAX_Cookbook_Setup.exe` and `PAX_Cookbook_Payload.zip` under `dist/setup/`.

</details>

---

## License

PAX Cookbook is licensed under the **MIT License**. See [LICENSE](LICENSE) for details.

---

## Links

- 🌐 **Website:** https://PAXcookbook.com
- ⚙️ **PAX engine:** https://github.com/microsoft/PAX
- 🐛 **Report issues:** https://github.com/microsoft/PAX-Cookbook/issues
