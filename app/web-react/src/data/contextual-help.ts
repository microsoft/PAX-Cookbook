/**
 * Centralized copy for the PAX Cookbook contextual help popovers.
 *
 * Each topic answers, in plain language:
 *   - What does this mean?
 *   - What should the user do?
 *   - What should the user not assume?
 *
 * Keep every body to 1-3 short sentences. These popovers are quick guidance
 * shown beside a label, header, or field - they are not FAQ articles, and they
 * must not duplicate the Help / FAQ section. No broker, IPC, argv, schema, or
 * WebAuthn jargon - this copy is for everyday users.
 *
 * The first block of ids is transplanted verbatim from the Mini-Kitchen
 * contextual-help kit so the guided builder keeps its identical popovers. The
 * second block adds full-Cookbook shell topics for the product shell screens.
 */

export type ContextualHelpTopicId =
  // Main sections
  | 'savedRecipes'
  | 'recipeBuilder'
  | 'reviewGeneratedCommand'
  | 'saveExportImport'
  // Builder steps
  | 'stepStartWithRecipe'
  | 'stepNameRecipe'
  | 'stepDefineAuditPull'
  | 'stepChooseOutputAuth'
  | 'stepAdvancedOptions'
  // Key builder options
  | 'starterPreset'
  | 'recipeName'
  | 'dataScope'
  | 'whatToCollect'
  | 'includeEntraUserInfo'
  | 'excludeCopilotInteraction'
  | 'customActivityTypes'
  | 'dateRange'
  | 'dateRangeScheduling'
  | 'rollup'
  | 'rollupDashboard'
  | 'userIds'
  | 'groupDisplayNames'
  | 'agentFilters'
  | 'agentIds'
  | 'promptFilter'
  | 'outputDestination'
  | 'auditDataOutputMode'
  | 'userInfoOutputMode'
  | 'auditActivityOutput'
  | 'auditOutputPath'
  | 'userInfoOutputPath'
  | 'authMethod'
  | 'webLogin'
  | 'deviceCode'
  | 'appRegistrationSecret'
  | 'appRegistrationCertificate'
  | 'managedIdentity'
  | 'advancedSwitches'
  // Review area
  | 'commandView'
  | 'warnings'
  | 'assumptions'
  | 'permissionsNeeded'
  | 'notes'
  | 'blockedItems'
  | 'graphPermissions'
  | 'runtimeRequirements'
  | 'outputAccess'
  // Help / FAQ
  | 'reportPrivacy'
  // Full Cookbook shell sections
  | 'cookbookHome'
  | 'cookbookPantry'
  | 'cookbookRecipes'
  | 'cookbookBakes'
  | 'cookbookTasteTests'
  | 'cookbookChefsKeys'
  | 'cookbookSettings'
  | 'cookbookUpdates'
  // Full Cookbook product concepts
  | 'cookbookImportRecipe'
  | 'cookbookImportCommand'
  | 'cookbookSaveToCookbook'
  | 'cookbookPaxFileTypes'
  | 'cookbookWindowsHello'
  | 'cookbookLocalStorage'
  | 'cookbookFileAssociations'
  | 'cookbookEngine'
  | 'cookbookStartup'
  | 'cookbookScheduling';

export type ContextualHelpTopic = {
  /** Optional short title shown at the top of the popover. */
  title: string;
  /** 1-3 short, plain-language sentences. */
  body: string;
};

export const CONTEXTUAL_HELP_TOPICS: Record<
  ContextualHelpTopicId,
  ContextualHelpTopic
> = {
  // ---------------------------------------------------------------------------
  // Main sections
  // ---------------------------------------------------------------------------
  savedRecipes: {
    title: 'Saved Recipes',
    body: 'Keep a working set of recipes you can reload, duplicate, or delete. From here you can also save the current recipe, import a .pax or .paxlite file someone shared, or export the current recipe as a full .pax file to keep and reopen later. Saved recipes are stored locally in PAX Cookbook; exported files are portable.',
  },
  recipeBuilder: {
    title: 'Recipe Builder',
    body: 'Choose a starter recipe, fill in the required details, and the builder composes a PAX command from your selections. Building a recipe here does not run anything.',
  },
  reviewGeneratedCommand: {
    title: 'Review generated command',
    body: 'Review the command, warnings, assumptions, notes, and permissions before copying it. The builder composes the command but does not run it; use Check readiness to validate it.',
  },
  saveExportImport: {
    title: 'Save, export, and import',
    body: 'Save a recipe in PAX Cookbook, or export it as a full .pax file to keep and reopen later. Import brings a .pax or .paxlite recipe back into the builder.',
  },

  // ---------------------------------------------------------------------------
  // Builder steps
  // ---------------------------------------------------------------------------
  stepStartWithRecipe: {
    title: 'Start with a recipe',
    body: 'Start from a dashboard-focused recipe or choose a custom export. Presets fill in common choices so you do not start from a blank command.',
  },
  stepNameRecipe: {
    title: 'Name the recipe',
    body: 'Use a clear name and optional notes or tags so the recipe is easy to find later. These details help with reuse but do not affect access.',
  },
  stepDefineAuditPull: {
    title: 'Define the audit pull',
    body: 'Choose what PAX should collect and the date range for audit data. Date ranges apply to audit records, not point-in-time user info snapshots.',
  },
  stepChooseOutputAuth: {
    title: 'Choose output and auth',
    body: 'Tell PAX where results should be written and how the command will sign in when it runs. The builder does not check the destination or sign in for you.',
  },
  stepAdvancedOptions: {
    title: 'Advanced options',
    body: 'Add optional PAX switches that are not covered by the guided fields. Most users can leave this blank.',
  },

  // ---------------------------------------------------------------------------
  // Key builder options
  // ---------------------------------------------------------------------------
  starterPreset: {
    title: 'Starter recipe',
    body: 'Start from a dashboard-focused recipe or a custom export. Presets fill in common choices so you do not start from a blank command.',
  },
  recipeName: {
    title: 'Recipe name',
    body: 'Use a clear name and optional notes or tags so the recipe is easy to find later. These details help with reuse but do not affect access.',
  },
  dataScope: {
    title: 'Data scope',
    body: 'Choose whether the command pulls audit data, Entra user info, or both. User info is exported separately from audit data.',
  },
  includeEntraUserInfo: {
    title: 'Include Entra user info',
    body: 'Adds Entra user details alongside the audit export. It is a point-in-time snapshot and is not limited by the audit date range.',
  },
  excludeCopilotInteraction: {
    title: 'Exclude CopilotInteraction',
    body: 'Leaves CopilotInteraction records out of the audit pull. Use this when your analysis should exclude those events.',
  },
  whatToCollect: {
    title: 'What to collect',
    body: 'Turn on any combination of data the run should gather. Each switch is independent, so you can mix audit data, Entra user info, and other supported sources in one recipe.',
  },
  customActivityTypes: {
    title: 'Custom activity types',
    body: 'Add specific Purview activity types when the guided options do not cover what you need. You can paste one per line or separate values with commas or semicolons.',
  },
  dateRange: {
    title: 'Date range',
    body: 'Custom range emits -StartDate and -EndDate; the end date is exclusive, so PAX pulls records through the day before it. Previous day omits both switches so PAX queries the previous full UTC day (yesterday 00:00 UTC through today 00:00 UTC) — the absence of the switches is the signal, and it is the ideal setup for scheduled daily or append runs. The builder cannot check your tenant’s audit retention.',
  },
  dateRangeScheduling: {
    title: 'Dates for scheduled runs',
    body: 'For a recipe that runs on a schedule (daily or append), use Previous day instead of a fixed custom range. Previous day leaves out -StartDate/-EndDate so each run automatically pulls the previous full UTC day — nothing drifts out of date and no edit is needed before each run. A fixed custom range would keep re-pulling the same historical window every time it fires.',
  },
  rollup: {
    title: 'Rollup',
    body: 'Rollup creates dashboard-friendly summary output from audit data and is required for the Power BI dashboard preset recipes. It needs Python on the machine where PAX runs — PAX detects whether Python is present and installs it for you if it is not. When rollup is on you can also choose the dashboard target — AI-in-One or AI Business Value — see Dashboard target.',
  },
  rollupDashboard: {
    title: 'Dashboard target',
    body: 'When rollup is on (and the M365 usage bundle is off), you can target either the AI-in-One dashboard (the default) or the AI Business Value dashboard. AI Business Value produces a wider 50-column superset fact for the AI Business Value Power BI dashboard; AI-in-One is the standard layout. Both use the same Copilot interaction audit data and Entra user info — only the output column profile differs. The M365 Usage dashboard is a separate data source selected by including M365 usage.',
  },
  userIds: {
    title: 'User IDs',
    body: 'Limit the pull to specific users. You can paste one per line or separate values with commas or semicolons.',
  },
  groupDisplayNames: {
    title: 'Group display names',
    body: 'Use Entra group names when PAX should expand group membership for the run. The account or app running PAX still needs the required Graph access.',
  },
  agentFilters: {
    title: 'Agent filters',
    body: 'Use agent filters when the audit data should include or exclude supported agent-related records. The builder does not surface every agent option, so check your selection before running.',
  },
  agentIds: {
    title: 'Agent IDs',
    body: 'Limit the pull to specific Copilot agents by their AgentId value. Paste one per line or separate values with commas or semicolons. See the guidance below for how to find an AgentId.',
  },
  promptFilter: {
    title: 'Prompt filter',
    body: 'Choose whether prompt, response, both, or default records are included when supported by the selected activity data. The default pulls both prompt and response metrics; leave it unless you know you need a narrower pull.',
  },
  outputDestination: {
    title: 'Output destination',
    body: 'Choose where PAX should write results when the command runs. The builder builds the path into the command but cannot check whether the location exists or whether you have access.',
  },
  auditDataOutputMode: {
    title: 'Audit data output mode',
    body: 'Write-new mode creates new output at the destination. Append mode adds to an existing target file when PAX supports that scenario.',
  },
  userInfoOutputMode: {
    title: 'User info output mode',
    body: 'Choose where Entra user info is written: alongside the audit output, in its own separate file, or appended to an existing user-info file when PAX supports it.',
  },
  auditActivityOutput: {
    title: 'Audit activity output',
    body: 'Combined writes the selected activity types into one audit output when applicable. Separate lets PAX write activity-specific outputs.',
  },
  auditOutputPath: {
    title: 'Audit output path',
    body: 'Enter the folder or file path PAX should write audit data to when the command runs. Use a full local, UNC, SharePoint, or Fabric/OneLake destination when possible.',
  },
  userInfoOutputPath: {
    title: 'User info output path',
    body: 'Enter the folder or file path PAX should use for Entra user info output. Use a full path when possible; relative paths depend on where the command is run.',
  },
  authMethod: {
    title: 'Auth method',
    body: 'Choose how PAX will sign in when the command runs. The builder does not sign in, store credentials, or confirm that access has already been granted.',
  },
  webLogin: {
    title: 'Web login',
    body: 'PAX uses an interactive sign-in flow when the command runs. The signed-in user still needs the right permissions.',
  },
  deviceCode: {
    title: 'Device code',
    body: 'PAX shows a sign-in code in the terminal. Complete sign-in in the browser, then return to the terminal.',
  },
  appRegistrationSecret: {
    title: 'App registration (secret)',
    body: 'The builder uses a placeholder for the secret. Replace it outside the app before running the command, and do not paste real secrets into the page.',
  },
  appRegistrationCertificate: {
    title: 'App registration (certificate)',
    body: 'Use this when PAX should sign in with an app registration and certificate thumbprint. The certificate and app permissions must already be configured outside the app.',
  },
  managedIdentity: {
    title: 'Managed identity',
    body: 'Use this only in an environment where the managed identity is available to the process running PAX. The builder cannot check that environment.',
  },
  advancedSwitches: {
    title: 'Advanced switches',
    body: 'Add optional PAX switches not already covered by the guided options. Secret-looking values and unsupported switches are blocked or warned before they reach the command.',
  },

  // ---------------------------------------------------------------------------
  // Review area
  // ---------------------------------------------------------------------------
  commandView: {
    title: 'Single line / Multi-line',
    body: 'Both views represent the same command. Single line is usually safer for copy and paste; multi-line can be easier to read but should be checked after pasting.',
  },
  warnings: {
    title: 'Warnings',
    body: 'Warnings are items to fix or review before running the command. They do not mean the builder ran or checked anything.',
  },
  assumptions: {
    title: 'Assumptions',
    body: 'Assumptions explain what the builder cannot confirm from the browser, such as tenant settings, granted permissions, or destination access.',
  },
  permissionsNeeded: {
    title: 'Permissions needed',
    body: 'These are the permissions and access requirements based on your selections. The builder does not check whether they are already granted.',
  },
  notes: {
    title: 'Notes',
    body: 'Notes provide useful context that is not necessarily a problem. Review them before copying the command.',
  },
  blockedItems: {
    title: 'Blocked items',
    body: 'Blocked items are values or switches the builder will not include in the generated command. This protects against unsupported options or secret-looking input.',
  },
  graphPermissions: {
    title: 'Graph permissions',
    body: 'Graph permissions are required by the Microsoft APIs PAX calls when the command runs. The list depends on your selected data and auth choices.',
  },
  runtimeRequirements: {
    title: 'Runtime requirements',
    body: 'Runtime requirements are things needed on the machine or environment where PAX runs, such as PowerShell or supporting tools. The builder cannot inspect that machine.',
  },
  outputAccess: {
    title: 'Output access',
    body: 'Output access means the user, app, or environment running PAX must be able to write to the selected destination. The builder cannot check local, SharePoint, or Fabric/OneLake access.',
  },
  reportPrivacy: {
    title: 'Your privacy',
    body: 'A report sends only the message you type, the category you pick, the page path, and the time. It does not collect your name, email, tenant, recipe, command text, credentials, or any other personal or identifiable information.',
  },

  // ---------------------------------------------------------------------------
  // Full Cookbook shell sections
  // ---------------------------------------------------------------------------
  cookbookHome: {
    title: 'Home',
    body: 'Home is your at-a-glance dashboard. Last Bake shows how your most recent run finished and links you to its output folder or log. Recent Recipes lists what you have saved with each one\u2019s last-run status, Recent Outputs lists the newest files PAX Cookbook produced, and What needs attention flags real issues - a failed bake, missing Chef\u2019s Keys, or a recipe that still needs setup. A recipe you simply haven\u2019t baked yet is not flagged. Open a section from the left to work: build in Recipes, preflight in Taste Tests, review runs in Bakes.',
  },
  cookbookPantry: {
    title: 'Pantry',
    body: 'Browse the recipe starting points that ship with PAX Cookbook to see what each one produces and the settings it pre-fills. Browsing changes nothing. Use this starting point opens a draft in Recipes pre-filled with those settings - nothing runs, and nothing is saved until you complete the required details and save it yourself.',
  },
  cookbookRecipes: {
    title: 'Recipes',
    body: 'Recipes is where you build, save, and run a PAX command one friendly step at a time - Basics, Authentication, Audit Operations, Output, Schedule, and Review. Start from a Pantry template, import a shared .pax or .paxlite file, or paste an existing PAX command line to turn it into a draft. For the date range, pick Custom range for explicit start and end dates, or Previous day to pull yesterday\u2019s full UTC day automatically - ideal for daily scheduled runs. On the Review step, Run Check Readiness confirms the engine, sign-in, and output are ready before your first bake. Bake (run) runs the recipe with the managed PAX engine on this PC; it stays unavailable until the recipe is saved, has no unsaved edits, and passes validation, and it always asks you to confirm with Windows Hello. To run a recipe on a schedule, give it a Schedule and keep Start PAX Cookbook at login on (Settings - Startup) so the background broker is there to run it.',
  },
  cookbookBakes: {
    title: 'Bakes',
    body: 'Bakes is the real history of recipes baked on this PC. A bake you just started shows here while it runs and updates on its own until it settles. Pick a bake to review its status - running, completed, failed, canceled, or interrupted - with when it ran, how long it took, the redacted command, the PAX engine fingerprint, and its output files. Open folder next to a file jumps straight to it in File Explorer, and the log is read-only and shown in-app. Output files are listed as metadata only - role, path, size, and whether the file exists - never their contents. Scheduled bakes appear here too, the same as manual ones. This page reviews history and lets you stop a bake that is still running; it never starts or re-runs a bake.',
  },
  cookbookTasteTests: {
    title: 'Taste Tests',
    body: 'A taste test is a safe preflight: pick a saved recipe and PAX Cookbook checks that the recipe is filled in and that the PAX engine, sign-in, and output destination are ready. It never runs PAX and never bakes. If something needs attention, the checklist points you to Edit recipe, Chef\u2019s Keys, or Settings to fix it, then taste-test again.',
  },
  cookbookChefsKeys: {
    title: "Chef's Keys",
    body: "Chef's Keys are the local sign-ins your recipes use to reach Microsoft 365 data. Each key stores a sign-in mode - App registration with a client secret, App registration with a certificate, or Device code - and stays on this PC; secrets are encrypted and never displayed. Create a key here, then bind it to a recipe in the Authentication step so the recipe knows who it runs as. App registration (secret or certificate) runs unattended, which is what scheduled bakes need; Device code prompts for a sign-in code each run. Saving a recipe and checking its readiness are separate, so use Check Readiness on a recipe to confirm exactly what sign-in it needs.",
  },
  cookbookSettings: {
    title: 'Settings',
    body: 'Settings is organized into sections. App information shows your app version, the managed PAX engine, and workspace status. Startup has Start PAX Cookbook at login - turn it on to keep the background broker running after you sign in to Windows so scheduled bakes fire on time; turning it off stops auto-start but does not close the app you are using. Notifications lets you opt in to Telegram messages through your own bot, with the bot token stored securely and never shown again. Security shows your Windows Hello and app-lock status. About has the branding, a help link, and the engine fingerprint you can copy for support. Apart from Startup and Notifications these details are read-only, and no secret or tenant data is ever shown.',
  },
  cookbookUpdates: {
    title: 'Updates',
    body: 'Updates shows the version and status of the build installed on this PC: the app version and release channel, the managed PAX engine version and fingerprint, and clear support details. Online update checking is not available in this build, so PAX Cookbook never contacts an update service and never downloads or installs updates. To move to a newer build, install the release package you were given for internal testing; your saved recipes stay on this PC.',
  },

  // ---------------------------------------------------------------------------
  // Full Cookbook product concepts
  // ---------------------------------------------------------------------------
  cookbookImportRecipe: {
    title: 'Import a recipe',
    body: 'Opening a .paxlite or a full .pax recipe file brings it into the builder as a fresh draft so you can review it. Nothing is saved or cooked until you choose to - importing only fills in the steps.',
  },
  cookbookImportCommand: {
    title: 'Import a PAX command',
    body: 'Paste a PAX command line - with its switches - and PAX Cookbook reads the switches and turns them into a recipe draft. It shows what was applied to the recipe, what was left out (a switch that was retired over time, or one that is not a PAX switch), and keeps any remaining options under Advanced arguments. Pasting a command never runs it: secrets such as a client secret or password are rejected and never stored, and a script path is read only as provenance because PAX Cookbook always cooks with its managed, verified PAX engine. Review the draft, then save it or load it into the editor - nothing is saved until you choose to.',
  },
  cookbookSaveToCookbook: {
    title: 'Save to your cookbook',
    body: 'Saving keeps the recipe in PAX Cookbook on this PC so you can reload it later. Saving does not run the recipe or sign in anywhere.',
  },
  cookbookPaxFileTypes: {
    title: 'Recipe files',
    body: 'A .paxlite file is a lightweight, shareable recipe. A .pax file is a full PAX recipe. Opening either one brings the recipe into the builder as a draft to review - neither runs a PAX script, and nothing is saved until you save it.',
  },
  cookbookWindowsHello: {
    title: 'Unlocking the kitchen',
    body: 'PAX Cookbook uses your normal Windows sign-in (Windows Hello or your lock screen) to unlock. Unlock in the main window first - opening a file never skips that step.',
  },
  cookbookLocalStorage: {
    title: 'Kept on your computer',
    body: 'Your saved recipes and sign-in profiles stay on this PC in the PAX Cookbook app, not in the cloud. This page never shows secrets - only friendly names.',
  },
  cookbookFileAssociations: {
    title: 'Opening recipe files',
    body: 'Double-clicking a .paxlite or a full .pax recipe file opens PAX Cookbook and brings it to the Recipes area as a draft to review. Neither runs a PAX script, and nothing is saved until you save it. If the app is already open, it comes to the front.',
  },
  cookbookEngine: {
    title: 'Cooking engine',
    body: 'The approved PAX script is what actually runs your recipes when you cook. It is managed by the Windows app - recipes here are prepared, not run, until you start a bake.',
  },
  cookbookStartup: {
    title: 'Start at login',
    body: 'Start PAX Cookbook at login keeps a small background broker running after you sign in to Windows. That broker is what actually runs your bakes - including scheduled ones - so it needs to be available when a scheduled time arrives. With it on, scheduled bakes fire on time even if you never open the window; with it off, they only run while PAX Cookbook is open. Turning it off does not close the app you are using now - it just won\u2019t start on its own the next time you sign in. Change it any time in Settings - Startup.',
  },
  cookbookScheduling: {
    title: 'Scheduled bakes',
    body: 'A scheduled bake runs a saved recipe automatically at a time you choose. Set a schedule on the recipe\u2019s Schedule step; Windows Task Scheduler then wakes PAX Cookbook at that time and hands the run to the background broker - the same engine and safety checks as a manual bake, just without you clicking Bake. Two things must be true for it to run: the recipe is saved with a schedule, and Start PAX Cookbook at login is on (Settings - Startup) so the broker is running. An unattended run needs an App registration Chef\u2019s Key (secret or certificate); Device code needs a person to enter a code. Scheduled runs show up in Bakes alongside manual ones.',
  },
};
