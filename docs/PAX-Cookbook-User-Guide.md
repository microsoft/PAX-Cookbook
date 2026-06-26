<p align="center">
  <a href="https://microsoft.github.io/PAX-Cookbook">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="../images/pax-cookbook-logo-readme-dark.png">
      <source media="(prefers-color-scheme: light)" srcset="../images/pax-cookbook-logo-readme.png">
      <img alt="PAX Cookbook" src="../images/pax-cookbook-logo-readme.png" width="400">
    </picture>
  </a>
</p>

# PAX Cookbook — User Guide

<p align="center"><strong>PAX Cookbook v1.1.0</strong> · Last updated: June 23, 2026</p>

Welcome to **PAX Cookbook**. This guide explains everything you need to collect Microsoft 365 Copilot adoption and usage data and turn it into reports — even if you have never written a line of code.

If you just want to get started quickly, begin with the [Quick Start](#1-quick-start). If you want to understand a specific feature, jump to it using the table of contents below.

---

## Table of Contents

- [1. Quick Start](#1-quick-start)
  - [1.1 What is PAX Cookbook?](#11-what-is-pax-cookbook)
  - [1.2 What is PAX?](#12-what-is-pax)
  - [1.3 What you'll need before you start](#13-what-youll-need-before-you-start)
  - [1.4 Install PAX Cookbook](#14-install-pax-cookbook)
  - [1.5 Create your first recipe](#15-create-your-first-recipe)
  - [1.6 Run your first bake](#16-run-your-first-bake)
  - [1.7 Next steps](#17-next-steps)
- [2. Installation](#2-installation)
  - [2.1 System requirements](#21-system-requirements)
  - [2.2 Downloading the installer](#22-downloading-the-installer)
  - [2.3 Running the Setup wizard](#23-running-the-setup-wizard)
  - [2.4 Uninstalling](#24-uninstalling)
  - [2.5 Updating](#25-updating)
- [3. Getting Started: Understanding the App](#3-getting-started-understanding-the-app)
  - [3.1 The home page](#31-the-home-page)
  - [3.2 Navigation](#32-navigation)
  - [3.3 The system tray icon](#33-the-system-tray-icon)
- [4. Recipes: Building Your Data Collection](#4-recipes-building-your-data-collection)
  - [4.1 What is a recipe?](#41-what-is-a-recipe)
  - [4.2 Creating a new recipe](#42-creating-a-new-recipe)
    - [4.2.1 Step 1: Basics](#421-step-1-basics)
    - [4.2.2 Step 2: Authentication](#422-step-2-authentication)
    - [4.2.3 Step 3: Date Range](#423-step-3-date-range)
    - [4.2.4 Step 4: Audit Operations](#424-step-4-audit-operations)
    - [4.2.5 Step 5: Output](#425-step-5-output)
    - [4.2.6 Step 6: Schedule](#426-step-6-schedule)
    - [4.2.7 Step 7: Review + Save](#427-step-7-review--save)
  - [4.3 Editing a recipe](#43-editing-a-recipe)
  - [4.4 Deleting a recipe](#44-deleting-a-recipe)
- [5. Bakes: Running and Monitoring Data Collection](#5-bakes-running-and-monitoring-data-collection)
  - [5.1 What is a bake?](#51-what-is-a-bake)
  - [5.2 Running a bake manually](#52-running-a-bake-manually)
  - [5.3 Bake history](#53-bake-history)
  - [5.4 Bake detail view](#54-bake-detail-view)
  - [5.5 Scheduled bakes](#55-scheduled-bakes)
  - [5.6 Troubleshooting bakes](#56-troubleshooting-bakes)
- [6. Pantry: Browsing Project Files](#6-pantry-browsing-project-files)
  - [6.1 What is the Pantry?](#61-what-is-the-pantry)
  - [6.2 Browsing files](#62-browsing-files)
- [7. Chef's Keys: Managing Authentication](#7-chefs-keys-managing-authentication)
  - [7.1 What are Chef's Keys?](#71-what-are-chefs-keys)
  - [7.2 Creating a new key](#72-creating-a-new-key)
  - [7.3 Editing a key](#73-editing-a-key)
  - [7.4 Deleting a key](#74-deleting-a-key)
  - [7.5 Security: how your credentials are protected](#75-security-how-your-credentials-are-protected)
- [8. Settings](#8-settings)
  - [8.1 Auto-start at login](#81-auto-start-at-login)
  - [8.2 Check for updates](#82-check-for-updates)
  - [8.3 About / version info](#83-about--version-info)
- [9. Help](#9-help)
  - [9.1 In-app help](#91-in-app-help)
  - [9.2 Where to get additional help](#92-where-to-get-additional-help)
- [10. Typical Scenarios](#10-typical-scenarios)
  - [10.1 Daily Copilot adoption tracking](#101-daily-copilot-adoption-tracking)
  - [10.2 One-time historical backfill](#102-one-time-historical-backfill)
  - [10.3 Multiple dashboards from one tenant](#103-multiple-dashboards-from-one-tenant)
  - [10.4 Multi-tenant data collection](#104-multi-tenant-data-collection)
  - [10.5 Feeding Power BI dashboards](#105-feeding-power-bi-dashboards)
- [11. Troubleshooting](#11-troubleshooting)
  - [11.1 Common issues and solutions](#111-common-issues-and-solutions)
  - [11.2 The app won't start](#112-the-app-wont-start)
  - [11.3 Bakes fail with authentication errors](#113-bakes-fail-with-authentication-errors)
  - [11.4 Output files are empty](#114-output-files-are-empty)
  - [11.5 Scheduled bakes aren't running](#115-scheduled-bakes-arent-running)
  - [11.6 SmartScreen blocks the installer](#116-smartscreen-blocks-the-installer)
  - [11.7 Prerequisites won't install](#117-prerequisites-wont-install)
- [12. Glossary](#12-glossary)

---

## 1. Quick Start

This section gets you from zero to your first set of data in about 15 minutes. Follow it top to bottom.

### 1.1 What is PAX Cookbook?

PAX Cookbook is a Windows app that collects Microsoft 365 Copilot adoption and usage data from your organization's Microsoft Purview audit logs and prepares it for Power BI dashboards — all through a friendly, guided experience with no scripting required. It is built for analysts, IT admins, project managers, and business leaders who need to measure how Copilot is being adopted and what value it delivers. Behind the scenes, it wraps a powerful tool called the **PAX Purview Audit Log Processor** in a visual, point-and-click experience, so you get all of its capability without having to learn its commands.

### 1.2 What is PAX?

**PAX** (the PAX Purview Audit Log Processor) is the underlying engine that does the actual data collection. It is an open-source tool that knows how to talk to Microsoft 365, pull the right audit and usage records, and shape them into clean output files. PAX Cookbook drives PAX for you — you make choices in a guided builder, and PAX Cookbook turns those choices into the correct PAX command and runs it.

You can learn more about the engine here: **https://github.com/microsoft/PAX**

### 1.3 What you'll need before you start

Before your first run, make sure you have:

- **A Microsoft 365 tenant with Purview audit logging turned on.** This is where your Copilot usage data lives. If audit logging is off, there will be no data to collect.
- **A way to sign in to your tenant's data.** The simplest option is **Device Code** sign-in, which just asks you to enter a code in your browser. For automated/scheduled runs you'll use an **Entra ID app registration** (your IT team can help create one). More on this in [Authentication](#422-step-2-authentication).
- **The PAX Cookbook installer.** Download it from the [Releases page](https://github.com/microsoft/PAX-Cookbook/releases/latest).

### 1.4 Install PAX Cookbook

1. Go to the [latest Release](https://github.com/microsoft/PAX-Cookbook/releases/latest) and download **`PAX_Cookbook_Setup.exe`**.
2. **Double-click** the file to start the setup wizard.
3. The wizard checks for the three tools PAX Cookbook needs — **.NET 8**, **PowerShell 7**, and **Python** — and installs any that are missing. (These are standard Microsoft components; they are explained in plain English in [Running the Setup wizard](#23-running-the-setup-wizard).)
4. Choose where to install (the default is fine for most people) and let the wizard finish.
5. When it's done, open **PAX Cookbook** from the **Start Menu**.

> **A note about the SmartScreen warning:** The installer is not yet code-signed, so Windows may show a blue "Windows protected your PC" message. Click **More info**, then **Run anyway** to continue. This is expected.

> 📸 **Screenshot:** The Setup wizard Welcome screen, showing the PAX Cookbook logo, the app description, and the Next button.

> 📸 **Screenshot:** The Setup wizard Prerequisites screen, showing the .NET 8, PowerShell 7, and Python checks with their status indicators.

> 📸 **Screenshot:** The Setup wizard Install location screen, showing the default install folder and the Browse button.

> 📸 **Screenshot:** The Setup wizard Progress screen, showing the installation progress bar partway through.

> 📸 **Screenshot:** The Setup wizard Complete screen, showing the "Launch PAX Cookbook now" and "Start at login" options.

### 1.5 Create your first recipe

A **recipe** is a saved set of choices that tells PAX Cookbook what to collect and how. Let's make a simple one.

1. Open PAX Cookbook and go to **Recipes**, then click **New Recipe**.
2. Walk through the seven steps. For your very first recipe, use these recommended settings:
   - **Step 1 — Basics:** Give it a name like "My First Copilot Pull" and choose the **AI-in-One** dashboard preset. This is the best starting point for most people.
   - **Step 2 — Authentication:** Choose **Device Code**. It's the simplest way to sign in for a first test.
   - **Step 3 — Date Range:** Choose **Previous Day**. This pulls yesterday's data, which is a quick, reliable test.
   - **Step 4 — Audit Operations:** Leave the defaults. The preset already filled these in for you.
   - **Step 5 — Output:** Choose a **local folder** on your computer where the results should be saved.
   - **Step 6 — Schedule:** Skip this for now (leave it on "manual"). You'll run it by hand first.
   - **Step 7 — Review + Save:** Review your choices and click **Save**.

> 📸 **Screenshot:** Step 1 Basics, with the name filled in and the AI-in-One preset selected.

> 📸 **Screenshot:** Step 2 Authentication, with Device Code selected.

> 📸 **Screenshot:** Step 3 Date Range, with Previous Day selected.

> 📸 **Screenshot:** Step 4 Audit Operations, showing the preset's default selections.

> 📸 **Screenshot:** Step 5 Output, with a local folder chosen.

> 📸 **Screenshot:** Step 6 Schedule, with "manual / no schedule" selected.

> 📸 **Screenshot:** Step 7 Review + Save, showing the summary of all choices and the Save button.

### 1.6 Run your first bake

A **bake** is a single run of a recipe.

1. Go to **Recipes** and click your new recipe to open it.
2. Click **Bake**. Windows will ask you to confirm with **Windows Hello** (your PIN, fingerprint, or face) — this is a safety check to confirm you meant to run it.
3. Watch the progress as PAX Cookbook signs in, collects the data, and saves the output.
4. When it finishes, open the output folder to see your files.

> 📸 **Screenshot:** A recipe open with the Bake button highlighted.

> 📸 **Screenshot:** A bake in progress, showing the live status and log.

> 📸 **Screenshot:** A completed bake, showing the success status and the list of output files.

> 📸 **Screenshot:** A Windows File Explorer window showing the output files produced by the bake.

### 1.7 Next steps

That's it — you've collected your first set of Copilot data. From here, explore the rest of this guide to:

- Customize what you collect ([Recipes](#4-recipes-building-your-data-collection))
- Run collections automatically on a schedule ([Schedule](#426-step-6-schedule) and [Scheduled bakes](#55-scheduled-bakes))
- Send data straight to SharePoint or Microsoft Fabric ([Output](#425-step-5-output))
- Connect the output to Power BI dashboards ([Feeding Power BI dashboards](#105-feeding-power-bi-dashboards))

---

## 2. Installation

### 2.1 System requirements

To run PAX Cookbook you need:

- **Windows 10 or Windows 11 (64‑bit).**
- **.NET 8, PowerShell 7, and Python.** These are standard Microsoft components; the Setup wizard checks for them and installs any that are missing (see [Running the Setup wizard](#23-running-the-setup-wizard)).
- **Microsoft Edge WebView2 Runtime.** Ships with Windows 11 and on most Windows 10 PCs; installed automatically if missing.
- **An internet connection.** This is needed to download the components during setup and to collect data from Microsoft 365.
- **A Microsoft 365 tenant with Purview audit logging enabled.** This is the source of your data.

PAX Cookbook installs just for your user account and does not require administrator rights for the app itself.

### 2.2 Downloading the installer

Go to the [Releases page](https://github.com/microsoft/PAX-Cookbook/releases/latest) and download **`PAX_Cookbook_Setup.exe`**. That single file is all you need — it pulls down everything else it requires during installation.

### 2.3 Running the Setup wizard

Double-click `PAX_Cookbook_Setup.exe` to launch the wizard. It walks you through five screens:

**Welcome screen.** Introduces the app and what it does. Click **Next** to begin.

> 📸 **Screenshot:** The Welcome screen with the description text and Next button.

**Prerequisites screen.** PAX Cookbook relies on three standard Microsoft tools. The wizard checks for each one and installs any that are missing:

- **.NET 8** — a standard Microsoft software component that PAX Cookbook needs in order to run.
- **PowerShell 7** — a modern automation tool from Microsoft that PAX uses to do its work.
- **Python** — used to build the dashboard-ready summary files for the Power BI presets.

You don't need to know how these work — the wizard handles them for you.

> 📸 **Screenshot:** The Prerequisites screen showing all three tools with checkmarks or "installing" status.

**Install location screen.** Choose where to install. The default location is recommended unless you have a specific reason to change it.

> 📸 **Screenshot:** The Install location screen with the default path shown.

**Progress screen.** Shows what's happening — downloading components, copying files, and finishing setup. No action needed; just wait.

> 📸 **Screenshot:** The Progress screen during installation.

**Complete screen.** Confirms the install is done. You can choose to **launch the app now** and to **start it automatically at login** (recommended if you plan to use scheduled collections).

> 📸 **Screenshot:** The Complete screen with the launch and auto-start options.

### 2.4 Uninstalling

To remove PAX Cookbook, do either of the following:

- Open Windows **Settings → Apps → Installed apps**, find **PAX Cookbook**, and choose **Uninstall**; or
- Run `PAX_Cookbook_Setup.exe` again and choose the uninstall option.

The app files are removed. Your collected output files (saved wherever you chose) are **not** touched — they stay on your computer.

> 📸 **Screenshot:** The Windows "Installed apps" list with PAX Cookbook and its Uninstall option visible.

### 2.5 Updating

PAX Cookbook checks for updates automatically each time it starts. When a new version is available, an **Update available** link appears in the footer, and the **Updates** page (in the navigation sidebar) shows what's new. Applying an update is a single, explicit action — PAX Cookbook verifies the download before installing it and never updates silently. If a scheduled bake is due soon, the app warns you before applying so you can pick a better time. Your recipes, keys, and history are preserved across updates.

> 📸 **Screenshot:** The Updates page showing an available version with the details of what's new and the Apply control.

---

## 3. Getting Started: Understanding the App

### 3.1 The home page

When you open PAX Cookbook, the home page gives you an at-a-glance view of everything that matters:

- **Last Bake summary** — how your most recent collection run went.
- **Recent Recipes** — quick links to the recipes you use most.
- **What needs attention** — anything that needs a fix, such as a failed run or a sign-in that needs renewing.
- **Recent Outputs** — the latest files produced, each with quick links to **Open folder**, **Open log**, and **Download log**.
- **System health indicator** — a simple signal that the app and its background service are running normally.

> 📸 **Screenshot:** The home page with labels pointing to the Last Bake summary, Recent Recipes, What needs attention, Recent Outputs, and the system health indicator.

### 3.2 Navigation

A sidebar on the left lets you move around the app. Its sections are:

- **Home** — the overview page described above.
- **Recipes** — build, edit, and run your data collections.
- **Bakes** — the history of every run.
- **Pantry** — browse the project's reference files and dashboard documentation.
- **Chef's Keys** — manage your sign-ins.
- **Updates** — check for and apply new versions of PAX Cookbook.
- **Settings** — app options.
- **Help** — in-app guidance.

Click any item to go to that section.

> 📸 **Screenshot:** The left sidebar with all navigation items visible (Home, Recipes, Bakes, Pantry, Chef's Keys, Updates, Settings, Help).

### 3.3 The system tray icon

PAX Cookbook keeps a small icon in the Windows system tray (the area near the clock). This icon represents the **background service** that lets scheduled collections run even when the main window is closed.

- **Right-click the icon** to open a menu with options such as opening the app or exiting.
- **Closing the main window** asks whether you want to **Minimize to tray** (keep the background service running so scheduled runs still happen) or **Exit** (stop the background service completely).
- If you enabled **Start at login**, the background service starts automatically each time you sign in to Windows, so your scheduled collections are always ready to run.

> 📸 **Screenshot:** The system tray icon with its right-click menu open.

> 📸 **Screenshot:** The close dialog showing the "Minimize to tray" and "Exit" choices.

---

## 4. Recipes: Building Your Data Collection

### 4.1 What is a recipe?

A **recipe** is a saved configuration. It records four things:

1. **What** data to collect,
2. **How** to sign in,
3. **Where** to save the results, and
4. **When** to run (manually, or on a schedule).

Once you save a recipe, you can run it again and again — by hand or automatically — and get consistent results every time.

### 4.2 Creating a new recipe

From the **Recipes** page, click **New Recipe** to open the seven-step builder. Each step is explained below.

**Already have a recipe file?** The New Recipe options also let you **import** a recipe someone shared with you. Choose **Import PAX Cookbook recipe (`.pax`)** or **Import Mini-Kitchen recipe (`.paxlite`)**, pick the file from the browse dialog, and the builder opens pre-filled with that recipe's settings — ready for you to review and save.

> 📸 **Screenshot:** The New Recipe screen showing the dashboard presets alongside the Import `.pax` and Import `.paxlite` options.

#### 4.2.1 Step 1: Basics

Give your recipe a clear **name**, then choose a **dashboard preset**. The preset is a shortcut that fills in sensible defaults for a specific reporting goal:

- **AI-in-One** — the comprehensive Copilot adoption view. Best for a complete picture of how Copilot is being used. Recommended for first-timers.
- **AI Business Value** — focuses on return-on-investment and business impact metrics. Produces a wider set of columns designed for the AI Business Value dashboard.
- **M365 Usage Analytics** — broader Microsoft 365 usage, beyond just Copilot.
- **Entra Directory Export** — exports user directory information (from Microsoft Entra ID), useful for enriching usage data with details about who your users are.
- **Custom** — start from a blank slate and choose everything yourself. Best for advanced or unusual needs.

> 📸 **Screenshot:** Step 1 Basics, with the recipe name field and the dashboard preset selector.

> 📸 **Screenshot:** The dashboard preset dropdown expanded, showing all preset options with their descriptions.

#### 4.2.2 Step 2: Authentication

To read your organization's data, PAX Cookbook needs permission to sign in to your Microsoft 365 tenant. You choose **how** it signs in here. There are three options:

- **Device Code** — the simplest. When the recipe runs, you'll be shown a short code to type into a browser to approve the sign-in. Great for testing and for runs you start by hand. It is **not** suitable for scheduled runs, because someone has to enter the code each time.
- **App Registration with a Client Secret** — uses an app identity (created in Entra ID) plus a secret password. It runs **unattended**, which is exactly what scheduled runs need.
- **App Registration with a Certificate** — also uses an app identity, but with a certificate instead of a password. This is the most secure option and is recommended for production and automated use.

Your sign-in details are stored as a **Chef's Key** (see [Chef's Keys](#7-chefs-keys-managing-authentication)). You can create the key here or pick an existing one. When you choose an existing Chef's Key, PAX Cookbook automatically fills in the matching tenant for you, so you don't have to type it again.

**Setting up an app registration:** An app registration is created once in the Microsoft Entra admin center and given permission to read audit and usage data. This is usually done by your IT or identity team. For the exact permissions and steps, see Microsoft's documentation on registering an app and assigning Microsoft Graph permissions.

> 📸 **Screenshot:** Step 2 Authentication with Device Code selected.

> 📸 **Screenshot:** Step 2 Authentication with App Registration + Client Secret selected.

> 📸 **Screenshot:** Step 2 Authentication with App Registration + Certificate selected.

#### 4.2.3 Step 3: Date Range

This step controls **which days** of data to collect.

- **Previous Day** — automatically collects yesterday's full day of data, measured in UTC (the previous full UTC day: yesterday 00:00 UTC up to today 00:00 UTC). Because it always means "yesterday," it never needs editing — making it perfect for **daily scheduled runs** and for building up data day by day.
- **Custom date range** — pick a specific **start** and **end** date. Best for **backfills** (collecting a block of historical days) or **one-time** pulls. Note that the end date is treated as exclusive — data is pulled through the day before the end date.

> 📸 **Screenshot:** Step 3 Date Range in Previous Day mode.

> 📸 **Screenshot:** Step 3 Date Range in Custom mode, with the start and end date pickers visible.

#### 4.2.4 Step 4: Audit Operations

**Audit operations** are the specific types of activity records to collect (for example, Copilot interaction events). The dashboard preset you picked in Step 1 already fills these in correctly, so **most people don't need to change anything here**.

If you have advanced needs, you can add or remove specific operations to fine-tune exactly what gets collected.

> 📸 **Screenshot:** Step 4 Audit Operations showing the preset's default selections.

#### 4.2.5 Step 5: Output

Choose **where** the collected data is saved. You have three destinations:

- **Local folder** — saves the files to a folder on your computer. Simple and great for getting started.
- **SharePoint** — uploads the results directly to a SharePoint document library, so they're available to your team.
- **Microsoft Fabric (OneLake)** — writes the results into a Microsoft Fabric Lakehouse for advanced analytics. Both the friendly name-based URL and the GUID-based URL forms are accepted.

Two related choices also live on this step:

- **Rollup mode and dashboard target** — most presets produce dashboard-ready summary files (a *rollup*) shaped for a specific **dashboard target**, such as AI-in-One. The dashboard target is chosen together with the rollup option here, and you'll see it again later on the bake detail view.
- **Hierarchy filler** — when a rollup is on, you can choose what fills empty levels of the org / manager hierarchy: leave them **blank** (the default), **repeat the person**, **repeat their manager**, or stamp a **custom label** you type. It applies only to rollup output and not to the M365 usage bundle.
- **De-identify output** — an optional privacy toggle (off by default) that anonymizes people in both the audit output and the Entra user-info output, and in the rollup built from them. It is one-way: the original identities can't be recovered from the de-identified files. If you append to an existing file, only append to one that is already de-identified.
- **Combined or separate files** — when a recipe collects **two or more** activity types, you can choose whether they're written to one combined file or kept as separate files. With only a single activity type this option doesn't appear, because there's nothing to combine.

> 📸 **Screenshot:** Step 5 Output with a local folder selected.

> 📸 **Screenshot:** Step 5 Output with a SharePoint document library URL entered.

> 📸 **Screenshot:** Step 5 Output with a Microsoft Fabric OneLake URL entered.

#### 4.2.6 Step 6: Schedule

Decide **when** the recipe runs:

- **Manual (no schedule)** — the recipe only runs when you click **Bake**.
- **Scheduled** — PAX Cookbook sets up a recurring run using **Windows Task Scheduler**. At the scheduled time, the app's background service runs the bake automatically — no clicking required.

A powerful combination is **Previous Day + a daily schedule**: every day, the recipe automatically collects the prior day's data, building a continuous, hands-off data pipeline.

For scheduled runs to work, two things must be true: the recipe is saved with a schedule, and **Start PAX Cookbook at login** is turned on (so the background service is running). Scheduled runs also need an **App Registration** Chef's Key, because Device Code requires a person to enter a code.

> 📸 **Screenshot:** Step 6 Schedule with "manual / no schedule" selected.

> 📸 **Screenshot:** Step 6 Schedule with a daily schedule configured.

#### 4.2.7 Step 7: Review + Save

The final step shows a **summary** of everything you configured so you can confirm it's correct.

- **Check Readiness** — click this to have PAX Cookbook verify, before you ever run, that the engine is present, your sign-in works, the output location is reachable, and the necessary permissions are in place. Each result tells you whether that piece is ready or needs attention.
- **Save** — saves the recipe so you can run it.

> 📸 **Screenshot:** Step 7 Review with the Check Readiness results showing each item's status.

> 📸 **Screenshot:** The Save confirmation after saving the recipe.

### 4.3 Editing a recipe

To change a recipe, open it from the **Recipes** page and step through the builder again. You can change any setting — the name, preset, sign-in, dates, output, or schedule. Save your changes when you're done. (Note: after editing, you may need to save before you can run, and a run always re-confirms with Windows Hello.)

### 4.4 Deleting a recipe

To remove a recipe you no longer need, open it (or use its menu on the Recipes page) and choose **Delete**. Deleting a recipe does not delete the output files it already produced.

---

## 5. Bakes: Running and Monitoring Data Collection

### 5.1 What is a bake?

A **bake** is a single run of a recipe. When you bake, PAX Cookbook signs in, collects the data your recipe asks for, and saves the output to your chosen destination. Every bake is recorded so you have a full history.

### 5.2 Running a bake manually

You can start a bake from either the **Recipes** page (open a recipe and click **Bake**) or directly from the **Home** page.

When you start a bake, Windows asks you to confirm with **Windows Hello** (PIN, fingerprint, or face). This appears because running a collection is a meaningful action, and the confirmation makes sure it was intentional.

During the bake, you'll see live progress: signing in, collecting, and saving. You can watch the log update as it goes.

### 5.3 Bake history

The **Bakes** page lists every run, past and present. Each row shows:

- **Status** — whether the run succeeded, failed, or is still running.
- **Recipe name** — which recipe was run.
- **Date** — when it ran.
- **Duration** — how long it took.

You can scroll and search to find a specific bake.

> 📸 **Screenshot:** The Bakes page with several bakes listed, showing the status, recipe name, date, and duration columns.

### 5.4 Bake detail view

Click any bake to open its detail view, which shows:

- The **status**, **timing**, and the **recipe** that was used, including the **dashboard target** the recipe was built for.
- **Output files** — each file produced, with an **Open folder** button to jump straight to where the files were saved.
- **Cook log** — a detailed record of exactly what happened during the run. You have three ways to work with it:
  - **Open log** — opens the log in your default text app.
  - **Copy log** — copies the log text to your clipboard.
  - **Download log** — opens a **Save As** dialog so you can save the log wherever you like.

> The **Open folder**, **Open log**, and **Download log** shortcuts also appear for recent runs in the **Recent Outputs** list on the Home page, so you can reach them without opening the full bake.

> 📸 **Screenshot:** A bake detail view showing the output files list, the Open folder button, and the log card with Open log, Copy log, and Download log.

### 5.5 Scheduled bakes

When a recipe has a schedule, Windows Task Scheduler wakes PAX Cookbook at the scheduled time and hands the run to the **background service** — using the same engine and the same safety checks as a manual bake, just without you clicking anything. Scheduled runs appear in the **Bakes** list right alongside manual ones, so you can confirm they ran.

The **Bakes** page also shows an **Upcoming bakes** card listing every recipe that has a schedule, with its next run time and how often it runs. Select an upcoming bake to manage it:

- **Skip next bake** — skips only the next scheduled run; the schedule keeps going after that.
- **Cancel all future bakes** — removes the recipe's schedule entirely (its Windows scheduled task is unregistered).

The Bakes list refreshes on its own, so a scheduled run appears as it starts and updates while it runs.

If your computer was turned off (or asleep) at the scheduled time, that run is simply skipped; the next scheduled time runs normally. For reliable daily collection, leave the computer on and signed in, with **Start PAX Cookbook at login** enabled.

### 5.6 Troubleshooting bakes

If a bake fails, open its detail view and read the **cook log** — it usually states the reason. Common causes:

- **Authentication failures** — the sign-in didn't work. Check your Chef's Key, and for scheduled runs make sure you're using an App Registration key (not Device Code). See [Bakes fail with authentication errors](#113-bakes-fail-with-authentication-errors).
- **Permission issues** — the app identity may be missing a required permission in Entra ID. Your IT team can confirm the app registration's permissions.
- **Network/connectivity issues** — a dropped connection or blocked endpoint. Check your internet connection and try again.

---

## 6. Pantry: Browsing Project Files

### 6.1 What is the Pantry?

The **Pantry** is a built-in file browser. It lets you explore the PAX project's reference files and dashboard documentation directly inside the app, without having to go hunting on the web.

### 6.2 Browsing files

In the Pantry you can:

- **Navigate folders** by clicking into them.
- **Preview files** — images, PDFs, text files, and code all display right in the window.
- **Save As** — download a file to your own computer when you want a local copy.

> 📸 **Screenshot:** The Pantry with a folder expanded in the file tree.

> 📸 **Screenshot:** The Pantry showing a file preview of a document.

---

## 7. Chef's Keys: Managing Authentication

### 7.1 What are Chef's Keys?

**Chef's Keys** are the saved sign-ins your recipes use to reach Microsoft 365 data. Each key stores one sign-in method:

- **Device Code** — interactive; prompts for a code each run.
- **App Registration with a Client Secret** — unattended; uses an app identity and a secret.
- **App Registration with a Certificate** — unattended; uses an app identity and a certificate (most secure).

A key stays on your computer. You create a key once, then attach it to one or more recipes in the [Authentication](#422-step-2-authentication) step.

> 📸 **Screenshot:** The Chef's Keys page with one or more keys listed.

### 7.2 Creating a new key

On the **Chef's Keys** page, choose to add a new key, pick the sign-in method, and enter the required details (for an app registration, that's the tenant, the application ID, and the secret or certificate). Save the key to make it available to your recipes.

> 📸 **Screenshot:** The create-key dialog with the sign-in method options.

### 7.3 Editing a key

Open an existing key to update its details — for example, to rotate a secret or swap a certificate. Save your changes when done.

> 📸 **Screenshot:** The edit-key dialog.

### 7.4 Deleting a key

Remove a key you no longer use. Make sure no recipe still depends on it first, or those recipes will need a new key assigned.

### 7.5 Security: how your credentials are protected

Your secrets are protected by the **Windows credential store** and are encrypted on your computer. They are **never shown** back to you in the app once saved, and they never leave your machine. This keeps sensitive sign-in information safe.

**Confirming it's you.** PAX Cookbook uses **Windows Hello** (your PIN, fingerprint, or face) to confirm it's you at a few key moments: when the app starts, when you reopen it from the system tray, and before each manual bake. It does **not** lock itself while you're working — once you're in, the app stays unlocked the whole time it's open, even if you step away from the computer.

---

## 8. Settings

### 8.1 Auto-start at login

Turn on **Start PAX Cookbook at login** so the background service is always running when you sign in to Windows. This is required for scheduled collections to run reliably.

### 8.2 Check for updates

PAX Cookbook checks for updates automatically on startup. To check or apply one yourself, open the **Updates** page from the navigation sidebar (or click the **Update available** link in the footer when one is found). See [Updating](#25-updating) for the full flow. Your recipes, keys, and history are kept across updates.

### 8.3 About / version info

The **About** area shows the version of PAX Cookbook you're running and details about the underlying PAX engine. This is helpful when reporting an issue.

> 📸 **Screenshot:** The Settings page showing the auto-start option, the update control, and the version/about information.

---

## 9. Help

### 9.1 In-app help

Throughout the app you'll find small help icons and tips that explain each option right where you need it. Look for these contextual hints on every step of the recipe builder and on each page.

> 📸 **Screenshot:** A contextual help popover open next to a recipe builder field.

### 9.2 Where to get additional help

If you need more help:

- **This User Guide** is the complete reference.
- **GitHub Issues** — report a bug or ask a question at https://github.com/microsoft/PAX-Cookbook/issues
- **The PAX engine** — learn about the underlying tool at https://github.com/microsoft/PAX

---

## 10. Typical Scenarios

### 10.1 Daily Copilot adoption tracking

**Goal:** Automatically track Copilot adoption every day.

1. Create a recipe with the **AI-in-One** preset.
2. Set the date range to **Previous Day**.
3. Set a **daily schedule** (Step 6).
4. Use an **App Registration** Chef's Key so it can run unattended.
5. Choose an output destination — a **local folder** or **SharePoint**.
6. Make sure **Start PAX Cookbook at login** is on.

Each day, yesterday's data is collected automatically. Point Power BI at the output to keep your dashboard current.

### 10.2 One-time historical backfill

**Goal:** Collect a block of past days once.

1. Create a recipe with the preset you want.
2. Set the date range to **Custom** and pick your start and end dates.
3. Leave the schedule on **manual**.
4. Click **Bake** to run it once.

### 10.3 Multiple dashboards from one tenant

**Goal:** Feed several different dashboards from the same tenant.

Create a **separate recipe for each preset** you need (for example, one AI-in-One recipe and one M365 Usage Analytics recipe). Each produces output tailored to its dashboard.

### 10.4 Multi-tenant data collection

**Goal:** Collect data from more than one Microsoft 365 tenant.

Create a **separate recipe for each tenant**, each with its **own Chef's Key** that signs in to the right tenant. Keep the output destinations separate so the data doesn't mix.

### 10.5 Feeding Power BI dashboards

The output files PAX Cookbook produces are structured and ready for reporting. To use them in Power BI:

1. Open Power BI Desktop.
2. Connect to your output location (the local folder, SharePoint library, or Fabric Lakehouse you chose).
3. Load the data and build (or refresh) your report.

The team maintains ready-made Power BI dashboard templates that these outputs feed directly — including **AI-in-One**, **AI Business Value**, and **M365 Usage Analytics**. The output also works with any reporting tool that can read structured files, not just Power BI.

> 📸 **Screenshot:** A Power BI dashboard populated with data collected by PAX Cookbook (or the app's home page if a dashboard image isn't available).

---

## 11. Troubleshooting

### 11.1 Common issues and solutions

Most problems fall into a few categories below. For any failed bake, the **cook log** in the bake detail view is the best first place to look — it usually states the exact reason.

### 11.2 The app won't start

- Make sure setup finished successfully. If in doubt, run `PAX_Cookbook_Setup.exe` again to repair the installation.
- Confirm the prerequisites installed (.NET 8, PowerShell 7, Python). Re-running setup will install anything missing.
- Restart your computer and try again.

### 11.3 Bakes fail with authentication errors

- Confirm your **Chef's Key** details are correct (tenant, application ID, and the secret or certificate).
- For **scheduled** runs, use an **App Registration** key — Device Code can't run unattended.
- Ask your IT team to confirm the app registration has the required permissions in Entra ID.
- If a secret expired, update the key with a new secret.

### 11.4 Output files are empty

- The date range may have no data. With **Previous Day**, confirm there was activity yesterday.
- Confirm Purview **audit logging is enabled** in your tenant and that data exists for the period.
- Check that the sign-in identity has permission to read the audit data.

### 11.5 Scheduled bakes aren't running

- Confirm the recipe is **saved with a schedule**.
- Confirm **Start PAX Cookbook at login** is turned on so the background service is running.
- Confirm the computer was **on and signed in** at the scheduled time.
- Confirm the recipe uses an **App Registration** Chef's Key.

### 11.6 SmartScreen blocks the installer

Windows may show "Windows protected your PC" because the installer isn't code-signed yet. Click **More info**, then **Run anyway**. This is expected and safe for this installer.

### 11.7 Prerequisites won't install

- Make sure you have an **internet connection** during setup — the components are downloaded.
- If installing PowerShell 7 prompts for administrator approval, accept it (or have an admin run setup).
- If a component still won't install, you can install it manually and then re-run setup.

---

## 12. Glossary

- **Recipe** — a saved configuration that defines what data to collect, how to sign in, where to save it, and when to run.
- **Bake** — a single run of a recipe. It collects the data and saves the output.
- **Pantry** — the built-in file browser for the project's reference files and dashboard documentation.
- **Chef's Key** — a saved sign-in (Device Code, app registration secret, or app registration certificate) that a recipe uses to reach Microsoft 365 data. Secrets are encrypted and never displayed.
- **Dashboard preset** — a starting template (AI-in-One, AI Business Value, M365 Usage Analytics, Entra Directory Export, or Custom) that fills in sensible defaults for a specific reporting goal.
- **Previous Day mode** — a date setting that automatically collects the previous full UTC day, ideal for daily scheduled runs.
- **Custom date range** — a date setting where you pick specific start and end dates, ideal for backfills and one-time pulls.
- **Audit operations** — the specific types of activity records collected from Purview (the dashboard preset fills these in for you).
- **Check Readiness** — a pre-run check that confirms the engine, sign-in, output location, and permissions are all ready before you bake.
- **Background service** — the part of PAX Cookbook that keeps running in the system tray so scheduled bakes can fire even when the main window is closed.
- **Cook log** — the detailed record of what happened during a bake, used to confirm success or investigate a failure.
- **Taste Test** — a planned validation check for confirming a recipe or sign-in works as expected. The Taste Tests area is reserved in the app today and lights up in a later release.
- **PAX** — the PAX Purview Audit Log Processor, the open-source engine that performs the actual data collection. PAX Cookbook drives it for you.
- **Rollup** — the step that turns raw audit data into dashboard-friendly summary files (required for the Power BI dashboard presets; uses Python).
- **Output destination** — where results are saved: a local folder, a SharePoint document library, or a Microsoft Fabric (OneLake) Lakehouse.
