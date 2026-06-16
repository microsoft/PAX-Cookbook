// PAX Cookbook -- Pantry page (bundled-template list + detail).
//
// Page module for routes '#/pantry' (list) and '#/pantry/<templateId>'
// (detail). One module, two states. The router dispatches both routes
// here with params.templateId set to null (list) or the template id
// (detail). Mount() owns the entire DOM under #page-root for the
// duration the page is active; teardown() releases listeners and bumps
// the epoch so any in-flight fetch is discarded.
//
// What this module IS:
//   - a read-only catalog browser for templates bundled with this
//     install (loaded once at broker startup; no per-request rescan)
//   - a materialization surface that submits per-instance inputs and,
//     on 201, opens the resulting recipe in the recipe editor
//
// What this module is NOT, by design and by user contract:
//   - a marketplace / store / gallery / community hub
//   - a download surface / cloud-sync surface / template installer
//   - an auto-update surface / template-upgrade surface
//   - a ratings / comments / sharing surface
//   - a runtime selector / manifest editor / advanced override surface
//   - a place where templates execute anything; materialize produces a
//     recipe, then exits. The recipe (not the template) drives the cook.
//
// All operator inputs map 1:1 to recipe leaves. There is no hidden
// trailer, no extraArguments override, no path mutation. The server
// enforces this contract via a bounded materialize-body schema.
(function () {
    'use strict';

    // Page-owned IDs (built inside #page-root on mount, gone on teardown).
    var STATUS_EL_ID    = 'pantry-status';
    var LIST_EL_ID      = 'pantry-list';
    var DETAIL_EL_ID    = 'pantry-detail';
    var FORM_EL_ID      = 'pantry-materialize-form';
    var BANNER_EL_ID    = 'pantry-banner';
    var SUBMIT_BTN_ID   = 'pantry-submit-button';
    var PREVIEW_BTN_ID  = 'pantry-preview-button';
    var PREVIEW_EL_ID   = 'pantry-preview';

    // Shell-owned IDs (live in the topbar; the page reads/writes their
    // text/className but does NOT create or remove them).
    var BROKER_HOST_ID  = 'broker-host';
    var TOKEN_STATUS_ID = 'token-status';
    var LAST_FETCHED_ID = 'last-fetched';
    var RELOAD_BTN_ID   = 'reload-button';

    var PAGE_TEMPLATE_LIST = [
        '<section class="page recipe-section pantry-page pantry-page-list">',
            '<header class="page-header">',
                '<div class="page-header-text">',
                    '<h1 class="page-title">Pantry<button type="button" class="help-hook" data-help-topic="pantry.intro" aria-label="Help: About the Pantry" title="About the Pantry"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h1>',
                    '<p class="page-lede">Start from a bundled, read-only recipe template, then customize it in Recipes before baking through PAX.</p>',
                '</div>',
            '</header>',
            '<div id="pantry-status" class="recipe-status">Loading bundled templates</div>',
            '<div id="pantry-list" class="pantry-list" hidden></div>',
        '</section>'
    ].join('');

    var PAGE_TEMPLATE_DETAIL = [
        '<nav class="breadcrumb" aria-label="Breadcrumb">',
            '<a href="#/pantry">Pantry</a>',
            '<span aria-hidden="true"> &rsaquo; </span>',
            '<span id="pantry-detail-crumb">&hellip;</span>',
        '</nav>',
        '<section class="page recipe-section pantry-page pantry-page-detail">',
            '<div id="pantry-status" class="recipe-status">Loading template</div>',
            '<div id="pantry-banner" class="editor-banner" role="status" aria-live="polite" hidden></div>',
            '<div id="pantry-detail" hidden></div>',
        '</section>'
    ].join('');

    // Monotonic counter -- bumped on every mount AND on every teardown.
    // An in-flight fetch captures the epoch at request time and discards
    // its render if the live state's epoch has moved on.
    var nextEpoch = 1;
    var state = null;

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    function byId(id) { return document.getElementById(id); }

    function setText(id, text) {
        var el = byId(id);
        if (el) { el.textContent = text; }
    }

    function setStatus(text, kind) {
        var el = byId(STATUS_EL_ID);
        if (!el) { return; }
        el.textContent = text;
        el.className = 'recipe-status' + (kind ? ' ' + kind : '');
        el.hidden = false;
    }

    function hideStatus() {
        var el = byId(STATUS_EL_ID);
        if (el) { el.hidden = true; }
    }

    function showBanner(kind, text) {
        var el = byId(BANNER_EL_ID);
        if (!el) { return; }
        while (el.firstChild) { el.removeChild(el.firstChild); }
        el.hidden = false;
        el.className = 'editor-banner banner-' + kind;
        el.appendChild(document.createTextNode(text));
    }

    function hideBanner() {
        var el = byId(BANNER_EL_ID);
        if (!el) { return; }
        el.hidden = true;
        el.className = 'editor-banner';
        while (el.firstChild) { el.removeChild(el.firstChild); }
    }

    function isoNowUtc() { return new Date().toISOString(); }

    function el(tag, className, text) {
        var n = document.createElement(tag);
        if (className) { n.className = className; }
        if (text !== undefined && text !== null) { n.textContent = String(text); }
        return n;
    }

    // ----------------------------------------------------------------
    // List rendering
    // ----------------------------------------------------------------

    function renderList(templates) {
        var listEl = byId(LIST_EL_ID);
        if (!listEl) { return; }
        while (listEl.firstChild) { listEl.removeChild(listEl.firstChild); }
        // Phase M: also clear any prior empty-state remediation note
        // so a successful reload after a transient empty response
        // never leaves stale guidance attached to the page.
        var prevNote = byId('pantry-empty-note');
        if (prevNote && prevNote.parentNode) { prevNote.parentNode.removeChild(prevNote); }

        if (!templates || templates.length === 0) {
            listEl.hidden = true;
            setStatus('No bundled templates were loaded by the broker.', 'empty');
            // Phase M: operational remediation. The broker loads the
            // Pantry directory exactly once at startup; an empty list
            // here means either the install is partial (no templates
            // shipped) or the broker could not enumerate the directory.
            // Both states are diagnosable from Settings -> Integrity.
            // No automatic retry, no auto-repair, no remote fetch --
            // just a pointer to the authoritative diagnostic surface.
            var statusEl = byId(STATUS_EL_ID);
            if (statusEl && statusEl.parentNode) {
                var note = document.createElement('p');
                note.id = 'pantry-empty-note';
                note.className = 'empty-onboarding-note';
                note.appendChild(document.createTextNode(
                    'The Pantry directory is loaded once at broker startup. If templates were expected, open '
                ));
                var link = document.createElement('a');
                link.className = 'empty-onboarding-link';
                link.href = '#/settings';
                link.textContent = 'Settings';
                note.appendChild(link);
                note.appendChild(document.createTextNode(
                    ' and review the Integrity card to confirm the templates directory path and bundled PAX integrity. Cookbook does not download or auto-install templates.'
                ));
                statusEl.parentNode.insertBefore(note, statusEl.nextSibling);
            }
            return;
        }

        for (var i = 0; i < templates.length; i++) {
            var t = templates[i] || {};
            var card = el('article', 'pantry-card');

            var header = el('header', 'pantry-card-header');
            var titleLink = document.createElement('a');
            titleLink.href = '#/pantry/' + String(t.templateId || '');
            titleLink.className = 'pantry-card-title';
            titleLink.textContent = String(t.displayName || t.templateId || '');
            header.appendChild(titleLink);

            var badge = el('span', 'pantry-card-badge', String(t.category || ''));
            header.appendChild(badge);
            card.appendChild(header);

            card.appendChild(el('p', 'pantry-card-desc', String(t.shortDescription || '')));

            var metaRow = el('dl', 'pantry-card-meta');
            metaRow.appendChild(el('dt', null, 'Template version'));
            metaRow.appendChild(el('dd', null, String(t.templateVersion || '')));
            metaRow.appendChild(el('dt', null, 'Min PAX'));
            metaRow.appendChild(el('dd', null, String(t.minPaxScriptVersion || '')));
            metaRow.appendChild(el('dt', null, 'Min Cookbook'));
            metaRow.appendChild(el('dd', null, String(t.minCookbookVersion || '')));
            if (t.manualGuidanceCount && t.manualGuidanceCount > 0) {
                metaRow.appendChild(el('dt', null, 'Manual guidance'));
                metaRow.appendChild(el('dd', null, String(t.manualGuidanceCount) + ' note(s)'));
            }
            card.appendChild(metaRow);

            listEl.appendChild(card);
        }
        hideStatus();
        listEl.hidden = false;
    }

    // ----------------------------------------------------------------
    // Detail rendering
    // ----------------------------------------------------------------

    function renderArtifacts(tpl) {
        var section = el('section', 'pantry-section');
        section.appendChild(el('h2', null, 'Produces'));
        if (tpl.produces && tpl.produces.summary) {
            section.appendChild(el('p', 'pantry-section-desc', tpl.produces.summary));
        }
        var artifacts = (tpl.produces && tpl.produces.artifacts) || [];
        if (artifacts.length > 0) {
            var ul = el('ul', 'pantry-artifact-list');
            for (var i = 0; i < artifacts.length; i++) {
                var a = artifacts[i] || {};
                var li = el('li', 'pantry-artifact-item');
                li.appendChild(el('span', 'pantry-artifact-kind', String(a.kind || '')));
                li.appendChild(el('strong', 'pantry-artifact-name', String(a.name || '')));
                li.appendChild(el('span', 'pantry-artifact-desc', String(a.description || '')));
                ul.appendChild(li);
            }
            section.appendChild(ul);
        }
        return section;
    }

    function renderRequires(tpl) {
        var section = el('section', 'pantry-section');
        section.appendChild(el('h2', null, 'Required inputs'));
        var authModes = (tpl.requires && tpl.requires.authModes) || [];
        section.appendChild(el('p', 'pantry-section-desc',
            'Supported auth modes: ' + authModes.join(', ')));
        var inputs = (tpl.requires && tpl.requires.inputs) || [];
        var ul = el('ul', 'pantry-input-list');
        for (var i = 0; i < inputs.length; i++) {
            var inp = inputs[i] || {};
            var li = el('li', 'pantry-input-item');
            li.appendChild(el('code', 'pantry-input-field', String(inp.field || '')));
            li.appendChild(el('span', 'pantry-input-desc', String(inp.description || '')));
            ul.appendChild(li);
        }
        section.appendChild(ul);
        return section;
    }

    // Flatten a nested object into dotted-path leaf entries. Used to
    // present template defaults as a labeled table instead of a raw
    // JSON block. Arrays are stringified as their JSON form so the
    // tabular surface stays one row per leaf.
    function flattenLeaves(obj, prefix, out) {
        if (obj === null || obj === undefined) { return out; }
        if (typeof obj !== 'object' || Array.isArray(obj)) {
            out.push({ path: prefix, value: obj });
            return out;
        }
        var keys = Object.keys(obj);
        if (keys.length === 0) {
            out.push({ path: prefix, value: {} });
            return out;
        }
        for (var i = 0; i < keys.length; i++) {
            var k = keys[i];
            var sub = obj[k];
            var nextPath = prefix ? (prefix + '.' + k) : k;
            flattenLeaves(sub, nextPath, out);
        }
        return out;
    }

    function formatLeafValue(v) {
        if (v === null || v === undefined) { return ''; }
        if (typeof v === 'string')  { return v; }
        if (typeof v === 'boolean') { return v ? 'true' : 'false'; }
        if (typeof v === 'number')  { return String(v); }
        return JSON.stringify(v);
    }

    function renderDefaults(tpl) {
        var section = el('section', 'pantry-section');
        section.appendChild(el('h2', null, 'Template defaults'));
        section.appendChild(el('p', 'pantry-section-desc',
            'These leaves come from the template. Materializing this template will stamp them into the new recipe.'));

        var defaults = tpl.recipeDefaults || {};
        var leaves = flattenLeaves(defaults, '', []);
        if (leaves.length === 0) {
            section.appendChild(el('p', 'pantry-section-desc', '(No template defaults are declared.)'));
        } else {
            var dl = el('dl', 'pantry-defaults-table');
            for (var i = 0; i < leaves.length; i++) {
                var leaf = leaves[i];
                dl.appendChild(el('dt', 'pantry-defaults-key', leaf.path));
                dl.appendChild(el('dd', 'pantry-defaults-value', formatLeafValue(leaf.value)));
            }
            section.appendChild(dl);
        }

        // Raw JSON disclosure for exact inspection. Closed by default;
        // the labeled table is the friendlier primary view.
        var details = document.createElement('details');
        var summary = document.createElement('summary');
        summary.textContent = 'Show raw JSON';
        details.appendChild(summary);
        var pre = el('pre', 'pantry-defaults');
        pre.textContent = JSON.stringify(defaults, null, 2);
        details.appendChild(pre);
        section.appendChild(details);

        return section;
    }

    // ----------------------------------------------------------------
    // Prerequisites + Limitations (NEW in V1.S05)
    //
    // Prerequisites unpacks the template's `requires` block into plain
    // English so an operator can see, at a glance, what they need to
    // have available before clicking Create recipe. Limitations is a
    // small static block stating what Cookbook does NOT do as a result
    // of materializing -- materialize creates a recipe file, nothing
    // else. These sections do not change the template schema, do not
    // change the recipe schema, and do not gate any backend behavior;
    // they are purely informational surfaces.
    // ----------------------------------------------------------------

    function describeAuthMode(mode) {
        if (mode === 'WebLogin')   { return 'WebLogin (interactive browser sign-in at bake time)'; }
        if (mode === 'DeviceCode') { return 'DeviceCode (device-code flow at bake time)'; }
        return String(mode || '');
    }

    function renderPrereqs(tpl) {
        var section = el('section', 'pantry-section');
        section.appendChild(el('h2', null, 'Prerequisites'));
        section.appendChild(el('p', 'pantry-section-desc',
            'Have these ready before you create a recipe from this template.'));

        var ul = el('ul', 'pantry-prereqs');

        // Auth modes -- supported sign-in approaches the template
        // declares. Translated to plain English so operators do not
        // have to interpret the enum.
        var authModes = (tpl.requires && tpl.requires.authModes) || [];
        var authLi = el('li', null);
        authLi.appendChild(el('strong', null, 'Supported sign-in modes: '));
        if (authModes.length === 0) {
            authLi.appendChild(document.createTextNode('(none declared)'));
        } else {
            var parts = [];
            for (var i = 0; i < authModes.length; i++) {
                parts.push(describeAuthMode(authModes[i]));
            }
            authLi.appendChild(document.createTextNode(parts.join('; ')));
        }
        ul.appendChild(authLi);

        // Per-instance inputs -- the leaves the operator must supply.
        var inputs = (tpl.requires && tpl.requires.inputs) || [];
        if (inputs.length > 0) {
            var inputsLi = el('li', null);
            inputsLi.appendChild(el('strong', null, 'Per-instance inputs you will supply: '));
            var names = [];
            for (var j = 0; j < inputs.length; j++) {
                names.push(String((inputs[j] && inputs[j].field) || ''));
            }
            inputsLi.appendChild(document.createTextNode(names.join(', ')));
            ul.appendChild(inputsLi);
        }

        // Local bundled framing -- reminds the operator this template
        // ships with the install, is not downloaded, and is never
        // updated remotely.
        var bundleLi = el('li', null);
        bundleLi.appendChild(el('strong', null, 'Source: '));
        bundleLi.appendChild(document.createTextNode(
            'Bundled with this install. Cookbook does not download templates, does not auto-update them, and does not fetch a remote catalog.'
        ));
        ul.appendChild(bundleLi);

        // Secrets statement -- the template itself never carries or
        // requires secret material. If a sign-in mode needs a token,
        // that token is obtained at cook time by the auth flow, not
        // here.
        var secretsLi = el('li', null);
        secretsLi.appendChild(el('strong', null, 'Secrets: '));
        secretsLi.appendChild(document.createTextNode(
            'Templates do not store credentials and do not ask for any client secret here. Sign-in happens at bake time through your chosen auth mode.'
        ));
        ul.appendChild(secretsLi);

        // PAX rollup framing -- only surfaced when the template's own
        // recipeDefaults declare rollup processing. We do not claim
        // the rollup will succeed, only that the recipe will be built
        // with rollup processing configured.
        var procRollup = tpl.recipeDefaults && tpl.recipeDefaults.processing && tpl.recipeDefaults.processing.rollup;
        if (procRollup) {
            var rollupLi = el('li', null);
            rollupLi.appendChild(el('strong', null, 'PAX processing: '));
            rollupLi.appendChild(document.createTextNode(
                'This template configures the recipe for PAX rollup processing (' + String(procRollup) + '). See the manual guidance below for any runtime steps the operator handles outside Cookbook.'
            ));
            ul.appendChild(rollupLi);
        }

        section.appendChild(ul);
        return section;
    }

    function renderLimitations() {
        var section = el('section', 'pantry-section');
        section.appendChild(el('h2', null, 'Limitations'));
        section.appendChild(el('p', 'pantry-section-desc',
            'What Create recipe does and does not do.'));

        var ul = el('ul', 'pantry-limitations');
        var items = [
            'Create recipe writes a local recipe file. It does not start a bake, does not run PAX, and does not run readiness.',
            'Template-created recipes still require the normal validation, readiness check, and bake action before any data is produced.',
            'Templates do not create Chef\u2019s Keys and do not store any credentials. You configure your sign-in once in Chef\u2019s Keys and select it at bake time.',
            'The output path you supply is treated as a chef-provided path. Cookbook validates it at readiness time and PAX validates it again at bake time; nothing on this page guarantees the path is reachable, writable, or correctly mapped.',
            'This page does not check tenant API permissions, Graph access, or any other server-side reachability. It cannot confirm a successful bake in advance.'
        ];
        for (var i = 0; i < items.length; i++) {
            ul.appendChild(el('li', null, items[i]));
        }
        section.appendChild(ul);
        return section;
    }

    function renderGuidance(tpl) {
        var notes = tpl.manualGuidance || [];
        if (notes.length === 0) { return null; }
        var section = el('section', 'pantry-section');
        section.appendChild(el('h2', null, 'Manual guidance'));
        for (var i = 0; i < notes.length; i++) {
            var n = notes[i] || {};
            var card = el('article', 'pantry-guidance-card');
            var h = el('header', 'pantry-guidance-header');
            h.appendChild(el('strong', 'pantry-guidance-heading', String(n.heading || '')));
            h.appendChild(el('span',   'pantry-guidance-audience', String(n.audience || '')));
            card.appendChild(h);
            var lines = n.body || [];
            for (var j = 0; j < lines.length; j++) {
                card.appendChild(el('p', 'pantry-guidance-line', String(lines[j])));
            }
            section.appendChild(card);
        }
        return section;
    }

    function renderProvenance(tpl) {
        var section = el('section', 'pantry-section');
        section.appendChild(el('h2', null, 'Provenance'));
        var dl = el('dl', 'pantry-card-meta');
        dl.appendChild(el('dt', null, 'Source'));
        dl.appendChild(el('dd', null, String((tpl.provenance && tpl.provenance.source) || '')));
        dl.appendChild(el('dt', null, 'Last reviewed'));
        dl.appendChild(el('dd', null, String((tpl.provenance && tpl.provenance.lastReviewed) || '')));
        dl.appendChild(el('dt', null, 'Template version'));
        dl.appendChild(el('dd', null, String(tpl.templateVersion || '')));
        section.appendChild(dl);
        return section;
    }

    // Field metadata for the materialize form. Bounded, declarative,
    // mirrors exactly the leaves the server's materialize-body schema
    // expects. Adding a field here without updating the server schema
    // would be rejected by the broker -- that is the intentional fence.
    //
    // `hint` is rendered under the input as a small helper line.
    // `instancePath` is the JSON Pointer the broker uses in 400
    // materialize_body_invalid responses; we map errors[*].instancePath
    // back to a field id for inline highlighting.
    var FORM_FIELDS = [
        {
            id: 'pantry-input-name',     label: 'Recipe name',
            placeholder: 'e.g. AI in One - April rollup', kind: 'text',
            instancePath: '/identity/name',
            hint: 'A short, recognizable label. This becomes the recipe display name in the Recipes list.'
        },
        {
            id: 'pantry-input-tenantid', label: 'Tenant ID',
            placeholder: '00000000-0000-0000-0000-000000000000', kind: 'text',
            instancePath: '/auth/tenantId',
            hint: 'GUID format: 8-4-4-4-12 hexadecimal characters. Use your Microsoft Entra ID tenant ID.'
        },
        {
            id: 'pantry-input-startdate',label: 'Query start date (UTC)',
            placeholder: 'YYYY-MM-DD', kind: 'text',
            instancePath: '/query/startDate',
            hint: 'UTC calendar date, format YYYY-MM-DD. Inclusive lower bound for the bake window.'
        },
        {
            id: 'pantry-input-enddate',  label: 'Query end date (UTC)',
            placeholder: 'YYYY-MM-DD', kind: 'text',
            instancePath: '/query/endDate',
            hint: 'UTC calendar date, format YYYY-MM-DD. Inclusive upper bound for the bake window.'
        },
        {
            id: 'pantry-input-outpath',  label: 'Fact destination path',
            placeholder: 'C:\\Reports\\fact.csv',  kind: 'text',
            instancePath: '/destinations/fact/path',
            hint: 'Where PAX should write the fact CSV. Cookbook validates this at readiness time; PAX validates it again at bake time.'
        }
    ];

    function renderMaterializeForm(tpl) {
        var section = el('section', 'pantry-section');
        section.appendChild(el('h2', null, 'Create a recipe from this template'));
        section.appendChild(el('p', 'pantry-section-desc',
            'Fill in the per-instance inputs below. Preview shows the recipe that would be built (without writing anything). Create recipe sends the inputs to the broker; on success the new recipe opens in the editor.'));
        var form = document.createElement('form');
        form.id = FORM_EL_ID;
        form.className = 'pantry-form';
        form.setAttribute('novalidate', 'novalidate');

        for (var i = 0; i < FORM_FIELDS.length; i++) {
            var f = FORM_FIELDS[i];
            var row = el('div', 'pantry-form-row');
            row.setAttribute('data-instance-path', f.instancePath || '');
            var lab = document.createElement('label');
            lab.setAttribute('for', f.id);
            lab.textContent = f.label;
            var inp = document.createElement('input');
            inp.type = 'text';
            inp.id = f.id;
            inp.name = f.id;
            inp.autocomplete = 'off';
            inp.spellcheck = false;
            if (f.placeholder) { inp.placeholder = f.placeholder; }
            row.appendChild(lab);
            row.appendChild(inp);
            if (f.hint) {
                row.appendChild(el('p', 'pantry-form-hint', f.hint));
            }
            form.appendChild(row);
        }

        var actions = el('div', 'pantry-form-actions');

        var previewBtn = document.createElement('button');
        previewBtn.type = 'button';
        previewBtn.id = PREVIEW_BTN_ID;
        previewBtn.className = 'btn-ghost';
        previewBtn.textContent = 'Preview recipe';
        actions.appendChild(previewBtn);

        var btn = document.createElement('button');
        btn.type = 'submit';
        btn.id = SUBMIT_BTN_ID;
        btn.className = 'btn-primary';
        btn.textContent = 'Create recipe';
        actions.appendChild(btn);

        var back = document.createElement('a');
        back.href = '#/pantry';
        back.className = 'btn-ghost';
        back.textContent = 'Back to Pantry';
        actions.appendChild(back);

        form.appendChild(actions);

        // Client-side preview output region. Populated by showPreview()
        // when the operator clicks Preview recipe. Read-only; cleared
        // on Clear preview or on form input. Never carries server data.
        var preview = document.createElement('div');
        preview.id = PREVIEW_EL_ID;
        preview.className = 'pantry-preview';
        preview.hidden = true;
        form.appendChild(preview);

        section.appendChild(form);
        return section;
    }

    // ----------------------------------------------------------------
    // Client-side preview (V1.S05)
    //
    // Mirrors the server-side ConvertTo-MaterializedRecipe builder
    // closely enough to make the preview useful, but stamps sentinel
    // values (`<server-assigned>`) for any field the server controls
    // authoritatively (recipeId, recipeSchemaVersion, paxAdapterVersion,
    // createdAt, updatedAt, createdBy.user). The preview is purely
    // client-side: no fetch, no broker call, no recipe file written,
    // no SQLite row, no cook started, no readiness run, no PAX.
    // ----------------------------------------------------------------

    var PREVIEW_SENTINEL = '<server-assigned>';

    function buildPreviewRecipe(tpl, body) {
        var defaultsRoot     = (tpl && tpl.recipeDefaults) || {};
        var defaultsIng      = defaultsRoot.ingredients     || {};
        var defaultsIngM365  = defaultsIng.m365Usage        || {};
        var defaultsIngEntra = defaultsIng.entraUserData    || {};
        var defaultsProc     = defaultsRoot.processing      || {};
        var defaultsAuth     = defaultsRoot.auth            || {};

        return {
            recipeId:            PREVIEW_SENTINEL,
            recipeSchemaVersion: PREVIEW_SENTINEL,
            paxAdapterVersion:   PREVIEW_SENTINEL,
            createdAt:           PREVIEW_SENTINEL,
            updatedAt:           PREVIEW_SENTINEL,
            createdBy: {
                user:         PREVIEW_SENTINEL,
                fromTemplate: {
                    templateId:      String(tpl.templateId || ''),
                    templateVersion: String(tpl.templateVersion || '')
                }
            },
            identity: {
                name: String(body.identity.name || '')
            },
            ingredients: {
                m365Usage: {
                    includeM365Usage: !!defaultsIngM365.includeM365Usage
                },
                entraUserData: {
                    includeUserInfo: !!defaultsIngEntra.includeUserInfo
                }
            },
            query: {
                startDate: String(body.query.startDate || ''),
                endDate:   String(body.query.endDate   || '')
            },
            processing: {
                rollup: String(defaultsProc.rollup || '')
            },
            destinations: {
                fact: {
                    path: String(body.destinations.fact.path || '')
                }
            },
            auth: {
                mode:     String(defaultsAuth.mode || ''),
                tenantId: String(body.auth.tenantId || '')
            }
        };
    }

    function showPreview(tpl) {
        var host = byId(PREVIEW_EL_ID);
        if (!host) { return; }
        while (host.firstChild) { host.removeChild(host.firstChild); }

        var body = readForm();
        var preview = buildPreviewRecipe(tpl, body);

        var header = el('div', 'pantry-preview-header');
        header.appendChild(el('strong', null, 'Preview only.'));
        header.appendChild(document.createTextNode(
            ' Nothing was created. No recipe file was written, no database row was inserted, and no bake was started. Fields shown as ' + PREVIEW_SENTINEL + ' are stamped by the broker when you click Create recipe.'
        ));
        host.appendChild(header);

        var pre = el('pre', 'pantry-preview-pre');
        pre.textContent = JSON.stringify(preview, null, 2);
        host.appendChild(pre);

        var actions = el('div', 'pantry-preview-actions');
        var clearBtn = document.createElement('button');
        clearBtn.type = 'button';
        clearBtn.className = 'btn-ghost';
        clearBtn.textContent = 'Clear preview';
        clearBtn.addEventListener('click', clearPreview);
        actions.appendChild(clearBtn);
        host.appendChild(actions);

        host.hidden = false;
    }

    function clearPreview() {
        var host = byId(PREVIEW_EL_ID);
        if (!host) { return; }
        while (host.firstChild) { host.removeChild(host.firstChild); }
        host.hidden = true;
    }

    function clearFieldErrors() {
        var form = byId(FORM_EL_ID);
        if (!form) { return; }
        var rows = form.querySelectorAll('.pantry-form-row.has-error');
        for (var i = 0; i < rows.length; i++) {
            rows[i].classList.remove('has-error');
        }
    }

    function applyFieldErrors(errors) {
        var form = byId(FORM_EL_ID);
        if (!form || !errors) { return; }
        // Build instancePath -> field-id lookup once per call.
        var pathToId = {};
        for (var k = 0; k < FORM_FIELDS.length; k++) {
            var f = FORM_FIELDS[k];
            if (f.instancePath) { pathToId[f.instancePath] = f.id; }
        }
        for (var i = 0; i < errors.length; i++) {
            var e = errors[i] || {};
            var path = String(e.instancePath || '');
            var hitId = pathToId[path] || null;
            // The broker may report `required` errors on a parent path;
            // try to map those to the missing child leaf when present.
            if (!hitId && e.params && e.params.missingProperty) {
                var childPath = (path ? path : '') + '/' + String(e.params.missingProperty);
                hitId = pathToId[childPath] || null;
            }
            if (hitId) {
                var inp = byId(hitId);
                if (inp && inp.parentNode) {
                    inp.parentNode.classList.add('has-error');
                }
            }
        }
    }

    function renderDetail(tpl) {
        setText('pantry-detail-crumb', String(tpl.displayName || tpl.templateId || ''));

        var host = byId(DETAIL_EL_ID);
        if (!host) { return; }
        while (host.firstChild) { host.removeChild(host.firstChild); }

        var head = el('header', 'page-header');
        var headText = el('div', 'page-header-text');
        headText.appendChild(el('h1', 'page-title', String(tpl.displayName || tpl.templateId || '')));
        headText.appendChild(el('p',  'page-lede',  String(tpl.shortDescription || '')));
        head.appendChild(headText);
        host.appendChild(head);

        var summary = el('dl', 'pantry-card-meta');
        summary.appendChild(el('dt', null, 'Template ID'));
        summary.appendChild(el('dd', null, String(tpl.templateId || '')));
        summary.appendChild(el('dt', null, 'Category'));
        summary.appendChild(el('dd', null, String(tpl.category || '')));
        summary.appendChild(el('dt', null, 'Template version'));
        summary.appendChild(el('dd', null, String(tpl.templateVersion || '')));
        summary.appendChild(el('dt', null, 'Min PAX'));
        summary.appendChild(el('dd', null, String(tpl.minPaxScriptVersion || '')));
        summary.appendChild(el('dt', null, 'Min Cookbook'));
        summary.appendChild(el('dd', null, String(tpl.minCookbookVersion || '')));
        host.appendChild(summary);

        host.appendChild(renderArtifacts(tpl));
        host.appendChild(renderPrereqs(tpl));
        host.appendChild(renderRequires(tpl));
        host.appendChild(renderLimitations());
        host.appendChild(renderDefaults(tpl));
        var g = renderGuidance(tpl);
        if (g) { host.appendChild(g); }
        host.appendChild(renderProvenance(tpl));
        host.appendChild(renderMaterializeForm(tpl));

        hideStatus();
        host.hidden = false;
    }

    // ----------------------------------------------------------------
    // Materialize submit
    // ----------------------------------------------------------------

    function readForm() {
        function v(id) {
            var el2 = byId(id);
            return el2 ? String(el2.value || '').trim() : '';
        }
        return {
            identity:     { name: v('pantry-input-name') },
            auth:         { tenantId: v('pantry-input-tenantid') },
            query:        { startDate: v('pantry-input-startdate'), endDate: v('pantry-input-enddate') },
            destinations: { fact: { path: v('pantry-input-outpath') } }
        };
    }

    function describeMaterializeError(resp) {
        if (resp.networkError) {
            // Phase AE: materialize is a state-mutating call. A
            // network failure does NOT mean no recipe was created
            // -- the POST may have reached the broker and committed
            // a new recipe on disk before the socket dropped. Tell
            // the truth and ask the operator to check the Recipes
            // list before retrying.
            return 'Failed to reach broker: ' + resp.networkError + '. ' +
                'A recipe may or may not have been created on the server. ' +
                'Open the Recipes page to verify before retrying.';
        }
        if (resp.status === 401) {
            return 'Session token rejected (HTTP 401). Reload the app from the launcher.';
        }
        if (resp.status === 403) {
            return 'Cross-origin request blocked (HTTP 403). Reload the app from the launcher.';
        }
        var bodyErr = resp.body && resp.body.error;
        if (resp.status === 404 && bodyErr === 'template_not_found') {
            return 'This template is no longer in the bundled catalog.';
        }
        if (resp.status === 412 && bodyErr === 'template_incompatible') {
            return 'This template requires a newer bundled PAX than the broker has loaded.';
        }
        if (resp.status === 400 && bodyErr === 'materialize_body_invalid') {
            var errs = (resp.body && resp.body.errors) || [];
            if (errs.length > 0) {
                var first = errs[0] || {};
                return 'Inputs are not valid: ' + String(first.instancePath || '') + ' [' + String(first.keyword || '') + '] ' + String(first.message || '');
            }
            return 'Inputs are not valid.';
        }
        if (resp.status === 400 && bodyErr === 'materialize_recipe_invalid') {
            return 'Materialized recipe failed schema validation. This is a broker-side mismatch; reload the launcher.';
        }
        return 'Materialize failed (HTTP ' + resp.status + (bodyErr ? ': ' + bodyErr : '') + ').';
    }

    function submitMaterialize(templateId) {
        if (!state) { return; }
        hideBanner();
        clearFieldErrors();

        var body = readForm();
        var btn = byId(SUBMIT_BTN_ID);
        if (btn) { btn.disabled = true; }

        var capturedEpoch = state.epoch;
        var signal = state.abortCtrl ? state.abortCtrl.signal : null;
        window.cookbookApi.post('/api/v1/templates/' + encodeURIComponent(templateId) + '/materialize', body, { signal: signal }).then(function (resp) {
            if (!state || state.epoch !== capturedEpoch) { return; }

            setText(LAST_FETCHED_ID, isoNowUtc());
            var btnNow = byId(SUBMIT_BTN_ID);
            if (btnNow) { btnNow.disabled = false; }

            if (resp.status === 201 && resp.body && resp.body.recipeId) {
                // Open the new recipe in the editor. replaceState so the
                // Pantry detail does not linger in history behind the
                // editor (cleaner Back behavior).
                var nextHash = '#/recipes/' + String(resp.body.recipeId);
                history.replaceState({}, '', window.location.pathname + nextHash);
                window.cookbookRouter.dispatch();
                return;
            }
            // V1.S05: on body-schema validation failure, also highlight
            // the offending form rows so the operator can see which
            // input the broker rejected, not just the banner text.
            var bodyErr = resp.body && resp.body.error;
            if (resp.status === 400 && bodyErr === 'materialize_body_invalid') {
                applyFieldErrors((resp.body && resp.body.errors) || []);
            }
            showBanner('error', describeMaterializeError(resp));
        });
    }

    // ----------------------------------------------------------------
    // Fetchers
    // ----------------------------------------------------------------

    function loadList() {
        if (!state) { return; }
        var capturedEpoch = state.epoch;
        var signal = state.abortCtrl ? state.abortCtrl.signal : null;
        var btn = byId(RELOAD_BTN_ID);
        if (btn) { btn.disabled = true; }
        setStatus('Loading bundled templates', '');
        var listEl = byId(LIST_EL_ID);
        if (listEl) { listEl.hidden = true; }

        window.cookbookApi.get('/api/v1/templates', { signal: signal }).then(function (resp) {
            if (!state || state.epoch !== capturedEpoch) { return; }
            setText(LAST_FETCHED_ID, isoNowUtc());
            var btnNow = byId(RELOAD_BTN_ID);
            if (btnNow) { btnNow.disabled = false; }

            if (resp.networkError) { setStatus('Failed to reach broker: ' + resp.networkError, 'error'); return; }
            if (resp.status === 401) { setStatus('Session token rejected (HTTP 401). Reload the app from the launcher.', 'error'); return; }
            if (!resp.ok) {
                var e = (resp.body && resp.body.error) ? resp.body.error : '';
                setStatus('Failed to load templates (HTTP ' + resp.status + (e ? ': ' + e : '') + ').', 'error');
                return;
            }
            var tpls = (resp.body && resp.body.templates) || [];
            renderList(tpls);
        });
    }

    function loadDetail(templateId) {
        if (!state) { return; }
        var capturedEpoch = state.epoch;
        var signal = state.abortCtrl ? state.abortCtrl.signal : null;
        var btn = byId(RELOAD_BTN_ID);
        if (btn) { btn.disabled = true; }
        setStatus('Loading template', '');
        var host = byId(DETAIL_EL_ID);
        if (host) { host.hidden = true; }

        window.cookbookApi.get('/api/v1/templates/' + encodeURIComponent(templateId), { signal: signal }).then(function (resp) {
            if (!state || state.epoch !== capturedEpoch) { return; }
            setText(LAST_FETCHED_ID, isoNowUtc());
            var btnNow = byId(RELOAD_BTN_ID);
            if (btnNow) { btnNow.disabled = false; }

            if (resp.networkError) { setStatus('Failed to reach broker: ' + resp.networkError, 'error'); return; }
            if (resp.status === 401) { setStatus('Session token rejected (HTTP 401). Reload the app from the launcher.', 'error'); return; }
            if (resp.status === 404) { setStatus('Template not found: ' + templateId, 'error'); return; }
            if (!resp.ok) {
                var e = (resp.body && resp.body.error) ? resp.body.error : '';
                setStatus('Failed to load template (HTTP ' + resp.status + (e ? ': ' + e : '') + ').', 'error');
                return;
            }
            var tpl = resp.body && resp.body.template;
            if (!tpl) { setStatus('Template response was empty.', 'error'); return; }

            renderDetail(tpl);

            // Wire submit on the materialize form and click on the
            // Preview button. Both are released on teardown (one
            // listener each, cleared in teardown()).
            var form = byId(FORM_EL_ID);
            if (form) {
                state.submitHandler = function (ev) {
                    if (ev) { ev.preventDefault(); }
                    submitMaterialize(templateId);
                };
                form.addEventListener('submit', state.submitHandler);
                state.formEl = form;
            }
            var prevBtn = byId(PREVIEW_BTN_ID);
            if (prevBtn) {
                state.previewHandler = function (ev) {
                    if (ev) { ev.preventDefault(); }
                    hideBanner();
                    showPreview(tpl);
                };
                prevBtn.addEventListener('click', state.previewHandler);
                state.previewBtnEl = prevBtn;
            }
        });
    }

    // ----------------------------------------------------------------
    // Lifecycle
    // ----------------------------------------------------------------

    function mount(container, params) {
        var isDetail = !!(params && params.templateId);
        container.innerHTML = isDetail ? PAGE_TEMPLATE_DETAIL : PAGE_TEMPLATE_LIST;

        state = {
            epoch:         nextEpoch++,
            mode:          isDetail ? 'detail' : 'list',
            templateId:    isDetail ? String(params.templateId) : null,
            reloadHandler: null,
            submitHandler: null,
            previewHandler:null,
            formEl:        null,
            previewBtnEl:  null,
            // Phase AE: per-mount AbortController; aborted on teardown.
            abortCtrl:     (typeof AbortController === 'function') ? new AbortController() : null
        };

        setText(BROKER_HOST_ID, window.location.host || '(unknown)');

        var tokenEl = byId(TOKEN_STATUS_ID);
        if (window.cookbookApi.hasToken()) {
            if (tokenEl) { tokenEl.textContent = 'Connected'; tokenEl.className = 'ok'; }
        } else {
            if (tokenEl) { tokenEl.textContent = 'No token'; tokenEl.className = 'miss'; }
        }

        var btn = byId(RELOAD_BTN_ID);
        if (btn) {
            state.reloadHandler = function () {
                if (!state) { return; }
                if (state.mode === 'detail') { loadDetail(state.templateId); }
                else { loadList(); }
            };
            btn.addEventListener('click', state.reloadHandler);
            btn.disabled = false;
            if (window.PaxTopbar) { window.PaxTopbar.pageReloadBound = true; }
        }

        if (!window.cookbookApi.hasToken()) {
            setText(LAST_FETCHED_ID, isoNowUtc());
            setStatus('No session token in this tab. Reload the app from the launcher.', 'error');
            return;
        }

        if (isDetail) { loadDetail(state.templateId); }
        else          { loadList(); }
    }

    function teardown() {
        if (!state) { return; }

        var btn = byId(RELOAD_BTN_ID);
        if (btn && state.reloadHandler) {
            btn.removeEventListener('click', state.reloadHandler);
            btn.disabled = false;
        }
        if (window.PaxTopbar) { window.PaxTopbar.pageReloadBound = false; }
        if (state.formEl && state.submitHandler) {
            state.formEl.removeEventListener('submit', state.submitHandler);
        }
        if (state.previewBtnEl && state.previewHandler) {
            state.previewBtnEl.removeEventListener('click', state.previewHandler);
        }
        // Phase AE: abort in-flight fetches before bumping epoch.
        if (state.abortCtrl) {
            try { state.abortCtrl.abort(); } catch (e) {}
        }
        // Bump the epoch so any in-flight response is discarded by its
        // .then handler when it eventually resolves.
        nextEpoch++;
        state = null;
    }

    window.cookbookPantryPage = {
        mount:    mount,
        teardown: teardown
    };
})();
