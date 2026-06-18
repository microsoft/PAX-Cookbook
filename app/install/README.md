# app/install — scheduled-task registration

Install, update, repair, and uninstall of PAX Cookbook run through **PAX Cookbook
Setup** (`PAXCookbookSetup`, the framework-dependent Setup assembly executed via
the Microsoft-signed `dotnet.exe` — see `src/PAXCookbookSetup`). The Setup
project's `install` / `update` / `repair` / `uninstall` verbs are the only
install surface. The legacy single-script PowerShell installer that used to live
in this folder has been removed.

This folder now contains only the Windows Task Scheduler registration scripts —
the only place in the codebase permitted to call `*-ScheduledTask` cmdlets:

| File | Purpose |
| --- | --- |
| `Register-PAXScheduledRecipe.ps1` | Native Windows Task Scheduler registrar for the per-recipe scheduling surface. Invoked by the C# runtime (the `…/api/v1/recipes/{id}/scheduled-task` routes) to register, query, and remove a recipe's scheduled task. The registered task launches the app via the signed `dotnet.exe "PAX Cookbook.dll"`. |
| `Register-PAXScheduler.ps1` | Earlier daily-scheduler registrar: registers (or unregisters) a single Windows task that fires `launcher/Invoke-ScheduledCook.ps1` once per day, which POSTs to the broker's cook route exactly as a manual browser would. |

Both scripts ship inside the release payload under `App\install\`. The broker
itself has zero scheduled-task runtime; all `*-ScheduledTask` cmdlet usage is
isolated to these two scripts (enforced by the Phase AB harness contract scan).
