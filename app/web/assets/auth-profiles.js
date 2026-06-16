// PAX Cookbook -- Auth Profiles page (Phase AF).
//
// Page module for route '#/auth-profiles'. Owns the DOM under
// #page-root for the duration it is mounted; on teardown, drops
// listeners + in-flight requests + refs.
//
// Surfaces (broker routes):
//   GET    /api/v1/auth/profiles                       list
//   POST   /api/v1/auth/profiles                       create  [reAuth: profileMutation]
//   GET    /api/v1/auth/profiles/{id}                  read
//   PUT    /api/v1/auth/profiles/{id}                  update  [reAuth: profileMutation]
//   DELETE /api/v1/auth/profiles/{id}                  delete  [reAuth: profileMutation]
//   POST   /api/v1/auth/profiles/{id}/secret           bind    [reAuth: secretBind]
//   DELETE /api/v1/auth/profiles/{id}/secret           remove  [reAuth: secretRemove]
//   POST   /api/v1/auth/profiles/{id}/test             test    [reAuth: profileTest]
//
// Doctrine (verbatim, in force):
//   - Secrets are written to Windows Credential Manager via the
//     broker; the SPA NEVER stores them in sessionStorage,
//     localStorage, IndexedDB, or memory beyond the lifetime of a
//     single in-flight POST.
//   - After saving, the only allowed operations are replace, remove,
//     test (structural), or show-metadata. There is no view,
//     reveal, export, copy-back, or display-plaintext path.
//   - 401/423 responses are NOT handled by this module; lock-overlay
//     intercepts those events. This module surfaces 4xx/5xx
//     application errors in its own banners only.
//
// No polling. No client-side AJV. No optimistic UI. No retry. The
// server is authoritative; we render what it returns.

(function () {
    'use strict';

    var STATUS_EL_ID    = 'authprof-status';
    var BANNER_EL_ID    = 'authprof-banner';
    var TABLE_EL_ID     = 'authprof-table';
    var TBODY_EL_ID     = 'authprof-table-body';
    var EMPTY_EL_ID     = 'authprof-empty';
    var NEW_BTN_ID      = 'authprof-new';
    var MODAL_ID        = 'authprof-modal';
    var SECRET_MODAL_ID = 'authprof-secret-modal';

    // Shell-owned IDs (live in the topbar; the page reads/writes their
    // text but does NOT create or remove them).
    var LAST_FETCHED_ID = 'last-fetched';

    // Profile modes that the BROKER persists. WebLogin / DeviceCode
    // are recipe-level only -- they don't need a profile.
    var PROFILE_MODES = ['AppRegistrationSecret', 'AppRegistrationCertificate'];

    var PAGE_TEMPLATE = [
        '<section class="page authprof-section">',
            '<header class="page-header">',
                '<div class="page-header-text">',
                    '<h1 class="page-title">Chef\u2019s Keys<button type="button" class="help-hook" data-help-topic="auth-profiles.intro" aria-label="Help: About Chef\u2019s Keys" title="About Chef\u2019s Keys"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h1>',
                    '<p class="page-lede">A Chef\u2019s Key is a saved Entra app registration (tenant + client) plus a workload credential bound to this Windows account. Recipes reference a key by id; the credential bytes never leave Windows Credential Manager.</p>',
                '</div>',
                '<div class="page-actions">',
                    '<button id="' + NEW_BTN_ID + '" type="button" class="btn-primary">Add Chef\u2019s Key</button>',
                '</div>',
            '</header>',
            '<div id="' + STATUS_EL_ID + '" class="recipe-status">Loading Chef\u2019s Keys</div>',
            '<div id="' + BANNER_EL_ID + '" class="editor-banner" hidden></div>',
            '<aside id="' + EMPTY_EL_ID + '" class="empty-onboarding" hidden>',
                '<h2 class="empty-onboarding-title">No Chef\u2019s Keys yet.</h2>',
                '<p class="empty-onboarding-desc">A Chef\u2019s Key is required only for recipes that authenticate as an Entra workload (AppRegistration with a client secret or certificate). Recipes using WebLogin or DeviceCode do not need a key.</p>',
                '<p class="empty-onboarding-note">Click <strong>Add Chef\u2019s Key</strong> above to register the first one.</p>',
            '</aside>',
            '<table id="' + TABLE_EL_ID + '" class="recipe-table" hidden>',
                '<thead>',
                    '<tr>',
                        '<th scope="col">Name</th>',
                        '<th scope="col">Mode</th>',
                        '<th scope="col">Tenant</th>',
                        '<th scope="col">Client</th>',
                        '<th scope="col">Secret/Cert</th>',
                        '<th scope="col">Last verified</th>',
                        '<th scope="col">Actions</th>',
                    '</tr>',
                '</thead>',
                '<tbody id="' + TBODY_EL_ID + '"></tbody>',
            '</table>',
        '</section>'
    ].join('');

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

    function isoNowUtc() {
        return new Date().toISOString();
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

    function showTable(show) {
        var t = byId(TABLE_EL_ID);
        if (t) { t.hidden = !show; }
    }

    function showEmpty(show) {
        var e = byId(EMPTY_EL_ID);
        if (e) { e.hidden = !show; }
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
    }

    function formatErrors(errors) {
        if (!errors || !errors.length) { return ''; }
        var parts = [];
        for (var i = 0; i < errors.length; i++) {
            var e = errors[i] || {};
            var path = e.instancePath || e.path || '';
            var msg  = e.message || '(unspecified error)';
            parts.push(path ? (path + ': ' + msg) : msg);
        }
        return parts.join('; ');
    }

    function cell(text, klass) {
        var td = document.createElement('td');
        td.textContent = text;
        if (klass) { td.className = klass; }
        return td;
    }

    function truncId(id) {
        if (!id || id.length < 12) { return String(id || ''); }
        return id.substring(0, 8) + '\u2026';
    }

    // ----------------------------------------------------------------
    // Row rendering
    // ----------------------------------------------------------------

    function renderRows(profiles) {
        var tbody = byId(TBODY_EL_ID);
        if (!tbody) { return; }
        while (tbody.firstChild) { tbody.removeChild(tbody.firstChild); }
        if (!profiles || !profiles.length) {
            showTable(false);
            hideStatus();
            showEmpty(true);
            return;
        }
        for (var i = 0; i < profiles.length; i++) {
            tbody.appendChild(buildRow(profiles[i] || {}));
        }
        hideStatus();
        showEmpty(false);
        showTable(true);
    }

    function buildRow(p) {
        var tr = document.createElement('tr');
        tr.appendChild(cell(String(p.name || ''),            'col-name'));
        tr.appendChild(cell(String(p.mode || ''),            'col-status'));
        tr.appendChild(cell(truncId(p.tenantId),             'col-id'));
        tr.appendChild(cell(truncId(p.clientId),             'col-id'));

        var secretState = '';
        if (p.mode === 'AppRegistrationSecret') {
            secretState = p.credManTarget ? 'bound' : 'not bound';
        } else if (p.mode === 'AppRegistrationCertificate') {
            secretState = p.certThumbprint ? 'thumbprint set' : 'no thumbprint';
        }
        tr.appendChild(cell(secretState, 'col-status'));

        var verified = '';
        if (p.lastVerifiedAt) {
            verified = String(p.lastVerifiedResult || '') + ' @ ' + String(p.lastVerifiedAt);
        } else {
            verified = '\u2014';
        }
        tr.appendChild(cell(verified, 'col-time'));

        tr.appendChild(actionsCell(p));
        return tr;
    }

    function actionsCell(p) {
        var td = document.createElement('td');
        td.className = 'col-actions';

        var edit = document.createElement('button');
        edit.type = 'button';
        edit.className = 'row-action';
        edit.textContent = 'Edit';
        edit.addEventListener('click', function () { openEditModal(p); });
        td.appendChild(edit);

        td.appendChild(actionSep());

        if (p.mode === 'AppRegistrationSecret') {
            var bind = document.createElement('button');
            bind.type = 'button';
            bind.className = 'row-action';
            bind.textContent = p.credManTarget ? 'Replace secret' : 'Bind secret';
            bind.addEventListener('click', function () { openSecretModal(p); });
            td.appendChild(bind);
            td.appendChild(actionSep());

            if (p.credManTarget) {
                var remove = document.createElement('button');
                remove.type = 'button';
                remove.className = 'row-action';
                remove.textContent = 'Remove secret';
                remove.addEventListener('click', function () { confirmAndRemoveSecret(p); });
                td.appendChild(remove);
                td.appendChild(actionSep());
            }
        }

        var test = document.createElement('button');
        test.type = 'button';
        test.className = 'row-action';
        test.textContent = 'Test';
        test.addEventListener('click', function () { performTest(p, test); });
        td.appendChild(test);

        td.appendChild(actionSep());

        var del = document.createElement('button');
        del.type = 'button';
        del.className = 'row-action row-action-danger';
        del.textContent = 'Remove';
        del.addEventListener('click', function () { confirmAndDelete(p); });
        td.appendChild(del);

        return td;
    }

    function actionSep() {
        var sep = document.createElement('span');
        sep.className = 'row-action-sep';
        sep.setAttribute('aria-hidden', 'true');
        sep.textContent = ' \u00B7 ';
        return sep;
    }

    // ----------------------------------------------------------------
    // List load
    // ----------------------------------------------------------------

    function loadList() {
        if (!state) { return; }
        var capturedEpoch = state.epoch;
        var signal = state.abortCtrl ? state.abortCtrl.signal : null;
        setStatus('Loading Chef\u2019s Keys', '');
        showTable(false);
        showEmpty(false);
        hideBanner();
        window.cookbookApi.get('/api/v1/auth/profiles', { signal: signal }).then(function (resp) {
            if (!state || state.epoch !== capturedEpoch) { return; }
            setText(LAST_FETCHED_ID, isoNowUtc());
            if (resp.networkError) {
                setStatus('Could not reach broker: ' + resp.networkError, 'error');
                return;
            }
            if (resp.status === 423 || resp.status === 401) {
                // lock-overlay handles UX; the page still surfaces this
                // as a real error so the operator sees a clear red bar
                // matching every other list page.
                setStatus('Verification required. Complete the prompt above and reload.', 'error');
                return;
            }
            if (!resp.ok || !resp.body) {
                setStatus('Failed to load Chef\u2019s Keys (status ' + resp.status + ').', 'error');
                return;
            }
            renderRows(resp.body.profiles || []);
        });
    }

    // ----------------------------------------------------------------
    // Create / Edit modal
    // ----------------------------------------------------------------

    function openEditModal(p) {
        openProfileModal({ mode: 'edit', profile: p });
    }

    function openNewModal() {
        openProfileModal({ mode: 'new' });
    }

    function openProfileModal(opts) {
        closeProfileModal();
        var isNew = (opts.mode === 'new');
        var p = opts.profile || {};

        var overlay = document.createElement('div');
        overlay.id = MODAL_ID;
        overlay.className = 'modal-overlay';
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');

        var panel = document.createElement('div');
        panel.className = 'modal-panel';

        var title = document.createElement('h2');
        title.className = 'modal-title';
        title.textContent = isNew ? 'Add a Chef\u2019s Key' : ('Edit ' + (p.name || ''));
        panel.appendChild(title);

        var form = document.createElement('form');
        form.className = 'modal-form';
        form.addEventListener('submit', function (ev) { ev.preventDefault(); submitProfile(isNew, p, form, panel); });

        form.appendChild(field('Name', 'name', 'text', p.name || '', { required: true, autoFocus: true }));

        // Mode is immutable on edit -- the broker rejects mode changes.
        if (isNew) {
            form.appendChild(modeField('mode', PROFILE_MODES[0]));
        } else {
            form.appendChild(fieldReadOnly('Mode (immutable)', String(p.mode || '')));
        }

        form.appendChild(field('Tenant ID (UUID)', 'tenantId', 'text', p.tenantId || '', { required: true, pattern: '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' }));
        form.appendChild(field('Client ID (UUID)', 'clientId', 'text', p.clientId || '', { required: true, pattern: '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' }));

        // Cert fields shown only for cert mode. On create we toggle
        // them by mode picker; on edit we toggle by current mode.
        var certWrap = document.createElement('div');
        certWrap.className = 'modal-cert-fields';
        certWrap.appendChild(field('Cert thumbprint (40 hex)', 'certThumbprint', 'text', p.certThumbprint || '', { pattern: '^[0-9A-Fa-f]{40}$' }));
        certWrap.appendChild(field('Cert store', 'certStore', 'text', p.certStore || 'LocalMachine\\My', {}));
        form.appendChild(certWrap);

        form.appendChild(field('Description', 'description', 'text', p.description || '', {}));

        var actions = document.createElement('div');
        actions.className = 'modal-actions';

        var submit = document.createElement('button');
        submit.type = 'submit';
        submit.className = 'btn-primary';
        submit.textContent = isNew ? 'Add key' : 'Save changes';
        actions.appendChild(submit);

        var cancel = document.createElement('button');
        cancel.type = 'button';
        cancel.className = 'btn-ghost';
        cancel.textContent = 'Cancel';
        cancel.addEventListener('click', closeProfileModal);
        actions.appendChild(cancel);

        form.appendChild(actions);

        var status = document.createElement('p');
        status.className = 'modal-status';
        status.setAttribute('aria-live', 'polite');
        panel.appendChild(form);
        panel.appendChild(status);

        overlay.appendChild(panel);
        document.body.appendChild(overlay);

        // Toggle cert fields based on initial / current mode.
        function syncCertVisibility(modeValue) {
            certWrap.style.display = (modeValue === 'AppRegistrationCertificate') ? '' : 'none';
        }
        var modeEl = form.querySelector('[name=mode]');
        syncCertVisibility(modeEl ? modeEl.value : (p.mode || PROFILE_MODES[0]));
        if (modeEl) {
            modeEl.addEventListener('change', function () { syncCertVisibility(modeEl.value); });
        }

        // Focus the first input for keyboard / SR ergonomics.
        var first = form.querySelector('input, select');
        if (first) {
            try { first.focus(); } catch (e) {}
        }
    }

    function closeProfileModal() {
        var el = byId(MODAL_ID);
        if (el && el.parentNode) { el.parentNode.removeChild(el); }
    }

    function field(labelText, name, type, value, opts) {
        opts = opts || {};
        var w = document.createElement('label');
        w.className = 'modal-field';
        var span = document.createElement('span');
        span.className = 'modal-field-label';
        span.textContent = labelText;
        w.appendChild(span);
        var inp = document.createElement('input');
        inp.type = type;
        inp.name = name;
        inp.value = value;
        if (opts.required) { inp.required = true; }
        if (opts.pattern)  { inp.pattern = opts.pattern; }
        w.appendChild(inp);
        return w;
    }

    function fieldReadOnly(labelText, value) {
        var w = document.createElement('div');
        w.className = 'modal-field';
        var span = document.createElement('span');
        span.className = 'modal-field-label';
        span.textContent = labelText;
        w.appendChild(span);
        var v = document.createElement('code');
        v.className = 'modal-readonly-value';
        v.textContent = value;
        w.appendChild(v);
        return w;
    }

    function modeField(name, defaultValue) {
        var w = document.createElement('label');
        w.className = 'modal-field';
        var span = document.createElement('span');
        span.className = 'modal-field-label';
        span.textContent = 'Mode';
        w.appendChild(span);
        var sel = document.createElement('select');
        sel.name = name;
        for (var i = 0; i < PROFILE_MODES.length; i++) {
            var opt = document.createElement('option');
            opt.value = PROFILE_MODES[i];
            opt.textContent = PROFILE_MODES[i];
            if (PROFILE_MODES[i] === defaultValue) { opt.selected = true; }
            sel.appendChild(opt);
        }
        w.appendChild(sel);
        return w;
    }

    function readForm(form) {
        var out = {};
        var els = form.querySelectorAll('input, select');
        for (var i = 0; i < els.length; i++) {
            var el = els[i];
            if (!el.name) { continue; }
            if (typeof el.value === 'string') {
                var v = el.value.trim();
                if (v.length > 0) { out[el.name] = v; }
            }
        }
        return out;
    }

    function setModalStatus(panel, text, kind) {
        var st = panel.querySelector('.modal-status');
        if (!st) { return; }
        st.textContent = text || '';
        st.className = 'modal-status' + (kind ? ' modal-status-' + kind : '');
    }

    function submitProfile(isNew, existing, form, panel) {
        var body = readForm(form);
        // For edit, mode is immutable and not in the form; the broker
        // ignores it anyway. Drop it so we don't send a stale value.
        if (!isNew && body.mode) { delete body.mode; }
        setModalStatus(panel, 'Saving\u2026', '');
        var p = isNew
            ? window.cookbookApi.post('/api/v1/auth/profiles', body)
            : window.cookbookApi.put('/api/v1/auth/profiles/' + existing.authProfileId, body);
        p.then(function (resp) {
            if (resp.networkError) {
                setModalStatus(panel, 'Could not reach broker: ' + resp.networkError, 'error');
                return;
            }
            if (resp.status === 423 || resp.status === 401) {
                // lock-overlay shows UX; close this modal and let the
                // operator retry after verification.
                closeProfileModal();
                return;
            }
            if (resp.status === 201 || resp.status === 200) {
                closeProfileModal();
                showBanner('ok', isNew ? 'Chef\u2019s Key added.' : 'Chef\u2019s Key updated.');
                loadList();
                return;
            }
            if (resp.status === 409 && resp.body && resp.body.error === 'name_conflict') {
                setModalStatus(panel, 'A Chef\u2019s Key with that name already exists.', 'error');
                return;
            }
            if ((resp.status === 422 || resp.status === 400) && resp.body && resp.body.errors) {
                setModalStatus(panel, 'Validation failed: ' + formatErrors(resp.body.errors), 'error');
                return;
            }
            setModalStatus(panel, 'Server error (status ' + resp.status + ').', 'error');
        });
    }

    // ----------------------------------------------------------------
    // Bind / replace secret modal
    // ----------------------------------------------------------------

    function openSecretModal(p) {
        closeSecretModal();
        var overlay = document.createElement('div');
        overlay.id = SECRET_MODAL_ID;
        overlay.className = 'modal-overlay';
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');

        var panel = document.createElement('div');
        panel.className = 'modal-panel';

        var title = document.createElement('h2');
        title.className = 'modal-title';
        title.textContent = (p.credManTarget ? 'Replace secret for ' : 'Bind secret for ') + (p.name || '');
        panel.appendChild(title);

        var lede = document.createElement('p');
        lede.className = 'modal-lede';
        lede.textContent =
            'The secret is written to Windows Credential Manager under this Windows account. ' +
            'It is never written to disk by Cookbook and never appears in recipes, bake logs, or argv. ' +
            'After saving, the secret is write-only \u2014 you cannot reveal, copy, or export it from here.';
        panel.appendChild(lede);

        var form = document.createElement('form');
        form.className = 'modal-form';
        form.addEventListener('submit', function (ev) { ev.preventDefault(); submitSecret(p, form, panel); });

        form.appendChild(field('Client secret', 'secret', 'password', '', { required: true, autoFocus: true }));

        var actions = document.createElement('div');
        actions.className = 'modal-actions';
        var submit = document.createElement('button');
        submit.type = 'submit';
        submit.className = 'btn-primary';
        submit.textContent = 'Save to Credential Manager';
        actions.appendChild(submit);
        var cancel = document.createElement('button');
        cancel.type = 'button';
        cancel.className = 'btn-ghost';
        cancel.textContent = 'Cancel';
        cancel.addEventListener('click', closeSecretModal);
        actions.appendChild(cancel);
        form.appendChild(actions);

        var status = document.createElement('p');
        status.className = 'modal-status';
        status.setAttribute('aria-live', 'polite');
        panel.appendChild(form);
        panel.appendChild(status);
        overlay.appendChild(panel);
        document.body.appendChild(overlay);

        var first = form.querySelector('input[name=secret]');
        if (first) { try { first.focus(); } catch (e) {} }
    }

    function closeSecretModal() {
        var el = byId(SECRET_MODAL_ID);
        if (el && el.parentNode) { el.parentNode.removeChild(el); }
    }

    function submitSecret(p, form, panel) {
        var inp = form.querySelector('input[name=secret]');
        var secret = (inp && typeof inp.value === 'string') ? inp.value : '';
        if (!secret) {
            setModalStatus(panel, 'Secret is required.', 'error');
            return;
        }
        setModalStatus(panel, 'Saving to Credential Manager\u2026', '');
        // Best-effort: clear the input on submit so the plaintext does
        // not linger in the DOM. The HTTP body is still constructed
        // from `secret` but the input.value (and any browser autofill
        // memory) is wiped immediately.
        if (inp) { inp.value = ''; }
        window.cookbookApi.post('/api/v1/auth/profiles/' + p.authProfileId + '/secret', { secret: secret }).then(function (resp) {
            // Drop our local reference. Browser keeps the string in
            // memory until GC; doctrine acknowledges this in
            // OPERATOR_GUIDE.
            secret = null;
            if (resp.networkError) {
                setModalStatus(panel, 'Could not reach broker: ' + resp.networkError, 'error');
                return;
            }
            if (resp.status === 423 || resp.status === 401) {
                closeSecretModal();
                return;
            }
            if (resp.status === 200 || resp.status === 201) {
                closeSecretModal();
                showBanner('ok', 'Secret saved to Credential Manager.');
                loadList();
                return;
            }
            if (resp.status === 422 && resp.body && resp.body.error === 'mode_mismatch') {
                setModalStatus(panel, 'Secret binding is only valid for AppRegistrationSecret mode.', 'error');
                return;
            }
            if (resp.body && resp.body.errors) {
                setModalStatus(panel, 'Validation failed: ' + formatErrors(resp.body.errors), 'error');
                return;
            }
            setModalStatus(panel, 'Server error (status ' + resp.status + ').', 'error');
        });
    }

    // ----------------------------------------------------------------
    // Remove secret
    // ----------------------------------------------------------------

    function confirmAndRemoveSecret(p) {
        if (!window.confirm('Remove the bound client secret for "' + (p.name || '') + '"? The Credential Manager entry will be deleted; the Chef\u2019s Key remains.')) { return; }
        window.cookbookApi.del('/api/v1/auth/profiles/' + p.authProfileId + '/secret').then(function (resp) {
            if (resp.networkError) {
                showBanner('error', 'Could not reach broker: ' + resp.networkError);
                return;
            }
            if (resp.status === 423 || resp.status === 401) { return; }
            if (resp.status === 200) {
                showBanner('ok', 'Secret removed from Credential Manager.');
                loadList();
                return;
            }
            showBanner('error', 'Failed to remove secret (status ' + resp.status + ').');
        });
    }

    // ----------------------------------------------------------------
    // Test (structural)
    // ----------------------------------------------------------------

    function performTest(p, btn) {
        if (btn) { btn.disabled = true; }
        window.cookbookApi.post('/api/v1/auth/profiles/' + p.authProfileId + '/test', {}).then(function (resp) {
            if (btn && document.body.contains(btn)) { btn.disabled = false; }
            if (resp.networkError) {
                showBanner('error', 'Could not reach broker: ' + resp.networkError);
                return;
            }
            if (resp.status === 423 || resp.status === 401) { return; }
            if (!resp.ok || !resp.body) {
                showBanner('error', 'Test failed (status ' + resp.status + ').');
                return;
            }
            var b = resp.body;
            var kind = b.validationKind ? (' [' + b.validationKind + ']') : '';
            var summary = (b.ok ? 'Test passed' : 'Test failed') + kind;
            if (b.detail) { summary += ': ' + b.detail; }
            showBanner(b.ok ? 'ok' : 'error', summary);
            // Refresh row so lastVerifiedAt updates.
            loadList();
        });
    }

    // ----------------------------------------------------------------
    // Delete profile
    // ----------------------------------------------------------------

    function confirmAndDelete(p) {
        var msg = 'Remove Chef\u2019s Key "' + (p.name || '') + '"?';
        if (p.credManTarget) {
            msg += ' The bound credential will also be removed from Windows Credential Manager.';
        }
        msg += ' Recipes that reference this key will fail until you re-create or re-bind one.';
        if (!window.confirm(msg)) { return; }
        window.cookbookApi.del('/api/v1/auth/profiles/' + p.authProfileId).then(function (resp) {
            if (resp.networkError) {
                showBanner('error', 'Could not reach broker: ' + resp.networkError);
                return;
            }
            if (resp.status === 423 || resp.status === 401) { return; }
            if (resp.status === 200) {
                showBanner('ok', 'Chef\u2019s Key removed.');
                loadList();
                return;
            }
            showBanner('error', 'Failed to remove Chef\u2019s Key (status ' + resp.status + ').');
        });
    }

    // ----------------------------------------------------------------
    // Mount / teardown
    // ----------------------------------------------------------------

    function mount(container, params) {
        teardown();
        var epoch = nextEpoch++;
        var ctrl = (typeof AbortController === 'function') ? new AbortController() : null;
        state = {
            epoch:     epoch,
            container: container,
            abortCtrl: ctrl
        };

        container.innerHTML = PAGE_TEMPLATE;
        var newBtn = byId(NEW_BTN_ID);
        if (newBtn) { newBtn.addEventListener('click', openNewModal); }

        // Baseline tick so 'Last fetched' has a real timestamp even
        // before the first profile list request resolves. The list
        // request below re-stamps it as soon as the broker responds.
        setText(LAST_FETCHED_ID, isoNowUtc());
        loadList();
    }

    function teardown() {
        if (!state) { return; }
        if (state.abortCtrl) {
            try { state.abortCtrl.abort(); } catch (e) {}
        }
        closeProfileModal();
        closeSecretModal();
        if (state.container) {
            state.container.innerHTML = '';
        }
        state = null;
        nextEpoch++;
    }

    window.cookbookAuthProfilesPage = {
        mount:    mount,
        teardown: teardown
    };
})();
