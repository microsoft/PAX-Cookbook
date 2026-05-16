# PAX Cookbook

**The Operational Experience Layer for PAX**

> **Coming soon.** PAX Cookbook is in active development. This repository is a public preview of the project's direction and operational philosophy ahead of its initial release.

PAX Cookbook transforms the [PAX Purview Audit Log Processor PowerShell script](https://github.com/Microsoft/PAX) from a powerful command-line tool into a guided, repeatable, enterprise-friendly operational experience — without changing the underlying PAX Purview Audit Log Processor PowerShell script at all.

Built as a local-first orchestration shell around PAX, Cookbook simplifies complex data collection workflows through recipes, guided execution, operational visibility, and automation-ready workflows while preserving the full power and transparency of native PAX execution.

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

---

## Guided Operational Experience

Cookbook replaces long command examples and documentation-heavy workflows with a clean guided experience built around five simple steps:

1. **What** — choose the data to collect
2. **When** — choose the time window
3. **Where** — choose the output destination
4. **Advanced** — tune optional behavior
5. **Cook** — run the recipe

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
- operational history
- recipe management

Every cook shows the exact native PAX command being executed.

---

## Real-Time Cook Visibility

Watch cooks execute live through an embedded terminal experience.

Track:

- execution progress
- validation
- warnings
- failures
- runtime metrics
- operational logs

Cook History provides searchable operational visibility across prior runs.

---

## Dashboard-Aligned Templates

Cookbook includes guided templates aligned to Microsoft reporting ecosystems powered by PAX.

Initial templates include:

- **M365 Usage Analytics Dashboard**
- **AI-in-One Dashboard**

Templates accelerate setup while still allowing full customization.

---

## Enterprise-Friendly by Design

Cookbook is intentionally designed to minimize security and deployment friction.

No:

- installers
- services
- daemons
- package managers
- browser extensions
- cloud-hosted infrastructure

Cookbook runs locally using:

- PowerShell 7+
- Python 3.9+
- a localhost browser experience

The operational model is intentionally simple:

1. Download
2. Run PowerShell
3. Launch Cookbook
4. Start cooking

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
- visibility into execution history
- automation-ready operational foundations

…without sacrificing the flexibility and power that made PAX valuable in the first place.

---

## Current Status

PAX Cookbook is in active development.

When the initial release is published, it will follow the same operational model described above — download, unzip, run from PowerShell, start cooking — with the orchestration layer remaining intentionally thin and PAX remaining authoritative.
