// PAX Cookbook -- Settings page (Phase L: environment experience hardening).
//
// Page module for route '#/settings'. Owns the DOM under #page-root for
// the duration it is mounted; on teardown, drops its listeners, signals
// in-flight responses to discard themselves, and releases its refs so
// the router can clear the container.
//
// Renders read-only environment metadata returned by the single
// endpoint GET /api/v1/runtime/version.  The body is split visually
// into seven disciplined cards:
//
//   1. Runtime identity        cookbookVersion + releaseChannel + host
//   2. Bundled PAX             version + sha256 + relativePath
//   3. Integrity               script integrity + manifest alignment
//   4. Environment paths       authoritative absolute paths (display-only)
//   5. Runtime assumptions     pid + port + startedAt + transport
//   6. Update readiness        version vs manifest.latestCookbook plus the
//                              three explicit operator actions of the
//                              update flow: Check for Updates, Download
//                              Cookbook Update, Install and Restart
//                              Cookbook. Each is a single click that
//                              issues a single HTTP request. Nothing is
//                              polled, scheduled, or retried.
//   7. Local diagnostics       a synthesized checklist derived from the
//                              same response body
//
// Single fetch on mount; re-fetched only when the user clicks the shell
// Reload button. The update buttons fire ONE outbound request per click
// and do nothing on their own \u2014 no setInterval, no setTimeout against
// any /api/v1/updates/* route, no auto-fetch on init or focus.
//
// Intentional non-features (do NOT add):
//   - editable runtime paths / engine-path picker / workspace switcher
//   - automatic update check, download, or install
//   - manifest URL editor / release-channel selector
//   - polling, auto-refresh, optimistic UI, reactive state
//   - any second metadata authority
//
// Visibility plus three explicit clicks.
(function () {
    'use strict';

    // Page-owned IDs (built inside #page-root on mount, gone on teardown).
    var STATUS_EL_ID         = 'settings-status';
    var BODY_EL_ID           = 'settings-body';
    var IDENTITY_EL_ID       = 'settings-identity';
    var PAX_EL_ID            = 'settings-pax';
    var INTEGRITY_EL_ID      = 'settings-integrity';
    var PATHS_EL_ID          = 'settings-paths';
    var ASSUMPTIONS_EL_ID    = 'settings-assumptions';
    var UPDATES_EL_ID        = 'settings-updates';
    var DIAGNOSTICS_EL_ID    = 'settings-diagnostics';
    var UPDATE_CHECK_BTN_ID  = 'settings-update-check';
    var UPDATE_DOWNLOAD_BTN_ID = 'settings-update-download';
    var UPDATE_APPLY_BTN_ID  = 'settings-update-apply';
    var UPDATE_RESULT_EL_ID  = 'settings-update-result';
    var UPDATE_APPLY_RESULT_EL_ID = 'settings-update-apply-result';
    var UPDATE_RELEASE_NOTES_EL_ID = 'settings-update-release-notes';
    var UPDATE_STAGED_EL_ID  = 'settings-update-staged';

    // Notification preferences card (editable). Four boolean controls plus
    // a save button and an accessible status region.
    var NOTIFY_FORM_ID        = 'settings-notify-form';
    var NOTIFY_COMPLETED_ID   = 'settings-notify-completed';
    var NOTIFY_ERRORED_ID     = 'settings-notify-errored';
    var NOTIFY_INTERRUPTED_ID = 'settings-notify-interrupted';
    var NOTIFY_OS_TOAST_ID    = 'settings-notify-os-toast';
    var NOTIFY_SAVE_BTN_ID    = 'settings-notify-save';
    var NOTIFY_STATUS_EL_ID   = 'settings-notify-status';

    // Opt-in outbound webhook controls (disabled by default). The enable
    // flag is a checkbox; the endpoint is a plain text input (it is a URL,
    // not a secret); the format is a fixed two-option select.
    var NOTIFY_WEBHOOK_ENABLED_ID = 'settings-notify-webhook-enabled';
    var NOTIFY_WEBHOOK_URL_ID     = 'settings-notify-webhook-url';
    var NOTIFY_WEBHOOK_FORMAT_ID  = 'settings-notify-webhook-format';
    var NOTIFY_WEBHOOK_ENABLED_KEY = 'notify.webhook.enabled';
    var NOTIFY_WEBHOOK_URL_KEY     = 'notify.webhook.url';
    var NOTIFY_WEBHOOK_FORMAT_KEY  = 'notify.webhook.format';

    // Maps each preference checkbox to the broker settings key it controls.
    // This is the single source of truth used to read GET responses into
    // the checkboxes and to build the PUT payload from them.
    var NOTIFY_KEY_MAP = [
        { id: NOTIFY_COMPLETED_ID,   key: 'notify.completed.enabled' },
        { id: NOTIFY_ERRORED_ID,     key: 'notify.errored.enabled' },
        { id: NOTIFY_INTERRUPTED_ID, key: 'notify.interrupted.enabled' },
        { id: NOTIFY_OS_TOAST_ID,    key: 'notify.os_toast.enabled' }
    ];

    // Shell-owned IDs (live in the topbar; the page reads/writes their
    // text/className but does NOT create or remove them).
    var BROKER_HOST_ID  = 'broker-host';
    var TOKEN_STATUS_ID = 'token-status';
    var LAST_FETCHED_ID = 'last-fetched';
    var RELOAD_BTN_ID   = 'reload-button';

    var PAGE_TEMPLATE = [
        '<section class="page recipe-section">',
            '<header class="page-header">',
                '<div class="page-header-text">',
                    '<h1 class="page-title">Settings<button type="button" class="help-hook" data-help-topic="settings.intro" aria-label="Help: About Settings" title="About Settings"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h1>',
                    '<p class="page-lede">Environment, integrity, paths, runtime assumptions, update readiness, and local diagnostics for this Cookbook install. All values are read locally from the broker; nothing on this page issues outbound network traffic except an explicit Check for Cookbook Update.</p>',
                '</div>',
            '</header>',
            '<div id="settings-status" class="recipe-status">Loading environment metadata</div>',
            '<div id="settings-body" class="settings-body" hidden>',
                '<article class="cook-card">',
                    '<h2 class="cook-card-title">Runtime identity<button type="button" class="help-hook" data-help-topic="settings.runtime-identity" aria-label="Help: Runtime identity" title="About Runtime identity"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h2>',
                    '<dl id="settings-identity" class="cook-meta"></dl>',
                '</article>',
                '<article class="cook-card">',
                    '<h2 class="cook-card-title">Bundled PAX<button type="button" class="help-hook" data-help-topic="settings.bundled-pax" aria-label="Help: Bundled PAX" title="About the bundled PAX script"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h2>',
                    '<dl id="settings-pax" class="cook-meta"></dl>',
                '</article>',
                '<article class="cook-card">',
                    '<h2 class="cook-card-title">Integrity<button type="button" class="help-hook" data-help-topic="settings.integrity" aria-label="Help: Integrity" title="About integrity"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h2>',
                    '<dl id="settings-integrity" class="cook-meta"></dl>',
                '</article>',
                '<article class="cook-card">',
                    '<h2 class="cook-card-title">Environment paths<button type="button" class="help-hook" data-help-topic="settings.paths" aria-label="Help: Environment paths" title="About environment paths"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h2>',
                    '<dl id="settings-paths" class="cook-meta settings-paths-grid"></dl>',
                '</article>',
                '<article class="cook-card">',
                    '<h2 class="cook-card-title">Runtime assumptions<button type="button" class="help-hook" data-help-topic="settings.assumptions" aria-label="Help: Runtime assumptions" title="About runtime assumptions"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h2>',
                    '<dl id="settings-assumptions" class="cook-meta"></dl>',
                '</article>',
                '<article class="cook-card">',
                    '<h2 class="cook-card-title">Notifications</h2>',
                    '<p class="settings-notify-lede">Choose which finished-bake notifications you want to receive. Every bake is always recorded to the Cookbook\u0027s in-app history regardless of these choices; these settings control only the pop-up notifications you see.</p>',
                    '<form id="settings-notify-form" class="settings-notify-form">',
                        '<div class="settings-notify-row">',
                            '<input type="checkbox" id="settings-notify-completed" class="settings-notify-check">',
                            '<label for="settings-notify-completed">Notify when a bake completes</label>',
                        '</div>',
                        '<div class="settings-notify-row">',
                            '<input type="checkbox" id="settings-notify-errored" class="settings-notify-check">',
                            '<label for="settings-notify-errored">Notify when a bake fails</label>',
                        '</div>',
                        '<div class="settings-notify-row">',
                            '<input type="checkbox" id="settings-notify-interrupted" class="settings-notify-check">',
                            '<label for="settings-notify-interrupted">Notify when a bake is stopped</label>',
                        '</div>',
                        '<div class="settings-notify-row">',
                            '<input type="checkbox" id="settings-notify-os-toast" class="settings-notify-check">',
                            '<label for="settings-notify-os-toast">Show Windows notifications (Action Center toasts)</label>',
                        '</div>',
                        '<div class="settings-notify-webhook">',
                            '<h3 class="settings-notify-webhook-title">Outbound webhook (advanced, optional)</h3>',
                            '<p class="settings-notify-webhook-lede">Off by default. When enabled, each finished bake also sends a small privacy-safe summary to an HTTPS endpoint you control (for example a Microsoft Teams or Power Automate incoming webhook). Only the bake summary is sent \u2014 never file paths, URLs, account names, tokens, or secrets. Delivery is best-effort and never delays or blocks a bake.</p>',
                            '<div class="settings-notify-row">',
                                '<input type="checkbox" id="settings-notify-webhook-enabled" class="settings-notify-check">',
                                '<label for="settings-notify-webhook-enabled">Send finished-bake notifications to a webhook</label>',
                            '</div>',
                            '<div class="settings-notify-field">',
                                '<label for="settings-notify-webhook-url">Webhook URL (https only)</label>',
                                '<input type="url" id="settings-notify-webhook-url" class="settings-notify-input" inputmode="url" autocomplete="off" spellcheck="false" placeholder="https://example.com/webhook">',
                            '</div>',
                            '<div class="settings-notify-field">',
                                '<label for="settings-notify-webhook-format">Payload format</label>',
                                '<select id="settings-notify-webhook-format" class="settings-notify-select">',
                                    '<option value="generic">Generic JSON</option>',
                                    '<option value="teams">Microsoft Teams (MessageCard)</option>',
                                '</select>',
                            '</div>',
                        '</div>',
                        '<div class="settings-notify-actions">',
                            '<button type="button" id="settings-notify-save" class="btn-ghost settings-notify-save">Save notification settings</button>',
                        '</div>',
                    '</form>',
                    '<div id="settings-notify-status" class="settings-notify-status" role="status" aria-live="polite" hidden></div>',
                '</article>',
                '<article class="cook-card">',
                    '<h2 class="cook-card-title">Update readiness<button type="button" class="help-hook" data-help-topic="settings.update-readiness" aria-label="Help: Update readiness" title="About update readiness"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h2>',
                    '<dl id="settings-updates" class="cook-meta"></dl>',
                    '<div class="settings-update-actions">',
                        '<button type="button" id="settings-update-check" class="btn-ghost settings-update-check">Check for Cookbook Update</button>',
                        '<button type="button" id="settings-update-download" class="btn-ghost settings-update-download" hidden disabled aria-disabled="true">Download Cookbook Update</button>',
                        '<button type="button" id="settings-update-apply" class="btn-ghost settings-update-apply" hidden disabled aria-disabled="true">Install and Restart Cookbook</button>',
                    '</div>',
                    '<p class="settings-update-note">The Cookbook never auto-fetches or auto-installs. Each of the three buttons above issues a single HTTP request when you click it: Check fetches the configured manifest once, Download stores the verified package under your workspace\u0027s Updates folder, and Install and Restart applies the staged update. No polling, no background refresh, no silent install.</p>',
                    '<div id="settings-update-release-notes" class="settings-update-release-notes" hidden></div>',
                    '<div id="settings-update-result" class="settings-update-result" hidden></div>',
                    '<div id="settings-update-apply-result" class="settings-update-apply-result" hidden></div>',
                    '<div id="settings-update-staged" class="settings-update-staged"></div>',
                '</article>',
                '<article class="cook-card">',
                    '<h2 class="cook-card-title">Local diagnostics<button type="button" class="help-hook" data-help-topic="settings.diagnostics" aria-label="Help: Local diagnostics" title="About local diagnostics"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h2>',
                    '<ul id="settings-diagnostics" class="settings-diagnostics"></ul>',
                    '<p class="settings-diagnostics-help">For broker-unreachable, token rejection, and other recovery steps, open <button type="button" class="help-panel-topic-link" data-help-topic="troubleshooting.broker-unreachable">Troubleshooting</button> in the Help panel.</p>',
                '</article>',
            '</div>',
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

    function showBody(show) {
        var el = byId(BODY_EL_ID);
        if (el) { el.hidden = !show; }
    }

    function isoNowUtc() {
        return new Date().toISOString();
    }

    function emDashIfEmpty(value) {
        if (value === null || value === undefined || value === '') {
            return null;
        }
        return String(value);
    }

    function appendMetaRow(dl, label, value) {
        var dt = document.createElement('dt');
        dt.className = 'cook-meta-label';
        dt.textContent = label;
        var dd = document.createElement('dd');
        dd.className = 'cook-meta-value';
        var text = emDashIfEmpty(value);
        if (text === null) {
            dd.classList.add('cook-meta-empty');
            dd.textContent = '\u2014';
        } else {
            dd.textContent = text;
        }
        dl.appendChild(dt);
        dl.appendChild(dd);
    }

    function clearChildren(el) {
        if (!el) { return; }
        while (el.firstChild) { el.removeChild(el.firstChild); }
    }

    // Synthesize the integrity headline string from the two backend
    // signals (bundledPax.integrity + manifest.aligned). Backend keeps
    // these separate so diagnostics tools can read each independently;
    // the UI rolls them into one human-readable line with no alarm-bell
    // styling.
    function integrityHeadline(meta) {
        var integ = (meta && meta.bundledPax && meta.bundledPax.integrity) || '';
        if (integ === 'mismatch') { return 'Mismatch'; }
        if (integ === 'missing')  { return 'Missing'; }
        if (integ !== 'ok') {
            return integ ? ('Unknown (' + integ + ')') : 'Unknown';
        }
        var aligned = !!(meta && meta.manifest && meta.manifest.aligned);
        return aligned ? 'OK' : 'Manifest drift';
    }

    function checklistItem(ok, label, detail) {
        var li = document.createElement('li');
        li.className = 'settings-diag-item ' + (ok ? 'ok' : 'warn');
        var mark = document.createElement('span');
        mark.className = 'settings-diag-mark';
        mark.textContent = ok ? '\u2713' : '\u26A0';
        var text = document.createElement('span');
        text.className = 'settings-diag-text';
        text.textContent = label;
        li.appendChild(mark);
        li.appendChild(text);
        if (detail) {
            var d = document.createElement('span');
            d.className = 'settings-diag-detail';
            d.textContent = detail;
            li.appendChild(d);
        }
        return li;
    }

    // ----------------------------------------------------------------
    // Renderers (one per card)
    // ----------------------------------------------------------------

    function renderIdentity(meta) {
        var dl = byId(IDENTITY_EL_ID);
        clearChildren(dl);
        appendMetaRow(dl, 'Cookbook version', meta.cookbookVersion);
        appendMetaRow(dl, 'Release channel',  meta.releaseChannel);
        var host = meta.host || {};
        appendMetaRow(dl, 'Host machine',     host.machineName);
        appendMetaRow(dl, 'OS platform',      host.osPlatform);
        appendMetaRow(dl, 'OS version',       host.osVersion);
        appendMetaRow(dl, 'PowerShell',       host.psVersion);
    }

    function renderPax(meta) {
        var dl = byId(PAX_EL_ID);
        clearChildren(dl);
        var px = meta.bundledPax || {};
        appendMetaRow(dl, 'Bundled PAX version', px.version);
        appendMetaRow(dl, 'Bundled PAX SHA-256', px.sha256);
        appendMetaRow(dl, 'Relative path',       px.relativePath);
    }

    function renderIntegrity(meta) {
        var dl = byId(INTEGRITY_EL_ID);
        clearChildren(dl);
        var mf = meta.manifest || {};
        appendMetaRow(dl, 'Engine integrity',  integrityHeadline(meta));
        appendMetaRow(dl, 'Script integrity',  (meta.bundledPax && meta.bundledPax.integrity) || '');
        appendMetaRow(dl, 'Manifest aligned',  mf.aligned ? 'Yes' : 'No');
        appendMetaRow(dl, 'Manifest channel',  mf.channel);
    }

    function renderPaths(meta) {
        var dl = byId(PATHS_EL_ID);
        clearChildren(dl);
        var p = meta.paths || {};
        appendMetaRow(dl, 'Cookbook app root', p.appRoot);
        appendMetaRow(dl, 'Bundled PAX path',  p.paxScript);
        appendMetaRow(dl, 'VERSION.json',      p.versionFile);
        appendMetaRow(dl, 'manifest.json',     p.manifestFile);
        appendMetaRow(dl, 'Workspace root',    p.workspace);
        appendMetaRow(dl, 'Recipes',           p.recipes);
        appendMetaRow(dl, 'Cooks',             p.cooks);
        appendMetaRow(dl, 'Database',          p.database);
        appendMetaRow(dl, 'Templates',         p.templates);
    }

    function renderAssumptions(meta) {
        var dl = byId(ASSUMPTIONS_EL_ID);
        clearChildren(dl);
        var r = meta.runtime || {};
        appendMetaRow(dl, 'Broker PID',     r.brokerProcessId);
        appendMetaRow(dl, 'Broker port',    r.brokerPort);
        appendMetaRow(dl, 'Started (UTC)',  r.startedAtUtc);
        appendMetaRow(dl, 'Transport',      r.transport);
        appendMetaRow(dl, 'Bind address',   r.bindAddress);
    }

    function renderUpdateReadiness(meta) {
        var dl = byId(UPDATES_EL_ID);
        clearChildren(dl);
        var u = meta.updateReadiness || {};
        var mf = meta.manifest || {};
        // updaterAvailable reflects whether a syntactically valid update-
        // manifest URL is configured in this build's VERSION.json. The
        // Check button enables only in that case. No background polling
        // happens regardless of the value.
        var availLabel  = u.updaterAvailable ? 'Available' : 'Not available in this build';
        var statusLabel;
        if (u.latestKnownCookbookVersion === null || u.latestKnownCookbookVersion === undefined) {
            statusLabel = 'Unknown (manifest missing or unaligned)';
        } else if (u.upToDate) {
            statusLabel = 'Up to date';
        } else {
            statusLabel = 'Update available: ' + u.latestKnownCookbookVersion;
        }
        var sourceLabel = mf.packageUrlConfigured ? 'Manifest packageUrl configured' : 'Manifest packageUrl not configured';
        var urlLabel    = mf.updateManifestUrlConfigured ? mf.updateManifestUrl : 'not configured';
        appendMetaRow(dl, 'Current Cookbook',  meta.cookbookVersion);
        appendMetaRow(dl, 'Current bundled PAX', (meta.bundledPax && meta.bundledPax.version) || '');
        appendMetaRow(dl, 'Latest known',      u.latestKnownCookbookVersion);
        appendMetaRow(dl, 'Status',            statusLabel);
        appendMetaRow(dl, 'Updater',           availLabel);
        appendMetaRow(dl, 'Update manifest URL', urlLabel);
        appendMetaRow(dl, 'Last check',        u.checkPerformedAt);
        appendMetaRow(dl, 'Source',            u.lastCheckSource);
        appendMetaRow(dl, 'Release source',    sourceLabel);

        // Mirror updater-availability to the Check button. Operator click
        // wires through to POST /api/v1/updates/check. The Download
        // button stays hidden until a check returns updateAvailable.
        var btn = byId(UPDATE_CHECK_BTN_ID);
        if (btn) {
            if (u.updaterAvailable) {
                btn.disabled = false;
                btn.removeAttribute('aria-disabled');
                btn.removeAttribute('title');
            } else {
                btn.disabled = true;
                btn.setAttribute('aria-disabled', 'true');
                btn.setAttribute('title', 'No update manifest URL is configured for this build.');
            }
        }
    }

    function renderDiagnostics(meta) {
        var ul = byId(DIAGNOSTICS_EL_ID);
        clearChildren(ul);
        var px = meta.bundledPax || {};
        var mf = meta.manifest   || {};
        var p  = meta.paths      || {};

        // The diagnostic items below are SYNTHESIZED from the runtime
        // body the broker already returned.  No additional probes.
        ul.appendChild(checklistItem(true, 'Broker reachable', 'GET /api/v1/runtime/version returned 200'));
        ul.appendChild(checklistItem(
            px.integrity === 'ok',
            'Bundled PAX script integrity',
            px.integrity || 'unknown'
        ));
        ul.appendChild(checklistItem(
            !!mf.aligned,
            'Manifest aligned with VERSION.json',
            mf.aligned ? 'aligned' : 'drift'
        ));
        ul.appendChild(checklistItem(
            !!p.workspace,
            'Workspace path resolved',
            p.workspace || 'missing'
        ));
        ul.appendChild(checklistItem(
            !!p.paxScript,
            'Bundled PAX path resolved',
            p.paxScript || 'missing'
        ));
    }

    function renderAll(meta) {
        renderIdentity(meta);
        renderPax(meta);
        renderIntegrity(meta);
        renderPaths(meta);
        renderAssumptions(meta);
        renderUpdateReadiness(meta);
        renderDiagnostics(meta);
        hideStatus();
        showBody(true);
    }

    function renderError(message) {
        showBody(false);
        setStatus(message, 'error');
    }

    // ----------------------------------------------------------------
    // Update flow renderers
    // ----------------------------------------------------------------

    // Maps the structured "state" string the broker returns into an
    // operator-readable phrase. Keep these strings short and unambiguous.
    function describeCheckState(check) {
        if (!check || !check.state) { return ''; }
        switch (check.state) {
            case 'notConfigured':       return 'No update manifest URL is configured for this build.';
            case 'manifestUrlInvalid':  return 'The configured update manifest URL is rejected (must be HTTPS).';
            case 'fetchFailed':         return 'Manifest fetch failed.';
            case 'manifestInvalid':     return 'Manifest was fetched but failed validation.';
            case 'upToDate':            return 'You are running the latest published Cookbook version.';
            case 'updateAvailable':     return 'Update available: ' + (check.latestVersion || '(unknown)');
            default:                    return 'Unknown check state: ' + check.state;
        }
    }

    function describeDownloadState(dl) {
        if (!dl || !dl.state) { return ''; }
        switch (dl.state) {
            case 'noManifestSnapshot':  return 'Run a successful update check before downloading.';
            case 'downloadFailed':      return 'Package download failed.';
            case 'staged':              return 'Update package staged at: ' + dl.path;
            case 'alreadyStaged':       return 'This package was already staged at: ' + dl.path;
            default:                    return 'Unknown download state: ' + dl.state;
        }
    }

    function renderUpdateResult(check, download) {
        var box = byId(UPDATE_RESULT_EL_ID);
        if (!box) { return; }
        clearChildren(box);
        box.hidden = false;

        if (check) {
            var head = document.createElement('p');
            head.className = 'settings-update-result-line';
            head.textContent = describeCheckState(check);
            box.appendChild(head);
            if (check.message) {
                var msg = document.createElement('p');
                msg.className = 'settings-update-result-detail';
                msg.textContent = check.message;
                box.appendChild(msg);
            }
            if (check.checkedAtUtc) {
                var ts = document.createElement('p');
                ts.className = 'settings-update-result-detail';
                ts.textContent = 'Checked at (UTC): ' + check.checkedAtUtc;
                box.appendChild(ts);
            }
            if (check.bundledPaxChanges && check.bundledPaxChanges.changes) {
                var pax = document.createElement('p');
                pax.className = 'settings-update-result-detail';
                pax.textContent = 'Bundled PAX in the available update: '
                    + (check.bundledPaxChanges.currentVersion || '(unknown)')
                    + ' \u2192 '
                    + (check.bundledPaxChanges.latestVersion || '(unknown)');
                box.appendChild(pax);
            }
        }

        if (download) {
            var dlHead = document.createElement('p');
            dlHead.className = 'settings-update-result-line';
            dlHead.textContent = describeDownloadState(download);
            box.appendChild(dlHead);
            if (download.message) {
                var dlMsg = document.createElement('p');
                dlMsg.className = 'settings-update-result-detail';
                dlMsg.textContent = download.message;
                box.appendChild(dlMsg);
            }
            if (download.sha256) {
                var sha = document.createElement('p');
                sha.className = 'settings-update-result-detail';
                sha.textContent = 'Verified SHA-256: ' + download.sha256;
                box.appendChild(sha);
            }
        }
    }

    function renderStagedPackages(stateBody) {
        var box = byId(UPDATE_STAGED_EL_ID);
        if (!box) { return; }
        clearChildren(box);
        if (!stateBody || !stateBody.stagedPackages || stateBody.stagedPackages.length === 0) {
            return;
        }
        var h = document.createElement('h3');
        h.className = 'settings-update-staged-title';
        h.textContent = 'Staged update packages';
        box.appendChild(h);

        // Optional trust-readiness note. Surfaced truthfully: if no
        // trusted-signers allowlist is configured for this workspace,
        // we say so plainly. We never assert signer identity for the
        // packages below unless the broker emits a 'verified'
        // overallStatus.
        var tr = stateBody.trustReadiness || null;
        if (tr) {
            var note = document.createElement('p');
            note.className = 'settings-update-staged-trust-note';
            if (tr.allowlistError) {
                note.textContent = 'Trust allowlist: error reading <workspace>\\Trust\\trusted-signers.json (' + tr.allowlistError + '). Signer identity cannot be matched until this is corrected.';
            } else if (tr.allowlistPresent) {
                note.textContent = 'Trust allowlist: ' + tr.signerCount + ' signer(s) configured under <workspace>\\Trust\\trusted-signers.json. Packages with a detached .sig matching an allowlisted thumbprint are surfaced as "Signature verified"; everything else is surfaced with its truthful sub-state.';
            } else {
                note.textContent = 'Trust allowlist: not configured. Update packages are integrity-checked by SHA-256 against the manifest at download time; signed packages can still be verified cryptographically, but no signer thumbprint will match without a trusted-signers.json under <workspace>\\Trust\\.';
            }
            box.appendChild(note);
        }

        var ul = document.createElement('ul');
        ul.className = 'settings-update-staged-list';
        stateBody.stagedPackages.forEach(function (p) {
            var li = document.createElement('li');
            li.className = 'settings-update-staged-item';
            var name = document.createElement('span');
            name.className = 'settings-update-staged-name';
            name.textContent = p.filename || '(unnamed)';
            li.appendChild(name);

            // Per-package trust badge. The badge class encodes severity:
            //   ok       -> green   (hash matched AND, only for 'verified',
            //                        the detached signature was cryptographically
            //                        verified AND the signer thumbprint matched
            //                        the operator allowlist)
            //   warn     -> amber   (signature present but verification
            //                        incomplete: signer unknown, schema-only,
            //                        verifier module missing)
            //   bad      -> red     (hash mismatch, signature invalid,
            //                        missing file)
            //   neutral  -> grey    (no info available yet)
            //
            // 'verified' is the ONLY state that emits a green badge labeled
            // "Signature verified". 'unsigned' is also green because the hash
            // matched the manifest snapshot at staging time -- a truthful
            // claim -- but the label explicitly notes the package is unsigned.
            if (p.trust && p.trust.overallStatus) {
                var badge = document.createElement('span');
                badge.className = 'settings-update-staged-trust';
                var label = '';
                var severity = 'neutral';
                switch (p.trust.overallStatus) {
                    case 'verified':
                        label = 'Signature verified (trusted signer)'; severity = 'ok'; break;
                    case 'unsigned':
                        label = 'Unsigned (hash verified)'; severity = 'ok'; break;
                    case 'hashMismatch':
                        label = 'Hash mismatch'; severity = 'bad'; break;
                    case 'hashUnknown':
                        label = 'Hash unknown'; severity = 'neutral'; break;
                    case 'missing':
                        label = 'File missing'; severity = 'bad'; break;
                    case 'signatureInvalid':
                        label = 'Signature invalid'; severity = 'bad'; break;
                    case 'signaturePresentNotVerified':
                        label = 'Signature on disk (verifier unavailable)'; severity = 'warn'; break;
                    case 'signerMetadataInvalid':
                        label = 'Signer metadata invalid'; severity = 'warn'; break;
                    case 'signerUnknown':
                        label = 'Signer not in allowlist'; severity = 'warn'; break;
                    default:
                        label = p.trust.overallStatus; severity = 'neutral'; break;
                }
                badge.classList.add('settings-update-staged-trust-' + severity);
                badge.textContent = label;
                li.appendChild(badge);
            }

            var path = document.createElement('span');
            path.className = 'settings-update-staged-path';
            path.textContent = p.path || '';
            li.appendChild(path);
            ul.appendChild(li);
        });
        box.appendChild(ul);
    }

    // Reveal / hide the Download button based on the most recent check.
    function updateDownloadButtonVisibility(check) {
        var dlBtn = byId(UPDATE_DOWNLOAD_BTN_ID);
        if (!dlBtn) { return; }
        if (check && check.state === 'updateAvailable') {
            dlBtn.hidden = false;
            dlBtn.disabled = false;
            dlBtn.removeAttribute('aria-disabled');
        } else {
            dlBtn.hidden = true;
            dlBtn.disabled = true;
            dlBtn.setAttribute('aria-disabled', 'true');
        }
    }

    // Reveal / hide the Install button. Visible only when the /updates/state
    // body lists at least one staged package whose overallStatus is a green
    // severity ('verified' or 'unsigned'). Any other state (hash mismatch,
    // signature invalid, signer unknown, file missing, neutral) keeps the
    // button hidden \u2014 the operator cannot click their way past a trust
    // failure from this surface.
    function updateApplyButtonVisibility(stateBody) {
        var btn = byId(UPDATE_APPLY_BTN_ID);
        if (!btn) { return; }
        var ok = false;
        if (stateBody && stateBody.stagedPackages && stateBody.stagedPackages.length > 0) {
            for (var i = 0; i < stateBody.stagedPackages.length; i++) {
                var p = stateBody.stagedPackages[i];
                var status = p && p.trust && p.trust.overallStatus;
                if (status === 'verified' || status === 'unsigned') { ok = true; break; }
            }
        }
        if (ok) {
            btn.hidden = false;
            btn.disabled = false;
            btn.removeAttribute('aria-disabled');
        } else {
            btn.hidden = true;
            btn.disabled = true;
            btn.setAttribute('aria-disabled', 'true');
        }
    }

    // Render the release-notes pane from the most recent check response.
    // The pane is a single hyperlink to the manifest-supplied releaseNotesUrl;
    // we never fetch the URL ourselves, so no second outbound request is
    // issued. If the manifest does not carry a releaseNotesUrl, the pane
    // stays hidden.
    function renderReleaseNotes(check) {
        var box = byId(UPDATE_RELEASE_NOTES_EL_ID);
        if (!box) { return; }
        clearChildren(box);
        var url = null;
        var version = null;
        if (check && check.manifestSnapshot && check.manifestSnapshot.latestCookbook) {
            url     = check.manifestSnapshot.latestCookbook.releaseNotesUrl || null;
            version = check.manifestSnapshot.latestCookbook.version || null;
        }
        if (!url) {
            box.hidden = true;
            return;
        }
        box.hidden = false;
        var heading = document.createElement('p');
        heading.className = 'settings-update-release-notes-title';
        heading.textContent = version ? ('Release notes for ' + version) : 'Release notes';
        box.appendChild(heading);
        var p = document.createElement('p');
        p.className = 'settings-update-release-notes-link';
        var a = document.createElement('a');
        a.href = url;
        a.target = '_blank';
        a.rel = 'noopener noreferrer';
        a.textContent = url;
        p.appendChild(a);
        box.appendChild(p);
    }

    // Maps the /api/v1/updates/apply response state into an operator-readable
    // headline. The route enumerates a bounded set of outcomes (HTTP status +
    // structured body); this function does not synthesize states the broker
    // did not return.
    function describeApplyState(status, body) {
        if (status === 202) {
            return 'Apply accepted. Cookbook is restarting against the new version.';
        }
        if (status === 200) {
            return 'Update applied. Cookbook is restarting.';
        }
        if (status === 401) {
            return 'Re-authentication required before applying an update.';
        }
        if (status === 409) {
            var reason = body && body.reason ? body.reason : '';
            if (reason === 'active_cooks_present') {
                return 'Active cooks are running. Wait for them to finish or stop them, then try again.';
            }
            if (reason === 'package_trust_mismatch' || reason === 'package_trust_apply_mismatch' || reason === 'at_apply_re_verification_failed') {
                return 'The staged package failed at-apply re-verification. Re-download before installing.';
            }
            if (reason === 'no_verified_staged_package') {
                return 'No verified staged package is available. Run Check and Download first.';
            }
            return 'Apply refused (HTTP 409): ' + (body && body.detail ? body.detail : reason || 'precondition not satisfied');
        }
        if (status === 500) {
            var reason500 = body && body.reason ? body.reason : '';
            if (reason500 === 'update_apply_extraction_failed') {
                return 'Apply failed while extracting the staged package. The broker did not restart.';
            }
            if (reason500 === 'update_apply_extracted_tree_malformed') {
                return 'The staged package extracted but did not contain a usable App\\VERSION.json.';
            }
            if (reason500 === 'update_apply_handoff_write_failed') {
                return 'Apply failed while writing the launcher handoff. The broker did not restart.';
            }
            return 'Apply failed (HTTP 500). Cookbook stayed running.';
        }
        if (status === 503) {
            return 'Apply preconditions could not be evaluated (HTTP 503).';
        }
        if (status === 501) {
            return 'Apply machinery is not available in this build. The staged package is verified and untouched.';
        }
        if (status === 0) {
            return 'Apply could not reach the broker.';
        }
        return 'Apply failed (HTTP ' + status + ').';
    }

    function renderApplyResult(resp) {
        var box = byId(UPDATE_APPLY_RESULT_EL_ID);
        if (!box) { return; }
        clearChildren(box);
        box.hidden = false;
        var status = resp ? (resp.status || 0) : 0;
        var body   = resp ? (resp.body || null) : null;

        var head = document.createElement('p');
        head.className = 'settings-update-apply-result-line';
        head.textContent = describeApplyState(status, body);
        box.appendChild(head);

        if (resp && resp.networkError) {
            var ne = document.createElement('p');
            ne.className = 'settings-update-apply-result-detail';
            ne.textContent = 'Network error: ' + resp.networkError;
            box.appendChild(ne);
        }

        if (body && body.detail) {
            var detail = document.createElement('p');
            detail.className = 'settings-update-apply-result-detail';
            detail.textContent = body.detail;
            box.appendChild(detail);
        }

        // HTTP 202 carries the apply-handoff payload: surface the
        // operator-facing fields so the user understands which
        // package is being applied and what to expect next. Every
        // field is a literal property of the broker response.
        if (status === 202 && body) {
            if (body.version) {
                var ver202 = document.createElement('p');
                ver202.className = 'settings-update-apply-result-detail';
                ver202.textContent = 'Target version: ' + String(body.version);
                box.appendChild(ver202);
            }
            if (body.expectedBehavior) {
                var eb202 = document.createElement('p');
                eb202.className = 'settings-update-apply-result-detail';
                eb202.textContent = String(body.expectedBehavior);
                box.appendChild(eb202);
            }
            if (body.stagedExtractedPath) {
                var sep202 = document.createElement('p');
                sep202.className = 'settings-update-apply-result-detail';
                sep202.textContent = 'Staged source: ' + String(body.stagedExtractedPath);
                box.appendChild(sep202);
            }
            if (body.handoffPath) {
                var hp202 = document.createElement('p');
                hp202.className = 'settings-update-apply-result-detail';
                hp202.textContent = 'Handoff record: ' + String(body.handoffPath);
                box.appendChild(hp202);
            }
        }

        // 501 is no longer the normal success path; preserved as
        // recovery copy in case a future build re-introduces an
        // apply-not-yet-implemented refusal with the same shape.
        if (status === 501 && body) {
            if (body.applyMachineryStatus) {
                var ms = document.createElement('p');
                ms.className = 'settings-update-apply-result-detail';
                ms.textContent = 'Apply machinery status: ' + body.applyMachineryStatus;
                box.appendChild(ms);
            }
            if (body.applyVerificationPassed === true) {
                var av = document.createElement('p');
                av.className = 'settings-update-apply-result-detail';
                av.textContent = 'At-apply re-verification: passed for every staged package.';
                box.appendChild(av);
            }
            if (body.followUpSlice) {
                var fs = document.createElement('p');
                fs.className = 'settings-update-apply-result-detail';
                fs.textContent = 'Follow-up slice that delivers the swap + restart hand-off: ' + body.followUpSlice;
                box.appendChild(fs);
            }
            if (body.nextSteps && body.nextSteps.length) {
                var ns = document.createElement('p');
                ns.className = 'settings-update-apply-result-line';
                ns.textContent = 'Operator next steps (manual install path):';
                box.appendChild(ns);
                var ol = document.createElement('ol');
                ol.className = 'settings-update-apply-result-steps';
                for (var i = 0; i < body.nextSteps.length; i++) {
                    var li = document.createElement('li');
                    li.textContent = String(body.nextSteps[i]);
                    ol.appendChild(li);
                }
                box.appendChild(ol);
            }
        }

        if (body && body.observation_id) {
            var ob = document.createElement('p');
            ob.className = 'settings-update-apply-result-detail';
            ob.textContent = 'Observation id: ' + body.observation_id;
            box.appendChild(ob);
        }
    }

    // ----------------------------------------------------------------
    // Update flow click handlers
    // ----------------------------------------------------------------

    function onClickCheck() {
        if (!state) { return; }
        var capturedEpoch = state.epoch;
        var btn = byId(UPDATE_CHECK_BTN_ID);
        var dlBtn = byId(UPDATE_DOWNLOAD_BTN_ID);
        if (btn)   { btn.disabled = true; }
        if (dlBtn) { dlBtn.hidden = true; dlBtn.disabled = true; }
        var box = byId(UPDATE_RESULT_EL_ID);
        if (box) {
            clearChildren(box);
            box.hidden = false;
            var p = document.createElement('p');
            p.className = 'settings-update-result-line';
            p.textContent = 'Checking for updates\u2026';
            box.appendChild(p);
        }
        window.cookbookApi.post('/api/v1/updates/check', {}, { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
            if (!state || state.epoch !== capturedEpoch) { return; }
            var btnNow = byId(UPDATE_CHECK_BTN_ID);
            if (btnNow) { btnNow.disabled = false; }
            if (resp.networkError) {
                renderUpdateResult({ state: 'fetchFailed', message: 'Network error: ' + resp.networkError }, null);
                return;
            }
            if (!resp.ok || !resp.body) {
                renderUpdateResult({ state: 'fetchFailed', message: 'HTTP ' + resp.status }, null);
                return;
            }
            state.lastCheckBody = resp.body;
            var check = resp.body.check || null;
            renderUpdateResult(check, null);
            renderReleaseNotes(check);
            updateDownloadButtonVisibility(check);
            renderStagedPackages(resp.body.state);
            updateApplyButtonVisibility(resp.body.state);
        });
    }

    function onClickDownload() {
        if (!state) { return; }
        var capturedEpoch = state.epoch;
        var dlBtn = byId(UPDATE_DOWNLOAD_BTN_ID);
        var checkBtn = byId(UPDATE_CHECK_BTN_ID);
        if (dlBtn)    { dlBtn.disabled = true; }
        if (checkBtn) { checkBtn.disabled = true; }
        var box = byId(UPDATE_RESULT_EL_ID);
        if (box) {
            clearChildren(box);
            box.hidden = false;
            var p = document.createElement('p');
            p.className = 'settings-update-result-line';
            p.textContent = 'Downloading update package\u2026';
            box.appendChild(p);
            var sub = document.createElement('p');
            sub.className = 'settings-update-result-detail';
            sub.textContent = 'The broker will block while the package downloads. Do not close this window.';
            box.appendChild(sub);
        }
        window.cookbookApi.post('/api/v1/updates/download', {}, { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
            if (!state || state.epoch !== capturedEpoch) { return; }
            var checkNow = byId(UPDATE_CHECK_BTN_ID);
            if (checkNow) { checkNow.disabled = false; }
            if (resp.networkError) {
                renderUpdateResult(null, { state: 'downloadFailed', message: 'Network error: ' + resp.networkError });
                return;
            }
            if (!resp.body) {
                renderUpdateResult(null, { state: 'downloadFailed', message: 'HTTP ' + resp.status });
                return;
            }
            var download = resp.body.download || null;
            renderUpdateResult(state.lastCheckBody ? state.lastCheckBody.check : null, download);
            // Hide the Download button on success; the operator runs the
            // installer manually from the staged package.
            var dlBtnNow = byId(UPDATE_DOWNLOAD_BTN_ID);
            if (dlBtnNow && download && (download.state === 'staged' || download.state === 'alreadyStaged')) {
                dlBtnNow.hidden = true;
                dlBtnNow.disabled = true;
            } else if (dlBtnNow) {
                dlBtnNow.disabled = false;
            }
            renderStagedPackages(resp.body.state);
            updateApplyButtonVisibility(resp.body.state);
        });
    }

    function onClickApply() {
        if (!state) { return; }
        var capturedEpoch = state.epoch;
        var applyBtn = byId(UPDATE_APPLY_BTN_ID);
        var checkBtn = byId(UPDATE_CHECK_BTN_ID);
        var dlBtn    = byId(UPDATE_DOWNLOAD_BTN_ID);
        if (applyBtn) { applyBtn.disabled = true; applyBtn.setAttribute('aria-disabled', 'true'); }
        if (checkBtn) { checkBtn.disabled = true; }
        if (dlBtn)    { dlBtn.disabled = true; }
        var box = byId(UPDATE_APPLY_RESULT_EL_ID);
        if (box) {
            clearChildren(box);
            box.hidden = false;
            var p = document.createElement('p');
            p.className = 'settings-update-apply-result-line';
            p.textContent = 'Cookbook is restarting against the new version\u2026';
            box.appendChild(p);
            var sub = document.createElement('p');
            sub.className = 'settings-update-apply-result-detail';
            sub.textContent = 'The broker is re-verifying the staged package, writing the handoff, and exiting. Do not close this window.';
            box.appendChild(sub);
        }
        window.cookbookApi.post('/api/v1/updates/apply', {}, { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
            if (!state || state.epoch !== capturedEpoch) { return; }
            state.lastApplyBody = resp ? resp.body : null;
            renderApplyResult(resp);
            // Re-enable Check button so the operator can recover.
            var checkNow = byId(UPDATE_CHECK_BTN_ID);
            if (checkNow) { checkNow.disabled = false; }
            // Apply button stays disabled after a 200/202 (apply succeeded
            // or is in progress). On any other status, re-enable it so the
            // operator can retry once the underlying condition is cleared.
            var applyNow = byId(UPDATE_APPLY_BTN_ID);
            if (applyNow) {
                var st = resp ? (resp.status || 0) : 0;
                if (st === 200 || st === 202) {
                    applyNow.disabled = true;
                    applyNow.setAttribute('aria-disabled', 'true');
                } else {
                    applyNow.disabled = false;
                    applyNow.removeAttribute('aria-disabled');
                }
            }
        });
    }

    function loadUpdatesState() {
        if (!state) { return; }
        var capturedEpoch = state.epoch;
        window.cookbookApi.get('/api/v1/updates/state', { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
            if (!state || state.epoch !== capturedEpoch) { return; }
            if (!resp.ok || !resp.body) { return; }
            renderStagedPackages(resp.body);
            updateApplyButtonVisibility(resp.body);
        });
    }

    // ----------------------------------------------------------------
    // Notification preferences (read + save)
    // ----------------------------------------------------------------

    function setNotifyStatus(text, kind) {
        var el = byId(NOTIFY_STATUS_EL_ID);
        if (!el) { return; }
        el.textContent = text;
        el.className = 'settings-notify-status' + (kind ? ' ' + kind : '');
        el.hidden = false;
    }

    function hideNotifyStatus() {
        var el = byId(NOTIFY_STATUS_EL_ID);
        if (el) { el.hidden = true; }
    }

    function setNotifyControlsDisabled(disabled) {
        for (var i = 0; i < NOTIFY_KEY_MAP.length; i++) {
            var cb = byId(NOTIFY_KEY_MAP[i].id);
            if (cb) { cb.disabled = disabled; }
        }
        var webhookIds = [NOTIFY_WEBHOOK_ENABLED_ID, NOTIFY_WEBHOOK_URL_ID, NOTIFY_WEBHOOK_FORMAT_ID];
        for (var w = 0; w < webhookIds.length; w++) {
            var wc = byId(webhookIds[w]);
            if (wc) { wc.disabled = disabled; }
        }
        var btn = byId(NOTIFY_SAVE_BTN_ID);
        if (btn) {
            btn.disabled = disabled;
            btn.setAttribute('aria-disabled', disabled ? 'true' : 'false');
        }
    }

    // Reflects a settings body into the editable controls. The four
    // status/toast preferences and the webhook enable flag are booleans;
    // the webhook url and format are strings. A boolean preference is
    // treated as enabled only when the broker returns an explicit boolean
    // true, matching the broker's stored representation. The webhook URL is
    // a URL (not a secret), so it is safe to echo back into the input.
    function applyNotificationSettings(body) {
        for (var i = 0; i < NOTIFY_KEY_MAP.length; i++) {
            var cb = byId(NOTIFY_KEY_MAP[i].id);
            if (cb) {
                cb.checked = (body[NOTIFY_KEY_MAP[i].key] === true);
            }
        }

        var enabledCb = byId(NOTIFY_WEBHOOK_ENABLED_ID);
        if (enabledCb) {
            enabledCb.checked = (body[NOTIFY_WEBHOOK_ENABLED_KEY] === true);
        }

        var urlInput = byId(NOTIFY_WEBHOOK_URL_ID);
        if (urlInput) {
            var urlVal = body[NOTIFY_WEBHOOK_URL_KEY];
            urlInput.value = (typeof urlVal === 'string') ? urlVal : '';
        }

        var formatSelect = byId(NOTIFY_WEBHOOK_FORMAT_ID);
        if (formatSelect) {
            var fmtVal = body[NOTIFY_WEBHOOK_FORMAT_KEY];
            formatSelect.value = (fmtVal === 'teams') ? 'teams' : 'generic';
        }
    }

    function loadNotificationSettings() {
        if (!state) { return; }
        var capturedEpoch = state.epoch;
        var signal = state.abortCtrl ? state.abortCtrl.signal : null;

        setNotifyControlsDisabled(true);
        setNotifyStatus('Loading notification settings', '');

        window.cookbookApi.get('/api/v1/settings/notifications', { signal: signal }).then(function (resp) {
            if (!state || state.epoch !== capturedEpoch) { return; }
            if (resp.networkError) {
                setNotifyStatus('Could not load notification settings: ' + resp.networkError, 'error');
                return;
            }
            if (!resp.ok || !resp.body) {
                setNotifyStatus('Could not load notification settings (HTTP ' + resp.status + ').', 'error');
                return;
            }
            applyNotificationSettings(resp.body);
            setNotifyControlsDisabled(false);
            hideNotifyStatus();
        });
    }

    function onClickSaveNotifications() {
        if (!state) { return; }
        var capturedEpoch = state.epoch;
        var signal = state.abortCtrl ? state.abortCtrl.signal : null;

        var payload = {};
        for (var i = 0; i < NOTIFY_KEY_MAP.length; i++) {
            var cb = byId(NOTIFY_KEY_MAP[i].id);
            payload[NOTIFY_KEY_MAP[i].key] = !!(cb && cb.checked);
        }

        var enabledCb = byId(NOTIFY_WEBHOOK_ENABLED_ID);
        payload[NOTIFY_WEBHOOK_ENABLED_KEY] = !!(enabledCb && enabledCb.checked);

        var urlInput = byId(NOTIFY_WEBHOOK_URL_ID);
        var urlVal = (urlInput && typeof urlInput.value === 'string') ? urlInput.value.trim() : '';
        payload[NOTIFY_WEBHOOK_URL_KEY] = urlVal;

        var formatSelect = byId(NOTIFY_WEBHOOK_FORMAT_ID);
        var fmtVal = (formatSelect && formatSelect.value === 'teams') ? 'teams' : 'generic';
        payload[NOTIFY_WEBHOOK_FORMAT_KEY] = fmtVal;

        setNotifyControlsDisabled(true);
        setNotifyStatus('Saving notification settings', '');

        window.cookbookApi.put('/api/v1/settings/notifications', payload, { signal: signal }).then(function (resp) {
            if (!state || state.epoch !== capturedEpoch) { return; }
            setNotifyControlsDisabled(false);
            if (resp.networkError) {
                setNotifyStatus('Could not save notification settings: ' + resp.networkError, 'error');
                return;
            }
            if (!resp.ok || !resp.body) {
                var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                var friendly = code;
                if (code === 'invalid_webhook_url') {
                    friendly = 'the webhook URL must be an https address that is not a local, private, or metadata host';
                } else if (code === 'invalid_webhook_format') {
                    friendly = 'the payload format must be Generic or Teams';
                }
                setNotifyStatus('Could not save notification settings (' + friendly + ').', 'error');
                return;
            }
            applyNotificationSettings(resp.body);
            setNotifyStatus('Notification settings saved.', 'ok');
        });
    }

    // ----------------------------------------------------------------
    // Fetch
    // ----------------------------------------------------------------

    function loadRuntime() {
        if (!state) { return; }
        var capturedEpoch = state.epoch;
        var signal = state.abortCtrl ? state.abortCtrl.signal : null;

        var btn = byId(RELOAD_BTN_ID);
        if (btn) { btn.disabled = true; }
        setStatus('Loading environment metadata', '');
        showBody(false);

        window.cookbookApi.get('/api/v1/runtime/version', { signal: signal }).then(function (resp) {
            // Discard the response if the page has been torn down or
            // remounted since the request went out.
            if (!state || state.epoch !== capturedEpoch) { return; }

            setText(LAST_FETCHED_ID, isoNowUtc());
            var btnNow = byId(RELOAD_BTN_ID);
            if (btnNow) { btnNow.disabled = false; }

            if (resp.networkError) {
                renderError('Failed to reach broker: ' + resp.networkError);
                return;
            }
            if (resp.status === 401) {
                renderError('Session token rejected (HTTP 401). Reload the app from the launcher.');
                return;
            }
            if (!resp.ok) {
                var err = (resp.body && resp.body.error) ? resp.body.error : '';
                renderError('Failed to load environment metadata (HTTP ' + resp.status + (err ? ': ' + err : '') + ').');
                return;
            }
            if (!resp.body) {
                renderError('Environment metadata response was empty.');
                return;
            }
            renderAll(resp.body);
        });
    }

    // ----------------------------------------------------------------
    // Page lifecycle
    // ----------------------------------------------------------------

    function mount(container) {
        container.innerHTML = PAGE_TEMPLATE;

        state = {
            epoch:             nextEpoch++,
            reloadHandler:     null,
            checkHandler:      null,
            downloadHandler:   null,
            applyHandler:      null,
            notifySaveHandler: null,
            lastCheckBody:     null,  // most recent { check, state } response from /updates/check
            lastApplyBody:     null,  // most recent body from /updates/apply
            // Phase AE: per-mount AbortController; aborted on teardown.
            abortCtrl:         (typeof AbortController === 'function') ? new AbortController() : null
        };

        setText(BROKER_HOST_ID, window.location.host || '(unknown)');

        var tokenEl = byId(TOKEN_STATUS_ID);
        if (window.cookbookApi.hasToken()) {
            if (tokenEl) { tokenEl.textContent = 'present'; tokenEl.className = 'ok'; }
        } else {
            if (tokenEl) { tokenEl.textContent = 'missing'; tokenEl.className = 'miss'; }
        }

        var btn = byId(RELOAD_BTN_ID);
        if (btn) {
            state.reloadHandler = function () { loadRuntime(); };
            btn.addEventListener('click', state.reloadHandler);
            btn.disabled = false;
            if (window.PaxTopbar) { window.PaxTopbar.pageReloadBound = true; }
        }

        // Check button: enabled by renderUpdateReadiness once the runtime
        // body reports updaterAvailable=true. Click triggers ONE outbound
        // manifest fetch via POST /api/v1/updates/check.
        var upBtn = byId(UPDATE_CHECK_BTN_ID);
        if (upBtn) {
            state.checkHandler = onClickCheck;
            upBtn.addEventListener('click', state.checkHandler);
        }

        // Download button: hidden until the most recent check returns
        // updateAvailable. Click triggers ONE outbound package download
        // via POST /api/v1/updates/download.
        var dlBtn = byId(UPDATE_DOWNLOAD_BTN_ID);
        if (dlBtn) {
            state.downloadHandler = onClickDownload;
            dlBtn.addEventListener('click', state.downloadHandler);
        }

        // Install and Restart button: hidden until /updates/state reports
        // at least one staged package with a green-severity trust state
        // ('verified' or 'unsigned'). Click triggers ONE outbound apply
        // via POST /api/v1/updates/apply.
        var applyBtn = byId(UPDATE_APPLY_BTN_ID);
        if (applyBtn) {
            state.applyHandler = onClickApply;
            applyBtn.addEventListener('click', state.applyHandler);
        }

        // Notification preferences Save button. Reads the four checkbox
        // controls and persists them via PUT /api/v1/settings/notifications.
        var notifyBtn = byId(NOTIFY_SAVE_BTN_ID);
        if (notifyBtn) {
            state.notifySaveHandler = onClickSaveNotifications;
            notifyBtn.addEventListener('click', state.notifySaveHandler);
        }

        if (!window.cookbookApi.hasToken()) {
            setText(LAST_FETCHED_ID, isoNowUtc());
            renderError('No session token in this tab. Reload the app from the launcher.');
            return;
        }
        loadRuntime();
        loadUpdatesState();
        loadNotificationSettings();
    }

    function teardown() {
        if (!state) { return; }

        var btn = byId(RELOAD_BTN_ID);
        if (btn && state.reloadHandler) {
            btn.removeEventListener('click', state.reloadHandler);
            btn.disabled = false;
        }
        if (window.PaxTopbar) { window.PaxTopbar.pageReloadBound = false; }
        var upBtn = byId(UPDATE_CHECK_BTN_ID);
        if (upBtn && state.checkHandler) {
            upBtn.removeEventListener('click', state.checkHandler);
        }
        var dlBtn = byId(UPDATE_DOWNLOAD_BTN_ID);
        if (dlBtn && state.downloadHandler) {
            dlBtn.removeEventListener('click', state.downloadHandler);
        }
        var applyBtn = byId(UPDATE_APPLY_BTN_ID);
        if (applyBtn && state.applyHandler) {
            applyBtn.removeEventListener('click', state.applyHandler);
        }
        var notifyBtn = byId(NOTIFY_SAVE_BTN_ID);
        if (notifyBtn && state.notifySaveHandler) {
            notifyBtn.removeEventListener('click', state.notifySaveHandler);
        }

        // Phase AE: abort in-flight fetches before bumping epoch.
        if (state.abortCtrl) {
            try { state.abortCtrl.abort(); } catch (e) {}
        }

        // Bump the epoch so any in-flight response is discarded by its
        // .then handler when it eventually resolves.
        nextEpoch++;

        // Drop refs. Container DOM is cleared by the router.
        state = null;
    }

    window.cookbookSettingsPage = {
        mount:    mount,
        teardown: teardown
    };
})();
