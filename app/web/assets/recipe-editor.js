// PAX Cookbook -- recipe editor.
//
// Page module for routes '#/recipes/new' and '#/recipes/<id>'. Owns the
// DOM under #page-root for the duration it is mounted; on teardown,
// drops its listeners and refs so the router can clear the container.
//
// Editor doctrine (load-bearing):
//
//   The browser is NOT authoritative. The server is. The DOM is the
//   editing surface; in-memory state is intentionally tiny and exists
//   only to support the Save guardrail and dirty-detection. There is
//   no synchronized form model, no observer system, no reactive store,
//   no schema-driven rendering, no autosave, no draft recovery, no
//   browser persistence, no optimistic UI, no background mutation.
//
//   AJV is validation-only: it inspects payloads. It does not render
//   forms, inject defaults, or interpret schema at runtime beyond
//   validate(). Humans designed the form.
//
//   extractPayloadFromDom() is the critical pure boundary: read DOM,
//   construct payload, return. No mutation, no normalization, no
//   default injection, no side effects.
//
// Preview is an explicit, operational, button-only action. Each click
// extracts a fresh DOM payload, runs the same AJV pre-flight as Save,
// and POSTs to /api/v1/recipes/preview. The preview rail renders the
// server's command response (or any validation / transport error)
// into a single wholesale-replaced subtree. Preview is informational
// only: it never mutates form fields, dirty-state, Save lifecycle,
// the per-field error slots, or any browser-side persistence. There
// is no preview cache, no preview history, no debounce, no scheduler,
// no AbortController, no MutationObserver. Stale preview responses
// are dropped by a per-mount monotonic sequence counter and the
// same session-drop guard Save uses. Save and Preview are mutually
// exclusive: only one operation may be in flight at a time.
(function () {
    'use strict';

    // ----------------------------------------------------------------
    // Page template
    // ----------------------------------------------------------------

    // The form layout is hand-designed. Field IDs are stable: the
    // path-to-id and id-to-path lookups below depend on them, and the
    // AJV instancePath mapping for error placement uses them. Do not
    // rename without updating both lookups.
    var PAGE_TEMPLATE = [
        '<nav class="breadcrumb" aria-label="Breadcrumb">',
            '<a href="#/recipes">Recipes</a>',
            '<span class="breadcrumb-sep" aria-hidden="true"> / </span>',
            '<span>{{CRUMB}}</span>',
        '</nav>',
        '<section class="page recipe-editor">',
            '<header class="page-header">',
                '<div class="page-header-text">',
                    '<h1 class="page-title">{{TITLE}}<button type="button" class="help-hook" data-help-topic="recipes.intro" aria-label="Help: About recipes" title="About recipes"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h1>',
                '</div>',
                '<div class="editor-actions">',
                    '<a href="#/recipes" id="cancel-link" class="btn-ghost">Cancel</a>',
                    '<button type="button" id="preview-button" class="btn-ghost">Preview</button>',
                    '<button type="button" id="readiness-button" class="btn-ghost" hidden>Check readiness</button>',
                    // X11A: pin / unpin toggle. Edit-mode only (revealed
                    // at mount when state.isNew is false). The control
                    // both shows the current pinned state and is the
                    // action that flips it; it manages its own in-flight
                    // disabled state and never participates in the
                    // Save / Preview / Bake mutual-exclusion gate.
                    '<button type="button" id="pin-toggle-button" class="btn-ghost" hidden>\u2606 Pin</button>',
                    // F2F: Export Recipe Takeout. Secondary action;
                    // visible only in edit mode (revealed at mount()
                    // when state.isNew is false) and only enabled
                    // against a clean saved recipe (no in-flight
                    // operation, no unsaved changes). Filename is
                    // transport only -- never the recipe identity.
                    '<button type="button" id="export-takeout-button" class="btn-ghost" hidden>Export Recipe Takeout</button>',
                    '<button type="button" id="save-button" class="btn-primary" disabled>Save</button>',
                    '<button type="button" id="cook-button" class="btn-primary" hidden>Bake Recipe</button>',
                '</div>',
            '</header>',
            // Provenance subtitle. Populated by populateForm() only when
            // state.initialRecipe.createdBy.fromTemplate is present
            // (recipes that were materialized from a bundled Pantry
            // template). It is read-only, never enters extractPayloadFromDom,
            // never mutates state.initialRecipe, and stays hidden if
            // the recipe has no template origin (Phase J req #12).
            '<div id="editor-origin" class="editor-origin" hidden></div>',
            '<div id="editor-banner" class="editor-banner" role="status" aria-live="polite" hidden></div>',
            '<div class="editor-grid">',
                '<div class="editor-cards">',

                    '<article class="editor-card" aria-labelledby="editor-card-what">',
                        '<h2 id="editor-card-what" class="editor-card-title">What</h2>',
                        '<p class="editor-card-doc">The recipe\u2019s human-readable identity and which optional ingredients PAX collects.</p>',
                        '<div class="form-row">',
                            '<label for="fld-name" class="form-label">Name</label>',
                            '<input type="text" id="fld-name" class="form-input" maxlength="200" autocomplete="off" aria-describedby="fld-name-hint fld-name-error">',
                            '<div id="fld-name-hint" class="form-hint">Stored verbatim in <code>identity.name</code>. Used only for display in the recipe list.</div>',
                            '<div id="fld-name-error" class="field-error" role="alert"></div>',
                        '</div>',
                        // V1.S26: query.mode picker. The two values name
                        // the two supported PAX run shapes -- audit (the
                        // default; collects the audit log, optionally
                        // with the M365 usage bundle) and userInfoOnly
                        // (queries only Entra directory data and emits
                        // no audit fact output). User-info-only hides
                        // the audit-only controls (dates, filters,
                        // M365 usage, fact output, rollup).
                        '<div class="form-row">',
                            '<label for="fld-queryMode" class="form-label">Query mode</label>',
                            '<select id="fld-queryMode" class="form-input" aria-describedby="fld-queryMode-hint fld-queryMode-error">',
                                '<option value="audit">Audit query (default)</option>',
                                '<option value="userInfoOnly">User info only (no audit query)</option>',
                            '</select>',
                            '<div id="fld-queryMode-hint" class="form-hint">Picks the supported PAX run shape. <code>Audit query</code> reads the Microsoft 365 audit log, with optional rollup and audit filters. <code>User info only</code> queries Entra directory user / license data and emits no audit fact output; date window, audit filters, M365 usage, and the fact output destination are skipped.</div>',
                            '<div id="fld-queryMode-error" class="field-error" role="alert"></div>',
                        '</div>',
                        '<div id="fld-m365-row" class="form-row form-row-check">',
                            '<input type="checkbox" id="fld-m365" class="form-checkbox" aria-describedby="fld-m365-hint fld-m365-error">',
                            '<label for="fld-m365" class="form-label-inline">Include M365 usage</label>',
                            '<div id="fld-m365-hint" class="form-hint">Adds the <code>-IncludeM365Usage</code> switch to the PAX command. Joins each row with Microsoft 365 active-user signals.</div>',
                            '<div id="fld-m365-error" class="field-error" role="alert"></div>',
                        '</div>',
                        // V1.S26: ingredients.m365Usage.includeCopilotInteraction.
                        // Default-checked = include CopilotInteraction
                        // alongside the M365 usage bundle (default true at
                        // the adapter; omitted from the saved recipe).
                        // Unchecked = save includeCopilotInteraction:false,
                        // which projects -ExcludeCopilotInteraction. Only
                        // visible when M365 usage is on; reset to checked
                        // and never saved when M365 usage is off.
                        '<div id="fld-includeCp-row" class="form-row form-row-check" hidden>',
                            '<input type="checkbox" id="fld-includeCp" class="form-checkbox" aria-describedby="fld-includeCp-hint fld-includeCp-error" checked>',
                            '<label for="fld-includeCp" class="form-label-inline">Include CopilotInteraction audit data</label>',
                            '<div id="fld-includeCp-hint" class="form-hint">When unchecked, the recipe saves <code>includeCopilotInteraction:false</code> and PAX adds <code>-ExcludeCopilotInteraction</code> so the audit query collects only the M365 usage activity types. Only meaningful when <em>Include M365 usage</em> is checked.</div>',
                            '<div id="fld-includeCp-error" class="field-error" role="alert"></div>',
                        '</div>',
                        '<div class="form-row form-row-check">',
                            '<input type="checkbox" id="fld-userInfo" class="form-checkbox" aria-describedby="fld-userInfo-hint fld-userInfo-error">',
                            '<label for="fld-userInfo" class="form-label-inline">Include Entra user info</label>',
                            '<div id="fld-userInfo-hint" class="form-hint">Adds the <code>-IncludeUserInfo</code> switch. Joins each row with Entra directory attributes (job title, department, manager).</div>',
                            '<div id="fld-userInfo-error" class="field-error" role="alert"></div>',
                        '</div>',
                    '</article>',

                    '<article id="editor-when-card" class="editor-card" aria-labelledby="editor-card-when">',
                        '<h2 id="editor-card-when" class="editor-card-title">When</h2>',
                        '<p class="editor-card-doc">PAX query window. Both dates are sent verbatim as <code>-StartDate</code> / <code>-EndDate</code>. End date must be on or after start date.</p>',
                        '<div class="form-row form-row-two">',
                            '<div class="form-field">',
                                '<label for="fld-startDate" class="form-label">Start date</label>',
                                '<input type="date" id="fld-startDate" class="form-input" aria-describedby="fld-startDate-error">',
                                '<div id="fld-startDate-error" class="field-error" role="alert"></div>',
                            '</div>',
                            '<div class="form-field">',
                                '<label for="fld-endDate" class="form-label">End date</label>',
                                '<input type="date" id="fld-endDate" class="form-input" aria-describedby="fld-endDate-error">',
                                '<div id="fld-endDate-error" class="field-error" role="alert"></div>',
                            '</div>',
                        '</div>',
                    '</article>',

                    // V1.S26: audit filters card. Hidden in full when
                    // query.mode='userInfoOnly' (Shape 3 forbids all
                    // of these fields). Otherwise each control is
                    // optional; empty lists / 'none' / 'No filter'
                    // map to schema omission.
                    '<article id="editor-filters-card" class="editor-card" aria-labelledby="editor-card-filters">',
                        '<h2 id="editor-card-filters" class="editor-card-title">Audit filters</h2>',
                        '<p class="editor-card-doc">Narrow the audit query. All filters are optional. Under rollup, ActivityTypes (if explicitly projected) must be <code>CopilotInteraction</code>.</p>',
                        '<div class="form-row form-row-check">',
                            '<input type="checkbox" id="fld-activityTypesExplicit" class="form-checkbox" aria-describedby="fld-activityTypesExplicit-hint fld-activityTypesExplicit-error">',
                            '<label for="fld-activityTypesExplicit" class="form-label-inline">Explicitly project <code>-ActivityTypes CopilotInteraction</code></label>',
                            '<div id="fld-activityTypesExplicit-hint" class="form-hint">When checked, the recipe saves <code>query.activityTypes = ["CopilotInteraction"]</code>. PAX defaults to CopilotInteraction under rollup, so this is normally omitted; check it to make the projection explicit. RecordTypes / ServiceTypes are not supported by Cookbook.</div>',
                            '<div id="fld-activityTypesExplicit-error" class="field-error" role="alert"></div>',
                        '</div>',
                        '<div class="form-row">',
                            '<label for="fld-userIds" class="form-label">Limit to users</label>',
                            '<textarea id="fld-userIds" class="form-input form-input-mono" rows="3" spellcheck="false" placeholder="alice@contoso.com&#10;bob@contoso.com" aria-describedby="fld-userIds-hint fld-userIds-error"></textarea>',
                            '<div id="fld-userIds-hint" class="form-hint">One UPN per line. Each non-empty line is sent as one entry under <code>-UserIds</code>. Leave blank to query all users.</div>',
                            '<div id="fld-userIds-error" class="field-error" role="alert"></div>',
                        '</div>',
                        '<div class="form-row">',
                            '<label for="fld-groupNames" class="form-label">Limit to groups</label>',
                            '<textarea id="fld-groupNames" class="form-input form-input-mono" rows="3" spellcheck="false" placeholder="Finance Team&#10;Sales Team" aria-describedby="fld-groupNames-hint fld-groupNames-error"></textarea>',
                            '<div id="fld-groupNames-hint" class="form-hint">One group display name per line. Each non-empty line is sent as one entry under <code>-GroupNames</code>. Group expansion requires <code>GroupMember.Read.All</code> (shown in the Required permissions rail).</div>',
                            '<div id="fld-groupNames-error" class="field-error" role="alert"></div>',
                        '</div>',
                        '<div class="form-row">',
                            '<label for="fld-agentFilterMode" class="form-label">Agent activity filter</label>',
                            '<select id="fld-agentFilterMode" class="form-input" aria-describedby="fld-agentFilterMode-hint fld-agentFilterMode-error">',
                                '<option value="none">No agent filter (default)</option>',
                                '<option value="agentIds">Specific agent IDs only</option>',
                                '<option value="agentsOnly">Agent activity only</option>',
                                '<option value="excludeAgents">Exclude agent activity</option>',
                            '</select>',
                            '<div id="fld-agentFilterMode-hint" class="form-hint">Picks exactly one of PAX\u2019s mutually-exclusive agent switches: <code>-AgentId</code>, <code>-AgentsOnly</code>, <code>-ExcludeAgents</code>. The native trio is never emitted simultaneously. Agent365Info parameters are not supported.</div>',
                            '<div id="fld-agentFilterMode-error" class="field-error" role="alert"></div>',
                        '</div>',
                        '<div id="fld-agentIds-row" class="form-row" hidden>',
                            '<label for="fld-agentIds" class="form-label">Agent IDs</label>',
                            '<textarea id="fld-agentIds" class="form-input form-input-mono" rows="3" spellcheck="false" placeholder="agent-id-1&#10;agent-id-2" aria-describedby="fld-agentIds-hint fld-agentIds-error"></textarea>',
                            '<div id="fld-agentIds-hint" class="form-hint">One agent id per line. Required when the filter is <em>Specific agent IDs only</em>; sent verbatim under <code>-AgentId</code>. Forbidden for the other three filter modes.</div>',
                            '<div id="fld-agentIds-error" class="field-error" role="alert"></div>',
                        '</div>',
                        '<div class="form-row">',
                            '<label for="fld-promptFilter" class="form-label">Prompt filter</label>',
                            '<select id="fld-promptFilter" class="form-input" aria-describedby="fld-promptFilter-hint fld-promptFilter-error">',
                                '<option value="">No filter (default)</option>',
                                '<option value="Prompt">Prompt</option>',
                                '<option value="Response">Response</option>',
                                '<option value="Both">Both</option>',
                                '<option value="Null">Null</option>',
                            '</select>',
                            '<div id="fld-promptFilter-hint" class="form-hint">This filters by prompt/response presence. Use intentionally. Sent as <code>-PromptFilter</code> when set; omitted otherwise.</div>',
                            '<div id="fld-promptFilter-error" class="field-error" role="alert"></div>',
                        '</div>',
                    '</article>',

                    '<article id="editor-where-card" class="editor-card" aria-labelledby="editor-card-where">',
                        '<div class="editor-card-title-row">',
                            '<h2 id="editor-card-where" class="editor-card-title">Where</h2>',
                            '<button type="button" class="help-hook editor-card-help" data-help-topic="permissions.required" aria-label="Open help: Rollup output and destination">?</button>',
                        '</div>',
                        '<p class="editor-card-doc">PAX writes a Rollup fact CSV at the path below. OneLake and Fabric destinations are rejected at save.</p>',
                        '<div id="fld-rollup-row" class="form-row">',
                            '<label for="fld-rollup" class="form-label">Rollup output mode</label>',
                            '<select id="fld-rollup" class="form-input" aria-describedby="fld-rollup-hint fld-rollup-error">',
                                '<option value="Rollup">Rollup (create dashboard-ready rollup output)</option>',
                                '<option value="RollupPlusRaw">Rollup + raw (create dashboard-ready rollup output and keep raw output)</option>',
                            '</select>',
                            '<div id="fld-rollup-hint" class="form-hint"><code>Rollup</code> creates dashboard-ready rollup output. <code>Rollup + raw</code> creates dashboard-ready rollup output and keeps raw output. Sent as <code>-Rollup</code> or <code>-RollupPlusRaw</code>; never both.</div>',
                            '<div id="fld-rollup-error" class="field-error" role="alert"></div>',
                        '</div>',
                        // V1.S26: destinations.fact.mode picker. The
                        // two values name PAX's mutually-exclusive
                        // fact output switches: outputPath -> -OutputPath,
                        // append -> -AppendFile. The adapter never
                        // emits both simultaneously. Legacy recipes
                        // that carry only destinations.fact.appendBehavior
                        // load as the matching mode value
                        // (fresh -> outputPath, append -> append); the
                        // SPA does not persist appendBehavior on save.
                        '<div id="fld-factMode-row" class="form-row">',
                            '<label for="fld-factMode" class="form-label">Fact output mode</label>',
                            '<select id="fld-factMode" class="form-input" aria-describedby="fld-factMode-hint fld-factMode-error">',
                                '<option value="outputPath">Write new fact output (default)</option>',
                                '<option value="append">Append to an existing fact CSV</option>',
                            '</select>',
                            '<div id="fld-factMode-hint" class="form-hint">Picks PAX\u2019s mutually-exclusive fact output switches: <code>-OutputPath &lt;path&gt;</code> for write, or <code>-AppendFile &lt;file&gt;</code> for append. Never both.</div>',
                            '<div id="fld-factMode-error" class="field-error" role="alert"></div>',
                        '</div>',
                        '<div id="fld-outputPath-row" class="form-row">',
                            '<label for="fld-outputPath" class="form-label">Output path</label>',
                            '<input type="text" id="fld-outputPath" class="form-input form-input-mono" autocomplete="off" spellcheck="false" placeholder="C:\\path\\to\\output" aria-describedby="fld-outputPath-hint fld-outputPath-error">',
                            '<div id="fld-outputPath-hint" class="form-hint">Local or UNC directory path. Sent verbatim as <code>-OutputPath</code>. Required when fact output mode is <em>Write new</em>; ignored otherwise.</div>',
                            '<div id="fld-outputPath-error" class="field-error" role="alert"></div>',
                        '</div>',
                        '<div id="fld-appendFile-row" class="form-row" hidden>',
                            '<label for="fld-appendFile" class="form-label">Append file</label>',
                            '<input type="text" id="fld-appendFile" class="form-input form-input-mono" autocomplete="off" spellcheck="false" placeholder="existing-fact.csv" aria-describedby="fld-appendFile-hint fld-appendFile-error">',
                            '<div id="fld-appendFile-hint" class="form-hint">Filename or full path of an existing fact CSV. Sent verbatim as <code>-AppendFile</code>. Required when fact output mode is <em>Append</em>; must be empty otherwise.</div>',
                            '<div id="fld-appendFile-error" class="field-error" role="alert"></div>',
                        '</div>',
                        '<div class="form-row form-row-two">',
                            '<div class="form-field">',
                                '<label for="fld-authMode" class="form-label">Auth mode</label>',
                                '<select id="fld-authMode" class="form-input" aria-describedby="fld-authMode-hint fld-authMode-error">',
                                    '<option value="WebLogin">WebLogin</option>',
                                    '<option value="DeviceCode">DeviceCode</option>',
                                    '<option value="AppRegistrationSecret">AppRegistrationSecret</option>',
                                    '<option value="AppRegistrationCertificate">AppRegistrationCertificate</option>',
                                    '<option value="ManagedIdentity">ManagedIdentity</option>',
                                '</select>',
                                '<div id="fld-authMode-hint" class="form-hint">WebLogin / DeviceCode: PAX prompts the chef interactively at bake time (local-manual only). AppRegistrationSecret / AppRegistrationCertificate: PAX authenticates as an Entra workload using a Chef\u2019s Key bound to a Windows Credential Manager entry. ManagedIdentity: requires a hosted execution environment that exposes a workload identity token (no key, no secret).</div>',
                                '<div id="fld-authMode-error" class="field-error" role="alert"></div>',
                            '</div>',
                            '<div class="form-field">',
                                '<label for="fld-tenantId" class="form-label">Tenant ID</label>',
                                '<input type="text" id="fld-tenantId" class="form-input form-input-mono" autocomplete="off" spellcheck="false" placeholder="00000000-0000-0000-0000-000000000000" aria-describedby="fld-tenantId-hint fld-tenantId-error">',
                                '<div id="fld-tenantId-hint" class="form-hint">Entra tenant GUID. Sent as <code>-TenantId</code>; required by interactive auth modes (ignored for ManagedIdentity).</div>',
                                '<div id="fld-tenantId-error" class="field-error" role="alert"></div>',
                            '</div>',
                        '</div>',
                        // Phase AF: auth-profile picker. Visible only
                        // when authMode is AppRegistrationSecret or
                        // AppRegistrationCertificate. Populated from
                        // GET /api/v1/auth/profiles on mount and
                        // filtered to profiles whose mode matches the
                        // current selection. The empty string is sent
                        // when no profile is chosen; the broker
                        // rejects with 412 keyword='required' if a
                        // mode requires a profile but none is set.
                        '<div id="fld-authProfileId-row" class="form-row" hidden>',
                            '<label for="fld-authProfileId" class="form-label">Chef\u2019s Key</label>',
                            '<select id="fld-authProfileId" class="form-input" aria-describedby="fld-authProfileId-hint fld-authProfileId-error">',
                                '<option value="">(none selected)</option>',
                            '</select>',
                            '<div id="fld-authProfileId-hint" class="form-hint">References an entry on the <a href="#/auth-profiles">Chef\u2019s Keys</a> page. The recipe never persists the secret; the broker reads it from Windows Credential Manager at bake time and passes it via an environment variable.</div>',
                            '<div id="fld-authProfileId-error" class="field-error" role="alert"></div>',
                        '</div>',
                        // Phase AF: executionMode picker. The
                        // operational identity boundary -- which
                        // host is expected to run this recipe.
                        // local-manual is the only mode that may
                        // collect interactive auth at cook time;
                        // local-scheduled / fabric-hosted /
                        // azure-hosted require ManagedIdentity or a
                        // bound credential.
                        '<div class="form-row">',
                            '<label for="fld-executionMode" class="form-label">Execution mode</label>',
                            '<select id="fld-executionMode" class="form-input" aria-describedby="fld-executionMode-hint fld-executionMode-error">',
                                '<option value="local-manual">local-manual (chef triggers from this UI)</option>',
                                '<option value="local-scheduled">local-scheduled (Windows Task Scheduler)</option>',
                                '<option value="fabric-hosted">fabric-hosted (Microsoft Fabric)</option>',
                                '<option value="azure-hosted">azure-hosted (Azure compute)</option>',
                            '</select>',
                            '<div id="fld-executionMode-hint" class="form-hint">Determines where the bake is expected to run. Manual bakes from this UI require <code>local-manual</code>; the broker refuses to spawn any other mode here. Non-interactive modes (scheduled / fabric / azure) require a non-interactive auth mode.</div>',
                            '<div id="fld-executionMode-error" class="field-error" role="alert"></div>',
                        '</div>',
                    '</article>',

                    // V1.S26: destinations.userInfo output card. The
                    // user-info channel is co-located with the fact
                    // output by default (default mode -> omit the
                    // destinations.userInfo object entirely; PAX writes
                    // the user-info CSV alongside the fact CSV). The
                    // chef can override to a specific path
                    // (-OutputPathUserInfo) or append target
                    // (-AppendUserInfo); the two are mutually
                    // exclusive (the adapter never emits both). Under
                    // query.mode=userInfoOnly this card is REQUIRED;
                    // syncQueryModeVisibility() auto-switches Default
                    // -> Write to a specific path so the recipe stays
                    // valid.
                    '<article id="editor-userInfoDest-card" class="editor-card" aria-labelledby="editor-card-userInfoDest">',
                        '<h2 id="editor-card-userInfoDest" class="editor-card-title">User-info output</h2>',
                        '<p class="editor-card-doc">Optional companion output for the user-info channel. Default mode lets PAX co-locate the user-info CSV with the fact output. Pick a specific path or append target to override. Required when Query mode is <em>User info only</em>.</p>',
                        '<div class="form-row">',
                            '<label for="fld-userInfoDestMode" class="form-label">User-info output mode</label>',
                            '<select id="fld-userInfoDestMode" class="form-input" aria-describedby="fld-userInfoDestMode-hint fld-userInfoDestMode-error">',
                                '<option value="default">Default (let PAX co-locate with fact output)</option>',
                                '<option value="outputPath">Write to a specific path</option>',
                                '<option value="append">Append to an existing user-info CSV</option>',
                            '</select>',
                            '<div id="fld-userInfoDestMode-hint" class="form-hint">Picks PAX\u2019s mutually-exclusive user-info output switches: <code>-OutputPathUserInfo</code> for write, or <code>-AppendUserInfo</code> for append. Never both. Default omits the <code>destinations.userInfo</code> object entirely.</div>',
                            '<div id="fld-userInfoDestMode-error" class="field-error" role="alert"></div>',
                        '</div>',
                        '<div id="fld-userInfoPath-row" class="form-row" hidden>',
                            '<label for="fld-userInfoPath" class="form-label">User-info path</label>',
                            '<input type="text" id="fld-userInfoPath" class="form-input form-input-mono" autocomplete="off" spellcheck="false" placeholder="C:\\path\\to\\userInfo" aria-describedby="fld-userInfoPath-hint fld-userInfoPath-error">',
                            '<div id="fld-userInfoPath-hint" class="form-hint">Local or UNC directory path. Sent verbatim as <code>-OutputPathUserInfo</code>. Required when user-info output mode is <em>Write to a specific path</em>.</div>',
                            '<div id="fld-userInfoPath-error" class="field-error" role="alert"></div>',
                        '</div>',
                        '<div id="fld-userInfoAppendFile-row" class="form-row" hidden>',
                            '<label for="fld-userInfoAppendFile" class="form-label">User-info append file</label>',
                            '<input type="text" id="fld-userInfoAppendFile" class="form-input form-input-mono" autocomplete="off" spellcheck="false" placeholder="existing-userInfo.csv" aria-describedby="fld-userInfoAppendFile-hint fld-userInfoAppendFile-error">',
                            '<div id="fld-userInfoAppendFile-hint" class="form-hint">Filename or full path of an existing user-info CSV. Sent verbatim as <code>-AppendUserInfo</code>. Required when user-info output mode is <em>Append</em>.</div>',
                            '<div id="fld-userInfoAppendFile-error" class="field-error" role="alert"></div>',
                        '</div>',
                    '</article>',

                    '<article class="editor-card" aria-labelledby="editor-card-advanced">',
                        '<h2 id="editor-card-advanced" class="editor-card-title">Advanced</h2>',
                        '<p class="editor-card-doc">Chef escape hatch. The text below is appended verbatim to the PAX command line, after every recipe-derived switch.</p>',
                        '<div class="form-row">',
                            '<label for="fld-extraArgs" class="form-label">Extra PAX arguments</label>',
                            '<textarea id="fld-extraArgs" class="form-input form-input-mono" rows="2" spellcheck="false" aria-describedby="fld-extraArgs-error fld-extraArgs-hint"></textarea>',
                            '<div id="fld-extraArgs-hint" class="form-hint">Appended verbatim to the PAX command line. Switches removed in PAX v1.11.2 are rejected at save; the server returns a validation error naming the offending switch.</div>',
                            '<div id="fld-extraArgs-error" class="field-error" role="alert"></div>',
                        '</div>',
                    '</article>',

                    // V1.S19: Required Permissions transparency rail.
                    // Populated by the shared permissions resolver
                    // (window.PaxPermissions.resolve) and re-rendered
                    // on every form input change. Purely a transparency
                    // view; does NOT participate in
                    // extractPayloadFromDom and does NOT affect Save
                    // dirty detection. The list updates dynamically as
                    // the chef toggles ingredients, rollup, output, or
                    // append behavior.
                    '<article class="editor-card permissions-section" aria-labelledby="editor-card-permissions">',
                        '<div class="editor-card-title-row">',
                            '<h2 id="editor-card-permissions" class="editor-card-title">Required permissions</h2>',
                            '<button type="button" class="help-hook editor-card-help" data-help-topic="permissions.required" aria-label="Open help: Required permissions">?</button>',
                        '</div>',
                        '<p class="editor-card-doc">These are the Graph permissions, runtimes, and environment prerequisites this recipe needs to bake successfully. The list updates as you change recipe options.</p>',
                        '<div id="permissions-section-body" class="permissions-section-body">',
                            '<p class="editor-card-placeholder">Loading\u2026</p>',
                        '</div>',
                    '</article>',

                    '<article id="recent-runs-card" class="editor-card recent-runs-card" aria-labelledby="editor-card-recent-runs" hidden>',
                        '<h2 id="editor-card-recent-runs" class="editor-card-title">Recent runs</h2>',
                        '<div id="recent-runs-body" class="recent-runs-body">',
                            '<p class="editor-card-placeholder">Loading\u2026</p>',
                        '</div>',
                    '</article>',

                    // V1.S06c: Schedule card. Edit-mode only and only
                    // enabled for recipes saved as local-manual with
                    // an Entra workload auth mode (AppRegistration
                    // Secret / AppRegistrationCertificate). The card
                    // hosts a tiny recurrence form (daily/weekly +
                    // hour + minute + weekly-day picker) and Save /
                    // Delete actions. AppRegistrationSecret save asks
                    // for a one-shot clientSecret on every save (the
                    // server rebinds CredMan on every PUT and does
                    // not silently reuse a prior binding); certificate
                    // recipes never prompt; delete never prompts. No
                    // calendar, no cron, no dashboard.
                    '<article id="schedule-card" class="editor-card schedule-card" aria-labelledby="editor-card-schedule" hidden>',
                        '<div class="editor-card-title-row">',
                            '<h2 id="editor-card-schedule" class="editor-card-title">Schedule</h2>',
                            '<button type="button" class="help-hook editor-card-help" data-help-topic="scheduling.intro" aria-label="Open help: Scheduling">?</button>',
                        '</div>',
                        '<p class="editor-card-doc">PAX is the execution engine. Windows Task Scheduler is the scheduler. Cookbook authors, registers, wraps invocations, and shows history; it does not run a scheduler loop. Cookbook never stores the client secret on disk. Windows Task Scheduler / Windows may retain the task action and credential material at the OS layer. Certificate auth (AppRegistrationCertificate) is preferred over secret auth when available.</p>',
                        '<div id="schedule-disabled-banner" class="schedule-disabled-banner" hidden role="status"></div>',
                        '<div id="schedule-status" class="schedule-status" hidden></div>',
                        '<div id="schedule-form" class="schedule-form" hidden>',
                            '<div class="form-row form-row-two">',
                                '<div class="form-field">',
                                    '<label for="fld-schedule-kind" class="form-label">Recurrence</label>',
                                    '<select id="fld-schedule-kind" class="form-input">',
                                        '<option value="daily">daily</option>',
                                        '<option value="weekly">weekly</option>',
                                    '</select>',
                                    '<div class="form-hint">Daily fires once a day at the local-time below. Weekly fires on the selected days.</div>',
                                '</div>',
                                '<div class="form-field">',
                                    '<label class="form-label">Time of day</label>',
                                    '<div class="schedule-time-row">',
                                        '<input type="number" id="fld-schedule-hour" class="form-input form-input-mono schedule-time-input" min="0" max="23" inputmode="numeric" placeholder="HH">',
                                        '<span class="schedule-time-sep">:</span>',
                                        '<input type="number" id="fld-schedule-minute" class="form-input form-input-mono schedule-time-input" min="0" max="59" inputmode="numeric" placeholder="MM">',
                                    '</div>',
                                    '<div class="form-hint">Local-time hour (0\u201323) and minute (0\u201359). Task Scheduler runs in the chef\u2019s local time zone.</div>',
                                '</div>',
                            '</div>',
                            '<div id="fld-schedule-weekly-row" class="form-row" hidden>',
                                '<span class="form-label">Days of week</span>',
                                '<div class="schedule-dow-row">',
                                    '<label class="schedule-dow-label"><input type="checkbox" class="schedule-dow-cb" value="0"> Sun</label>',
                                    '<label class="schedule-dow-label"><input type="checkbox" class="schedule-dow-cb" value="1"> Mon</label>',
                                    '<label class="schedule-dow-label"><input type="checkbox" class="schedule-dow-cb" value="2"> Tue</label>',
                                    '<label class="schedule-dow-label"><input type="checkbox" class="schedule-dow-cb" value="3"> Wed</label>',
                                    '<label class="schedule-dow-label"><input type="checkbox" class="schedule-dow-cb" value="4"> Thu</label>',
                                    '<label class="schedule-dow-label"><input type="checkbox" class="schedule-dow-cb" value="5"> Fri</label>',
                                    '<label class="schedule-dow-label"><input type="checkbox" class="schedule-dow-cb" value="6"> Sat</label>',
                                '</div>',
                                '<div class="form-hint">Pick at least one day. Sunday=0, Saturday=6.</div>',
                            '</div>',
                            '<div id="fld-schedule-secret-row" class="form-row" hidden>',
                                '<label for="fld-schedule-secret" class="form-label">Client secret (one-shot)</label>',
                                '<input type="password" id="fld-schedule-secret" class="form-input" autocomplete="new-password" spellcheck="false">',
                                '<div class="form-hint">Required for AppRegistrationSecret recipes on every save. Cookbook hands the secret to Windows Credential Manager and discards the plaintext immediately. Windows Task Scheduler / Windows may retain the credential material at the OS layer.</div>',
                            '</div>',
                            '<div id="fld-schedule-error" class="field-error schedule-form-error" role="alert"></div>',
                            '<div class="schedule-actions">',
                                '<button type="button" id="schedule-save-button" class="btn btn-primary">Save schedule</button>',
                                '<button type="button" id="schedule-delete-button" class="btn btn-secondary" hidden>Delete schedule</button>',
                            '</div>',
                        '</div>',
                    '</article>',

                    // Server-managed metadata strip (edit mode only).
                    // Read-only display of the broker-stamped fields so
                    // the operator can see exactly what the server
                    // owns: recipeSchemaVersion, paxAdapterVersion,
                    // createdAt, updatedAt, and (when present) the
                    // verbatim createdBy provenance block. NOT part of
                    // extractPayloadFromDom — purely a transparency
                    // surface (Phase J req #6, #7).
                    '<article id="editor-meta-card" class="editor-card editor-meta-card" aria-labelledby="editor-card-meta" hidden>',
                        '<h2 id="editor-card-meta" class="editor-card-title">Server-managed metadata</h2>',
                        '<p class="editor-card-doc">Stamped by the broker. Read-only here; preserved across every save.</p>',
                        '<dl id="editor-meta-list" class="editor-meta-strip"></dl>',
                    '</article>',
                '</div>',

                '<aside class="editor-preview" aria-label="Preview">',
                    '<h2 class="editor-preview-title">Preview</h2>',
                    '<div class="editor-preview-body">',
                        '<p class="editor-preview-placeholder">Click Preview to generate the PAX command.</p>',
                    '</div>',
                '</aside>',
                // V1.S04: advisory readiness rail. Rendered in edit mode
                // only; click 'Check readiness' to populate. Never
                // auto-runs and never polls. A green result does NOT
                // guarantee a successful cook -- PAX remains
                // authoritative; readiness is sanity-only.
                '<aside id="editor-readiness" class="editor-readiness" aria-label="Readiness" hidden>',
                    '<h2 class="editor-readiness-title">Readiness</h2>',
                    '<div class="editor-readiness-body">',
                        '<p class="editor-readiness-placeholder">Click “Check readiness” to run advisory pre-flight checks. Readiness is informational only; PAX remains authoritative.</p>',
                    '</div>',
                '</aside>',
            '</div>',
        '</section>'
    ].join('');

    // ----------------------------------------------------------------
    // Module-scoped state
    //
    // Intentionally tiny. Not a mirror of the form. The DOM is the
    // working copy; this object only carries enough information to
    // gate Save, detect dirtiness, and discard stale async replies.
    // ----------------------------------------------------------------

    var state         = null;
    var editorRoot    = null;
    var validatorPromise = null; // shared across mounts; AJV compile is expensive enough to amortize

    // Mirrors the latest cookbook:acquisitionStateChanged event from
    // pax-engine-overlay.js. When true the PAX engine is not yet
    // acquired and any operation that would invoke the engine is
    // refused locally so the operator does not have to round-trip a
    // 409 acquisitionRequired. Module-scoped so the value survives
    // mount/teardown cycles.
    var acquisitionBlocked = false;

    var SCHEMA_PATH = '/assets/schemas/recipe.schema.json';

    // V1.S19 PART C: built-in recipe name reservation.
    //
    // The two bundled templates surface as "built-in recipes" whose
    // canonical names the chef must NOT overwrite. Editing a built-in
    // is fully supported (open, change ingredients, change rollup,
    // change output, change append), but the customized version must
    // be saved under a NEW name; the built-in stays canonical.
    //
    // These two strings MUST stay in sync with the displayName of the
    // matching template under app/templates/. The Help topic
    // permissions.required and the Pantry materialize default name
    // both rely on these strings as the canonical built-in names.
    var BUILT_IN_RECIPE_NAMES = Object.freeze([
        'AI-in-One Dashboard',
        'M365 Usage Analytics Dashboard'
    ]);

    function isBuiltInRecipeName(name) {
        if (typeof name !== 'string') { return false; }
        var trimmed = name.trim();
        if (!trimmed) { return false; }
        for (var i = 0; i < BUILT_IN_RECIPE_NAMES.length; i++) {
            if (BUILT_IN_RECIPE_NAMES[i] === trimmed) { return true; }
        }
        return false;
    }

    // ----------------------------------------------------------------
    // Pure helpers
    // ----------------------------------------------------------------

    function pad2(n) { return (n < 10 ? '0' : '') + n; }

    function formatYmd(d) {
        return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate());
    }

    function buildDefaults() {
        // Approved defaults for a brand-new recipe (V1.S26 Shape 1 = default audit):
        //   identity.name                            = ''
        //   query.mode                               = omitted (= 'audit')
        //   m365Usage.includeM365Usage               = false (Shape 1; opt in to Shape 2 by checking)
        //   m365Usage.includeCopilotInteraction      = omitted (defaults to true; only saved when M365 is on and the checkbox is unchecked)
        //   entraUserData.includeUserInfo            = true
        //   query.startDate / query.endDate          = last 30 days (today and today-30)
        //   processing.rollup                        = 'Rollup' (selectable; chef can choose 'RollupPlusRaw')
        //   destinations.fact.mode                   = 'outputPath'
        //   destinations.fact.path                   = ''
        //   destinations.userInfo                    = omitted (Default mode)
        //   auth.mode                                = 'WebLogin'
        //   auth.tenantId                            = ''
        //   advanced                                 = omitted
        var today = new Date();
        var thirty = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 30);
        return {
            identity:     { name: '' },
            ingredients:  {
                m365Usage:     { includeM365Usage: false },
                entraUserData: { includeUserInfo:  true }
            },
            query:        { startDate: formatYmd(thirty), endDate: formatYmd(today) },
            processing:   { rollup: 'Rollup' },
            destinations: { fact: { mode: 'outputPath', path: '' } },
            auth:         { mode: 'WebLogin', tenantId: '' },
            // Phase AF: every new recipe defaults to local-manual.
            // The chef can switch to non-interactive modes once an
            // auth profile is configured.
            executionMode: 'local-manual'
        };
    }

    // Canonical JSON: sorted keys, recursive. Used by isDirty() to
    // compare DOM-extracted draft against the post-save snapshot in
    // a key-order-independent way. Pure, deterministic.
    function canonicalize(value) {
        if (value === null || value === undefined) { return 'null'; }
        var t = typeof value;
        if (t === 'string')  { return JSON.stringify(value); }
        if (t === 'number' || t === 'boolean') { return String(value); }
        if (Array.isArray(value)) {
            var parts = [];
            for (var i = 0; i < value.length; i++) { parts.push(canonicalize(value[i])); }
            return '[' + parts.join(',') + ']';
        }
        if (t === 'object') {
            var keys = Object.keys(value).sort();
            var kv = [];
            for (var k = 0; k < keys.length; k++) {
                kv.push(JSON.stringify(keys[k]) + ':' + canonicalize(value[keys[k]]));
            }
            return '{' + kv.join(',') + '}';
        }
        return 'null';
    }

    // ----------------------------------------------------------------
    // DOM extraction -- the critical pure boundary.
    //
    // RULES:
    //   - read DOM
    //   - construct payload
    //   - return payload
    //   - DO NOT mutate the DOM
    //   - DO NOT normalize values (no toLowerCase, no trim of user
    //     text -- the trimmed check below is a presence test for the
    //     omit-advanced branch, not normalization of stored data)
    //   - DO NOT inject defaults
    //   - DO NOT have any side effect
    // ----------------------------------------------------------------
    function extractPayloadFromDom(root) {
        var v = function (id) { return root.querySelector('#' + id).value; };
        var c = function (id) { return root.querySelector('#' + id).checked; };

        // V1.S26 query.mode picker drives the projection branches.
        // The two values map to the two supported PAX run shapes.
        var queryMode = v('fld-queryMode') || 'audit';
        var isUserInfoOnly = (queryMode === 'userInfoOnly');

        // V1.S26 Shape 3 forces ingredients into a fixed shape:
        // includeM365Usage MUST be false, includeUserInfo MUST be true,
        // and includeCopilotInteraction MUST be absent.
        var effIncludeM365     = isUserInfoOnly ? false : c('fld-m365');
        var effIncludeUserInfo = isUserInfoOnly ? true  : c('fld-userInfo');

        var m365 = { includeM365Usage: effIncludeM365 };
        if (effIncludeM365) {
            // includeCopilotInteraction defaults to true at the adapter
            // when absent. Save false only when the chef explicitly
            // unchecks the box; never save when M365 usage is off (the
            // schema forbids includeCopilotInteraction outside the
            // M365 usage shape).
            if (!c('fld-includeCp')) {
                m365.includeCopilotInteraction = false;
            }
        }

        var payload = {
            identity: {
                name: v('fld-name')
            },
            ingredients: {
                m365Usage:     m365,
                entraUserData: { includeUserInfo: effIncludeUserInfo }
            },
            query:        {},
            processing:   {},
            destinations: {},
            auth: {
                mode:     v('fld-authMode'),
                tenantId: v('fld-tenantId')
            }
        };

        if (isUserInfoOnly) {
            // Shape 3: emit query.mode='userInfoOnly'. No dates, no
            // filters, no rollup, no fact destination. destinations.userInfo
            // is required; it is built below from the user-info card.
            payload.query.mode = 'userInfoOnly';
        } else {
            // Shape 1 / Shape 2 (audit). Omit query.mode for byte-identical
            // back-compat with pre-S26 recipes; the schema treats omitted
            // as audit and the adapter projects identically.
            payload.query.startDate = v('fld-startDate');
            payload.query.endDate   = v('fld-endDate');

            // activityTypes: only ['CopilotInteraction'] is supported.
            if (c('fld-activityTypesExplicit')) {
                payload.query.activityTypes = ['CopilotInteraction'];
            }

            var userIds = parseListLines(v('fld-userIds'));
            if (userIds.length > 0) { payload.query.userIds = userIds; }

            var groupNames = parseListLines(v('fld-groupNames'));
            if (groupNames.length > 0) { payload.query.groupNames = groupNames; }

            // agentFilter: omit when 'none'; emit object when any of the
            // three real modes are picked. mode='agentIds' also carries
            // the agentIds list (validator rejects empty list).
            var agentMode = v('fld-agentFilterMode') || 'none';
            if (agentMode !== 'none') {
                var af = { mode: agentMode };
                if (agentMode === 'agentIds') {
                    var agentIds = parseListLines(v('fld-agentIds'));
                    if (agentIds.length > 0) { af.agentIds = agentIds; }
                }
                payload.query.agentFilter = af;
            }

            var promptFilter = v('fld-promptFilter');
            if (promptFilter) { payload.query.promptFilter = promptFilter; }

            payload.processing.rollup = v('fld-rollup');

            // destinations.fact projection.
            //   mode='outputPath' -> save path only; never appendFile.
            //   mode='append'     -> save appendFile only; never path.
            // The SPA writes only 'mode' going forward (no appendBehavior).
            var factMode = v('fld-factMode') || 'outputPath';
            var fact = { mode: factMode };
            if (factMode === 'outputPath') {
                fact.path = v('fld-outputPath');
            } else if (factMode === 'append') {
                fact.appendFile = v('fld-appendFile');
            }
            payload.destinations.fact = fact;
        }

        // destinations.userInfo projection (both shapes; required under
        // Shape 3). Default mode -> omit destinations.userInfo entirely.
        var uiDestMode = v('fld-userInfoDestMode') || 'default';
        if (uiDestMode === 'outputPath') {
            payload.destinations.userInfo = { mode: 'outputPath', path: v('fld-userInfoPath') };
        } else if (uiDestMode === 'append') {
            payload.destinations.userInfo = { mode: 'append', appendFile: v('fld-userInfoAppendFile') };
        }

        // Phase AF: optional authProfileId. Sent only when non-empty
        // AND the selected mode is an AppRegistration variant; the
        // broker rejects the field on other modes. For WebLogin /
        // DeviceCode / ManagedIdentity the picker is hidden, so the
        // select value defaults to '' and we omit the property
        // entirely rather than sending an empty string.
        var authProfileId = v('fld-authProfileId');
        var authModeVal = payload.auth.mode;
        if (authProfileId &&
            (authModeVal === 'AppRegistrationSecret' || authModeVal === 'AppRegistrationCertificate')) {
            payload.auth.authProfileId = authProfileId;
        }

        // Phase AF: executionMode is a top-level field. It is always
        // present in the persisted recipe; the broker stamps a
        // default ('local-manual') if a recipe is created without
        // one. We always send the current dropdown value.
        var execMode = v('fld-executionMode');
        if (execMode) {
            payload.executionMode = execMode;
        }

        // A10: omit the entire `advanced` branch when extra arguments
        // are empty/whitespace-only. Whitespace-only is logically empty
        // (the server adapter Trim()s before deciding whether to
        // append). Send the raw value when non-empty -- do not trim.
        var extra = v('fld-extraArgs');
        if (extra && extra.trim().length > 0) {
            payload.advanced = { extraArguments: extra };
        }

        return payload;
    }

    // V1.S26: split a textarea's value into a list of non-empty trimmed
    // entries. One line = one entry. Empty / whitespace-only lines are
    // dropped silently. The result is passed directly into the schema
    // (which validates minItems and minLength on each item).
    function parseListLines(raw) {
        if (!raw || typeof raw !== 'string') { return []; }
        var lines = raw.split(/\r?\n/);
        var out = [];
        for (var i = 0; i < lines.length; i++) {
            var t = lines[i].replace(/^\s+|\s+$/g, '');
            if (t.length > 0) { out.push(t); }
        }
        return out;
    }

    function populateForm(recipe) {
        editorRoot.querySelector('#fld-name').value          = (recipe.identity && recipe.identity.name) || '';

        // V1.S26 query.mode read-through. Recipes that omit the field
        // are audit-shape (the schema's effective default).
        var qmode = (recipe.query && recipe.query.mode === 'userInfoOnly') ? 'userInfoOnly' : 'audit';
        editorRoot.querySelector('#fld-queryMode').value     = qmode;

        editorRoot.querySelector('#fld-m365').checked        = !!(recipe.ingredients && recipe.ingredients.m365Usage && recipe.ingredients.m365Usage.includeM365Usage);

        // V1.S26: includeCopilotInteraction defaults to true at the
        // adapter when absent; the checkbox is true unless the recipe
        // explicitly stores false. The checkbox is only meaningful
        // when M365 usage is on; syncM365Visibility() hides the row
        // and force-resets to true when M365 usage is off.
        var inclCp = !(recipe.ingredients && recipe.ingredients.m365Usage && recipe.ingredients.m365Usage.includeCopilotInteraction === false);
        editorRoot.querySelector('#fld-includeCp').checked   = inclCp;

        editorRoot.querySelector('#fld-userInfo').checked    = !!(recipe.ingredients && recipe.ingredients.entraUserData && recipe.ingredients.entraUserData.includeUserInfo);
        editorRoot.querySelector('#fld-startDate').value     = (recipe.query && recipe.query.startDate) || '';
        editorRoot.querySelector('#fld-endDate').value       = (recipe.query && recipe.query.endDate)   || '';

        // V1.S26: audit filter read-throughs.
        var at = recipe.query && recipe.query.activityTypes;
        var atExplicit = Array.isArray(at) && at.length === 1 && at[0] === 'CopilotInteraction';
        editorRoot.querySelector('#fld-activityTypesExplicit').checked = !!atExplicit;

        var uids = (recipe.query && Array.isArray(recipe.query.userIds)) ? recipe.query.userIds : [];
        editorRoot.querySelector('#fld-userIds').value = uids.join('\n');

        var grps = (recipe.query && Array.isArray(recipe.query.groupNames)) ? recipe.query.groupNames : [];
        editorRoot.querySelector('#fld-groupNames').value = grps.join('\n');

        var af = recipe.query && recipe.query.agentFilter;
        var afMode = (af && af.mode) ? af.mode : 'none';
        editorRoot.querySelector('#fld-agentFilterMode').value = afMode;
        var afIds = (af && Array.isArray(af.agentIds)) ? af.agentIds : [];
        editorRoot.querySelector('#fld-agentIds').value = afIds.join('\n');

        editorRoot.querySelector('#fld-promptFilter').value  = (recipe.query && recipe.query.promptFilter) || '';

        // Rollup output mode read-through. Recipes that omit the field
        // fall back to 'Rollup' so the pre-selector behavior is
        // preserved exactly.
        editorRoot.querySelector('#fld-rollup').value         = (recipe.processing && recipe.processing.rollup) || 'Rollup';

        // V1.S26: destinations.fact.mode read-through with legacy
        // fallback. New recipes carry the explicit `mode` field;
        // legacy recipes carry only `appendBehavior` (fresh|append),
        // which maps to fresh -> outputPath and append -> append. If
        // neither field is present but a path is, treat as outputPath.
        var fact = recipe.destinations && recipe.destinations.fact;
        var factMode = 'outputPath';
        if (fact) {
            if (fact.mode === 'append' || fact.mode === 'outputPath') {
                factMode = fact.mode;
            } else if (fact.appendBehavior === 'append') {
                factMode = 'append';
            }
        }
        editorRoot.querySelector('#fld-factMode').value      = factMode;
        editorRoot.querySelector('#fld-outputPath').value    = (fact && fact.path)       || '';
        editorRoot.querySelector('#fld-appendFile').value    = (fact && fact.appendFile) || '';

        // V1.S26: destinations.userInfo read-through. Default mode
        // when the field is absent.
        var uid = recipe.destinations && recipe.destinations.userInfo;
        var uidMode = 'default';
        if (uid) {
            if (uid.mode === 'outputPath' || uid.mode === 'append') {
                uidMode = uid.mode;
            }
        }
        editorRoot.querySelector('#fld-userInfoDestMode').value     = uidMode;
        editorRoot.querySelector('#fld-userInfoPath').value         = (uid && uid.path)       || '';
        editorRoot.querySelector('#fld-userInfoAppendFile').value   = (uid && uid.appendFile) || '';

        editorRoot.querySelector('#fld-authMode').value      = (recipe.auth && recipe.auth.mode) || 'WebLogin';
        editorRoot.querySelector('#fld-tenantId').value      = (recipe.auth && recipe.auth.tenantId) || '';
        editorRoot.querySelector('#fld-extraArgs').value     = (recipe.advanced && recipe.advanced.extraArguments) || '';
        // Phase AF: authProfileId + executionMode read-throughs.
        // Setting the value of fld-authProfileId before the list of
        // <option>s is fetched will leave it as '' until the load
        // resolves; loadAuthProfileOptions() restores the saved
        // value once the options are appended.
        editorRoot.querySelector('#fld-authProfileId').value = (recipe.auth && recipe.auth.authProfileId) || '';
        editorRoot.querySelector('#fld-executionMode').value = recipe.executionMode || 'local-manual';

        // V1.S26: drive all conditional visibility sync after the DOM
        // values are in place. Order matters: query-mode visibility
        // toggles cards on/off and may force-switch the user-info
        // destination mode, so it runs first; the per-control sync
        // functions then settle the row visibility within each card.
        syncQueryModeVisibility();
        syncM365Visibility();
        syncFactModeVisibility();
        syncUserInfoDestModeVisibility();
        syncAgentFilterVisibility();
        syncAuthProfileVisibility();

        // Read-only read-throughs of server-managed fields (no DOM
        // mutation upstream beyond display, no participation in
        // extractPayloadFromDom).
        renderProvenance(recipe);
        renderServerMetadata(recipe);
        // V1.S19: initial paint of the Required Permissions rail. The
        // rail mirrors the current DOM (post-populate) so it reflects
        // the persisted recipe exactly. Subsequent updates are driven
        // by the editor-root 'input'/'change' listener installed in
        // attachListeners().
        renderPermissionsSection();
    }

    // V1.S19: render (or re-render) the Required Permissions rail
    // inside the editor card. Reads the DOM via extractPayloadFromDom
    // (the same pure boundary the Save and Preview lifecycles use) and
    // hands the recipe-shape to the shared viewer. No side effects on
    // payload, save state, dirty detection, or readiness staleness.
    function renderPermissionsSection() {
        if (!editorRoot) { return; }
        var container = editorRoot.querySelector('#permissions-section-body');
        if (!container) { return; }
        if (!window.PaxPermissions || !window.PaxPermissions.viewer || typeof window.PaxPermissions.viewer.render !== 'function') {
            return;
        }
        var recipeLike = extractPayloadFromDom(editorRoot);
        window.PaxPermissions.viewer.render(container, recipeLike, { compact: true });
    }

    // V1.S19: input/change listener that drives dynamic recomputation
    // of the Required Permissions rail. Keep this distinct from the
    // Save-enable trigger (onFirstInput) and the readiness-staleness
    // trigger (onAnyInputStaleReadiness); each listener is single-
    // purpose so the lifecycle stays auditable.
    function onAnyInputRefreshPermissions() {
        renderPermissionsSection();
    }

    // Phase J: render the template-origin subtitle if the recipe was
    // materialized from a Pantry template. Read-only, deterministic.
    // No-op (and hidden) when createdBy.fromTemplate is absent, so a
    // recipe authored directly with no template origin still renders
    // a clean editor (Phase J req #12).
    function renderProvenance(recipe) {
        var box = editorRoot && editorRoot.querySelector('#editor-origin');
        if (!box) { return; }
        var from = recipe && recipe.createdBy && recipe.createdBy.fromTemplate;
        if (!from || !from.templateId) {
            box.hidden = true;
            box.replaceChildren();
            return;
        }
        var tid = String(from.templateId || '');
        var tv  = String(from.templateVersion || '');
        var prefix = document.createElement('span');
        prefix.className = 'editor-origin-label';
        prefix.textContent = 'From template: ';
        var idEl = document.createElement('code');
        idEl.className = 'editor-origin-id';
        idEl.textContent = tid;
        var sep = document.createElement('span');
        sep.className = 'editor-origin-sep';
        sep.textContent = ' \u2022 ';
        var verEl = document.createElement('code');
        verEl.className = 'editor-origin-version';
        verEl.textContent = tv;
        box.replaceChildren(prefix, idEl, sep, verEl);
        box.hidden = false;
    }

    // Phase J: render the server-managed metadata strip in edit mode.
    // Reveals exactly which fields are stamped by the broker and not
    // editable here. Hidden in new-mode because none of these fields
    // exist yet (the broker stamps them on POST /api/v1/recipes). The
    // strip is purely informational; nothing it shows is read back
    // into extractPayloadFromDom.
    function renderServerMetadata(recipe) {
        var card = editorRoot && editorRoot.querySelector('#editor-meta-card');
        var list = editorRoot && editorRoot.querySelector('#editor-meta-list');
        if (!card || !list) { return; }
        if (!state || state.isNew) {
            card.hidden = true;
            list.replaceChildren();
            return;
        }
        var rows = [
            { label: 'Recipe id',             value: recipe && recipe.recipeId },
            { label: 'Schema version',        value: recipe && recipe.recipeSchemaVersion },
            { label: 'PAX adapter version',   value: recipe && recipe.paxAdapterVersion },
            { label: 'Created at (UTC)',      value: recipe && recipe.createdAt },
            { label: 'Updated at (UTC)',      value: recipe && recipe.updatedAt }
        ];
        var cb = recipe && recipe.createdBy;
        if (cb) {
            if (cb.cookbookVersion)   { rows.push({ label: 'Cookbook version at creation',   value: cb.cookbookVersion }); }
            if (cb.bundledPaxVersion) { rows.push({ label: 'Bundled PAX at creation',        value: cb.bundledPaxVersion }); }
            if (cb.releaseChannel)    { rows.push({ label: 'Release channel at creation',   value: cb.releaseChannel }); }
        }
        // X11A: surface the broker-managed pin state read-only alongside
        // the other server-stamped fields. The pin toggle in the header
        // is the action that changes it.
        rows.push({ label: 'Pinned', value: (state && state.isPinned) ? 'Yes' : 'No' });
        var frag = document.createDocumentFragment();
        for (var i = 0; i < rows.length; i++) {
            var dt = document.createElement('dt');
            dt.className = 'editor-meta-key';
            dt.textContent = rows[i].label;
            var dd = document.createElement('dd');
            dd.className = 'editor-meta-value';
            var raw = rows[i].value;
            dd.textContent = (raw === null || raw === undefined || raw === '') ? '\u2014' : String(raw);
            frag.appendChild(dt);
            frag.appendChild(dd);
        }
        list.replaceChildren(frag);
        card.hidden = false;
    }

    // ----------------------------------------------------------------
    // Pin / unpin toggle (X11A)
    //
    // A self-contained edit-mode control. It reflects state.isPinned
    // (seeded from meta.is_pinned at load) and POSTs /pin or /unpin to
    // flip it. It owns its disabled state via state.pinInFlight and is
    // deliberately NOT wired into applyButtonStates()/opLocked, so a
    // pin toggle can never block Save/Preview/Bake and vice versa.
    // Pinning is a pure list-ordering preference -- it never touches
    // the PAX engine -- so it is not gated by acquisition state.
    // ----------------------------------------------------------------
    function applyPinToggleButton() {
        if (!editorRoot || !state) { return; }
        var pb = editorRoot.querySelector('#pin-toggle-button');
        if (!pb || pb.hidden) { return; }
        var pinned = !!state.isPinned;
        pb.disabled = !!state.pinInFlight;
        if (state.pinInFlight) {
            pb.textContent = pinned ? 'Unpinning\u2026' : 'Pinning\u2026';
        } else {
            pb.textContent = pinned ? '\u2605 Pinned' : '\u2606 Pin';
        }
        pb.setAttribute('aria-pressed', pinned ? 'true' : 'false');
        pb.setAttribute('aria-label', pinned ? 'Unpin recipe' : 'Pin recipe');
        pb.title = pinned ? 'Unpin recipe' : 'Pin recipe';
    }

    function onPinToggleClick(ev) {
        if (ev) { ev.preventDefault(); }
        doPinToggleDetail();
    }

    function doPinToggleDetail() {
        if (!state || state.isNew || !state.recipeId) { return; }
        if (state.pinInFlight) { return; }
        var mySession = state;
        var wantPinned = !state.isPinned;
        state.pinInFlight = true;
        applyPinToggleButton();
        hideBanner();

        var path = '/api/v1/recipes/' + state.recipeId + (wantPinned ? '/pin' : '/unpin');
        var signal = state.abortCtrl ? state.abortCtrl.signal : null;
        window.cookbookApi.post(path, {}, { signal: signal }).then(function (resp) {
            // Discard if the page was torn down / remounted mid-flight.
            if (state !== mySession) { return; }
            state.pinInFlight = false;

            if (resp.networkError) {
                applyPinToggleButton();
                showBanner('network',
                    'Could not reach broker: ' + resp.networkError + '. The pin state was not changed.',
                    null);
                return;
            }
            if (resp.ok) {
                state.isPinned = (resp.body && typeof resp.body.isPinned !== 'undefined')
                    ? !!resp.body.isPinned
                    : wantPinned;
                applyPinToggleButton();
                renderServerMetadata(state.initialRecipe);
                hideBanner();
                return;
            }
            applyPinToggleButton();
            if (resp.status === 404) {
                showBanner('error', 'Recipe not found.', null);
                return;
            }
            if (resp.status === 400 && resp.body && resp.body.error === 'invalid_recipe_id') {
                showBanner('error', 'Invalid recipe id.', null);
                return;
            }
            var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
            showBanner('error', (wantPinned ? 'Pin' : 'Unpin') + ' failed: ' + code + '.', null);
        });
    }

    // Strip the server-managed fields (recipeId, *Version, *At) from a
    // server-returned recipe so it can be compared apples-to-apples
    // with extractPayloadFromDom() output.
    function userLeavesOnly(recipe) {
        if (!recipe) { return null; }
        var out = {
            identity:     recipe.identity,
            ingredients:  recipe.ingredients,
            query:        recipe.query,
            processing:   recipe.processing,
            destinations: recipe.destinations,
            auth:         recipe.auth
        };
        if (recipe.advanced &&
            typeof recipe.advanced.extraArguments === 'string' &&
            recipe.advanced.extraArguments.trim().length > 0) {
            out.advanced = recipe.advanced;
        }
        return out;
    }

    // ----------------------------------------------------------------
    // Dirty detection -- computed on demand, not tracked reactively.
    // ----------------------------------------------------------------
    function isDirty() {
        if (!state || !editorRoot) { return false; }
        if (!state.initialRecipe) {
            // New mode before first save, OR Edit mode before fetch
            // resolves. Coarse signal only.
            return !!state.formTouched;
        }
        var currentJson = canonicalize(extractPayloadFromDom(editorRoot));
        var initialJson = canonicalize(userLeavesOnly(state.initialRecipe));
        return currentJson !== initialJson;
    }

    // ----------------------------------------------------------------
    // AJV wiring -- validation only.
    //
    // Loads the schema once (lazily) and compiles a single validator
    // function. The compiled validator is held module-scoped so it
    // survives mount/teardown cycles. The schema URI is the same one
    // the server validates against; client / server drift is impossible
    // by construction.
    //
    // AJV options:
    //   allErrors: collect every error, not just the first, so the
    //              banner+per-field placement reflects everything wrong
    //   meta:      false -- the schema declares $schema=2020-12 but
    //              uses only draft-07 keywords; skip meta validation
    //   useDefaults: NEVER set true -- that would mutate the input
    //                payload, which violates the no-default-injection
    //                rule for the editor's data path.
    // ----------------------------------------------------------------
    function getValidator() {
        if (validatorPromise) { return validatorPromise; }
        if (typeof window.Ajv !== 'function') {
            // AJV bundle hasn't loaded (or load failed). Soft-skip:
            // server remains authoritative.
            return Promise.resolve(null);
        }
        validatorPromise = fetch(SCHEMA_PATH, { credentials: 'omit', cache: 'no-store' })
            .then(function (r) {
                if (!r.ok) { throw new Error('schema HTTP ' + r.status); }
                return r.json();
            })
            .then(function (schema) {
                var ajv = new window.Ajv({ allErrors: true, meta: false });
                return ajv.compile(schema);
            })
            .catch(function (err) {
                // Soft-fail. The server still validates authoritatively.
                console.warn('[recipe-editor] schema load failed:', err);
                validatorPromise = null;
                return null;
            });
        return validatorPromise;
    }

    // AJV 6 reports errors under .dataPath; AJV 7+ uses .instancePath.
    // Server reports under .instancePath. Normalize to the server shape
    // so error rendering does not branch on the source.
    function normalizeAjvError(err) {
        return {
            instancePath: (err && (err.instancePath || err.dataPath)) || '',
            keyword:      (err && err.keyword) || '',
            message:      (err && err.message) || '',
            params:       (err && err.params)  || {}
        };
    }

    // Resolve an error to the user-facing field path it should anchor
    // on. 'required' errors land on the PARENT object with the missing
    // child name in params; collapse them onto the child path so the
    // error renders next to the empty field.
    function resolveErrorPath(err) {
        if (err.keyword === 'required' && err.params && err.params.missingProperty) {
            var base = err.instancePath || '';
            return base + '/' + err.params.missingProperty;
        }
        return err.instancePath || '';
    }

    // Static instancePath -> field id map. Kept hand-maintained so the
    // schema cannot drive the form layout. Every editable leaf has
    // exactly one row here.
    var PATH_TO_FIELD = {
        '/identity/name':                                  'fld-name',
        '/query/mode':                                     'fld-queryMode',
        '/ingredients/m365Usage/includeM365Usage':         'fld-m365',
        '/ingredients/m365Usage/includeCopilotInteraction':'fld-includeCp',
        '/ingredients/entraUserData/includeUserInfo':      'fld-userInfo',
        '/query/startDate':                                'fld-startDate',
        '/query/endDate':                                  'fld-endDate',
        '/query/activityTypes':                            'fld-activityTypesExplicit',
        '/query/userIds':                                  'fld-userIds',
        '/query/groupNames':                               'fld-groupNames',
        '/query/agentFilter':                              'fld-agentFilterMode',
        '/query/agentFilter/mode':                         'fld-agentFilterMode',
        '/query/agentFilter/agentIds':                     'fld-agentIds',
        '/query/promptFilter':                             'fld-promptFilter',
        '/processing/rollup':                              'fld-rollup',
        '/destinations/fact/mode':                         'fld-factMode',
        '/destinations/fact/path':                         'fld-outputPath',
        '/destinations/fact/appendFile':                   'fld-appendFile',
        '/destinations/userInfo/mode':                     'fld-userInfoDestMode',
        '/destinations/userInfo/path':                     'fld-userInfoPath',
        '/destinations/userInfo/appendFile':               'fld-userInfoAppendFile',
        '/auth/mode':                                      'fld-authMode',
        '/auth/tenantId':                                  'fld-tenantId',
        '/auth/authProfileId':                             'fld-authProfileId',
        '/executionMode':                                  'fld-executionMode',
        '/advanced/extraArguments':                        'fld-extraArgs'
    };

    function fieldIdForPath(path) {
        return PATH_TO_FIELD[path] || null;
    }

    function pathForFieldId(fieldId) {
        for (var p in PATH_TO_FIELD) {
            if (PATH_TO_FIELD[p] === fieldId) { return p; }
        }
        return null;
    }

    // The schema requires recipeId, recipeSchemaVersion, paxAdapterVersion,
    // createdAt, updatedAt at the top level -- and stamps createdBy on
    // create -- but all of those are server-managed. A freshly-built
    // draft will always be missing them, so client-side validation must
    // ignore them (along with anything inside the createdBy block). The
    // server has the canonical schema and re-validates.
    var SERVER_MANAGED_PATHS = {
        '/recipeId':            true,
        '/recipeSchemaVersion': true,
        '/paxAdapterVersion':   true,
        '/createdAt':           true,
        '/updatedAt':           true,
        '/createdBy':           true
    };

    function isServerManaged(path) {
        if (!path) { return false; }
        if (SERVER_MANAGED_PATHS[path]) { return true; }
        // Treat any error nested inside /createdBy as server-managed.
        // Without this, an AJV error like "/createdBy/cookbookVersion"
        // on a draft (which intentionally omits createdBy) would leak
        // to the banner.
        if (path === '/createdBy' || path.indexOf('/createdBy/') === 0) { return true; }
        return false;
    }

    function filterClientErrors(errors) {
        var out = [];
        for (var i = 0; i < errors.length; i++) {
            if (!isServerManaged(resolveErrorPath(errors[i]))) { out.push(errors[i]); }
        }
        return out;
    }

    // ----------------------------------------------------------------
    // Error rendering
    // ----------------------------------------------------------------
    function clearAllFieldErrors() {
        var slots = editorRoot.querySelectorAll('.field-error');
        for (var i = 0; i < slots.length; i++) { slots[i].textContent = ''; }
    }

    function clearFieldError(fieldId) {
        var el = editorRoot.querySelector('#' + fieldId + '-error');
        if (el) { el.textContent = ''; }
    }

    function setFieldError(fieldId, message) {
        var el = editorRoot.querySelector('#' + fieldId + '-error');
        if (!el) { return; }
        el.textContent = (el.textContent ? el.textContent + '; ' : '') + message;
    }

    function hideBanner() {
        var el = editorRoot.querySelector('#editor-banner');
        if (!el) { return; }
        el.hidden = true;
        el.className = 'editor-banner';
        el.textContent = '';
    }

    function showBanner(kind, text, retryFn) {
        var el = editorRoot.querySelector('#editor-banner');
        if (!el) { return; }
        el.hidden = false;
        el.className = 'editor-banner banner-' + kind;
        el.textContent = text;
        if (retryFn) {
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'btn-ghost banner-retry';
            btn.textContent = 'Retry';
            btn.addEventListener('click', retryFn);
            el.appendChild(document.createTextNode(' '));
            el.appendChild(btn);
        }
    }

    // Render a set of normalized errors. Each error is placed next to
    // its field; any error that does not resolve to a known field is
    // surfaced in the banner so it cannot be lost.
    function renderErrors(errors) {
        clearAllFieldErrors();
        if (!errors || errors.length === 0) {
            hideBanner();
            return;
        }
        var unplaced = 0;
        for (var i = 0; i < errors.length; i++) {
            var err = errors[i];
            var path = resolveErrorPath(err);
            var fid  = fieldIdForPath(path);
            if (fid) {
                setFieldError(fid, err.message);
            } else {
                unplaced++;
            }
        }
        var msg = errors.length + ' issue' + (errors.length === 1 ? '' : 's') +
                  ' \u2014 please fix the highlighted fields.';
        if (unplaced > 0) {
            msg += ' (' + unplaced + ' could not be placed inline.)';
        }
        showBanner('error', msg, null);
    }

    // ----------------------------------------------------------------
    // Unified button gating.
    //
    // Save and Preview are mutually exclusive: while either is in
    // flight, both buttons are disabled. loadInFlight (the GET that
    // populates Edit mode) also blocks both, so the user cannot
    // click Save / Preview against a half-populated form. Save's
    // semantic gate is state.formTouched (set true on first input,
    // reset to false after a successful Save or on a fresh load).
    // Preview has no semantic gate: its operational gate is the only
    // gate, matching the approved "always enabled" disposition.
    // ----------------------------------------------------------------
    function applyButtonStates() {
        if (!editorRoot || !state) { return; }
        var sb = editorRoot.querySelector('#save-button');
        var pb = editorRoot.querySelector('#preview-button');
        var rb = editorRoot.querySelector('#readiness-button');
        var kb = editorRoot.querySelector('#cook-button');
        var xb = editorRoot.querySelector('#export-takeout-button');
        var opLocked = state.saveInFlight || state.previewRunInFlight || state.loadInFlight || state.cookInFlight || state.readinessInFlight || state.exportInFlight;
        if (sb) { sb.disabled = opLocked || !state.formTouched; }
        if (pb) { pb.disabled = opLocked; }
        // Readiness button mirrors the cook button visibility (edit
        // mode only); while hidden it stays disabled so a forced
        // keyboard click cannot fire.
        if (rb) {
            if (rb.hidden) {
                rb.disabled = true;
            } else {
                rb.disabled = opLocked;
                rb.textContent = state.readinessInFlight ? 'Checking\u2026' : 'Check readiness';
            }
        }
        // Cook button is rendered hidden by default; it is revealed in
        // edit mode at mount time. While hidden it stays disabled so a
        // keyboard-driven click on a forced-visible state cannot fire.
        if (kb) {
            if (kb.hidden) {
                kb.disabled = true;
                kb.removeAttribute('title');
            } else if (acquisitionBlocked) {
                kb.disabled = true;
                kb.textContent = 'Bake Recipe';
                kb.title = 'PAX engine acquisition required';
            } else {
                kb.disabled = opLocked;
                kb.textContent = state.cookInFlight ? 'Starting\u2026' : 'Bake Recipe';
                kb.removeAttribute('title');
            }
        }
        // F2F: Export Recipe Takeout. Revealed in edit mode at mount
        // time (alongside cook-button). Disabled until the recipe is
        // clean and saved:
        //   - new/unsaved recipe (no recipeId)   -> disabled, hint to save first
        //   - unsaved edits in the form           -> disabled, hint to save first
        //   - any other operation in flight       -> disabled (busy)
        // The button label flips to a busy indicator while the
        // export POST is in flight so the chef gets immediate
        // feedback.
        if (xb) {
            if (xb.hidden) {
                xb.disabled = true;
                xb.removeAttribute('title');
            } else {
                var noId   = state.isNew || !state.recipeId;
                var dirty  = !!state.formTouched;
                xb.disabled = opLocked || noId || dirty;
                if (state.exportInFlight) {
                    xb.textContent = 'Exporting\u2026';
                    xb.removeAttribute('title');
                } else {
                    xb.textContent = 'Export Recipe Takeout';
                    if (noId) {
                        xb.title = 'Save the recipe before exporting.';
                    } else if (dirty) {
                        xb.title = 'Save changes before exporting.';
                    } else {
                        xb.removeAttribute('title');
                    }
                }
            }
        }
    }

    // ----------------------------------------------------------------
    // Preview rail rendering -- wholesale replacement only.
    //
    // The rail has exactly five render states. Each helper replaces
    // the entire .editor-preview-body subtree via replaceChildren().
    // No partial DOM patching, no incremental reconciliation, no
    // node diffing. Each render uses createElement + textContent so
    // dynamic content (error messages, command strings) can never
    // become HTML.
    // ----------------------------------------------------------------
    function getPreviewBody() {
        return editorRoot ? editorRoot.querySelector('.editor-preview-body') : null;
    }

    function renderPreviewInitial() {
        var body = getPreviewBody();
        if (!body) { return; }
        var p = document.createElement('p');
        p.className = 'editor-preview-placeholder';
        p.textContent = 'Click Preview to generate the PAX command.';
        body.replaceChildren(p);
    }

    function renderPreviewInFlight() {
        var body = getPreviewBody();
        if (!body) { return; }
        var p = document.createElement('p');
        p.className = 'editor-preview-status';
        p.textContent = 'Generating preview\u2026';
        body.replaceChildren(p);
    }

    function renderPreviewSuccess(recipeId, command, argv) {
        var body = getPreviewBody();
        if (!body) { return; }
        var h = document.createElement('h3');
        h.className = 'preview-success-heading';
        h.textContent = 'PAX command';
        var pre = document.createElement('pre');
        pre.className = 'preview-command';
        pre.textContent = command || '';
        // Phase J: render the authoritative argv tokens explicitly so
        // the operator sees the deterministic projection alongside the
        // rendered command. The argv list is what cook actually spawns
        // (modulo the outer pwsh wrapper); displaying it here makes
        // the trust-surface contract visible: "Preview shows the same
        // projection that cook uses."
        var argvHeading = document.createElement('h3');
        argvHeading.className = 'preview-success-heading preview-argv-heading';
        argvHeading.textContent = 'Argv (authoritative projection)';
        var argvList = document.createElement('ol');
        argvList.className = 'preview-argv-list';
        var tokens = Array.isArray(argv) ? argv : [];
        if (tokens.length === 0) {
            var emptyLi = document.createElement('li');
            emptyLi.className = 'preview-argv-empty';
            emptyLi.textContent = '(no tokens)';
            argvList.appendChild(emptyLi);
        } else {
            for (var i = 0; i < tokens.length; i++) {
                var li = document.createElement('li');
                li.className = 'preview-argv-token';
                li.textContent = String(tokens[i]);
                argvList.appendChild(li);
            }
        }
        var cap = document.createElement('p');
        cap.className = 'preview-caption';
        cap.textContent = 'recipe id: ' + (recipeId || '');
        body.replaceChildren(h, pre, argvHeading, argvList, cap);
    }

    function renderPreviewValidationErrors(errs) {
        var body = getPreviewBody();
        if (!body) { return; }
        var h = document.createElement('p');
        h.className = 'preview-error-heading';
        h.textContent = 'Cannot generate preview \u2014 fix these errors:';
        var ul = document.createElement('ul');
        ul.className = 'preview-error-list';
        for (var i = 0; i < errs.length; i++) {
            var li = document.createElement('li');
            var path = resolveErrorPath(errs[i]);
            var msg  = errs[i].message || '';
            li.textContent = (path ? path + ': ' : '') + msg;
            ul.appendChild(li);
        }
        body.replaceChildren(h, ul);
    }

    function renderPreviewTransportError(detail) {
        var body = getPreviewBody();
        if (!body) { return; }
        var h = document.createElement('p');
        h.className = 'preview-error-heading';
        h.textContent = 'Preview failed.';
        var d = document.createElement('p');
        d.className = 'preview-error-detail';
        d.textContent = detail || '';
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn-ghost preview-retry';
        btn.textContent = 'Retry';
        btn.addEventListener('click', onPreviewRetryClick);
        body.replaceChildren(h, d, btn);
    }

    // ----------------------------------------------------------------
    // Preview lifecycle.
    //
    // doPreview() is the only request-issuing function for preview.
    // Each click captures (mySession, mySeq) at entry and re-checks
    // them after every await. Older clicks lose to newer clicks via
    // seq-drop; teardowns drop everything via session-drop. There is
    // no shared state with Save's lifecycle except applyButtonStates
    // (the unified op-lock) -- preview cannot mutate canonical state,
    // dirty-state, or per-field error slots.
    // ----------------------------------------------------------------
    function doPreview() {
        if (!state) { return; }
        if (state.previewRunInFlight || state.saveInFlight || state.loadInFlight) { return; }
        var mySession = state;
        var mySeq     = ++state.previewSeq;

        state.previewRunInFlight = true;
        applyButtonStates();
        renderPreviewInFlight();

        var payload = extractPayloadFromDom(editorRoot);

        getValidator().then(function (validate) {
            if (state !== mySession || mySeq !== state.previewSeq) { return; }

            if (validate) {
                var ok = validate(payload);
                if (!ok) {
                    var errs = filterClientErrors((validate.errors || []).map(normalizeAjvError));
                    if (errs.length > 0) {
                        renderPreviewValidationErrors(errs);
                        state.previewRunInFlight = false;
                        applyButtonStates();
                        return;
                    }
                }
            }

            window.cookbookApi.post('/api/v1/recipes/preview', payload, { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
                if (state !== mySession || mySeq !== state.previewSeq) { return; }

                if (resp.networkError) {
                    renderPreviewTransportError('Could not reach broker: ' + resp.networkError + '.');
                    state.previewRunInFlight = false;
                    applyButtonStates();
                    return;
                }
                if (resp.status === 400 && resp.body && resp.body.error === 'validation_failed') {
                    var srvErrs = filterClientErrors((resp.body.errors || []).map(normalizeAjvError));
                    renderPreviewValidationErrors(srvErrs);
                    state.previewRunInFlight = false;
                    applyButtonStates();
                    return;
                }
                if (!resp.ok) {
                    var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                    renderPreviewTransportError('Preview failed: ' + code + '.');
                    state.previewRunInFlight = false;
                    applyButtonStates();
                    return;
                }

                var rid = (resp.body && resp.body.recipeId) || '';
                var cmd = (resp.body && resp.body.command)  || '';
                var av  = (resp.body && resp.body.argv)     || [];
                renderPreviewSuccess(rid, cmd, av);
                state.previewRunInFlight = false;
                applyButtonStates();
            });
        });
    }

    function onPreviewClick(ev) {
        if (ev) { ev.preventDefault(); }
        doPreview();
    }

    function onPreviewRetryClick(ev) {
        if (ev) { ev.preventDefault(); }
        doPreview();
    }

    // ----------------------------------------------------------------
    // Save lifecycle
    // ----------------------------------------------------------------
    function doSave() {
        if (!state || state.saveInFlight) { return; }
        if (state.previewRunInFlight || state.loadInFlight) { return; }
        var mySession = state; // capture; abort if torn down or remounted
        var isNew     = state.isNew;
        var recipeId  = state.recipeId;

        state.saveInFlight = true;
        applyButtonStates();
        hideBanner();
        clearAllFieldErrors();

        var payload = extractPayloadFromDom(editorRoot);

        // V1.S19 PART C: built-in recipe names are reserved. Editing a
        // built-in recipe must NOT overwrite it; saving back to the
        // canonical built-in name is blocked client-side so the chef
        // is forced to choose a new name for the customized copy. The
        // built-in remains the canonical version on disk.
        if (isBuiltInRecipeName(payload && payload.identity && payload.identity.name)) {
            showBanner(
                'error',
                "Built-in recipes can't be overwritten. Choose a new name to save your customized version.",
                null
            );
            renderErrors([{
                instancePath: '/identity/name',
                schemaPath:   '',
                keyword:      'reserved',
                params:       {},
                message:      'Reserved built-in recipe name. Choose a different name.'
            }]);
            var nameInput = editorRoot.querySelector('#fld-name');
            if (nameInput) {
                try { nameInput.focus(); } catch (e) { /* focus may fail in detached DOM */ }
            }
            state.saveInFlight = false;
            applyButtonStates();
            return;
        }

        getValidator().then(function (validate) {
            if (state !== mySession) { return; }

            if (validate) {
                var ok = validate(payload);
                if (!ok) {
                    var errs = filterClientErrors((validate.errors || []).map(normalizeAjvError));
                    if (errs.length > 0) {
                        renderErrors(errs);
                        state.saveInFlight = false;
                        applyButtonStates();
                        return;
                    }
                }
            }
            // Fall through to network send.

            var sendPromise;
            if (isNew) {
                sendPromise = window.cookbookApi.post('/api/v1/recipes', payload, { signal: state.abortCtrl ? state.abortCtrl.signal : null });
            } else {
                sendPromise = window.cookbookApi.put('/api/v1/recipes/' + recipeId, payload, { signal: state.abortCtrl ? state.abortCtrl.signal : null });
            }

            sendPromise.then(function (resp) {
                if (state !== mySession) { return; }

                if (resp.networkError) {
                    // Phase AE: save is a state-mutating call. A
                    // network failure here does NOT mean the save
                    // was rejected -- the request may have reached
                    // the broker and committed on disk before the
                    // socket dropped. Tell the truth and ask the
                    // operator to reload before retrying.
                    showBanner('network',
                        'Could not reach broker: ' + resp.networkError + '. ' +
                        'Your save may or may not have been recorded on the server. ' +
                        'Reload the recipe to verify the on-disk state before retrying.',
                        doSave);
                    state.saveInFlight = false;
                    applyButtonStates();
                    return;
                }
                if (resp.status === 400 && resp.body && resp.body.error === 'validation_failed') {
                    var serverErrs = filterClientErrors((resp.body.errors || []).map(normalizeAjvError));
                    renderErrors(serverErrs);
                    state.saveInFlight = false;
                    applyButtonStates();
                    return;
                }
                if (!resp.ok) {
                    var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                    showBanner('error', 'Save failed: ' + code + '.', null);
                    state.saveInFlight = false;
                    applyButtonStates();
                    return;
                }

                // Success. Server response is authoritative.
                var saved = resp.body && resp.body.recipe ? resp.body.recipe : null;
                if (!saved) {
                    showBanner('error', 'Save returned an unexpected response shape.', null);
                    state.saveInFlight = false;
                    applyButtonStates();
                    return;
                }

                if (isNew) {
                    // A5: navigate to '#/recipes/<newId>' via replaceState so
                    // browser back does not return to '#/recipes/new'. The
                    // router will dispatch and remount the editor in Edit
                    // mode, which will GET the recipe and populate. The
                    // double-fetch is intentional -- server is the only
                    // authoritative source.
                    var newId = resp.body.recipeId || saved.recipeId;
                    state.saveInFlight = false; // about to be replaced anyway
                    history.replaceState({}, '', window.location.pathname + '#/recipes/' + newId);
                    window.cookbookRouter.dispatch();
                    return;
                }

                // A4 + Slice 5B: Edit mode -- stay, install the server
                // response as the new authoritative snapshot, repopulate
                // (server may normalize), show success, buttons re-flow.
                // Per Slice 5B doctrine, a successful Save also clears
                // the preview rail back to the initial placeholder: any
                // prior preview was request-owned and disposable, and
                // the canonical recipe has just changed.
                state.initialRecipe = saved;
                state.formTouched   = false;
                populateForm(saved);
                showBanner('success', 'Saved.', null);
                state.saveInFlight = false;
                applyButtonStates();
                renderPreviewInitial();
            });
        });
    }

    // ----------------------------------------------------------------
    // V1.S04 -- Readiness rail
    //
    // Advisory pre-flight diagnostics. The button is the ONLY trigger:
    // no auto-run, no polling, no debounced re-check on edit. A form
    // mutation simply invalidates any previously displayed result so
    // the operator does not mistake an old green for a current one.
    // The Cook button does not block on warnings or not_checked -- it
    // only refuses to start when readiness reports blockers; that
    // contract is enforced in doCook() and not here.
    // ----------------------------------------------------------------
    function getReadinessBody() {
        return editorRoot ? editorRoot.querySelector('.editor-readiness-body') : null;
    }

    function renderReadinessInitial() {
        var body = getReadinessBody();
        if (!body) { return; }
        var p = document.createElement('p');
        p.className = 'editor-readiness-placeholder';
        p.textContent = 'Click \u201cCheck readiness\u201d to run advisory pre-flight checks. Readiness is informational only; PAX remains authoritative.';
        body.replaceChildren(p);
    }

    function renderReadinessInFlight() {
        var body = getReadinessBody();
        if (!body) { return; }
        var p = document.createElement('p');
        p.className = 'editor-readiness-status';
        p.textContent = 'Checking readiness\u2026';
        body.replaceChildren(p);
    }

    function renderReadinessStale() {
        var body = getReadinessBody();
        if (!body) { return; }
        var p = document.createElement('p');
        p.className = 'editor-readiness-placeholder';
        p.textContent = 'Form was edited after readiness was last checked. Re-run \u201cCheck readiness\u201d for an up-to-date result.';
        body.replaceChildren(p);
    }

    function renderReadinessTransportError(detail) {
        var body = getReadinessBody();
        if (!body) { return; }
        var h = document.createElement('p');
        h.className = 'readiness-error-heading';
        h.textContent = 'Readiness check failed.';
        var d = document.createElement('p');
        d.className = 'readiness-error-detail';
        d.textContent = detail || '';
        body.replaceChildren(h, d);
    }

    function renderReadinessSuccess(result) {
        var body = getReadinessBody();
        if (!body) { return; }
        var overall = (result && result.status) ? String(result.status) : 'unknown';
        var summary = (result && result.summary) ? result.summary : { blocked: 0, warning: 0, ok: 0, notChecked: 0 };

        // Header line: overall status chip + tally.
        var header = document.createElement('div');
        header.className = 'readiness-summary';
        var chip = document.createElement('span');
        chip.className = 'readiness-status-chip readiness-status-' + overall;
        chip.textContent = overall.replace('_', ' ');
        header.appendChild(chip);
        var tally = document.createElement('span');
        tally.className = 'readiness-tally';
        tally.textContent =
            ' blockers: ' + (summary.blocked|0) +
            ' \u00b7 warnings: ' + (summary.warning|0) +
            ' \u00b7 ok: ' + (summary.ok|0) +
            ' \u00b7 not checked: ' + (summary.notChecked|0);
        header.appendChild(tally);

        var disclaimer = document.createElement('p');
        disclaimer.className = 'readiness-disclaimer';
        disclaimer.textContent =
            'A green result does not guarantee a successful bake. PAX validates auth, reachability, and destination at bake time and is authoritative.';

        var list = document.createElement('ul');
        list.className = 'readiness-check-list';
        var checks = (result && result.checks) ? result.checks : [];
        for (var i = 0; i < checks.length; i++) {
            var c = checks[i] || {};
            var li = document.createElement('li');
            li.className = 'readiness-check readiness-check-' + (c.status || 'unknown');

            var head = document.createElement('div');
            head.className = 'readiness-check-head';
            var s = document.createElement('span');
            s.className = 'readiness-status-chip readiness-status-' + (c.status || 'unknown');
            s.textContent = String(c.status || 'unknown').replace('_', ' ');
            head.appendChild(s);
            var lbl = document.createElement('span');
            lbl.className = 'readiness-check-label';
            lbl.textContent = String(c.label || c.id || '');
            head.appendChild(lbl);
            var scope = document.createElement('span');
            scope.className = 'readiness-check-scope';
            scope.textContent = String(c.scope || '');
            head.appendChild(scope);
            li.appendChild(head);

            if (c.detail) {
                var det = document.createElement('p');
                det.className = 'readiness-check-detail';
                det.textContent = String(c.detail);
                li.appendChild(det);
            }
            if (c.remediation) {
                var rem = document.createElement('p');
                rem.className = 'readiness-check-remediation';
                rem.textContent = 'Remediation: ' + String(c.remediation);
                li.appendChild(rem);
            }
            list.appendChild(li);
        }

        var ts = document.createElement('p');
        ts.className = 'readiness-timestamp';
        ts.textContent = 'Generated at ' + String(result.generatedAtUtc || '') + ' (UTC).';

        body.replaceChildren(header, disclaimer, list, ts);
    }

    function doReadiness() {
        if (!state) { return; }
        if (state.isNew || !state.recipeId) { return; }
        if (state.readinessInFlight || state.saveInFlight || state.previewRunInFlight || state.loadInFlight || state.cookInFlight) { return; }
        var mySession = state;
        var mySeq     = ++state.readinessSeq;

        state.readinessInFlight = true;
        applyButtonStates();
        renderReadinessInFlight();

        window.cookbookApi.post(
            '/api/v1/cooks/readiness',
            { recipeId: state.recipeId },
            { signal: state.abortCtrl ? state.abortCtrl.signal : null }
        ).then(function (resp) {
            if (state !== mySession || mySeq !== state.readinessSeq) { return; }

            if (resp.networkError) {
                renderReadinessTransportError('Could not reach broker: ' + resp.networkError + '.');
                state.readinessInFlight = false;
                state.latestReadiness   = null;
                applyButtonStates();
                return;
            }
            if (!resp.ok || !resp.body) {
                var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                renderReadinessTransportError('Readiness check failed: ' + code + '.');
                state.readinessInFlight = false;
                state.latestReadiness   = null;
                applyButtonStates();
                return;
            }

            state.latestReadiness   = resp.body;
            state.readinessInFlight = false;
            renderReadinessSuccess(resp.body);
            applyButtonStates();
        });
    }

    function onReadinessClick(ev) {
        if (ev) { ev.preventDefault(); }
        doReadiness();
    }

    // Any form input marks a previously displayed readiness result
    // as stale. We deliberately do NOT auto-re-run; the operator
    // must click again. While a readiness call is in flight we leave
    // the rail alone -- doReadiness() will overwrite the body when
    // the response lands.
    function onAnyInputStaleReadiness(ev) {
        if (!state) { return; }
        if (state.readinessInFlight) { return; }
        if (!state.latestReadiness) { return; }
        if (ev && ev.target && ev.target.tagName) {
            var tag = ev.target.tagName.toUpperCase();
            if (tag !== 'INPUT' && tag !== 'SELECT' && tag !== 'TEXTAREA') { return; }
        }
        state.latestReadiness = null;
        renderReadinessStale();
    }

    // ----------------------------------------------------------------
    // Cook lifecycle (S6A)
    //
    // The Cook button is rendered in the editor-actions group only in
    // edit mode (the recipe must exist server-side before it can be
    // cooked). Clicking POSTs /api/v1/recipes/<id>/cook and on the
    // 201 success navigates to '#/cooks/<cookId>'. The broker always
    // re-reads the on-disk recipe -- unsaved DOM edits are NOT part
    // of the cook. If the form is dirty, a confirm dialog warns the
    // user before navigating away (the editor tears down on route
    // change and any unsaved edits are lost).
    //
    // Error rendering is per disposition C2: 409 recipe_busy surfaces
    // an explicit informational banner with an "Open running cook"
    // affordance that the user must click; the page does NOT auto-
    // navigate to the running cook.
    // ----------------------------------------------------------------
    function showCookBusyBanner(activeCookId) {
        var el = editorRoot.querySelector('#editor-banner');
        if (!el) { return; }
        el.hidden = false;
        el.className = 'editor-banner banner-info';
        el.textContent = 'This recipe is already baking.';
        if (activeCookId) {
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'btn-ghost banner-retry';
            btn.textContent = 'Open running bake';
            btn.addEventListener('click', function (ev) {
                if (ev) { ev.preventDefault(); }
                history.replaceState({}, '', window.location.pathname + '#/cooks/' + activeCookId);
                window.cookbookRouter.dispatch();
            });
            el.appendChild(document.createTextNode(' '));
            el.appendChild(btn);
        }
    }

    function doCook() {
        if (!state) { return; }
        if (acquisitionBlocked) {
            showBanner('error', 'PAX engine acquisition is required before recipes can be baked.', null);
            return;
        }
        if (state.cookInFlight || state.saveInFlight || state.previewRunInFlight || state.loadInFlight || state.readinessInFlight) { return; }
        if (state.isNew || !state.recipeId) { return; }
        // V1.S04: advisory readiness gating.  Only a present, non-null
        // readiness payload with summary.blocked > 0 refuses the cook;
        // warnings, not_checked, and never-ran do NOT block.  This
        // keeps readiness honest: it surfaces problems without
        // becoming a hard precondition.
        if (state.latestReadiness && state.latestReadiness.summary && (state.latestReadiness.summary.blocked|0) > 0) {
            var n = (state.latestReadiness.summary.blocked|0);
            showBanner('error', 'Readiness reports ' + n + ' blocker' + (n === 1 ? '' : 's') + '. Resolve them, then re-run readiness or proceed at your own risk by clearing the readiness panel.', null);
            return;
        }
        var mySession = state;
        var recipeId  = state.recipeId;

        state.cookInFlight = true;
        applyButtonStates();
        hideBanner();
        clearAllFieldErrors();

        // X16C -- Bake state machine.
        //   idle -> starting: the cook POST is issued.
        //   starting -> reauth_required: the broker answered 401
        //       reAuthRequired (opClass manualCook); the Bake stays
        //       in-flight (button disabled) while we step up.
        //   reauth_required -> reauth_in_progress: the browser runs
        //       Windows Hello via cookbookManualCookReauth.reauth().
        //   reauth_in_progress -> retrying_after_reauth: on a verified
        //       grant we re-issue the cook EXACTLY ONCE (allowReauth
        //       is false on the retry so a second 401 cannot loop).
        //   * -> started: a 201 navigates to the cook detail view.
        //   * -> failed: any bounded failure (including a cancelled or
        //       failed Hello) clears cookInFlight and surfaces a
        //       banner. A failed step-up never reads as a started cook.
        function sendCook(allowReauth) {
            window.cookbookApi.post('/api/v1/recipes/' + recipeId + '/cook', {}, { signal: mySession.abortCtrl ? mySession.abortCtrl.signal : null }).then(function (resp) {
                if (state !== mySession) { return; }

                if (resp.networkError) {
                    state.cookInFlight = false;
                    // Phase AE: cook trigger is a state-mutating call.
                    // A network failure does NOT mean the cook was not
                    // started -- the POST may have committed on the
                    // broker before the socket dropped. Direct the
                    // operator to the Cooks page rather than retrying
                    // blind.
                    showBanner('network',
                        'Could not reach broker: ' + resp.networkError + '. ' +
                        'A bake may or may not have been started on the server. ' +
                        'Open the Bakes page to verify before retrying.',
                        null);
                    applyButtonStates();
                    return;
                }
                if (resp.status === 201 && resp.body && resp.body.cookId) {
                    // Success. Navigate to the cook detail view. The router
                    // teardown will reset cookInFlight on its own by
                    // tearing down state entirely; we do not need to clear
                    // it here.
                    history.replaceState({}, '', window.location.pathname + '#/cooks/' + resp.body.cookId);
                    window.cookbookRouter.dispatch();
                    return;
                }
                if (resp.status === 401 && resp.body &&
                    (resp.body.code === 'reAuthRequired' || resp.body.error === 'reAuthRequired') &&
                    resp.body.opClass === 'manualCook') {
                    if (!allowReauth) {
                        // Re-auth already attempted once this Bake.
                        // Do not loop: surface a bounded failure.
                        state.cookInFlight = false;
                        showBanner('error', 'Windows Hello is still required. The bake did not start.', null);
                        applyButtonStates();
                        return;
                    }
                    showBanner('info', 'Confirm with Windows Hello to start this bake\u2026', null);
                    window.cookbookManualCookReauth.reauth(recipeId).then(function (rr) {
                        if (state !== mySession) { return; }
                        if (rr && rr.ok) {
                            hideBanner();
                            sendCook(false);
                            return;
                        }
                        state.cookInFlight = false;
                        showBanner('error', window.cookbookManualCookReauth.describe(rr), null);
                        applyButtonStates();
                    });
                    return;
                }
                if (resp.status === 409 && resp.body && resp.body.error === 'recipe_busy') {
                    state.cookInFlight = false;
                    showCookBusyBanner(resp.body.cookId || '');
                    applyButtonStates();
                    return;
                }
                if (resp.status === 409 && resp.body && resp.body.error === 'acquisitionRequired') {
                    state.cookInFlight = false;
                    showBanner('error', 'PAX engine acquisition is required before recipes can be baked.', null);
                    applyButtonStates();
                    return;
                }
                if (resp.status === 500 && resp.body && resp.body.error === 'pax_script_integrity') {
                    state.cookInFlight = false;
                    showBanner('error', 'PAX integrity check failed. Re-install PAX Cookbook from the latest release.', null);
                    applyButtonStates();
                    return;
                }
                if (resp.status === 412 && resp.body && resp.body.error === 'recipe_invalid') {
                    state.cookInFlight = false;
                    var n = (resp.body.errors && resp.body.errors.length) || 0;
                    showBanner(
                        'error',
                        'On-disk recipe failed validation (' + n + ' issue' + (n === 1 ? '' : 's') + '). Save the current form first, then try again.',
                        null
                    );
                    applyButtonStates();
                    return;
                }
                if (resp.status === 404 && resp.body && resp.body.error === 'not_found') {
                    state.cookInFlight = false;
                    showBanner('error', 'Recipe not found on the broker.', null);
                    applyButtonStates();
                    return;
                }
                if (resp.status === 400 && resp.body && resp.body.error === 'invalid_recipe_id') {
                    state.cookInFlight = false;
                    showBanner('error', 'Invalid recipe id.', null);
                    applyButtonStates();
                    return;
                }
                state.cookInFlight = false;
                var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                showBanner('error', 'Bake failed: ' + code + '.', null);
                applyButtonStates();
            });
        }

        sendCook(true);
    }

    function onCookClick(ev) {
        if (ev) { ev.preventDefault(); }
        if (!state) { return; }
        if (isDirty()) {
            var ok = window.confirm(
                'Bake Recipe will run the saved recipe on disk. Your unsaved edits will not be included.\n\n' +
                'Continue?'
            );
            if (!ok) { return; }
        }
        doCook();
    }

    // ----------------------------------------------------------------
    // Recent runs rail (S6A)
    //
    // Stale-on-mount GET /api/v1/cooks?recipeId=<id>. No refresh
    // affordance on the rail itself; navigating away and back is the
    // refresh mechanism (the editor tears down and remounts, which
    // re-issues the GET). No polling, no timers.
    //
    // The rail is rendered as a small table with one row per cook
    // (newest first by broker order). Each row's "Open" cell is a
    // hash-anchor to '#/cooks/<cookId>' -- no JS handler needed; the
    // router handles the dispatch on hashchange.
    // ----------------------------------------------------------------
    function recentRunsBody() {
        return editorRoot ? editorRoot.querySelector('#recent-runs-body') : null;
    }

    function renderRecentRunsLoading() {
        var body = recentRunsBody();
        if (!body) { return; }
        var p = document.createElement('p');
        p.className = 'editor-card-placeholder';
        p.textContent = 'Loading\u2026';
        body.replaceChildren(p);
    }

    function renderRecentRunsError(text) {
        var body = recentRunsBody();
        if (!body) { return; }
        var p = document.createElement('p');
        p.className = 'editor-card-placeholder recent-runs-error';
        p.textContent = text;
        body.replaceChildren(p);
    }

    function renderRecentRunsEmpty() {
        var body = recentRunsBody();
        if (!body) { return; }
        var p = document.createElement('p');
        p.className = 'editor-card-placeholder';
        p.textContent = 'No runs yet.';
        body.replaceChildren(p);
    }

    function renderRecentRunsList(cooks) {
        var body = recentRunsBody();
        if (!body) { return; }
        if (!cooks || cooks.length === 0) {
            renderRecentRunsEmpty();
            return;
        }
        var table = document.createElement('table');
        table.className = 'recent-runs-table';
        var thead = document.createElement('thead');
        var thr   = document.createElement('tr');
        var hdrs  = ['Started (UTC)', 'Status', 'Duration', 'Trigger', 'Bake'];
        for (var i = 0; i < hdrs.length; i++) {
            var th = document.createElement('th');
            th.textContent = hdrs[i];
            thr.appendChild(th);
        }
        thead.appendChild(thr);
        table.appendChild(thead);
        var tbody = document.createElement('tbody');
        for (var r = 0; r < cooks.length; r++) {
            var c = cooks[r] || {};
            var tr = document.createElement('tr');

            var tdStart = document.createElement('td');
            tdStart.className = 'recent-runs-col-start';
            tdStart.textContent = c.startedAt ? c.startedAt : (c.createdAt || '\u2014');
            tr.appendChild(tdStart);

            var tdStatus = document.createElement('td');
            tdStatus.className = 'recent-runs-col-status';
            var chip = document.createElement('span');
            var s = c.status ? String(c.status) : '';
            chip.className = 'cook-status-chip status-' + (s ? s : 'unknown');
            chip.textContent = s ? s : '(unknown)';
            tdStatus.appendChild(chip);
            tr.appendChild(tdStatus);

            var tdDur = document.createElement('td');
            tdDur.className = 'recent-runs-col-duration';
            tdDur.textContent = (c.durationSeconds === null || c.durationSeconds === undefined)
                ? '\u2014'
                : (String(c.durationSeconds) + ' s');
            tr.appendChild(tdDur);

            var tdTrig = document.createElement('td');
            tdTrig.className = 'recent-runs-col-trigger';
            tdTrig.textContent = c.trigger || '\u2014';
            tr.appendChild(tdTrig);

            var tdOpen = document.createElement('td');
            tdOpen.className = 'recent-runs-col-open';
            if (c.cookId) {
                var a = document.createElement('a');
                a.href = '#/cooks/' + c.cookId;
                a.className = 'recent-runs-open';
                a.textContent = 'Open';
                tdOpen.appendChild(a);
            } else {
                tdOpen.textContent = '\u2014';
            }
            tr.appendChild(tdOpen);

            tbody.appendChild(tr);
        }
        table.appendChild(tbody);
        body.replaceChildren(table);
    }

    function loadRecentRuns() {
        if (!state || state.isNew || !state.recipeId) { return; }
        if (state.recentRunsLoadInFlight) { return; }
        var mySession = state;
        var recipeId  = state.recipeId;
        state.recentRunsLoadInFlight = true;
        renderRecentRunsLoading();

        window.cookbookApi.get('/api/v1/cooks?recipeId=' + encodeURIComponent(recipeId), { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
            if (state !== mySession) { return; }
            state.recentRunsLoadInFlight = false;

            if (resp.networkError) {
                renderRecentRunsError('Could not reach broker: ' + resp.networkError + '.');
                return;
            }
            if (!resp.ok || !resp.body || !Array.isArray(resp.body.cooks)) {
                var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                renderRecentRunsError('Failed to load recent runs: ' + code + '.');
                return;
            }
            renderRecentRunsList(resp.body.cooks);
        });
    }

    // ----------------------------------------------------------------
    // Schedule card (V1.S06c)
    //
    // Authoring + lifecycle UI for a single Windows Scheduled Task
    // bound to this recipe. The wrapper is invoked by Task Scheduler
    // at fire-time; Cookbook never polls a tick and never spawns the
    // wrapper from the broker. This rail loads stale-on-mount and
    // re-loads after every successful Save / Delete; no polling.
    //
    // Gating rules (UI-level; the broker re-enforces all of them):
    //   - Edit mode only (a new recipe has no recipeId yet).
    //   - executionMode must be local-manual on the saved recipe.
    //   - authMode must be AppRegistrationSecret OR AppRegistration
    //     Certificate. WebLogin / DeviceCode / ManagedIdentity are
    //     refused.
    //
    // Secret behavior (SEC-A): every save (create AND update) of an
    // AppRegistrationSecret schedule asks the chef to re-enter the
    // client secret. The plaintext is sent in the PUT body once and
    // is never persisted by the editor; the broker rebinds CredMan
    // and discards the plaintext immediately. AppRegistration
    // Certificate saves never prompt. Delete never prompts.
    // ----------------------------------------------------------------
    function scheduleCard()      { return editorRoot ? editorRoot.querySelector('#schedule-card') : null; }
    function scheduleBanner()    { return editorRoot ? editorRoot.querySelector('#schedule-disabled-banner') : null; }
    function scheduleStatusEl()  { return editorRoot ? editorRoot.querySelector('#schedule-status') : null; }
    function scheduleFormEl()    { return editorRoot ? editorRoot.querySelector('#schedule-form') : null; }
    function scheduleErrorEl()   { return editorRoot ? editorRoot.querySelector('#fld-schedule-error') : null; }

    function setScheduleDisabled(reason) {
        var banner = scheduleBanner();
        var form   = scheduleFormEl();
        var status = scheduleStatusEl();
        if (banner) {
            banner.hidden = false;
            banner.textContent = reason;
        }
        if (form)   { form.hidden   = true; }
        if (status) { status.hidden = true; }
    }

    function setScheduleEnabled() {
        var banner = scheduleBanner();
        var form   = scheduleFormEl();
        if (banner) { banner.hidden = true; banner.textContent = ''; }
        if (form)   { form.hidden   = false; }
    }

    function showScheduleError(text) {
        var el = scheduleErrorEl();
        if (el) { el.textContent = text || ''; }
    }

    function clearScheduleError() { showScheduleError(''); }

    function readRecurrenceFromForm() {
        if (!editorRoot) { return { ok: false, error: 'editor not mounted' }; }
        var kindEl   = editorRoot.querySelector('#fld-schedule-kind');
        var hourEl   = editorRoot.querySelector('#fld-schedule-hour');
        var minuteEl = editorRoot.querySelector('#fld-schedule-minute');
        if (!kindEl || !hourEl || !minuteEl) { return { ok: false, error: 'recurrence controls missing' }; }
        var kind = String(kindEl.value || '').toLowerCase();
        if (kind !== 'daily' && kind !== 'weekly') { return { ok: false, error: 'recurrence kind must be daily or weekly' }; }
        var hourRaw = String(hourEl.value || '').trim();
        var minRaw  = String(minuteEl.value || '').trim();
        if (!/^[0-9]+$/.test(hourRaw)) { return { ok: false, error: 'hour must be an integer 0\u201323' }; }
        if (!/^[0-9]+$/.test(minRaw))  { return { ok: false, error: 'minute must be an integer 0\u201359' }; }
        var hour = parseInt(hourRaw, 10);
        var minute = parseInt(minRaw, 10);
        if (hour < 0 || hour > 23)   { return { ok: false, error: 'hour must be 0\u201323' }; }
        if (minute < 0 || minute > 59) { return { ok: false, error: 'minute must be 0\u201359' }; }
        var rec = { kind: kind, hour: hour, minute: minute };
        if (kind === 'weekly') {
            var cbs = editorRoot.querySelectorAll('.schedule-dow-cb');
            var days = [];
            for (var i = 0; i < cbs.length; i++) {
                if (cbs[i].checked) {
                    var d = parseInt(cbs[i].value, 10);
                    if (!isNaN(d) && d >= 0 && d <= 6) { days.push(d); }
                }
            }
            if (days.length === 0) { return { ok: false, error: 'select at least one day for weekly recurrence' }; }
            days.sort(function (a, b) { return a - b; });
            rec.daysOfWeek = days;
        }
        return { ok: true, recurrence: rec };
    }

    function populateRecurrenceForm(rec) {
        if (!editorRoot) { return; }
        var kindEl   = editorRoot.querySelector('#fld-schedule-kind');
        var hourEl   = editorRoot.querySelector('#fld-schedule-hour');
        var minuteEl = editorRoot.querySelector('#fld-schedule-minute');
        var weeklyRow = editorRoot.querySelector('#fld-schedule-weekly-row');
        var cbs       = editorRoot.querySelectorAll('.schedule-dow-cb');
        var safeRec   = rec || { kind: 'daily', hour: 9, minute: 0 };
        var kind      = (safeRec.kind === 'weekly') ? 'weekly' : 'daily';
        if (kindEl)   { kindEl.value = kind; }
        if (hourEl)   { hourEl.value   = (typeof safeRec.hour === 'number')   ? String(safeRec.hour)   : ''; }
        if (minuteEl) { minuteEl.value = (typeof safeRec.minute === 'number') ? String(safeRec.minute) : ''; }
        if (weeklyRow) { weeklyRow.hidden = (kind !== 'weekly'); }
        var selected = {};
        if (Array.isArray(safeRec.daysOfWeek)) {
            for (var i = 0; i < safeRec.daysOfWeek.length; i++) { selected[String(safeRec.daysOfWeek[i])] = true; }
        }
        for (var j = 0; j < cbs.length; j++) { cbs[j].checked = !!selected[cbs[j].value]; }
    }

    function applyScheduleSecretRowVisibility() {
        if (!editorRoot) { return; }
        var row = editorRoot.querySelector('#fld-schedule-secret-row');
        if (!row) { return; }
        var modeEl = editorRoot.querySelector('#fld-authMode');
        var mode = modeEl ? String(modeEl.value || '') : '';
        row.hidden = (mode !== 'AppRegistrationSecret');
        var input = editorRoot.querySelector('#fld-schedule-secret');
        if (input && row.hidden) { input.value = ''; }
    }

    function formatRecurrenceForDisplay(rec) {
        if (!rec) { return '(unknown)'; }
        var hh = (typeof rec.hour === 'number')   ? (rec.hour   < 10 ? '0' + rec.hour   : String(rec.hour))   : '??';
        var mm = (typeof rec.minute === 'number') ? (rec.minute < 10 ? '0' + rec.minute : String(rec.minute)) : '??';
        if (rec.kind === 'weekly') {
            var names = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
            var dows  = Array.isArray(rec.daysOfWeek) ? rec.daysOfWeek.map(function (d) { return names[d] || ('?' + d); }) : [];
            return 'weekly ' + (dows.length ? dows.join(', ') : '(no days)') + ' at ' + hh + ':' + mm;
        }
        return 'daily at ' + hh + ':' + mm;
    }

    // V1.S07 -- map a health.status code to the human-facing pill
    // text + CSS modifier suffix. Pure function over the code; the
    // call site appends 'schedule-status-pill-' + suffix to compose
    // the className. The set is closed: any unknown status falls
    // through to the 'Unknown' pill.
    function healthPillLabelForStatus(status) {
        switch (status) {
            case 'not_registered':      return { text: 'Not registered',      suffix: 'not_registered' };
            case 'current':             return { text: 'Current',             suffix: 'current' };
            case 'stale':               return { text: 'Stale',               suffix: 'stale' };
            case 'last_run_failed':     return { text: 'Last run failed',     suffix: 'last_run_failed' };
            case 'last_run_refused':    return { text: 'Last run refused',    suffix: 'last_run_refused' };
            case 'last_run_interrupted':return { text: 'Last run interrupted',suffix: 'last_run_interrupted' };
            case 'last_run_running':    return { text: 'Running',             suffix: 'last_run_running' };
            default:                    return { text: 'Unknown',             suffix: 'unknown' };
        }
    }

    function makeHealthPillElement(health) {
        // Returns the pill <span> for a health object, or null when
        // there is nothing to render (defensive; the backend always
        // emits the health block, but old broker responses are
        // tolerated).
        if (!health || typeof health.status !== 'string') { return null; }
        var labelled = healthPillLabelForStatus(health.status);
        var pill = document.createElement('span');
        pill.className = 'schedule-status-pill schedule-status-pill-' + labelled.suffix;
        pill.textContent = labelled.text;
        return pill;
    }

    function appendHealthLastTerminalLine(parentEl, health) {
        // Renders the "Last terminal scheduled run: ..." paragraph
        // from the health block. Skipped when no terminal cook is
        // recorded yet. Uses the same #/cooks/<id> hash route as the
        // last-imported link (V1.S06d). Pure DOM construction; no
        // innerHTML, no template literals.
        if (!parentEl || !health || !health.lastTerminalCookId) { return; }
        var line = document.createElement('p');
        line.className = 'schedule-status-line schedule-status-terminal';

        var summary;
        switch (health.lastTerminalStatus) {
            case 'completed':   summary = 'Last scheduled run completed'; break;
            case 'failed':
                if (health.lastTerminalErrorClass === 'pax_nonzero_exit') {
                    summary = 'Last scheduled run failed in PAX';
                } else if (health.lastTerminalErrorClass === 'wrapper_spawn_failed') {
                    summary = 'Last scheduled run failed: wrapper could not spawn PAX';
                } else if (health.lastTerminalErrorClass === 'wrapper_internal_error') {
                    summary = 'Last scheduled run failed: wrapper internal error';
                } else {
                    summary = 'Last scheduled run failed';
                }
                break;
            case 'refused':
                if (health.lastTerminalErrorClass === 'refused_stale_projection') {
                    summary = 'Last scheduled run refused: recipe changed since registration';
                } else {
                    summary = 'Last scheduled run refused';
                }
                break;
            case 'interrupted':
                if (health.lastTerminalErrorClass === 'wrapper_orphan_classified') {
                    summary = 'Last scheduled run was interrupted or orphan-classified';
                } else {
                    summary = 'Last scheduled run was interrupted';
                }
                break;
            default:
                summary = 'Last scheduled run: ' + String(health.lastTerminalStatus || 'unknown');
                break;
        }
        line.appendChild(document.createTextNode(summary + ': '));

        var anchor = document.createElement('a');
        anchor.href = '#/cooks/' + health.lastTerminalCookId;
        anchor.className = 'schedule-status-terminal-link';
        anchor.textContent = health.lastTerminalCookId;
        line.appendChild(anchor);

        if (health.lastTerminalAt) {
            line.appendChild(document.createTextNode(' (' + health.lastTerminalAt + ')'));
        }
        line.appendChild(document.createTextNode('.'));
        parentEl.appendChild(line);
    }

    function appendHealthLastCheckedLine(parentEl, health) {
        // Renders the "Last checked: <iso>" paragraph using
        // health.staleProjectionCheckedAt (mapped server-side from
        // scheduled_tasks.last_stale_check_at). Skipped when null.
        if (!parentEl || !health || !health.staleProjectionCheckedAt) { return; }
        var p = document.createElement('p');
        p.className = 'schedule-status-line schedule-status-checked';
        p.textContent = 'Last checked: ' + health.staleProjectionCheckedAt + '.';
        parentEl.appendChild(p);
    }

    // V1.S3 -- map an osDrift.status code to the OS-task drift pill
    // text + severity bucket. The osDrift block is the read-only
    // comparison between the live Windows Task Scheduler state and
    // the registered schedule, produced by the broker on the
    // scheduled-task GET path. The set is closed: any unrecognised
    // status is treated as 'unknown' with caution (warning) styling,
    // never error styling. A null result means "render no pill":
    // 'in_sync' and 'not_applicable' are not surfaced as drift
    // warnings. Severity buckets map to the three pill classes
    // (in-sync / warning / error).
    function osDriftPillLabelForStatus(status) {
        switch (status) {
            case 'in_sync':           return { text: 'Windows task in sync',        severity: 'in-sync' };
            case 'not_applicable':    return null;
            case 'row_without_task':  return { text: 'Windows task missing',        severity: 'error' };
            case 'trigger_divergent': return { text: 'Windows trigger changed',     severity: 'warning' };
            case 'disabled_in_os':    return { text: 'Disabled in Windows',         severity: 'warning' };
            case 'action_divergent':  return { text: 'Windows task action changed', severity: 'warning' };
            case 'probe_failed':      return { text: 'Drift check failed',          severity: 'error' };
            case 'unknown':           return { text: 'Windows task state unknown',  severity: 'warning' };
            default:                  return { text: 'Windows task state unknown',  severity: 'warning' };
        }
    }

    function appendOsDriftStatus(parentEl, osDrift) {
        // V1.S3 -- read-only OS-task drift indicator. Surfaces the
        // additive osDrift block from the scheduled-task GET payload
        // as a small pill plus its supporting message. Display only:
        // no button, no click handler, no repair / re-register /
        // delete-task action, and no extra route call. Older broker
        // responses without an osDrift block are tolerated -- the
        // function simply renders nothing and the existing health
        // pill and status lines are unaffected. Pure DOM
        // construction; no innerHTML, no template literals.
        if (!parentEl || !osDrift || typeof osDrift.status !== 'string') { return; }
        var labelled = osDriftPillLabelForStatus(osDrift.status);
        if (!labelled) { return; }

        var container = document.createElement('div');
        container.className = 'schedule-osdrift';

        var pill = document.createElement('span');
        pill.className = 'schedule-osdrift-pill schedule-osdrift-pill-' + labelled.severity;
        pill.textContent = labelled.text;
        container.appendChild(pill);

        var message = (typeof osDrift.message === 'string') ? osDrift.message : '';
        if (message) {
            var msg = document.createElement('p');
            msg.className = 'schedule-status-line schedule-osdrift-message';
            msg.textContent = message;
            container.appendChild(msg);
        }

        parentEl.appendChild(container);
    }

    function renderScheduleStatusNotScheduled(notRegisteredBody) {
        var s = scheduleStatusEl();
        if (!s) { return; }
        s.hidden = false;
        s.replaceChildren();

        // V1.S07 -- render the 'Not registered' pill from the health
        // block when the backend supplied one; tolerate the legacy
        // no-health response shape by synthesising a minimal pill.
        var health = (notRegisteredBody && notRegisteredBody.health)
            ? notRegisteredBody.health
            : { status: 'not_registered' };
        var pill = makeHealthPillElement(health);
        if (pill) { s.appendChild(pill); }

        var p = document.createElement('p');
        p.className = 'schedule-status-line';
        p.textContent = 'Not scheduled.';
        s.appendChild(p);

        // V1.S3 -- read-only OS-task drift indicator. On the
        // not-registered path the broker reports osDrift.status
        // 'not_applicable', which renders nothing; the call is wired
        // for completeness and forward-compatibility.
        appendOsDriftStatus(s, (notRegisteredBody && notRegisteredBody.osDrift) ? notRegisteredBody.osDrift : null);

        var deleteBtn = editorRoot.querySelector('#schedule-delete-button');
        if (deleteBtn) { deleteBtn.hidden = true; }
    }

    function renderScheduleStatusRegistered(task) {
        var s = scheduleStatusEl();
        if (!s) { return; }
        s.hidden = false;
        s.replaceChildren();

        // V1.S07 -- pill first. health.status drives the at-a-glance
        // signal; the legacy stale-line and last-imported-link below
        // remain for backward compatibility and operator detail.
        var health = task && task.health ? task.health : null;
        var pill = makeHealthPillElement(health);
        if (pill) { s.appendChild(pill); }

        var line1 = document.createElement('p');
        line1.className = 'schedule-status-line';
        var rec = task && task.recurrence ? task.recurrence : null;
        line1.textContent = 'Registered: ' + formatRecurrenceForDisplay(rec) + '.';
        s.appendChild(line1);

        // V1.S07 -- "Last checked: <iso>" line surfacing the
        // staleProjectionCheckedAt watermark.
        appendHealthLastCheckedLine(s, health);

        if (task && task.stale) {
            var stale = document.createElement('p');
            stale.className = 'schedule-status-line schedule-status-stale';
            var why = (task.staleReason === 'pax_version_changed')
                ? 'PAX engine version changed since registration.'
                : 'Recipe or Chef\u2019s Key changed since registration.';
            stale.textContent = 'Stale: ' + why + ' Re-save the schedule to refresh the projection.';
            s.appendChild(stale);
        }

        if (task && task.lastImportedCookId) {
            // V1.S06d -- the "Last imported run" line links into the
            // cook detail view so the operator can open the run with
            // one click. The link uses the SPA's existing hash route
            // (#/cooks/<cookId>); we never compose absolute URLs here.
            var lr = document.createElement('p');
            lr.className = 'schedule-status-line schedule-status-last';
            lr.appendChild(document.createTextNode('Last imported run: '));
            var anchor = document.createElement('a');
            anchor.href = '#/cooks/' + task.lastImportedCookId;
            anchor.className = 'schedule-status-last-link';
            anchor.textContent = task.lastImportedCookId;
            lr.appendChild(anchor);
            if (task.lastImportedAt) {
                lr.appendChild(document.createTextNode(' (' + task.lastImportedAt + ')'));
            }
            lr.appendChild(document.createTextNode('.'));
            s.appendChild(lr);
        }

        // V1.S07 -- "Last terminal scheduled run: ..." line surfacing
        // the most recent terminal outcome. Distinct from the
        // last-imported link above, which is the most-recently
        // reconciled cook (terminal OR running).
        appendHealthLastTerminalLine(s, health);

        // V1.S07 -- short operator-facing message ("Update the
        // scheduled task", "Open the run and inspect the PAX log",
        // etc.) sourced from the backend so messaging stays in one
        // place. Skipped when null.
        if (health && health.message) {
            var msg = document.createElement('p');
            msg.className = 'schedule-status-line schedule-status-message';
            msg.textContent = health.message;
            s.appendChild(msg);
        }

        // V1.S3 -- read-only OS-task drift indicator. Compares the
        // live Windows task state against the registered schedule and
        // surfaces a small pill + supporting message. Display only;
        // absent on older broker responses (renders nothing).
        appendOsDriftStatus(s, task && task.osDrift ? task.osDrift : null);

        var deleteBtn = editorRoot.querySelector('#schedule-delete-button');
        if (deleteBtn) { deleteBtn.hidden = false; }
    }

    function evaluateScheduleGate(recipe) {
        if (!recipe) { return { ok: false, reason: 'Recipe has not loaded yet.' }; }
        var execMode = recipe.executionMode || 'local-manual';
        if (execMode !== 'local-manual') {
            return { ok: false, reason: 'Scheduling requires executionMode = local-manual. Current: ' + execMode + '. Save the recipe as local-manual before registering a schedule.' };
        }
        var authMode = recipe.auth && recipe.auth.mode ? recipe.auth.mode : '';
        if (authMode !== 'AppRegistrationSecret' && authMode !== 'AppRegistrationCertificate') {
            return { ok: false, reason: 'Scheduling requires AppRegistrationSecret or AppRegistrationCertificate auth. WebLogin / DeviceCode / ManagedIdentity cannot run unattended via Task Scheduler. Current: ' + (authMode || '(none)') + '.' };
        }
        return { ok: true };
    }

    function refreshScheduleCard() {
        if (!state || state.isNew || !state.recipeId) { return; }
        var card = scheduleCard();
        if (!card) { return; }
        var gate = evaluateScheduleGate(state.initialRecipe);
        if (!gate.ok) {
            setScheduleDisabled(gate.reason);
            return;
        }
        setScheduleEnabled();
        applyScheduleSecretRowVisibility();
        loadScheduleTask();
    }

    function loadScheduleTask() {
        if (!state || state.isNew || !state.recipeId) { return; }
        if (state.scheduleLoadInFlight) { return; }
        var mySession = state;
        state.scheduleLoadInFlight = true;
        clearScheduleError();

        window.cookbookApi.get('/api/v1/recipes/' + encodeURIComponent(state.recipeId) + '/scheduled-task', { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
            if (state !== mySession) { return; }
            state.scheduleLoadInFlight = false;
            if (resp.networkError) {
                showScheduleError('Could not reach broker: ' + resp.networkError + '.');
                return;
            }
            if (resp.status === 404 && resp.body && resp.body.error === 'task_not_found') {
                state.scheduleTask = null;
                populateRecurrenceForm(null);
                renderScheduleStatusNotScheduled(resp.body);
                return;
            }
            if (!resp.ok || !resp.body) {
                var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                showScheduleError('Failed to load schedule: ' + code + '.');
                return;
            }
            // V1.S07 -- the backend returns 200 with registered:false
            // when no scheduled_tasks row exists for this recipe.
            // Route that path through the not-registered renderer so
            // the Schedule card pill says "Not registered" rather
            // than falling through to the registered renderer with
            // a missing recurrence.
            if (resp.body.registered === false) {
                state.scheduleTask = resp.body;
                populateRecurrenceForm(null);
                renderScheduleStatusNotScheduled(resp.body);
                return;
            }
            state.scheduleTask = resp.body;
            populateRecurrenceForm(resp.body.recurrence || null);
            renderScheduleStatusRegistered(resp.body);
        });
    }

    function onScheduleKindChange() {
        if (!editorRoot) { return; }
        var kindEl   = editorRoot.querySelector('#fld-schedule-kind');
        var weeklyRow = editorRoot.querySelector('#fld-schedule-weekly-row');
        if (kindEl && weeklyRow) { weeklyRow.hidden = (String(kindEl.value || '') !== 'weekly'); }
    }

    function onScheduleSaveClick() {
        if (!state || state.isNew || !state.recipeId) { return; }
        if (state.scheduleSaveInFlight) { return; }
        clearScheduleError();

        var parsed = readRecurrenceFromForm();
        if (!parsed.ok) { showScheduleError(parsed.error); return; }

        var authMode = (state.initialRecipe && state.initialRecipe.auth && state.initialRecipe.auth.mode) ? state.initialRecipe.auth.mode : '';
        var body = { recurrence: parsed.recurrence };
        if (authMode === 'AppRegistrationSecret') {
            var secretEl = editorRoot.querySelector('#fld-schedule-secret');
            var secret = secretEl ? String(secretEl.value || '') : '';
            if (!secret) {
                showScheduleError('Client secret is required on every save for AppRegistrationSecret recipes.');
                return;
            }
            body.clientSecret = secret;
        }

        var mySession = state;
        state.scheduleSaveInFlight = true;
        var saveBtn = editorRoot.querySelector('#schedule-save-button');
        if (saveBtn) { saveBtn.disabled = true; saveBtn.textContent = 'Saving\u2026'; }

        window.cookbookApi.put('/api/v1/recipes/' + encodeURIComponent(state.recipeId) + '/scheduled-task', body, { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
            if (state !== mySession) { return; }
            state.scheduleSaveInFlight = false;
            if (saveBtn) { saveBtn.disabled = false; saveBtn.textContent = 'Save schedule'; }

            // Best-effort plaintext scrub once the request has left.
            var sEl = editorRoot.querySelector('#fld-schedule-secret');
            if (sEl) { sEl.value = ''; }

            if (resp.networkError) {
                showScheduleError('Could not reach broker: ' + resp.networkError + '.');
                return;
            }
            if (!resp.ok) {
                var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                var detail = (resp.body && resp.body.detail) ? (' ' + resp.body.detail) : '';
                showScheduleError('Save failed: ' + code + '.' + detail);
                return;
            }
            loadScheduleTask();
        });
    }

    function onScheduleDeleteClick() {
        if (!state || state.isNew || !state.recipeId) { return; }
        if (state.scheduleDeleteInFlight) { return; }
        clearScheduleError();
        var mySession = state;
        state.scheduleDeleteInFlight = true;
        var delBtn = editorRoot.querySelector('#schedule-delete-button');
        if (delBtn) { delBtn.disabled = true; delBtn.textContent = 'Deleting\u2026'; }

        window.cookbookApi.del('/api/v1/recipes/' + encodeURIComponent(state.recipeId) + '/scheduled-task', null, { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
            if (state !== mySession) { return; }
            state.scheduleDeleteInFlight = false;
            if (delBtn) { delBtn.disabled = false; delBtn.textContent = 'Delete schedule'; }
            if (resp.networkError) {
                showScheduleError('Could not reach broker: ' + resp.networkError + '.');
                return;
            }
            if (!resp.ok && resp.status !== 404) {
                var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                showScheduleError('Delete failed: ' + code + '.');
                return;
            }
            state.scheduleTask = null;
            populateRecurrenceForm(null);
            renderScheduleStatusNotScheduled();
        });
    }

    function attachScheduleListeners() {
        if (!editorRoot) { return; }
        var kindEl = editorRoot.querySelector('#fld-schedule-kind');
        if (kindEl) { kindEl.addEventListener('change', onScheduleKindChange); }
        var saveBtn = editorRoot.querySelector('#schedule-save-button');
        if (saveBtn) { saveBtn.addEventListener('click', onScheduleSaveClick); }
        var delBtn = editorRoot.querySelector('#schedule-delete-button');
        if (delBtn) { delBtn.addEventListener('click', onScheduleDeleteClick); }
        var modeEl = editorRoot.querySelector('#fld-authMode');
        if (modeEl) { modeEl.addEventListener('change', applyScheduleSecretRowVisibility); }
    }

    // ----------------------------------------------------------------
    // Listeners
    // ----------------------------------------------------------------
    function onFirstInput(ev) {
        if (!state) { return; }
        // Single coarse "form has been touched" flag. The listener is
        // removed after firing once so the editor section adds no
        // ongoing input-stream surface.
        if (ev && ev.target && ev.target.tagName) {
            var tag = ev.target.tagName.toUpperCase();
            if (tag !== 'INPUT' && tag !== 'SELECT' && tag !== 'TEXTAREA') { return; }
        }
        state.formTouched = true;
        editorRoot.removeEventListener('input', onFirstInput, true);
        applyButtonStates();
        hideBanner();
    }

    function onFieldBlur(ev) {
        if (!state) { return; }
        var t = ev && ev.target;
        if (!t || !t.id) { return; }
        var path = pathForFieldId(t.id);
        if (!path) { return; }

        getValidator().then(function (validate) {
            if (!validate || state === null) { return; }
            var payload = extractPayloadFromDom(editorRoot);
            var ok = validate(payload);
            // Update only this field's error slot; other fields are
            // updated only on Save or on their own blur.
            clearFieldError(t.id);
            if (ok) { return; }
            var errs = filterClientErrors((validate.errors || []).map(normalizeAjvError));
            for (var i = 0; i < errs.length; i++) {
                if (resolveErrorPath(errs[i]) === path) {
                    setFieldError(t.id, errs[i].message);
                }
            }
        });
    }

    function onSaveClick(ev) {
        if (ev) { ev.preventDefault(); }
        doSave();
    }

    function onCancelClick(ev) {
        if (!state) { return; }
        if (isDirty()) {
            var ok = window.confirm('You have unsaved changes. Discard them?');
            if (!ok) {
                if (ev) { ev.preventDefault(); }
                return;
            }
        }
        // Otherwise let the <a href="#/recipes"> navigate normally; the
        // router teardown will fire on hashchange.
    }

    // ----------------------------------------------------------------
    // F2F -- Recipe Takeout export
    //
    // The Export Recipe Takeout button posts the saved recipe to the
    // F2B backend route and triggers a browser download of the
    // resulting Recipe Takeout JSON. The route is the ONLY source of
    // truth for the envelope contents and the canonical filename
    // slug; this client mirrors the slug rules as a transport-only
    // fallback because window.cookbookApi.post does not expose
    // response headers (the broker sets Content-Disposition with
    // filename="<slug>.json.pax", but the helper drops headers
    // after parsing the body). The broker still accepts the legacy
    // .paxrecipe.json and bare .json extensions on import.
    //
    // Identity rule: the download filename is a transport convenience.
    // It MUST NOT flow into identity.name, into the editor's saved
    // recipe, into localStorage / sessionStorage, or anywhere else.
    // It exists only as the user-facing "Save as..." default in the
    // browser's download prompt.
    //
    // Runtime gates (defense-in-depth on top of applyButtonStates):
    //   1. Refuses to run when state.exportInFlight is already true.
    //   2. Refuses to run when any other operation is in flight.
    //   3. Refuses to run for state.isNew or !state.recipeId.
    //   4. Refuses to run when isDirty() (or formTouched) -- the
    //      chef must save first so the export reflects the canonical
    //      server-side recipe, not the unsaved DOM state.
    // The export NEVER calls Save or Bake automatically, and never
    // mutates the in-memory recipe / dirty flag / preview rail.
    // ----------------------------------------------------------------
    function recipeTakeoutFilenameSlug(recipeName) {
        // Mirrors broker Get-RecipeTakeoutFilenameSlug. Pure; called
        // only with the current saved recipe's identity.name.
        if (typeof recipeName !== 'string' || recipeName.length === 0) {
            return 'recipe.json.pax';
        }
        var lower = recipeName.toLowerCase();
        var out = '';
        for (var i = 0; i < lower.length; i++) {
            var c = lower.charAt(i);
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) {
                out += c;
            } else {
                out += '-';
            }
        }
        while (out.indexOf('--') >= 0) { out = out.split('--').join('-'); }
        // Trim leading/trailing '-'.
        var start = 0;
        var end = out.length;
        while (start < end && out.charAt(start) === '-') { start++; }
        while (end > start && out.charAt(end - 1) === '-') { end--; }
        out = out.substring(start, end);
        if (out.length > 60) {
            out = out.substring(0, 60);
            // Re-trim trailing '-' after truncate.
            var e2 = out.length;
            while (e2 > 0 && out.charAt(e2 - 1) === '-') { e2--; }
            out = out.substring(0, e2);
        }
        if (out.length === 0) { out = 'recipe'; }
        return out + '.json.pax';
    }

    function exportErrorMessageForCode(code, status) {
        if (code === 'recipe_not_found') {
            return 'Cookbook could not find this recipe.';
        }
        if (code === 'takeout_sanitization_failed' ||
            code === 'takeout_secret_leak_detected' ||
            code === 'takeout_envelope_invalid') {
            return 'Cookbook blocked export because the recipe contains data that cannot be included safely.';
        }
        if (status === 401 || status === 423) {
            return 'Please sign in first.';
        }
        return 'Cookbook could not export this Recipe Takeout.';
    }

    function onExportClick(ev) {
        if (ev) { ev.preventDefault(); }
        doExport();
    }

    function doExport() {
        if (!state) { return; }
        if (state.exportInFlight) { return; }
        if (state.saveInFlight || state.previewRunInFlight || state.loadInFlight || state.cookInFlight || state.readinessInFlight) { return; }
        if (state.isNew || !state.recipeId) { return; }
        if (state.formTouched) {
            // The dirty gate is also enforced via applyButtonStates();
            // refuse here too so a programmatic click cannot bypass.
            showBanner('error', 'Save changes before exporting.', null);
            return;
        }

        // Filename derived from the current saved recipe's identity
        // name. This is a TRANSPORT convenience for the browser's
        // download prompt. It is never assigned back to the recipe
        // and never persisted.
        var savedRecipeName = (state.initialRecipe && state.initialRecipe.identity && state.initialRecipe.identity.name)
            ? state.initialRecipe.identity.name : '';
        var transportFilename = recipeTakeoutFilenameSlug(savedRecipeName);

        var mySession = state;
        var recipeId  = state.recipeId;

        state.exportInFlight = true;
        applyButtonStates();
        showBanner('info', 'Exporting Recipe Takeout\u2026', null);

        // F2B contract: POST returns application/json with the envelope
        // as the body. cookbookApi.post() parses the JSON into resp.body
        // and ALSO leaves the original UTF-8 bytes in resp.rawText, so
        // we can stream the server's exact bytes into the Blob without
        // a re-stringify round trip.
        window.cookbookApi.post('/api/v1/recipes/' + recipeId + '/takeout', {}, {
            signal: state.abortCtrl ? state.abortCtrl.signal : null
        }).then(function (resp) {
            if (state !== mySession) { return; }
            state.exportInFlight = false;
            applyButtonStates();

            if (resp.networkError) {
                showBanner('error', 'Could not reach broker: ' + resp.networkError + '.', null);
                return;
            }
            if (resp.status === 200 && resp.rawText) {
                triggerRecipeTakeoutDownload(resp.rawText, transportFilename);
                showBanner('success', 'Recipe Takeout exported. Downloaded file: ' + transportFilename + '.', null);
                return;
            }
            var code = (resp.body && resp.body.error) ? resp.body.error : null;
            showBanner('error', exportErrorMessageForCode(code, resp.status), null);
        });
    }

    function triggerRecipeTakeoutDownload(jsonText, transportFilename) {
        // Build a Blob from the exact bytes the broker sent. The Blob
        // type stays application/json to match the server's
        // Content-Type. The temporary anchor is created, clicked,
        // and removed; the object URL is revoked synchronously after
        // click so we do not leak it.
        var blob   = new Blob([jsonText], { type: 'application/json' });
        var url    = URL.createObjectURL(blob);
        var anchor = document.createElement('a');
        anchor.href     = url;
        anchor.download = transportFilename;
        // anchor is not attached to the DOM tree; modern browsers
        // honour click() on detached anchors for downloads.
        document.body.appendChild(anchor);
        try {
            anchor.click();
        } finally {
            document.body.removeChild(anchor);
            URL.revokeObjectURL(url);
        }
    }

    function attachListeners() {
        // Single 'input' listener (capture phase, removed after first
        // hit) for the Save-enable trigger. No 'input' listener stays
        // alive past the first keystroke -- the editor is not reactive.
        editorRoot.addEventListener('input', onFirstInput, true);
        // V1.S04: any form input invalidates a prior readiness result.
        // The listener is attached for the lifetime of the mount and
        // does not interact with the formTouched flag.
        editorRoot.addEventListener('input', onAnyInputStaleReadiness, true);
        // V1.S19: any form input or select-change recomputes the
        // Required Permissions rail. Lifetime of the mount. Pure read
        // from DOM -> resolver -> viewer; no payload side effects.
        editorRoot.addEventListener('input',  onAnyInputRefreshPermissions, true);
        editorRoot.addEventListener('change', onAnyInputRefreshPermissions, true);
        // Per-field blur validation (A6). 'focusout' bubbles; 'blur'
        // does not. One listener at the editor root.
        editorRoot.addEventListener('focusout', onFieldBlur);
        editorRoot.querySelector('#save-button').addEventListener('click', onSaveClick);
        editorRoot.querySelector('#preview-button').addEventListener('click', onPreviewClick);
        editorRoot.querySelector('#cancel-link').addEventListener('click', onCancelClick);
        var kb = editorRoot.querySelector('#cook-button');
        if (kb) { kb.addEventListener('click', onCookClick); }
        var rb = editorRoot.querySelector('#readiness-button');
        if (rb) { rb.addEventListener('click', onReadinessClick); }
        // F2F: Export Recipe Takeout click handler. The button only
        // appears in edit mode (revealed at mount()), so the listener
        // is bound unconditionally and the runtime gate inside
        // doExport() refuses to run for new/dirty/in-flight cases.
        var xb = editorRoot.querySelector('#export-takeout-button');
        if (xb) { xb.addEventListener('click', onExportClick); }
        // X11A: pin / unpin toggle click. Edit-mode only (button stays
        // hidden in new mode); doPinToggleDetail() refuses to run
        // without a recipeId, so binding unconditionally is safe.
        var pinBtn = editorRoot.querySelector('#pin-toggle-button');
        if (pinBtn) { pinBtn.addEventListener('click', onPinToggleClick); }
        // every authMode change. The 'change' event fires on
        // <select> commit -- no input-listener interaction.
        var modeSel = editorRoot.querySelector('#fld-authMode');
        if (modeSel) { modeSel.addEventListener('change', onAuthModeChange); }
        // V1.S26: re-sync conditional visibility on every change
        // that flips a card or row on/off. Each sync function is
        // idempotent and runs no DOM mutation when its inputs are
        // unchanged.
        var qmSel = editorRoot.querySelector('#fld-queryMode');
        if (qmSel) { qmSel.addEventListener('change', onQueryModeChange); }
        var m365Chk = editorRoot.querySelector('#fld-m365');
        if (m365Chk) { m365Chk.addEventListener('change', onM365Change); }
        var fmSel = editorRoot.querySelector('#fld-factMode');
        if (fmSel) { fmSel.addEventListener('change', onFactModeChange); }
        var afSel = editorRoot.querySelector('#fld-agentFilterMode');
        if (afSel) { afSel.addEventListener('change', onAgentFilterModeChange); }
        var uidSel = editorRoot.querySelector('#fld-userInfoDestMode');
        if (uidSel) { uidSel.addEventListener('change', onUserInfoDestModeChange); }
    }

    function detachListeners() {
        if (!editorRoot) { return; }
        editorRoot.removeEventListener('input', onFirstInput, true);
        editorRoot.removeEventListener('input', onAnyInputStaleReadiness, true);
        editorRoot.removeEventListener('input',  onAnyInputRefreshPermissions, true);
        editorRoot.removeEventListener('change', onAnyInputRefreshPermissions, true);
        editorRoot.removeEventListener('focusout', onFieldBlur);
        var sb = editorRoot.querySelector('#save-button');
        if (sb) { sb.removeEventListener('click', onSaveClick); }
        var pb = editorRoot.querySelector('#preview-button');
        if (pb) { pb.removeEventListener('click', onPreviewClick); }
        var cl = editorRoot.querySelector('#cancel-link');
        if (cl) { cl.removeEventListener('click', onCancelClick); }
        var kb = editorRoot.querySelector('#cook-button');
        if (kb) { kb.removeEventListener('click', onCookClick); }
        var rb = editorRoot.querySelector('#readiness-button');
        if (rb) { rb.removeEventListener('click', onReadinessClick); }
        var xb = editorRoot.querySelector('#export-takeout-button');
        if (xb) { xb.removeEventListener('click', onExportClick); }
        var pinBtn = editorRoot.querySelector('#pin-toggle-button');
        if (pinBtn) { pinBtn.removeEventListener('click', onPinToggleClick); }
        var qmSel = editorRoot.querySelector('#fld-queryMode');
        if (qmSel) { qmSel.removeEventListener('change', onQueryModeChange); }
        var m365Chk = editorRoot.querySelector('#fld-m365');
        if (m365Chk) { m365Chk.removeEventListener('change', onM365Change); }
        var fmSel = editorRoot.querySelector('#fld-factMode');
        if (fmSel) { fmSel.removeEventListener('change', onFactModeChange); }
        var afSel = editorRoot.querySelector('#fld-agentFilterMode');
        if (afSel) { afSel.removeEventListener('change', onAgentFilterModeChange); }
        var uidSel = editorRoot.querySelector('#fld-userInfoDestMode');
        if (uidSel) { uidSel.removeEventListener('change', onUserInfoDestModeChange); }
    }

    // Phase AF: authMode change handler. Toggles the auth-profile
    // picker visibility (only AppRegistration variants need it). The
    // picker's <option> list is loaded lazily on the first reveal,
    // and on every subsequent reveal the saved value is preserved.
    function onAuthModeChange() {
        syncAuthProfileVisibility();
    }

    function syncAuthProfileVisibility() {
        if (!editorRoot) { return; }
        var modeSel = editorRoot.querySelector('#fld-authMode');
        var row     = editorRoot.querySelector('#fld-authProfileId-row');
        if (!modeSel || !row) { return; }
        var mode = modeSel.value || '';
        var needsProfile =
            (mode === 'AppRegistrationSecret' || mode === 'AppRegistrationCertificate');
        if (needsProfile) {
            row.hidden = false;
            loadAuthProfileOptions(mode);
        } else {
            row.hidden = true;
        }
    }

    // M2.2: appendBehavior change handler + visibility sync. The
    // appendFile row is revealed only when behavior is 'append';
    // when behavior is 'fresh' the input is hidden AND cleared so
    // a stale value cannot leak into the payload.
    function onFactModeChange() {
        syncFactModeVisibility();
    }

    function syncFactModeVisibility() {
        if (!editorRoot) { return; }
        var sel    = editorRoot.querySelector('#fld-factMode');
        var opRow  = editorRoot.querySelector('#fld-outputPath-row');
        var apRow  = editorRoot.querySelector('#fld-appendFile-row');
        if (!sel || !opRow || !apRow) { return; }
        var mode = sel.value || 'outputPath';
        if (mode === 'append') {
            apRow.hidden = false;
            opRow.hidden = true;
            var op = editorRoot.querySelector('#fld-outputPath');
            if (op) { op.value = ''; }
            var opErr = editorRoot.querySelector('#fld-outputPath-error');
            if (opErr) { opErr.textContent = ''; }
        } else {
            apRow.hidden = true;
            opRow.hidden = false;
            var ap = editorRoot.querySelector('#fld-appendFile');
            if (ap) { ap.value = ''; }
            var apErr = editorRoot.querySelector('#fld-appendFile-error');
            if (apErr) { apErr.textContent = ''; }
        }
    }

    // V1.S26: query.mode is the topmost shape switch. When the chef
    // picks 'userInfoOnly' the entire audit query surface is hidden
    // (dates, filters, M365 usage bundle, fact destination, rollup);
    // the user-info destination card stays visible and is required.
    // 'audit' restores everything. The sync only toggles visibility;
    // it does NOT mutate stored DOM values for the hidden controls
    // (so the chef can flip back without losing draft input) except
    // for two schema-required forces: includeM365Usage MUST be false
    // and includeUserInfo MUST be true under userInfoOnly. The
    // user-info destination mode is auto-promoted from 'default' to
    // 'outputPath' so the recipe stays valid (Shape 3 requires the
    // destinations.userInfo block).
    function onQueryModeChange() {
        syncQueryModeVisibility();
        // M365 visibility depends on the current query mode; refresh
        // the includeCp row as a follow-on.
        syncM365Visibility();
    }

    function syncQueryModeVisibility() {
        if (!editorRoot) { return; }
        var sel = editorRoot.querySelector('#fld-queryMode');
        if (!sel) { return; }
        var mode = sel.value || 'audit';
        var isUio = (mode === 'userInfoOnly');

        // Card-level hides.
        var whenCard    = editorRoot.querySelector('#editor-when-card');
        var filtersCard = editorRoot.querySelector('#editor-filters-card');
        if (whenCard)    { whenCard.hidden    = isUio; }
        if (filtersCard) { filtersCard.hidden = isUio; }

        // Row-level hides inside the Where card. Under audit shape
        // each row's per-row sync (factMode / m365) decides the
        // final hidden state, so we only enforce the userInfoOnly
        // hide here and let the per-row syncs take over below.
        var rollupRow     = editorRoot.querySelector('#fld-rollup-row');
        var factModeRow   = editorRoot.querySelector('#fld-factMode-row');
        var outputPathRow = editorRoot.querySelector('#fld-outputPath-row');
        var appendFileRow = editorRoot.querySelector('#fld-appendFile-row');
        if (rollupRow)     { rollupRow.hidden     = isUio; }
        if (factModeRow)   { factModeRow.hidden   = isUio; }
        if (isUio) {
            if (outputPathRow) { outputPathRow.hidden = true; }
            if (appendFileRow) { appendFileRow.hidden = true; }
        }

        // M365 row inside the What card.
        var m365Row     = editorRoot.querySelector('#fld-m365-row');
        var includeCpRow= editorRoot.querySelector('#fld-includeCp-row');
        if (m365Row) { m365Row.hidden = isUio; }
        if (isUio && includeCpRow) { includeCpRow.hidden = true; }

        if (isUio) {
            // Force the two ingredients-shape requirements.
            var m365Chk = editorRoot.querySelector('#fld-m365');
            if (m365Chk) { m365Chk.checked = false; }
            var uiChk = editorRoot.querySelector('#fld-userInfo');
            if (uiChk) { uiChk.checked = true; }
            // Auto-promote user-info destination from default ->
            // outputPath so the recipe is valid out of the gate.
            var uidSel = editorRoot.querySelector('#fld-userInfoDestMode');
            if (uidSel && (uidSel.value === 'default' || !uidSel.value)) {
                uidSel.value = 'outputPath';
                syncUserInfoDestModeVisibility();
            }
        } else {
            // Audit shape -- restore per-row visibility by delegating
            // to the per-row sync functions.
            syncFactModeVisibility();
            syncM365Visibility();
        }
    }

    // V1.S26: agent filter mode -> show the agentIds textarea row
    // only when mode === 'agentIds'. The other three modes
    // ('none', 'agentsOnly', 'excludeAgents') keep the row hidden
    // and clear any stale agentIds input so it does not leak into
    // the payload.
    function onAgentFilterModeChange() {
        syncAgentFilterVisibility();
    }

    function syncAgentFilterVisibility() {
        if (!editorRoot) { return; }
        var sel = editorRoot.querySelector('#fld-agentFilterMode');
        var row = editorRoot.querySelector('#fld-agentIds-row');
        if (!sel || !row) { return; }
        var mode = sel.value || 'none';
        if (mode === 'agentIds') {
            row.hidden = false;
        } else {
            row.hidden = true;
            var ta = editorRoot.querySelector('#fld-agentIds');
            if (ta) { ta.value = ''; }
            var err = editorRoot.querySelector('#fld-agentIds-error');
            if (err) { err.textContent = ''; }
        }
    }

    // V1.S26: user-info destination mode -> show the path row when
    // mode === 'outputPath', the appendFile row when mode === 'append',
    // and neither row (default mode = omit destinations.userInfo).
    function onUserInfoDestModeChange() {
        syncUserInfoDestModeVisibility();
    }

    function syncUserInfoDestModeVisibility() {
        if (!editorRoot) { return; }
        var sel    = editorRoot.querySelector('#fld-userInfoDestMode');
        var pathRow = editorRoot.querySelector('#fld-userInfoPath-row');
        var apRow   = editorRoot.querySelector('#fld-userInfoAppendFile-row');
        if (!sel || !pathRow || !apRow) { return; }
        var mode = sel.value || 'default';
        if (mode === 'outputPath') {
            pathRow.hidden = false;
            apRow.hidden = true;
            var ap = editorRoot.querySelector('#fld-userInfoAppendFile');
            if (ap) { ap.value = ''; }
            var apErr = editorRoot.querySelector('#fld-userInfoAppendFile-error');
            if (apErr) { apErr.textContent = ''; }
        } else if (mode === 'append') {
            pathRow.hidden = true;
            apRow.hidden = false;
            var p = editorRoot.querySelector('#fld-userInfoPath');
            if (p) { p.value = ''; }
            var pErr = editorRoot.querySelector('#fld-userInfoPath-error');
            if (pErr) { pErr.textContent = ''; }
        } else {
            // default: both rows hidden, both inputs cleared.
            pathRow.hidden = true;
            apRow.hidden = true;
            var p2  = editorRoot.querySelector('#fld-userInfoPath');
            if (p2) { p2.value = ''; }
            var ap2 = editorRoot.querySelector('#fld-userInfoAppendFile');
            if (ap2) { ap2.value = ''; }
            var pErr2 = editorRoot.querySelector('#fld-userInfoPath-error');
            if (pErr2) { pErr2.textContent = ''; }
            var apErr2= editorRoot.querySelector('#fld-userInfoAppendFile-error');
            if (apErr2) { apErr2.textContent = ''; }
        }
    }

    // V1.S26: M365 usage checkbox -> show the includeCp row only when
    // M365 usage is checked. When the chef unchecks M365 the
    // includeCp checkbox is forced back to true (the persisted
    // recipe never carries includeCopilotInteraction=false outside
    // the M365 usage shape).
    function onM365Change() {
        syncM365Visibility();
    }

    function syncM365Visibility() {
        if (!editorRoot) { return; }
        var chk = editorRoot.querySelector('#fld-m365');
        var row = editorRoot.querySelector('#fld-includeCp-row');
        if (!chk || !row) { return; }
        var qmSel = editorRoot.querySelector('#fld-queryMode');
        var isUio = (qmSel && qmSel.value === 'userInfoOnly');
        if (isUio) {
            // Under Shape 3 the M365 row is hidden in full; the
            // includeCp row stays hidden regardless of the
            // checkbox state.
            row.hidden = true;
            return;
        }
        if (chk.checked) {
            row.hidden = false;
        } else {
            row.hidden = true;
            var inclCp = editorRoot.querySelector('#fld-includeCp');
            if (inclCp) { inclCp.checked = true; }
            var err = editorRoot.querySelector('#fld-includeCp-error');
            if (err) { err.textContent = ''; }
        }
    }

    // Phase AF: populate the auth-profile picker from the broker.
    // Filtered client-side by mode so the options only contain
    // profiles whose mode matches the recipe's current selection.
    // Network failures are surfaced as a 'fld-authProfileId-error'
    // alert but do not block the save path; the broker validates
    // the reference authoritatively at save time.
    function loadAuthProfileOptions(mode) {
        if (!editorRoot || !state) { return; }
        var sel = editorRoot.querySelector('#fld-authProfileId');
        if (!sel) { return; }
        var preserved = sel.value || '';
        var mySession = state;
        window.cookbookApi.get(
            '/api/v1/auth/profiles',
            { signal: state.abortCtrl ? state.abortCtrl.signal : null }
        ).then(function (resp) {
            if (state !== mySession || !editorRoot) { return; }
            // Reset the field-level error before re-rendering.
            var errEl = editorRoot.querySelector('#fld-authProfileId-error');
            if (errEl) { errEl.textContent = ''; }
            if (resp.networkError) {
                if (errEl) { errEl.textContent = 'Could not load Chef\u2019s Keys: ' + resp.networkError + '.'; }
                return;
            }
            if (!resp.ok) {
                // 423 / 401 are owned by the lock-overlay; suppress
                // a duplicate inline error for those.
                if (resp.status !== 423 && resp.status !== 401) {
                    if (errEl) {
                        var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                        errEl.textContent = 'Could not load Chef\u2019s Keys: ' + code + '.';
                    }
                }
                return;
            }
            var profiles = (resp.body && resp.body.profiles) || [];
            // Build option list. Always include the empty sentinel
            // so the chef can clear the picker.
            var opts = ['<option value="">(none selected)</option>'];
            var matched = false;
            for (var i = 0; i < profiles.length; i++) {
                var p = profiles[i];
                if (!p || p.mode !== mode) { continue; }
                var id   = String(p.authProfileId || '');
                var name = String(p.name || id);
                if (id === preserved) { matched = true; }
                opts.push(
                    '<option value="' + escapeAttr(id) + '">' +
                    escapeText(name) + ' \u2014 ' + escapeText(p.mode) +
                    '</option>'
                );
            }
            sel.innerHTML = opts.join('');
            // Restore the saved value if present in the filtered set.
            sel.value = matched ? preserved : '';
        });
    }

    // Cheap escapers used only for the auth-profile picker; the
    // editor's other innerHTML strings are static templates so they
    // do not need a general-purpose escaper.
    function escapeText(s) {
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;');
    }
    function escapeAttr(s) {
        return escapeText(s)
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    // ----------------------------------------------------------------
    // Edit-mode fetch
    // ----------------------------------------------------------------
    function loadExistingRecipe(recipeId) {
        var mySession = state;
        showBanner('info', 'Loading recipe \u2026', null);
        state.loadInFlight = true;
        applyButtonStates();

        window.cookbookApi.get('/api/v1/recipes/' + recipeId, { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
            if (state !== mySession) { return; }
            state.loadInFlight = false;
            applyButtonStates();

            if (resp.networkError) {
                showBanner('network', 'Could not reach broker: ' + resp.networkError + '.', function () {
                    loadExistingRecipe(recipeId);
                });
                return;
            }
            if (resp.status === 404) {
                showBanner('error', 'Recipe not found.', null);
                return;
            }
            if (!resp.ok || !resp.body || !resp.body.recipe) {
                var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                showBanner('error', 'Failed to load recipe: ' + code + '.', null);
                return;
            }
            state.initialRecipe = resp.body.recipe;
            // X11A: capture the broker's pin state for this recipe from
            // the detail meta so the pin toggle and the metadata strip
            // reflect it. Read-only int (0/1) projected to a boolean.
            state.isPinned = !!(resp.body.meta && resp.body.meta.is_pinned);
            populateForm(resp.body.recipe);
            applyPinToggleButton();
            hideBanner();
            // formTouched stays false; Save stays disabled until edit.

            // S6A: fetch the recipe-local cook history once the recipe
            // is loaded. Stale-on-mount only.
            loadRecentRuns();

            // V1.S06c: stale-on-mount evaluation of the schedule card.
            // Gating (executionMode / authMode) is computed from the
            // saved recipe, so this must run AFTER initialRecipe is
            // populated.
            refreshScheduleCard();
        });
    }

    // ----------------------------------------------------------------
    // Page lifecycle
    // ----------------------------------------------------------------
    function mount(container, params) {
        var p          = params || {};
        var isNew      = !p.recipeId;
        var title      = isNew ? 'New recipe' : 'Edit recipe';
        var crumbLabel = title;

        var html = PAGE_TEMPLATE
            .replace('{{TITLE}}', title)
            .replace('{{CRUMB}}', crumbLabel);
        container.innerHTML = html;

        editorRoot = container.querySelector('.recipe-editor');

        state = {
            isNew:                   isNew,
            recipeId:                p.recipeId || null,
            initialRecipe:           null,
            saveInFlight:            false,
            previewRunInFlight:      false,
            previewSeq:              0,
            loadInFlight:            false,
            cookInFlight:            false,
            recentRunsLoadInFlight:  false,
            scheduleLoadInFlight:    false,
            scheduleSaveInFlight:    false,
            scheduleDeleteInFlight:  false,
            scheduleTask:            null,
            formTouched:             false,
            // V1.S04: advisory readiness.  latestReadiness holds the
            // most recent server payload (or null if never run /
            // invalidated by a form edit).  readinessSeq mirrors the
            // preview-rail pattern so stale responses drop on the
            // floor.
            readinessInFlight:       false,
            readinessSeq:            0,
            latestReadiness:         null,
            // F2F: in-flight gate for the Export Recipe Takeout
            // action. Mutual exclusion is enforced through
            // applyButtonStates() so Save / Preview / Bake / Export
            // never run concurrently.
            exportInFlight:          false,
            // X11A: detail pin state + its own in-flight gate. isPinned
            // is populated from the recipe-load meta (meta.is_pinned);
            // pinInFlight disables only the pin toggle while its POST
            // is outstanding.
            isPinned:                false,
            pinInFlight:             false,
            // Phase AE: per-mount AbortController; aborted on teardown.
            abortCtrl:               (typeof AbortController === 'function') ? new AbortController() : null
        };

        attachListeners();
        attachScheduleListeners();

        // Reveal cook-related affordances only in edit mode. New mode
        // has no recipeId on the server, so neither the Cook button
        // nor the Recent runs rail can do anything useful.
        if (!isNew) {
            var kb = editorRoot.querySelector('#cook-button');
            if (kb) { kb.hidden = false; }
            var rrCard = editorRoot.querySelector('#recent-runs-card');
            if (rrCard) { rrCard.hidden = false; }
            // V1.S06c: Schedule card is edit-mode only. Gating is
            // evaluated against the saved recipe (executionMode /
            // authMode), and the broker re-enforces the same rules.
            var schedCard = editorRoot.querySelector('#schedule-card');
            if (schedCard) { schedCard.hidden = false; }
            // V1.S04: readiness panel + button are edit-mode only,
            // for the same reason as Cook -- there is no recipeId on
            // the server to probe in new mode.
            var rb = editorRoot.querySelector('#readiness-button');
            if (rb) { rb.hidden = false; }
            var ra = editorRoot.querySelector('#editor-readiness');
            if (ra) { ra.hidden = false; }
            // F2F: Export Recipe Takeout is an edit-mode action --
            // there is no recipe to export when state.isNew is true.
            // applyButtonStates() handles the dirty/in-flight gates.
            var xb = editorRoot.querySelector('#export-takeout-button');
            if (xb) { xb.hidden = false; }
            // X11A: pin / unpin toggle is edit-mode only. Its label and
            // disabled state are set by applyPinToggleButton() once the
            // recipe (and its meta.is_pinned) has loaded.
            var pinBtn = editorRoot.querySelector('#pin-toggle-button');
            if (pinBtn) { pinBtn.hidden = false; }
        }

        applyButtonStates();

        if (isNew) {
            populateForm(buildDefaults());
            // formTouched stays false; Save stays disabled.
        } else {
            loadExistingRecipe(state.recipeId);
        }

        // Kick the schema fetch as a soft warm-up. Save and blur both
        // await this same promise, so warming it here only avoids
        // first-blur lag.
        getValidator();
    }

    function teardown() {
        if (!state) { return; }
        detachListeners();
        // Phase AE: abort in-flight fetches before dropping refs.
        if (state.abortCtrl) {
            try { state.abortCtrl.abort(); } catch (e) {}
        }
        state      = null;
        editorRoot = null;
    }

    window.cookbookRecipeEditorPage = {
        mount:    mount,
        teardown: teardown
    };

    // Stage 4: subscribe to acquisition-state changes emitted by
    // pax-engine-overlay.js. The listener is module-scoped (added
    // once at script load, not at mount) so the flag stays current
    // even when the editor is unmounted; the next mount picks up
    // the latest value through applyButtonStates() which self-
    // guards against !editorRoot. dispatchEvent is best-effort and
    // never throws synchronously, but we wrap the handler so a
    // future listener that throws cannot block re-render.
    try {
        window.addEventListener('cookbook:acquisitionStateChanged', function (ev) {
            try {
                acquisitionBlocked = !!(ev && ev.detail && ev.detail.blocked);
                applyButtonStates();
            } catch (e) { /* swallow */ }
        });
    } catch (e) { /* listener registration unsupported -- non-fatal */ }
})();

