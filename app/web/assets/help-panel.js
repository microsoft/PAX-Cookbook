// PAX Cookbook -- contextual help panel framework (V1.S13).
//
// One global help surface for the whole SPA:
//   * Right-side slide-out panel anchored by index.html (#help-panel)
//   * Topbar Help button -> opens to Help Home
//   * Per-control "?" affordance -> opens to a specific topic
//   * Search box -> filters the topic registry by title / category /
//     keyword tokens. Pure client-side filter; no fetches.
//
// Architectural shape:
//   * Self-contained module. Boots on DOMContentLoaded; owns the
//     #help-panel DOM and a single document-level delegated click
//     handler for [data-help-topic] triggers.
//   * Topic registry below is the SINGLE source of truth for the
//     in-app help content shipped with this slice. Topic bodies are
//     short, behavioral, and ready to be enriched later without
//     touching any page module.
//   * Pages opt in by calling window.PaxHelp.hook(topicId) to render
//     a "?" button next to a control. The result is a string of HTML
//     that the page can splice into a string-built template, OR a
//     DOM Element if the page builds with the el() helper.
//   * No framework. No router coupling. No reflow on data changes.
//
// Doctrine:
//   * Help content is documentation, not instructions to the broker.
//     Nothing in this module issues HTTP requests, mutates session
//     storage, or watches broker status. It is presentation-only.
//   * Topic ids are stable strings. Pages reference them by id; the
//     registry owns the prose.
//   * If a referenced topic id is missing from the registry the
//     panel falls back to a clearly-labeled "Coverage gap" placeholder
//     so an authoring miss is visible rather than silent.
(function () {
    'use strict';

    // ---------------------------------------------------------------
    // Topbar coordination shim.
    //
    // The static topbar Reload button is owned by index.html and is
    // shared between every page module. Each page module that knows
    // how to refresh its own data registers itself via the topbar
    // coordination object: pageReloadBound = true means "a mounted
    // page has installed its own click handler that performs the
    // in-place data refresh". The fallback click handler installed
    // below in init() consults that flag and bails when the page
    // has it covered, so the Reload button never both refreshes
    // page data AND triggers a full document reload at the same
    // time. When no page has claimed the click (lock-overlay state,
    // teardown gap between routes, or a detail page that does not
    // register a refresh), the fallback performs a safe full
    // document reload -- which re-runs boot.js, the router re-
    // dispatches the current hash, and the next mounted page re-
    // fetches its data. No broker shell-out, no new endpoint, no
    // session token mutation.
    // ---------------------------------------------------------------
    if (typeof window.PaxTopbar !== 'object' || window.PaxTopbar === null) {
        window.PaxTopbar = { pageReloadBound: false };
    }

    // ---------------------------------------------------------------
    // Categories (Help Home cards, in order)
    // ---------------------------------------------------------------

    var CATEGORIES = [
        { id: 'getting-started', title: 'Getting Started',
          summary: 'What PAX Cookbook is, what you can do in this build, and how to read the status rail.' },
        { id: 'recipes',         title: 'Recipes',
          summary: 'Editable execution plans for a PAX query. Created by materializing a Pantry template.' },
        { id: 'cooks',           title: 'Bakes',
          summary: 'One bake is one execution of a recipe. Baking is turned off in this build.' },
        { id: 'pantry',          title: 'Pantry',
          summary: 'Bundled, read-only recipe templates. Pick one, inspect it, then materialize a recipe.' },
        { id: 'taste-tests',     title: 'Taste Tests',
          summary: 'Placeholder for output validation, sample preview, and run-to-run comparison. Lights up in a later release.' },
        { id: 'auth-profiles',   title: 'Chef\u2019s Keys',
          summary: 'Entra app-registration credentials bound to this Windows account via Credential Manager.' },
        { id: 'settings',        title: 'Settings',
          summary: 'Runtime identity, bundled PAX integrity, and the environment paths Cookbook resolved.' },
        { id: 'updates',         title: 'Updates',
          summary: 'Where Cookbook updates will appear. The update flow is not wired up in this build.' },
        { id: 'scheduling',      title: 'Scheduling',
          summary: 'How recurring bakes will be scheduled. Scheduling is turned off in this build.' },
        { id: 'security',        title: 'Security and Auth',
          summary: 'What stays on this PC, how credentials are stored, and how unlocking works.' },
        { id: 'troubleshooting', title: 'Troubleshooting',
          summary: 'What to do when the server status is not OK or PAX Cookbook will not respond.' },
        { id: 'glossary',        title: 'Glossary and Concepts',
          summary: 'Plain-English definitions for Recipe, Bake, Pantry, Chef\u2019s Key, Sentinel, Artifact, and more.' }
    ];

    // ---------------------------------------------------------------
    // Topic registry
    //
    // Each topic:
    //   id        stable string id, dotted by convention
    //   category  matches CATEGORIES.id
    //   title     short heading for the panel
    //   keywords  optional extra search tokens
    //   body      array of HTML-string blocks; concatenated and
    //             injected verbatim. Use plain semantic tags only;
    //             no scripts, no external assets.
    // ---------------------------------------------------------------

    var TOPICS = {
        // -------- Help Home itself --------
        'home': {
            id: 'home', category: null, title: 'Help',
            body: []  // Help Home is rendered by renderHome(), not from a body string.
        },

        // -------- Getting Started --------
        'getting-started.intro': {
            id: 'getting-started.intro', category: 'getting-started',
            title: 'Welcome to PAX Cookbook',
            keywords: ['welcome', 'start', 'overview'],
            body: [
                '<p>PAX Cookbook is a Windows desktop app that turns the PAX Purview audit log processor into a curated, repeatable workflow. It runs entirely on this PC; every action maps to one explicit, local step &mdash; nothing runs in the background and nothing is sent anywhere on its own.</p>',
                '<p>The lower-left status rail shows whether the local PAX Cookbook server is responding, whether you are signed in, your workspace, and the app and bundled PAX versions. <em>Check for updates</em> sits just below it.</p>',
                '<p>This build is for building and reviewing recipes. You can build a recipe, preview the exact PAX command, and run a readiness check. <strong>Baking, Taste Tests, and scheduling are turned off in this build.</strong></p>',
                '<p>Click <strong>Help</strong> in the top bar at any time to open this panel. Click the small <strong>?</strong> next to any control to jump straight to its help topic.</p>'
            ]
        },
        'getting-started.first-cook': {
            id: 'getting-started.first-cook', category: 'getting-started',
            title: 'Build your first recipe',
            keywords: ['first', 'tutorial', 'walkthrough', 'recipe', 'preview', 'readiness'],
            body: [
                '<ol>',
                    '<li>Open <strong>Pantry</strong> and pick a bundled template.</li>',
                    '<li>Click <em>Materialize</em> to create an editable recipe from it.</li>',
                    '<li>On the <strong>Recipes</strong> page, open the new recipe and edit dates, tenant, and output path.</li>',
                    '<li>Review the <em>command preview</em> to see the exact PAX command the recipe would use.</li>',
                    '<li>Click <em>Check readiness</em> to have PAX Cookbook validate the recipe.</li>',
                '</ol>',
                '<p>Preview and readiness are safe: they do not run PAX, connect to a tenant, or collect credentials. Baking is turned off in this build, so no recipe runs yet.</p>'
            ]
        },
        'getting-started.status-rail': {
            id: 'getting-started.status-rail', category: 'getting-started',
            title: 'The status rail (Server, Sign-in, Workspace)',
            keywords: ['status', 'rail', 'workspace', 'server', 'sign-in', 'ready', 'not selected'],
            body: [
                '<p>The lower-left rail summarizes the current state:</p>',
                '<ul>',
                    '<li><strong>Server</strong> &mdash; whether the local PAX Cookbook server is responding.</li>',
                    '<li><strong>Sign-in</strong> &mdash; whether you are signed in for tenant work.</li>',
                    '<li><strong>Workspace</strong> &mdash; <em>Ready</em> once PAX Cookbook has its working folder. PAX Cookbook creates and manages this folder for you; you do not pick it.</li>',
                    '<li><strong>App</strong> and <strong>PAX</strong> &mdash; the running app version and the bundled PAX version.</li>',
                '</ul>',
                '<p><em>Check for updates</em> sits just below the rail.</p>'
            ]
        },

        // -------- Recipes --------
        'recipes.intro': {
            id: 'recipes.intro', category: 'recipes',
            title: 'About recipes',
            keywords: ['recipe', 'plan', 'edit', 'bake', 'prep station'],
            body: [
                '<p>A <strong>recipe</strong> is the editable execution plan for one PAX query. Recipes are created by materializing a bundled <em>template</em> from the Pantry; the template itself is read-only and never executed.</p>',
                '<p>Each recipe field maps 1:1 to a PAX command-line argument. The recipe editor shows the full preview before any bake begins.</p>',
                '<p>The editor is laid out as a <strong>Prep Station</strong>: the left column stacks the recipe cards (What, When, Audit filters, Where, User-info output, Advanced, Required permissions, Recent runs, Schedule); the right column is a sticky rail for the PAX command preview and the readiness check. Save and Bake Recipe live in the page header so they stay reachable while you scroll.</p>'
            ]
        },
        'recipes.name': {
            id: 'recipes.name', category: 'recipes',
            title: 'Recipe name',
            body: [
                '<p>A human-readable label for this recipe. Stored verbatim in <code>identity.name</code> and used only for display in the recipe list and breadcrumbs.</p>',
                '<p>Renaming a recipe does not affect bakes that have already been recorded; each bake freezes a snapshot of the recipe at execution time.</p>'
            ]
        },
        'recipes.dates': {
            id: 'recipes.dates', category: 'recipes',
            title: 'Start and end date',
            keywords: ['date', 'window', 'range', 'StartDate', 'EndDate'],
            body: [
                '<p>The PAX query window. Sent verbatim as <code>-StartDate</code> and <code>-EndDate</code>. The end date must be on or after the start date.</p>',
                '<p>Cookbook does not infer or shift these dates; the value you save is the value PAX receives.</p>'
            ]
        },
        'recipes.output-path': {
            id: 'recipes.output-path', category: 'recipes',
            title: 'Output path',
            keywords: ['path', 'output', 'destination', 'rollup', 'csv'],
            body: [
                '<p>PAX writes a single Rollup fact CSV to this path. OneLake and Fabric destinations are rejected at save: Cookbook is local-disk only.</p>',
                '<p>The folder must exist before bake time. If it does not, the bake fails fast with a clear error.</p>'
            ]
        },
        'recipes.m365': {
            id: 'recipes.m365', category: 'recipes',
            title: 'Include M365 usage',
            keywords: ['M365', 'usage'],
            body: [
                '<p>Adds the <code>-IncludeM365Usage</code> switch to the PAX command, joining each row with Microsoft 365 active-user signals.</p>'
            ]
        },
        'recipes.user-info': {
            id: 'recipes.user-info', category: 'recipes',
            title: 'Include Entra user info',
            keywords: ['Entra', 'userinfo', 'directory'],
            body: [
                '<p>Adds the <code>-IncludeUserInfo</code> switch, joining each row with Entra directory attributes such as job title, department, and manager.</p>'
            ]
        },
        'recipes.tenant': {
            id: 'recipes.tenant', category: 'recipes',
            title: 'Tenant',
            keywords: ['tenant', 'directory', 'chef key', 'auth profile'],
            body: [
                '<p>The Entra tenant ID or domain that PAX queries against. Recipes that use a <strong>Chef&rsquo;s Key</strong> inherit the tenant from the key; recipes that use WebLogin or DeviceCode prompt at bake time.</p>'
            ]
        },
        'recipes.auth-mode': {
            id: 'recipes.auth-mode', category: 'recipes',
            title: 'Authentication mode',
            keywords: ['auth', 'login', 'devicecode', 'weblogin', 'profile', 'chef key', 'auth profile'],
            body: [
                '<p>Choose how PAX authenticates for this bake:</p>',
                '<ul>',
                    '<li><strong>WebLogin</strong> &mdash; interactive sign-in at bake time.</li>',
                    '<li><strong>DeviceCode</strong> &mdash; PAX prints a code you use to complete sign-in.</li>',
                    '<li><strong>AppRegistration</strong> &mdash; non-interactive, backed by a <strong>Chef&rsquo;s Key</strong> bound to this Windows account.</li>',
                '</ul>'
            ]
        },
        'recipes.preview': {
            id: 'recipes.preview', category: 'recipes',
            title: 'Preview',
            body: [
                '<p><em>Preview</em> renders the exact PAX command and argv that the next bake will use. Nothing executes until you click <em>Bake Recipe</em>.</p>'
            ]
        },
        'recipes.cook-button': {
            id: 'recipes.cook-button', category: 'recipes',
            title: 'Bake Recipe',
            keywords: ['bake', 'cook', 'run', 'execute'],
            body: [
                '<p>Begins one execution of this recipe. The bake appears in the Bakes list as soon as the broker accepts the request; outputs are written to the recipe&rsquo;s output path.</p>'
            ]
        },

        // V1.S26 controls -- each topic describes one switch / control
        // that the recipe editor exposes for the three supported run
        // shapes (default audit, M365 usage bundle, user-info-only).
        // These topics are searchable from the help-home search box;
        // the permissions rail topic permissions.required is the
        // umbrella entry point linked from the editor card.

        'recipes.fact-output-mode': {
            id: 'recipes.fact-output-mode', category: 'recipes',
            title: 'Fact output mode (OutputPath or AppendFile)',
            keywords: ['fact', 'output', 'OutputPath', 'AppendFile', 'append', 'mutex', 'mutually exclusive'],
            body: [
                '<p>A recipe writes its fact CSV in exactly one of two modes. The two modes are <strong>mutually exclusive</strong>; a recipe carries one at a time.</p>',
                '<ul>',
                    '<li><strong>OutputPath</strong> &mdash; PAX writes a new fact CSV at the path you supply. Use this for a fresh export.</li>',
                    '<li><strong>AppendFile</strong> &mdash; PAX appends rows to an existing fact CSV. The append target file must already exist and must have the column layout this bake would produce.</li>',
                '</ul>',
                '<p>Append does not require additional permissions on top of the selected data and output options. If the fact output path is a SharePoint URL, the SharePoint output permissions still apply in both modes.</p>'
            ]
        },

        'recipes.user-info-output-mode': {
            id: 'recipes.user-info-output-mode', category: 'recipes',
            title: 'User-info output mode (default, OutputPathUserInfo, or AppendUserInfo)',
            keywords: ['userInfo', 'user info', 'OutputPathUserInfo', 'AppendUserInfo', 'mutex', 'mutually exclusive', 'enrichment'],
            body: [
                '<p>When a recipe enables Entra user-info enrichment, the user/license rows can be sent to one of three destinations. The path and append destinations are <strong>mutually exclusive</strong>.</p>',
                '<ul>',
                    '<li><strong>Default</strong> &mdash; user-info rows ride along with the fact output (no separate user-info file).</li>',
                    '<li><strong>OutputPathUserInfo</strong> &mdash; PAX writes a separate fresh user-info CSV at the path you supply.</li>',
                    '<li><strong>AppendUserInfo</strong> &mdash; PAX appends user-info rows to an existing user-info CSV.</li>',
                '</ul>',
                '<p>Choosing <strong>OutputPathUserInfo</strong> or <strong>AppendUserInfo</strong> requires user-info enrichment permissions because the destination causes user-info output: <code>User.Read.All</code> and <code>Organization.Read.All</code>.</p>'
            ]
        },

        'recipes.user-info-only': {
            id: 'recipes.user-info-only', category: 'recipes',
            title: 'UserInfoOnly run shape',
            keywords: ['UserInfoOnly', 'user info only', 'shape 3', 'no audit', 'directory only', 'enrichment only'],
            body: [
                '<p><strong>UserInfoOnly</strong> is a separate run shape. It skips the audit query entirely and runs only the user / license enrichment path.</p>',
                '<p>Under UserInfoOnly the recipe must:</p>',
                '<ul>',
                    '<li>Provide a user-info output destination (OutputPathUserInfo or AppendUserInfo).</li>',
                    '<li>Have <code>includeUserInfo = true</code> (the editor enforces this).</li>',
                '</ul>',
                '<p>Under UserInfoOnly the recipe must NOT carry any of:</p>',
                '<ul>',
                    '<li>Audit window dates (start/end date).</li>',
                    '<li>A fact destination (OutputPath or AppendFile).</li>',
                    '<li>Rollup mode.</li>',
                    '<li>ActivityTypes.</li>',
                    '<li>UserIds.</li>',
                    '<li>GroupNames.</li>',
                    '<li>Agent filters.</li>',
                    '<li>PromptFilter.</li>',
                    '<li>The M365 usage bundle (<code>includeM365Usage</code> must be false).</li>',
                '</ul>',
                '<p>UserInfoOnly requires <code>User.Read.All</code> and <code>Organization.Read.All</code>. It does <em>not</em> require <code>AuditLogsQuery.Read.All</code> (no audit query runs).</p>'
            ]
        },

        'recipes.activity-types': {
            id: 'recipes.activity-types', category: 'recipes',
            title: 'ActivityTypes (constrained)',
            keywords: ['ActivityTypes', 'CopilotInteraction', 'RecordTypes', 'ServiceTypes'],
            body: [
                '<p>In Cookbook V1.S26, <code>ActivityTypes</code> is supported only in its constrained <code>CopilotInteraction</code> form under a rollup run. PAX&rsquo;s broader <code>ActivityTypes</code> surface, and the related <code>RecordTypes</code> / <code>ServiceTypes</code> surfaces, are deliberately not exposed in Cookbook.</p>',
                '<ul>',
                    '<li><code>RecordTypes</code> &mdash; not exposed by Cookbook. Recipes that carry it via the verbatim trailer are rejected.</li>',
                    '<li><code>ServiceTypes</code> &mdash; not exposed by Cookbook. Recipes that carry it via the verbatim trailer are rejected.</li>',
                '</ul>',
                '<p>The constrained <code>ActivityTypes = [CopilotInteraction]</code> shape does not add new Graph scopes beyond the base audit query that already requires <code>AuditLogsQuery.Read.All</code>.</p>'
            ]
        },

        'recipes.user-ids': {
            id: 'recipes.user-ids', category: 'recipes',
            title: 'UserIds',
            keywords: ['UserIds', 'users', 'filter', 'limit'],
            body: [
                '<p><strong>UserIds</strong> limits audit results to the listed users. One UPN or object id per line.</p>',
                '<p>UserIds does <strong>not</strong> add new Graph scopes beyond the base audit query. Whatever Graph permissions the audit query already requires remain unchanged.</p>'
            ]
        },

        'recipes.group-names': {
            id: 'recipes.group-names', category: 'recipes',
            title: 'GroupNames',
            keywords: ['GroupNames', 'group expansion', 'GroupMember.Read.All', 'Entra groups', 'membership'],
            body: [
                '<p><strong>GroupNames</strong> expands the named Entra groups to their members and limits audit results to those members. One group display name per line.</p>',
                '<p>GroupNames requires <code>GroupMember.Read.All</code> so PAX can resolve each group&rsquo;s membership, plus the approved user / organization read permissions for the resolved members:</p>',
                '<ul>',
                    '<li><code>GroupMember.Read.All</code></li>',
                    '<li><code>User.Read.All</code></li>',
                    '<li><code>Organization.Read.All</code></li>',
                '</ul>',
                '<p>GroupNames does not add any other scopes.</p>'
            ]
        },

        'recipes.agent-filter': {
            id: 'recipes.agent-filter', category: 'recipes',
            title: 'Agent filters (AgentId, AgentsOnly, ExcludeAgents)',
            keywords: ['agentFilter', 'AgentId', 'agentIds', 'AgentsOnly', 'ExcludeAgents', 'Agent365Info'],
            body: [
                '<p>An audit recipe can carry at most one agent filter. The three filter modes are <strong>mutually exclusive</strong>:</p>',
                '<ul>',
                    '<li><strong>AgentId</strong> &mdash; the audit query is limited to interactions with the listed agent ids.</li>',
                    '<li><strong>AgentsOnly</strong> &mdash; the audit query keeps only interactions that involved an agent.</li>',
                    '<li><strong>ExcludeAgents</strong> &mdash; the audit query removes interactions that involved an agent.</li>',
                '</ul>',
                '<p>The agent filters are active filters on the existing audit query. They are <strong>not</strong> the Agent365Info catalog/export surface. The Agent365Info catalog/export switches (<code>IncludeAgent365Info</code>, <code>OnlyAgent365Info</code>, <code>OutputPathAgent365Info</code>, <code>AppendAgent365Info</code>) remain disabled / unsupported in this Cookbook version.</p>',
                '<p>The agent filters do not add any Graph scopes beyond the base audit query and do not remove base audit permissions.</p>'
            ]
        },

        'recipes.prompt-filter': {
            id: 'recipes.prompt-filter', category: 'recipes',
            title: 'PromptFilter',
            keywords: ['PromptFilter', 'prompt', 'response', 'governance', 'privacy', 'PromptOnly', 'ResponseOnly', 'Both', 'Null'],
            body: [
                '<p><strong>PromptFilter</strong> limits audit results by which prompt / response fields are present in the row. Allowed values: <code>Prompt</code>, <code>Response</code>, <code>Both</code>, <code>Null</code>.</p>',
                '<p class="help-perm-callout">This filters by prompt/response presence. Use intentionally.</p>',
                '<p>PromptFilter does <strong>not</strong> add Graph scopes. It is a content filter on data the audit query is already authorized to read.</p>'
            ]
        },

        'recipes.exclude-copilot-interaction': {
            id: 'recipes.exclude-copilot-interaction', category: 'recipes',
            title: 'ExcludeCopilotInteraction (M365 usage bundle only)',
            keywords: ['ExcludeCopilotInteraction', 'includeCopilotInteraction', 'M365 usage', 'bundle'],
            body: [
                '<p><strong>ExcludeCopilotInteraction</strong> appears <em>only</em> inside the M365 usage bundle. Setting <code>includeCopilotInteraction = false</code> tells the M365 usage bundle to omit the CopilotInteraction audit data while still pulling Exchange, OneDrive, and SharePoint activity.</p>',
                '<p>ExcludeCopilotInteraction is not valid for the default CopilotInteraction-only run shape: the validator refuses a recipe that sets <code>includeCopilotInteraction = false</code> while <code>includeM365Usage</code> is not true.</p>',
                '<p>ExcludeCopilotInteraction only changes the query composition inside the M365 usage bundle. It does not remove or change required permissions; the M365 bundle audit permissions remain required.</p>'
            ]
        },

        'recipes.client-certificate-path': {
            id: 'recipes.client-certificate-path', category: 'recipes',
            title: 'ClientCertificatePath (auth)',
            keywords: ['ClientCertificatePath', 'certificate', 'PFX', 'AppRegistrationCertificate', 'passwordless', 'thumbprint'],
            body: [
                '<p><strong>ClientCertificatePath</strong> changes how PAX authenticates to Microsoft Graph &mdash; auth mechanics only. It does <strong>not</strong> change which Graph scopes the recipe requires.</p>',
                '<p>Cookbook surfaces a certificate file path only for the <strong>passwordless</strong> PFX case (a PFX with no protecting password). Password-protected PFX files require future secure secret storage; Cookbook does not yet have a UI for that and deliberately refuses any path that would put a certificate password into the recipe JSON or onto the bake&rsquo;s argv.</p>',
                '<ul>',
                    '<li>No certificate passwords are written into the recipe JSON.</li>',
                    '<li>No certificate passwords are passed on argv.</li>',
                    '<li>For password-protected certificates today, use the thumbprint / certificate-store auth mode instead.</li>',
                '</ul>'
            ]
        },

        'recipes.unsupported-switches': {
            id: 'recipes.unsupported-switches', category: 'recipes',
            title: 'Unsupported / blocked PAX switches',
            keywords: ['unsupported', 'blocked', 'RecordTypes', 'ServiceTypes', 'IncludeAgent365Info', 'OnlyAgent365Info', 'OutputPathAgent365Info', 'AppendAgent365Info', 'UseEOM', 'removed', 'trailer'],
            body: [
                '<p>Cookbook V1.S26 deliberately blocks a set of PAX switches. The recipe schema does not expose them as leaves, and the verbatim escape hatch (<code>advanced.extraArguments</code>) is scanned at validate time and rejects any recipe that carries them.</p>',
                '<ul>',
                    '<li><code>RecordTypes</code> &mdash; not exposed; Cookbook only supports the constrained <code>ActivityTypes</code> shape.</li>',
                    '<li><code>ServiceTypes</code> &mdash; not exposed; Cookbook only supports the constrained <code>ActivityTypes</code> shape.</li>',
                    '<li><code>IncludeAgent365Info</code>, <code>OnlyAgent365Info</code>, <code>OutputPathAgent365Info</code>, <code>AppendAgent365Info</code> &mdash; the Agent365Info catalog/export surface remains disabled / unsupported in this Cookbook version. The active agent filters (AgentId / AgentsOnly / ExcludeAgents) are a separate surface.</li>',
                    '<li><code>UseEOM</code> &mdash; not exposed; rejected by the rollup pre-validator.</li>',
                    '<li>Removed PAX v1.11.2 switches &mdash; <code>ExportWorkbook</code>, <code>ExplodeArrays</code>, <code>ExplodeDeep</code>, <code>RawInputCSV</code> &mdash; blocked at trailer-gate.</li>',
                    '<li>Tuning switches not in Cookbook v1 &mdash; not exposed; recipes that carry them via the trailer are rejected.</li>',
                '</ul>',
                '<p>Blocking is enforced at validate time, before any bake is spawned. The errors name the path and the blocked switch so the chef can correct the recipe.</p>'
            ]
        },

        // Dynamic Required Permissions transparency rail in the
        // recipe editor links here. Structure: non-collapsible main
        // category headings + collapsible subsections. AI-in-One
        // Dashboard and M365 Usage Analytics Dashboard subsections
        // are open by default so the chef immediately sees the
        // permissions for the built-in recipes. The other
        // subsections cover every permission shape Cookbook can
        // surface (audit data options, user / license / group
        // options, output destination including SharePoint output,
        // rollup options including RollupPlusRaw, append behavior,
        // and runtime prerequisites) and are collapsed by default
        // so the panel stays focused on the chef's actual recipe.
        'permissions.required': {
            id: 'permissions.required', category: 'recipes',
            title: 'Permissions required by recipes',
            keywords: ['permissions', 'graph', 'audit', 'rollup', 'python', 'powershell', 'sharepoint', 'append', 'user.read', 'organization.read', 'auditlogsquery', 'environment'],
            body: [
                '<p>PAX Cookbook calculates required permissions from the recipe options you select. The list is cumulative: if a recipe uses multiple options, the recipe needs the combined permissions for those options.</p>',
                '<p>Cookbook shows permissions for the recipe as configured, not every permission PAX can use.</p>',

                '<h3 class="help-perm-h">Built-in recipes</h3>',

                '<details class="help-perm-sub" open>',
                    '<summary>AI-in-One Dashboard</summary>',
                    '<p class="help-perm-shape"><strong>Recipe shape:</strong> CopilotInteraction-only audit export. Uses <code>Rollup</code>.</p>',
                    '<h4 class="help-perm-h4">Graph permissions</h4>',
                    '<ul>',
                        '<li><code>AuditLogsQuery.Read.All</code> &mdash; required for the CopilotInteraction audit export.</li>',
                        '<li><code>User.Read.All</code> &mdash; required because CopilotInteraction-only rollup requires user info.</li>',
                        '<li><code>Organization.Read.All</code> &mdash; required because CopilotInteraction-only rollup requires user/license enrichment.</li>',
                    '</ul>',
                    '<h4 class="help-perm-h4">Prerequisites</h4>',
                    '<ul>',
                        '<li><strong>PowerShell 7+</strong> &mdash; required for Cookbook&rsquo;s normal Graph API execution path.</li>',
                        '<li><strong>Python</strong> &mdash; required because this recipe uses <code>Rollup</code>. PAX handles the Python requirement; Cookbook does not install Python directly.</li>',
                        '<li><strong>Unified Audit Logging enabled</strong> &mdash; required so audit data exists for the tenant.</li>',
                        '<li><strong>Microsoft Graph / Microsoft 365 endpoint access</strong> &mdash; required so PAX can query Graph and Microsoft 365 audit data.</li>',
                        '<li><strong>Output path write access</strong> &mdash; required so PAX can write generated files.</li>',
                    '</ul>',
                    '<h4 class="help-perm-h4">Rollup behavior</h4>',
                    '<ul>',
                        '<li><code>Rollup</code> &mdash; produces dashboard-ready rollup output and does not preserve raw output after successful rollup.</li>',
                        '<li><code>RollupPlusRaw</code> &mdash; produces dashboard-ready rollup output and keeps raw output.</li>',
                    '</ul>',
                '</details>',

                '<details class="help-perm-sub" open>',
                    '<summary>M365 Usage Analytics Dashboard</summary>',
                    '<p class="help-perm-shape"><strong>Recipe shape:</strong> M365 usage bundle. Uses <code>Rollup</code>.</p>',
                    '<h4 class="help-perm-h4">Graph permissions</h4>',
                    '<ul>',
                        '<li><code>AuditLogsQuery.Read.All</code> &mdash; required for the normal audit query run.</li>',
                        '<li><code>AuditLogsQuery-Exchange.Read.All</code> &mdash; required because the M365 usage bundle includes Exchange audit activity.</li>',
                        '<li><code>AuditLogsQuery-OneDrive.Read.All</code> &mdash; required because the M365 usage bundle includes OneDrive audit activity.</li>',
                        '<li><code>AuditLogsQuery-SharePoint.Read.All</code> &mdash; required because the M365 usage bundle includes SharePoint audit activity.</li>',
                    '</ul>',
                    '<h4 class="help-perm-h4">Prerequisites</h4>',
                    '<ul>',
                        '<li><strong>PowerShell 7+</strong> &mdash; required for Cookbook&rsquo;s normal Graph API execution path.</li>',
                        '<li><strong>Python</strong> &mdash; required because this recipe uses <code>Rollup</code>. PAX handles the Python requirement; Cookbook does not install Python directly.</li>',
                        '<li><strong>Unified Audit Logging enabled</strong> &mdash; required so audit data exists for the tenant.</li>',
                        '<li><strong>Microsoft Graph / Microsoft 365 endpoint access</strong> &mdash; required so PAX can query Graph and Microsoft 365 audit data.</li>',
                        '<li><strong>Output path write access</strong> &mdash; required so PAX can write generated files.</li>',
                    '</ul>',
                    '<p class="help-perm-note"><strong>Important distinction:</strong> M365 usage bundle rollup does not automatically require <code>User.Read.All</code> or <code>Organization.Read.All</code>.</p>',
                '</details>',

                '<h3 class="help-perm-h">Audit data options</h3>',

                '<details class="help-perm-sub">',
                    '<summary>Baseline audit export</summary>',
                    '<ul>',
                        '<li><code>AuditLogsQuery.Read.All</code> &mdash; required when any recipe runs a normal audit export. Not required for user-info-only recipes that do not run an audit query.</li>',
                    '</ul>',
                '</details>',

                '<details class="help-perm-sub">',
                    '<summary>M365 usage bundle</summary>',
                    '<p>Cookbook treats the M365 usage bundle as one bundled option. These permissions are required together:</p>',
                    '<ul>',
                        '<li><code>AuditLogsQuery-Exchange.Read.All</code></li>',
                        '<li><code>AuditLogsQuery-OneDrive.Read.All</code></li>',
                        '<li><code>AuditLogsQuery-SharePoint.Read.All</code></li>',
                    '</ul>',
                    '<p>They are not individually selected in Cookbook.</p>',
                '</details>',

                '<h3 class="help-perm-h">User, license, and group options</h3>',

                '<details class="help-perm-sub">',
                    '<summary>User and license enrichment</summary>',
                    '<ul>',
                        '<li><code>User.Read.All</code> &mdash; required when user info enrichment is enabled, user-info-only mode is used, or CopilotInteraction-only rollup is selected.</li>',
                        '<li><code>Organization.Read.All</code> &mdash; required when user/license enrichment is enabled, user-info-only mode is used, or CopilotInteraction-only rollup is selected.</li>',
                    '</ul>',
                '</details>',

                '<details class="help-perm-sub">',
                    '<summary>User-info-only mode</summary>',
                    '<p>User-info-only mode skips the audit query entirely and runs only the user / license enrichment path.</p>',
                    '<ul>',
                        '<li><code>User.Read.All</code> &mdash; required to read the user directory.</li>',
                        '<li><code>Organization.Read.All</code> &mdash; required to read license and organization data.</li>',
                        '<li><code>AuditLogsQuery.Read.All</code> &mdash; <em>not</em> required (no audit query runs).</li>',
                        '<li>M365 usage bundle audit permissions &mdash; <em>not</em> required (no audit query runs).</li>',
                    '</ul>',
                    '<p class="help-perm-note">User-info-only mode is normally mutually exclusive with the audit options. If a recipe selects both, the audit selection wins for what PAX runs.</p>',
                '</details>',

                '<details class="help-perm-sub">',
                    '<summary>Group expansion</summary>',
                    '<p>Group expansion targets audit data by group membership. It requires the user / license enrichment pair plus a Graph permission for reading group members:</p>',
                    '<ul>',
                        '<li><code>User.Read.All</code> &mdash; required to read the user directory for the resolved members.</li>',
                        '<li><code>Organization.Read.All</code> &mdash; required to read license and organization data for the resolved members.</li>',
                        '<li><code>GroupMember.Read.All</code> &mdash; required so PAX can resolve the members of each named group.</li>',
                    '</ul>',
                    '<p class="help-perm-note">Group expansion does <em>not</em> by itself add audit permissions. The audit permissions come from whether the recipe also runs an audit query and whether it uses the M365 usage bundle.</p>',
                '</details>',

                '<h3 class="help-perm-h">Output destination</h3>',

                '<details class="help-perm-sub">',
                    '<summary>Local or UNC output</summary>',
                    '<ul>',
                        '<li>Output folder write access is required.</li>',
                        '<li>Local/UNC output does not require SharePoint Graph write permissions.</li>',
                    '</ul>',
                '</details>',

                '<details class="help-perm-sub">',
                    '<summary>SharePoint output</summary>',
                    '<p>Shown / applied only when the selected recipe writes output to a SharePoint URL.</p>',
                    '<ul>',
                        '<li><code>Sites.ReadWrite.All</code> &mdash; required so PAX can resolve the SharePoint site and drive for the output destination.</li>',
                        '<li><code>Files.ReadWrite.All</code> &mdash; required so PAX can upload generated files to the SharePoint destination.</li>',
                        '<li>Destination library/folder write access &mdash; required because Graph permissions do not replace SharePoint site/library/folder permissions. The destination identity must have at least Member access on the target site.</li>',
                    '</ul>',
                '</details>',

                '<h3 class="help-perm-h">Rollup options</h3>',

                '<details class="help-perm-sub">',
                    '<summary>CopilotInteraction-only rollup</summary>',
                    '<ul>',
                        '<li>CopilotInteraction-only + <code>Rollup</code> &mdash; adds Python; adds <code>User.Read.All</code>; adds <code>Organization.Read.All</code>.</li>',
                        '<li>CopilotInteraction-only + <code>RollupPlusRaw</code> &mdash; adds Python; adds <code>User.Read.All</code>; adds <code>Organization.Read.All</code>.</li>',
                    '</ul>',
                '</details>',

                '<details class="help-perm-sub">',
                    '<summary>M365 usage bundle rollup</summary>',
                    '<ul>',
                        '<li>M365 usage bundle + <code>Rollup</code> &mdash; adds Python only.</li>',
                        '<li>M365 usage bundle + <code>RollupPlusRaw</code> &mdash; adds Python only.</li>',
                    '</ul>',
                    '<p class="help-perm-note"><strong>Rollup notes.</strong> <code>Rollup</code> and <code>RollupPlusRaw</code> are mutually exclusive (a recipe carries one rollup mode at a time). Rollup options are valid for CopilotInteraction-only runs and for M365 usage bundle runs.</p>',
                '</details>',

                '<h3 class="help-perm-h">Append options</h3>',

                '<details class="help-perm-sub">',
                    '<summary>Append behavior</summary>',
                    '<ul>',
                        '<li>Append options do not require additional Graph permissions.</li>',
                        '<li>Append options do not require additional user/license permissions.</li>',
                        '<li>Append options do not require additional SharePoint permissions beyond the selected output destination.</li>',
                        '<li>If appending to a local/UNC output, output folder/file write access is still required.</li>',
                        '<li>If appending to a SharePoint output, the SharePoint output permissions and destination access still apply.</li>',
                    '</ul>',
                    '<p class="help-perm-callout">Append does not require additional permissions. It uses the same permissions required by the selected data, enrichment, and output options.</p>',
                '</details>',

                '<h3 class="help-perm-h">Runtime prerequisites</h3>',

                '<details class="help-perm-sub">',
                    '<summary>Core runtime requirements</summary>',
                    '<ul>',
                        '<li><strong>PowerShell 7+</strong> &mdash; required for all normal Cookbook Graph runs.</li>',
                        '<li><strong>Python</strong> &mdash; required only when <code>Rollup</code> or <code>RollupPlusRaw</code> is selected.</li>',
                        '<li><strong>Unified Audit Logging enabled</strong> &mdash; required for audit export recipes.</li>',
                        '<li><strong>Microsoft Graph / Microsoft 365 endpoint access</strong> &mdash; required for Graph audit and enrichment calls.</li>',
                        '<li><strong>Execution policy Bypass or RemoteSigned</strong> &mdash; required for PowerShell execution.</li>',
                        '<li><strong>Output path write access</strong> &mdash; required for every recipe that writes files.</li>',
                    '</ul>',
                '</details>'
            ]
        },

        // -------- Recipe Takeout --------
        'recipes.takeout': {
            id: 'recipes.takeout', category: 'recipes',
            title: 'Recipe files (.pax and .paxlite)',
            keywords: ['recipe file', 'pax file', 'paxlite', 'import recipe', 'export recipe', 'transfer recipe', 'share recipe', 'open recipe', 'chef key', 'chef\u2019s key'],
            body: [
                '<p>A recipe can be saved to a recipe file so you can keep a copy, move it to another PC, or hand it to a teammate running their own PAX Cookbook. PAX Cookbook registers two recipe file types so double-clicking one opens it here:</p>',
                '<ul>',
                    '<li><code>.pax</code> &mdash; a full editable recipe definition (identity, query, ingredients, advanced switches).</li>',
                    '<li><code>.paxlite</code> &mdash; a lighter recipe file that carries the core choices and is finished off in the recipe editor.</li>',
                '</ul>',
                '<p>Recipe files are the recipe definition only. They are <strong>not</strong> a backup of your workspace, bakes, output data, or your Chef&rsquo;s Keys, and they never contain secrets, certificates, passwords, or tokens.</p>',
                '<p>Because credentials are never included, a recipe that authenticated with an app-registration Chef&rsquo;s Key needs that key picked again on this PC before it could run. Open the recipe and choose a local Chef&rsquo;s Key in the auth card.</p>'
            ]
        },

        // -------- Bakes --------
        'cooks.intro': {
            id: 'cooks.intro', category: 'cooks',
            title: 'About bakes',
            keywords: ['bake', 'bakes', 'execution', 'run', 'cook'],
            body: [
                '<p>A <strong>bake</strong> is one execution of a recipe. Cookbook records the frozen command, recipe snapshot, sentinels, and output artifacts for every bake &mdash; nothing is reconstructed after the fact.</p>',
                '<p>Baking is turned off in this build, so no new bakes run yet.</p>'
            ]
        },
        'cooks.list': {
            id: 'cooks.list', category: 'cooks',
            title: 'Bake list columns',
            keywords: ['bake', 'bakes', 'columns', 'status', 'duration', 'outcome'],
            body: [
                '<p>Each row shows the bake&rsquo;s recipe name, lifecycle status, created/started/finished timestamps (UTC), duration, terminal outcome (exit code plus error class), row and artifact counts, and the bundled PAX and Cookbook versions captured at bake time.</p>'
            ]
        },
        'cooks.resumable': {
            id: 'cooks.resumable', category: 'cooks',
            title: 'Resumable bakes',
            keywords: ['bake', 'resume', 'interrupted'],
            body: [
                '<p>A bake in the <em>interrupted</em> state is shown with a <em>resumable</em> marker when the broker has determined the bake can be safely resumed from its last checkpoint. Open the bake to use the <em>Resume</em> action.</p>'
            ]
        },
        'cooks.detail': {
            id: 'cooks.detail', category: 'cooks',
            title: 'Bake detail view',
            keywords: ['bake', 'detail', 'snapshot', 'sentinels', 'artifacts'],
            body: [
                '<p>Opens the persisted command, recipe snapshot, sentinel files, and a list of output artifacts on disk. The detail view never re-derives any of these fields; everything was frozen at bake time.</p>'
            ]
        },

        // -------- Pantry --------
        'pantry.intro': {
            id: 'pantry.intro', category: 'pantry',
            title: 'About the Pantry',
            keywords: ['pantry', 'template', 'bundled', 'recipe', 'bake', 'prep station'],
            body: [
                '<p>The Pantry is the curated library of audit-log <strong>recipe templates</strong> bundled with this Cookbook install. Templates are <strong>read-only blueprints</strong> &mdash; they are never executed directly. You materialize a template into a new editable Recipe, then customize that recipe in <strong>Recipes</strong> before baking it through PAX.</p>',
                '<p>The Pantry itself never starts a bake and never touches Chef&rsquo;s Keys. After materialization you move the new recipe through readiness in <strong>Prep Station</strong>, exactly like any other recipe.</p>'
            ]
        },
        'pantry.materialize': {
            id: 'pantry.materialize', category: 'pantry',
            title: 'Create a recipe from a template',
            keywords: ['pantry', 'template', 'materialize', 'create', 'recipe', 'bake'],
            body: [
                '<p>Creating a recipe from a Pantry template copies the template into a new editable Recipe. The new recipe records its template origin in <code>createdBy.fromTemplate</code> so the provenance is visible later in Recipes and on the recipe detail page.</p>',
                '<p>The Pantry page does not start a bake. After the recipe is created you customize it in Recipes, then move it through Prep Station readiness before baking through PAX.</p>'
            ]
        },

        // -------- Taste Tests --------
        'taste-tests.intro': {
            id: 'taste-tests.intro', category: 'taste-tests',
            title: 'About Taste Tests',
            keywords: ['taste', 'test', 'tests', 'validation', 'compare', 'placeholder', 'coming later'],
            body: [
                '<p>Taste Tests is a <strong>reserved surface</strong>. The nav rail entry resolves to a placeholder page today so the route stays stable and discoverable.</p>',
                '<p>The full workflow &mdash; output validation, sample preview, and run-to-run comparison &mdash; lights up in a later release. Until then this surface intentionally shows the <em>Taste Tests arrive in a later release</em> placeholder and performs no bakes, no rollups, and no comparisons.</p>'
            ]
        },

        // -------- Chef\u2019s Keys --------
        'auth-profiles.intro': {
            id: 'auth-profiles.intro', category: 'auth-profiles',
            title: 'About Chef\u2019s Keys',
            keywords: ['chef', 'key', 'keys', 'auth', 'profile', 'credential', 'entra'],
            body: [
                '<p>A <strong>Chef&rsquo;s Key</strong> is a saved Entra app registration (tenant plus client) paired with a workload credential bound to this Windows account. Recipes reference a key by id; the credential bytes never leave Windows Credential Manager.</p>',
                '<p>Chef&rsquo;s Keys are needed only for recipes that authenticate as an Entra workload. Recipes using WebLogin or DeviceCode do not need a key.</p>'
            ]
        },
        'auth-profiles.new': {
            id: 'auth-profiles.new', category: 'auth-profiles',
            title: 'Add a Chef\u2019s Key',
            keywords: ['add', 'new', 'key', 'chef', 'register'],
            body: [
                '<p>Opens the dialog to register a new Chef&rsquo;s Key. You provide the tenant id, client id, and credential type (client secret or certificate). The credential itself is written to Windows Credential Manager via the broker &mdash; the SPA never persists it.</p>'
            ]
        },
        'auth-profiles.secret': {
            id: 'auth-profiles.secret', category: 'auth-profiles',
            title: 'Secrets and certificates',
            keywords: ['secret', 'certificate', 'cred', 'credential'],
            body: [
                '<p>After a Chef&rsquo;s Key is saved, the only allowed operations are <em>replace</em>, <em>remove</em>, <em>test</em>, or <em>show metadata</em>. There is no view, reveal, export, copy-back, or display-plaintext path.</p>'
            ]
        },
        'auth-profiles.test': {
            id: 'auth-profiles.test', category: 'auth-profiles',
            title: 'Test a key',
            keywords: ['test', 'verify', 'key', 'chef'],
            body: [
                '<p>The <em>Test</em> action issues a structural credential validation through the broker. It does not run a recipe.</p>'
            ]
        },

        // -------- Settings --------
        'settings.intro': {
            id: 'settings.intro', category: 'settings',
            title: 'About Settings',
            body: [
                '<p>Settings is a read-only view of runtime identity, bundled PAX integrity, environment paths, runtime assumptions, update readiness, and local diagnostics. The single underlying endpoint is <code>GET /api/v1/runtime/version</code>. Nothing on this page is editable.</p>'
            ]
        },
        'settings.runtime-identity': {
            id: 'settings.runtime-identity', category: 'settings',
            title: 'Runtime identity',
            body: [
                '<p>Cookbook version, release channel, and the local host this broker is bound to. Used to confirm which Cookbook build is running.</p>'
            ]
        },
        'settings.bundled-pax': {
            id: 'settings.bundled-pax', category: 'settings',
            title: 'Bundled PAX',
            keywords: ['pax', 'sha', 'integrity'],
            body: [
                '<p>The version, SHA-256, and relative path of the PAX script bundled inside this Cookbook install. The bundled script is authoritative; Cookbook never modifies it.</p>'
            ]
        },
        'settings.integrity': {
            id: 'settings.integrity', category: 'settings',
            title: 'Integrity',
            body: [
                '<p>Script integrity and manifest alignment. If either is degraded, the broker reports it here and refuses to start a bake until the install is repaired or replaced.</p>'
            ]
        },
        'settings.paths': {
            id: 'settings.paths', category: 'settings',
            title: 'Environment paths',
            body: [
                '<p>Display-only absolute paths the broker resolved at launch: install root, workspace, recipes folder, bakes folder, templates folder, and database. These are not editable from the UI. The literal folder paths shown on this machine are the source of truth.</p>'
            ]
        },
        'settings.assumptions': {
            id: 'settings.assumptions', category: 'settings',
            title: 'Runtime assumptions',
            body: [
                '<p>The broker process id, port, start time, and transport. Useful when filing a support report.</p>'
            ]
        },
        'settings.update-readiness': {
            id: 'settings.update-readiness', category: 'updates',
            title: 'Update readiness',
            keywords: ['update', 'check', 'download', 'install', 'restart'],
            body: [
                '<p>The update flow is not wired up in this build. When it is enabled it will use three explicit operator actions &mdash; check, download, and install &mdash; each a single deliberate click. Nothing is polled, scheduled, retried, or installed silently.</p>'
            ]
        },
        'settings.diagnostics': {
            id: 'settings.diagnostics', category: 'settings',
            title: 'Local diagnostics',
            keywords: ['diagnostics', 'support'],
            body: [
                '<p>A synthesized checklist derived from the runtime metadata above. Use it to confirm at a glance that the broker, PAX, and database paths are healthy. If something here is red, the <em>Troubleshooting</em> section of Help is the next stop.</p>'
            ]
        },
        'settings.pax-export': {
            id: 'settings.pax-export', category: 'settings',
            title: 'Export bundled PAX',
            body: [
                '<p>Saves a verbatim copy of the bundled PAX script to a location you choose. The bundled script inside PAX Cookbook remains authoritative and unchanged &mdash; this is a one-shot export, not a replacement or upload path.</p>'
            ]
        },

        // -------- Updates --------
        'updates.flow': {
            id: 'updates.flow', category: 'updates',
            title: 'How updates work',
            body: [
                '<p>PAX Cookbook never fetches or installs updates on its own. In this build the update flow is not wired up yet &mdash; the Updates view is a placeholder and <em>Check for updates</em> opens it.</p>',
                '<p>When updates are enabled, any downloaded package will be verified before it is applied, and installing will be an explicit, single action.</p>'
            ]
        },

        // -------- Scheduling --------
        'scheduling.intro': {
            id: 'scheduling.intro', category: 'scheduling',
            title: 'About scheduling',
            keywords: ['schedule', 'task', 'scheduled', 'bake', 'cook'],
            body: [
                '<p>Scheduling will register recurring bakes as Windows Scheduled Tasks and reconcile them on launch. <strong>Scheduling is turned off in this build</strong>; the Schedule controls are visible but do not create tasks yet.</p>'
            ]
        },

        // -------- Security / Auth --------
        'security.session-token': {
            id: 'security.session-token', category: 'security',
            title: 'How your session is protected',
            keywords: ['token', 'session', 'unlock', 'lock'],
            body: [
                '<p>PAX Cookbook runs as a local app on this PC and talks only to its own local server. Your session is unlocked while the app is open and is cleared when you lock it or close the app.</p>',
                '<p>If the app asks you to unlock, enter your credentials in the unlock prompt to continue.</p>'
            ]
        },
        'security.lock-session': {
            id: 'security.lock-session', category: 'security',
            title: 'Lock Session',
            keywords: ['lock', 'logout', 'sign out'],
            body: [
                '<p>The <strong>Lock Session</strong> button in the top bar returns PAX Cookbook to a locked state. It does not sign you out of any Microsoft account.</p>',
                '<p>Unlock again from the app to keep working.</p>'
            ]
        },
        'security.stays-local': {
            id: 'security.stays-local', category: 'security',
            title: 'What stays on this PC',
            keywords: ['local', 'privacy', 'offline', 'preview', 'readiness', 'no run'],
            body: [
                '<p>PAX Cookbook keeps your recipes, settings, and workspace on this PC. Building a recipe, previewing its command, and checking readiness are safe: they do not run PAX, connect to a tenant, or collect credentials.</p>',
                '<p>Chef&rsquo;s Key credentials are stored in Windows Credential Manager for your Windows account and are read only by the local server. PAX Cookbook does not send them anywhere.</p>'
            ]
        },
        'security.credentials': {
            id: 'security.credentials', category: 'security',
            title: 'Credential storage',
            keywords: ['credential', 'credentials', 'chef key', 'auth profile', 'credential manager'],
            body: [
                '<p>Chef&rsquo;s Key credentials are stored in Windows Credential Manager, scoped to the current Windows account, and accessed only by the local broker. The browser never sees, persists, or transmits the credential value.</p>'
            ]
        },

        // -------- Troubleshooting --------
        'troubleshooting.reopen-running-broker': {
            id: 'troubleshooting.reopen-running-broker', category: 'troubleshooting',
            title: 'Reopen PAX Cookbook',
            keywords: ['reopen', 'closed', 'shortcut', 'desktop', 'start menu', 'reconnect'],
            body: [
                '<p>If you close PAX Cookbook, open it again from the Start Menu or the Desktop shortcut. The local server keeps running briefly so the next launch is fast and reconnects to it.</p>',
                '<p>Use <strong>Repair PAX Cookbook Shortcuts</strong> in the Start Menu to refresh the Start Menu and Desktop shortcuts against the current install. It does not delete workspace data.</p>'
            ]
        },
        'troubleshooting.broker-unreachable': {
            id: 'troubleshooting.broker-unreachable', category: 'troubleshooting',
            title: 'PAX Cookbook is not responding',
            keywords: ['server', 'unreachable', 'not responding', 'failed'],
            body: [
                '<p>If the lower-left <strong>Server</strong> status is not OK, the local PAX Cookbook server is not responding. Close PAX Cookbook and open it again from the Start Menu or Desktop shortcut. If it still does not respond, check the Cookbook log in your workspace <code>Logs</code> folder.</p>'
            ]
        },
        'troubleshooting.token-rejected': {
            id: 'troubleshooting.token-rejected', category: 'troubleshooting',
            title: 'Asked to sign in or unlock again',
            keywords: ['401', 'token', 'unlock', 'sign in'],
            body: [
                '<p>Your session ended. Unlock PAX Cookbook again from the app to continue.</p>'
            ]
        },
        'troubleshooting.no-token': {
            id: 'troubleshooting.no-token', category: 'troubleshooting',
            title: 'Session is locked',
            body: [
                '<p>PAX Cookbook is locked. Unlock it from the app to continue.</p>'
            ]
        },
        'troubleshooting.degraded': {
            id: 'troubleshooting.degraded', category: 'troubleshooting',
            title: 'Status shows degraded',
            body: [
                '<p>The broker booted but one or more health checks reported a warning &mdash; for example, the bundled PAX script did not verify, or a workspace path is missing. Open <strong>Settings</strong> and review the Integrity and Environment paths cards.</p>'
            ]
        },
        'troubleshooting.support-mode': {
            id: 'troubleshooting.support-mode', category: 'troubleshooting',
            title: 'Support Mode',
            keywords: ['support', 'mode', 'console', 'visible', 'logs', 'diagnostics', 'shortcut'],
            body: [
                '<p>A normal PAX Cookbook launch hides the local server window. <strong>Support Mode</strong> is a separate Start Menu shortcut that opens PAX Cookbook with the server window and recent log and status information visible for diagnostic work.</p>',
                '<p>Support Mode does <strong>not</strong> restart the server. If the server is already running it reuses it; closing the Support Mode window does not stop it.</p>'
            ]
        },
        'troubleshooting.stop-pax-cookbook': {
            id: 'troubleshooting.stop-pax-cookbook', category: 'troubleshooting',
            title: 'Stop PAX Cookbook',
            keywords: ['stop', 'shutdown', 'exit', 'quit', 'server', 'session', 'lock', 'tray'],
            body: [
                '<p>To fully stop PAX Cookbook, choose <strong>Exit</strong> — from the system tray icon (right-click the PAX Cookbook tray icon → Exit) or from the close dialog when you close the window. Exit asks the running background broker to shut down cleanly and ends the session.</p>',
                '<p>Closing the window alone leaves the background broker running so scheduled bakes still fire and the next launch is fast. Use Exit when you want to end the session completely; the next launch then starts a fresh broker.</p>'
            ]
        },
        'troubleshooting.uninstall-pax-cookbook': {
            id: 'troubleshooting.uninstall-pax-cookbook', category: 'troubleshooting',
            title: 'Uninstall PAX Cookbook',
            keywords: ['uninstall', 'remove', 'delete', 'shortcut', 'workspace', 'purge'],
            body: [
                '<p><strong>Uninstall PAX Cookbook</strong> is a Start Menu shortcut that removes PAX Cookbook from this Windows user profile. Clicking it shows a confirmation dialog that spells out exactly what will and will not be deleted; the default focus is <strong>No</strong>, so pressing Enter cancels safely.</p>',
                '<p>On confirmation, uninstall removes the installed app files, the Backups and Updates folders, all PAX Cookbook Start Menu shortcuts, and the PAX-owned Desktop shortcut. Uninstall <strong>does not</strong> delete workspace folders (recipes, bake outputs, runtime state), external folders where you previously exported data, Purview / Entra / Microsoft 365 tenant data, or Windows scheduled tasks you created with the Schedule card. Workspaces live at user-chosen paths the installer does not track; remove them by hand if you want to.</p>',
                '<p>If the dialog refuses with "PAX Cookbook is currently running", choose <strong>Exit</strong> from the tray icon or close dialog first, wait for the app to close, and try again.</p>'
            ]
        },

        // -------- Glossary --------
        'glossary.recipe': {
            id: 'glossary.recipe', category: 'glossary',
            title: 'Recipe',
            body: ['<p>The editable execution plan for one PAX query. Created by materializing a Pantry template.</p>']
        },
        'glossary.cook': {
            id: 'glossary.cook', category: 'glossary',
            title: 'Bake',
            keywords: ['bake', 'cook', 'run', 'execution'],
            body: ['<p>One execution of a recipe. Cookbook freezes the recipe snapshot, command, sentinels, and outputs for each bake.</p>']
        },
        'glossary.pantry': {
            id: 'glossary.pantry', category: 'glossary',
            title: 'Pantry',
            body: ['<p>The bundled, read-only set of audit-log templates that ship with this Cookbook install.</p>']
        },
        'glossary.template': {
            id: 'glossary.template', category: 'glossary',
            title: 'Template',
            body: ['<p>A curated blueprint in the Pantry. Templates are never executed; you materialize one into a recipe and run the recipe.</p>']
        },
        'glossary.sentinel': {
            id: 'glossary.sentinel', category: 'glossary',
            title: 'Sentinel',
            body: ['<p>A small marker file Cookbook writes during a bake to record lifecycle transitions: started, finished, interrupted. Used to make bake state reconstructable from disk.</p>']
        },
        'glossary.artifact': {
            id: 'glossary.artifact', category: 'glossary',
            title: 'Artifact',
            body: ['<p>An output file produced by a bake &mdash; typically the Rollup fact CSV and any companion files PAX wrote alongside it.</p>']
        },
        'glossary.auth-profile': {
            id: 'glossary.auth-profile', category: 'glossary',
            title: 'Chef\u2019s Key',
            keywords: ['chef key', 'auth profile', 'credential', 'entra', 'app registration'],
            body: ['<p>A saved pairing of an Entra app registration and a workload credential, bound to your Windows account via Credential Manager. A Chef&rsquo;s Key lets a recipe authenticate as an Entra workload without prompting at bake time.</p>']
        }
    };

    // ---------------------------------------------------------------
    // Panel state and DOM refs
    // ---------------------------------------------------------------

    var refs = null;            // {panel, scrim, title, body, search, btnHome, btnClose, btnTopbar}
    var currentTopicId = null;  // last topic shown (for search reset etc.)
    var lastFocus = null;       // element to return focus to on close

    function byId(id) { return document.getElementById(id); }

    function captureRefs() {
        refs = {
            panel:     byId('help-panel'),
            scrim:     byId('help-panel-scrim'),
            title:     byId('help-panel-title'),
            body:      byId('help-panel-body'),
            search:    byId('help-panel-search'),
            btnHome:   byId('help-panel-home'),
            btnClose:  byId('help-panel-close'),
            btnTopbar: byId('help-button')
        };
        return refs && refs.panel && refs.body;
    }

    // ---------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------

    function escapeHtml(s) {
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function renderHome() {
        currentTopicId = 'home';
        refs.title.textContent = 'Help';
        refs.btnTopbar && refs.btnTopbar.setAttribute('aria-expanded', 'true');

        var html = [];
        html.push(
            '<button type="button" class="help-panel-doclink" data-help-userguide="1" style="display:flex;align-items:center;gap:10px;width:100%;text-align:left;padding:12px 14px;margin:0 0 14px 0;border:1px solid #c7d2e8;border-radius:8px;background:#eef4ff;cursor:pointer;">',
                '<span aria-hidden="true" style="font-size:22px;line-height:1;">📖</span>',
                '<span style="display:flex;flex-direction:column;gap:2px;">',
                    '<span style="font-weight:600;color:#0a1f44;font-size:14px;">PAX Cookbook User Guide</span>',
                    '<span style="color:#4a5568;font-size:12px;">Complete guide covering installation, recipes, bakes, and every feature</span>',
                '</span>',
            '</button>'
        );
        html.push('<p class="help-panel-intro">Pick a category, search above, or use the small <strong>?</strong> next to any control on the page.</p>');
        html.push('<div class="help-panel-grid">');
        for (var i = 0; i < CATEGORIES.length; i += 1) {
            var c = CATEGORIES[i];
            html.push(
                '<button type="button" class="help-panel-card" data-help-category="' + escapeHtml(c.id) + '">',
                    '<span class="help-panel-card-title">' + escapeHtml(c.title) + '</span>',
                    '<span class="help-panel-card-summary">' + escapeHtml(c.summary) + '</span>',
                '</button>'
            );
        }
        html.push('</div>');
        refs.body.innerHTML = html.join('');
        try { refs.body.scrollTop = 0; } catch (e) {}
    }

    function renderCategory(catId) {
        var cat = null;
        for (var i = 0; i < CATEGORIES.length; i += 1) {
            if (CATEGORIES[i].id === catId) { cat = CATEGORIES[i]; break; }
        }
        if (!cat) { renderHome(); return; }
        currentTopicId = 'category:' + catId;
        refs.title.textContent = cat.title;

        var topics = [];
        for (var key in TOPICS) {
            if (!Object.prototype.hasOwnProperty.call(TOPICS, key)) { continue; }
            if (TOPICS[key].category === catId) { topics.push(TOPICS[key]); }
        }
        var html = ['<p class="help-panel-intro">' + escapeHtml(cat.summary) + '</p>'];
        html.push('<ul class="help-panel-topic-list">');
        if (topics.length === 0) {
            html.push('<li class="help-panel-topic-empty">No topics yet in this category.</li>');
        } else {
            for (var j = 0; j < topics.length; j += 1) {
                var t = topics[j];
                html.push(
                    '<li>',
                        '<button type="button" class="help-panel-topic-link" data-help-topic="' + escapeHtml(t.id) + '">',
                            escapeHtml(t.title),
                        '</button>',
                    '</li>'
                );
            }
        }
        html.push('</ul>');
        refs.body.innerHTML = html.join('');
        try { refs.body.scrollTop = 0; } catch (e) {}
    }

    function renderTopic(topicId) {
        var t = TOPICS[topicId];
        if (!t) {
            currentTopicId = topicId;
            refs.title.textContent = 'Help';
            refs.body.innerHTML = [
                '<p class="help-panel-intro"><strong>Coverage gap.</strong> No help topic is registered for <code>' + escapeHtml(topicId) + '</code> yet. This is an authoring miss, not a feature problem.</p>',
                '<p><button type="button" class="help-panel-topic-link" data-help-topic-home="1">Back to Help home</button></p>'
            ].join('');
            return;
        }
        currentTopicId = t.id;
        refs.title.textContent = t.title;
        var html = [];
        var catLabel = '';
        if (t.category) {
            for (var i = 0; i < CATEGORIES.length; i += 1) {
                if (CATEGORIES[i].id === t.category) { catLabel = CATEGORIES[i].title; break; }
            }
        }
        if (catLabel) {
            html.push(
                '<div class="help-panel-crumbs">',
                    '<button type="button" class="help-panel-crumb-link" data-help-topic-home="1">Help home</button>',
                    '<span class="help-panel-crumb-sep" aria-hidden="true"> &rsaquo; </span>',
                    '<button type="button" class="help-panel-crumb-link" data-help-category="' + escapeHtml(t.category) + '">' + escapeHtml(catLabel) + '</button>',
                '</div>'
            );
        }
        html.push('<article class="help-panel-topic">');
        html.push(t.body.join(''));
        html.push('</article>');
        refs.body.innerHTML = html.join('');
        try { refs.body.scrollTop = 0; } catch (e) {}
    }

    function renderSearch(query) {
        var q = String(query || '').trim().toLowerCase();
        if (q.length === 0) { renderHome(); return; }
        currentTopicId = 'search:' + q;
        refs.title.textContent = 'Help search';

        var hits = [];
        for (var key in TOPICS) {
            if (!Object.prototype.hasOwnProperty.call(TOPICS, key)) { continue; }
            var t = TOPICS[key];
            if (t.id === 'home') { continue; }
            var hay = (t.title || '') + ' ' + (t.category || '') + ' ' + ((t.keywords || []).join(' ')) + ' ' + (t.body || []).join(' ');
            if (hay.toLowerCase().indexOf(q) !== -1) { hits.push(t); }
        }
        var html = ['<p class="help-panel-intro">' + hits.length + ' result' + (hits.length === 1 ? '' : 's') + ' for &ldquo;' + escapeHtml(q) + '&rdquo;.</p>'];
        if (hits.length === 0) {
            html.push('<p>No topics matched. Try the categories on <button type="button" class="help-panel-topic-link" data-help-topic-home="1">Help home</button>.</p>');
        } else {
            html.push('<ul class="help-panel-topic-list">');
            for (var i = 0; i < hits.length; i += 1) {
                html.push(
                    '<li>',
                        '<button type="button" class="help-panel-topic-link" data-help-topic="' + escapeHtml(hits[i].id) + '">',
                            escapeHtml(hits[i].title),
                        '</button>',
                    '</li>'
                );
            }
            html.push('</ul>');
        }
        refs.body.innerHTML = html.join('');
        try { refs.body.scrollTop = 0; } catch (e) {}
    }

    // ---------------------------------------------------------------
    // Open / close
    // ---------------------------------------------------------------

    function openPanel(topicId) {
        if (!refs && !captureRefs()) { return; }
        lastFocus = document.activeElement;
        refs.panel.hidden = false;
        refs.panel.setAttribute('aria-hidden', 'false');
        refs.scrim.hidden = false;
        refs.btnTopbar && refs.btnTopbar.setAttribute('aria-expanded', 'true');
        document.body.classList.add('help-panel-open');
        if (topicId && topicId !== 'home') {
            renderTopic(topicId);
        } else {
            renderHome();
        }
        // Focus management: search box first, then panel body.
        try { refs.search.focus(); } catch (e) {}
    }

    function closePanel() {
        if (!refs) { return; }
        refs.panel.hidden = true;
        refs.panel.setAttribute('aria-hidden', 'true');
        refs.scrim.hidden = true;
        refs.btnTopbar && refs.btnTopbar.setAttribute('aria-expanded', 'false');
        document.body.classList.remove('help-panel-open');
        if (lastFocus && typeof lastFocus.focus === 'function') {
            try { lastFocus.focus(); } catch (e) {}
        }
        lastFocus = null;
    }

    function togglePanel() {
        if (!refs && !captureRefs()) { return; }
        if (refs.panel.hidden) { openPanel('home'); } else { closePanel(); }
    }

    // Open the PAX Cookbook User Guide in the in-app Pantry viewer. Hands the
    // Pantry surface a one-shot intent flag (shared on this top window with the
    // embedded React iframe, same origin) and switches the shell to the Pantry.
    // Also nudges the embedded surface directly in case it is already showing
    // the Pantry (same hash fires no hashchange). Navigation only -- no HTTP,
    // no storage, no external browser.
    function openUserGuideInPantry() {
        try { window.__paxPantryDocIntent = 'userguide'; } catch (e) {}
        try { window.location.hash = '#/pantry'; } catch (e) {}
        try {
            var frames = document.querySelectorAll('iframe');
            for (var i = 0; i < frames.length; i += 1) {
                try {
                    frames[i].contentWindow.postMessage(
                        { type: 'mk-nav', section: 'pantry' },
                        window.location.origin
                    );
                } catch (e) {}
            }
        } catch (e) {}
        closePanel();
    }

    // ---------------------------------------------------------------
    // Lock Session
    // ---------------------------------------------------------------
    //
    // Locks the broker in place and mounts the unlock overlay, so the
    // operator can step away from the machine and unlock with Windows
    // Hello when they come back. The broker is the source of truth
    // for lock state -- merely clearing client-side storage would
    // leave the broker Unlocked and any subsequent fetch (or a hard
    // reload that pulls the bearer token from the sidecar) would
    // walk straight back into the app. So:
    //
    //   1. POST /api/v1/broker/lock so the broker transitions to
    //      Locked. From now on every non-allowlisted route returns
    //      423, and a hard reload's boot-time /broker/lock-state
    //      probe will mount the overlay too.
    //   2. On success: dispatch the same 'cookbook:brokerLocked'
    //      event api.js fires on 423 responses. lock-overlay.js owns
    //      the UI from there; we do NOT reload the page (the overlay
    //      gates the whole SPA in place).
    //   3. On failure: surface a visible alert. No silent fall-back,
    //      no token-clear-and-reload, because that would lie about
    //      the broker's state to the operator.

    function lockSession() {
        if (!window.cookbookApi || typeof window.cookbookApi.post !== 'function') {
            // api.js loads before help-panel.js in index.html, so
            // this branch should be unreachable in production. If it
            // ever fires, surface it -- do NOT silently fall through
            // to a reload that would suggest the lock worked.
            try { window.alert('Lock Session failed: cookbook API helper is not loaded.'); } catch (e) {}
            return;
        }
        var btn = byId('lock-session-button');
        if (btn && btn.disabled) { return; }
        if (btn) { btn.disabled = true; }
        window.cookbookApi.post('/api/v1/broker/lock', {}).then(function (resp) {
            if (btn) { btn.disabled = false; }
            if (resp && resp.ok) {
                // Broker is now Locked. Mount the unlock overlay by
                // dispatching the same event the api.js 423 path
                // fires; lock-overlay.js listens for it and owns
                // both the WebAuthn unlock UI and the diagnostic
                // fall-back. We do NOT reload -- the overlay is a
                // modal scrim that gates the live SPA in place.
                try {
                    window.dispatchEvent(new CustomEvent('cookbook:brokerLocked', {
                        detail: {
                            code:            'brokerLocked',
                            message:         'Session locked by operator.',
                            attemptedMethod: null,
                            attemptedPath:   null,
                            timestampUtc:    new Date().toISOString()
                        }
                    }));
                } catch (e) {}
                return;
            }
            var msg = 'Lock Session failed: broker did not accept the lock request';
            if (resp && resp.status) { msg += ' (HTTP ' + resp.status + ')'; }
            if (resp && resp.networkError) { msg += ' [' + resp.networkError + ']'; }
            msg += '. The session is NOT locked. Try again or stop the broker manually.';
            try { window.alert(msg); } catch (e) {}
        });
    }

    // ---------------------------------------------------------------
    // Wire-up
    // ---------------------------------------------------------------

    function onDocumentClick(ev) {
        var target = ev.target;
        if (!target || typeof target.closest !== 'function') { return; }

        var insidePanel = target.closest('#help-panel');

        // 1. Help triggers anywhere OUTSIDE the panel ('?' affordance,
        //    page-body buttons that link into the panel, e.g. the
        //    Settings -> Local Diagnostics 'Troubleshooting' link)
        //    open the panel to the requested topic. Using the actual
        //    DOM location (insidePanel) lets ANY element with
        //    [data-help-topic] reach openPanel; clicks inside the panel
        //    fall through to step 2 below so they call renderTopic
        //    against the already-open panel rather than re-opening it.
        var trigger = target.closest('[data-help-topic]');
        if (trigger && !insidePanel) {
            ev.preventDefault();
            openPanel(trigger.getAttribute('data-help-topic'));
            return;
        }

        // 2. Inside the panel: topic links, category cards, home crumbs.
        if (insidePanel) {
            var userGuideLink = target.closest('[data-help-userguide]');
            if (userGuideLink) {
                ev.preventDefault();
                openUserGuideInPantry();
                return;
            }
            var topicLink = target.closest('[data-help-topic]');
            if (topicLink) {
                ev.preventDefault();
                renderTopic(topicLink.getAttribute('data-help-topic'));
                return;
            }
            var catLink = target.closest('[data-help-category]');
            if (catLink) {
                ev.preventDefault();
                renderCategory(catLink.getAttribute('data-help-category'));
                return;
            }
            var homeLink = target.closest('[data-help-topic-home]');
            if (homeLink) {
                ev.preventDefault();
                renderHome();
                return;
            }
        }

        // 3. Scrim click -> close.
        if (refs && target === refs.scrim) {
            closePanel();
            return;
        }
    }

    function onSearchInput() {
        if (!refs) { return; }
        renderSearch(refs.search.value);
    }

    function onKeyDown(ev) {
        if (!refs || refs.panel.hidden) { return; }
        if (ev.key === 'Escape' || ev.key === 'Esc') {
            ev.preventDefault();
            closePanel();
        }
    }

    function init() {
        if (!captureRefs()) { return; }
        refs.btnTopbar && refs.btnTopbar.addEventListener('click', function (ev) {
            ev.preventDefault();
            togglePanel();
        });
        refs.btnClose && refs.btnClose.addEventListener('click', function (ev) {
            ev.preventDefault();
            closePanel();
        });
        refs.btnHome && refs.btnHome.addEventListener('click', function (ev) {
            ev.preventDefault();
            if (refs.search) { refs.search.value = ''; }
            renderHome();
        });
        refs.search && refs.search.addEventListener('input', onSearchInput);

        // Lock Session button.
        var lockBtn = byId('lock-session-button');
        if (lockBtn) {
            lockBtn.addEventListener('click', function (ev) {
                ev.preventDefault();
                lockSession();
            });
        }

        document.addEventListener('click', onDocumentClick, false);
        document.addEventListener('keydown', onKeyDown, false);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init, { once: true });
    } else {
        init();
    }

    // ---------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------
    //
    // window.PaxHelp.hook(topicId, opts)
    //   Returns an HTML string for a "?" affordance button bound to
    //   the given topic id. Pages that build markup as string
    //   templates can splice the result directly.
    //
    // window.PaxHelp.hookEl(topicId, opts)
    //   Returns an HTMLButtonElement form of the same affordance for
    //   pages that build with the el() helper.
    //
    // window.PaxHelp.open(topicId)
    //   Opens the panel to the given topic id (or 'home').
    //
    // window.PaxHelp.close()
    //   Closes the panel.

    function buildHookAttributes(topicId, opts) {
        var label = (opts && opts.label) ? opts.label : 'Help';
        var attrs = {
            type: 'button',
            'class': 'help-hook',
            'data-help-topic': topicId,
            'aria-label': label,
            title: label
        };
        return attrs;
    }

    function hookHtml(topicId, opts) {
        var attrs = buildHookAttributes(topicId, opts);
        return [
            '<button type="', escapeHtml(attrs.type), '" class="', escapeHtml(attrs['class']),
            '" data-help-topic="', escapeHtml(attrs['data-help-topic']),
            '" aria-label="', escapeHtml(attrs['aria-label']),
            '" title="', escapeHtml(attrs.title), '">',
                '<svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg>',
            '</button>'
        ].join('');
    }

    function hookEl(topicId, opts) {
        var btn = document.createElement('button');
        var attrs = buildHookAttributes(topicId, opts);
        btn.type = 'button';
        btn.className = attrs['class'];
        btn.setAttribute('data-help-topic', attrs['data-help-topic']);
        btn.setAttribute('aria-label', attrs['aria-label']);
        btn.title = attrs.title;
        btn.innerHTML = '<svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg>';
        return btn;
    }

    window.PaxHelp = {
        hook: hookHtml,
        hookEl: hookEl,
        open: function (topicId) { openPanel(topicId || 'home'); },
        close: closePanel,
        // Exposed for the smoke test only; not used by page modules.
        _topics: TOPICS,
        _categories: CATEGORIES
    };
})();
