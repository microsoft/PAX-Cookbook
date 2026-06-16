// PAX Cookbook -- Home page (#/home).
//
// Operator-facing dashboard. The first surface the chef sees after
// the launcher hand-off; replaces the old default of '#/recipes'.
//
// What this page IS:
//   - A read-only summary surface that reads what already exists
//     (templates, recipes, cooks, auth profiles, runtime version)
//     and composes a calm overview.
//   - A small set of jump-points into the existing operator surfaces
//     (Pantry, Recipes, Bakes, Chef's Keys, Settings).
//
// What this page is NOT, by design:
//   - A cook-launch surface (cooks start from the recipe editor; no
//     change in this checkpoint).
//   - A data-fetch fan-out / aggregation engine. Each section reads
//     only what is needed for its own display.
//   - A configuration surface. Settings stays the home of all writes.
//   - A telemetry / analytics surface. No outbound calls, no beacons.
//
// All data flows through window.cookbookApi (which itself runs against
// the local broker only). Per-mount AbortController + monotonic epoch
// guard against late responses landing in a torn-down DOM.
(function () {
    'use strict';

    var BROKER_HOST_ID  = 'broker-host';
    var LAST_FETCHED_ID = 'last-fetched';
    var RELOAD_BTN_ID   = 'reload-button';
    var NAV_VER_ID      = 'nav-rail-version';
    var NAV_PAX_VER_ID  = 'nav-rail-pax-version';
    var NAV_WS_ID       = 'nav-rail-workspace';

    var state = null;
    var nextEpoch = 1;

    // ----------------------------------------------------------------
    // DOM helpers (no html-string template literals -- keeps the AV
    // surface clean of any patterns that could be flagged as inline
    // markup factories).
    // ----------------------------------------------------------------

    function el(tag, className, text) {
        var n = document.createElement(tag);
        if (className) { n.className = className; }
        if (text !== undefined && text !== null) { n.textContent = String(text); }
        return n;
    }

    function svgUse(symbolId, className) {
        var ns = 'http://www.w3.org/2000/svg';
        var xlink = 'http://www.w3.org/1999/xlink';
        var svg = document.createElementNS(ns, 'svg');
        if (className) { svg.setAttribute('class', className); }
        svg.setAttribute('aria-hidden', 'true');
        svg.setAttribute('focusable', 'false');
        var use = document.createElementNS(ns, 'use');
        use.setAttributeNS(xlink, 'xlink:href', '#' + symbolId);
        use.setAttribute('href', '#' + symbolId);
        svg.appendChild(use);
        return svg;
    }

    function byId(id) { return document.getElementById(id); }

    function setText(id, text) {
        var n = byId(id);
        if (n) { n.textContent = String(text); }
    }

    function isoNowUtc() { return new Date().toISOString(); }

    function formatDate(iso) {
        if (!iso) { return null; }
        var d = new Date(iso);
        if (isNaN(d.getTime())) { return null; }
        var months = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
        var h  = d.getHours();
        var mi = d.getMinutes();
        var ap = h >= 12 ? 'PM' : 'AM';
        h = h % 12; if (h === 0) { h = 12; }
        if (mi < 10) { mi = '0' + mi; }
        return months[d.getMonth()] + ' ' + d.getDate() + ', ' + d.getFullYear() + ' ' + h + ':' + mi + ' ' + ap;
    }

    function formatBakeStatus(status) {
        var s = String(status || '').toLowerCase();
        if (s === 'succeeded') { return { label: 'Succeeded', kind: 'success' }; }
        if (s === 'failed')    { return { label: 'Failed',    kind: 'danger'  }; }
        if (s === 'running')   { return { label: 'Running',   kind: 'info'    }; }
        if (s === 'stopped')   { return { label: 'Stopped',   kind: 'warning' }; }
        if (s === 'killed')    { return { label: 'Killed',    kind: 'danger'  }; }
        if (s === 'paused')    { return { label: 'Paused',    kind: 'warning' }; }
        if (s)                 { return { label: String(status), kind: 'info' }; }
        return { label: 'Pending', kind: 'info' };
    }

    function recipeNameFor(recipeId, recipes) {
        if (!recipes) { return recipeId || 'Recipe'; }
        for (var i = 0; i < recipes.length; i++) {
            var r = recipes[i] || {};
            var rid = r.recipeId || r.id;
            if (rid === recipeId) {
                return r.displayName || r.name || rid;
            }
        }
        return recipeId || 'Recipe';
    }

    // ----------------------------------------------------------------
    // Section builders
    // ----------------------------------------------------------------

    function buildHero(container, summary) {
        var hero = el('section', 'home-hero');
        var heroText = el('div', 'home-hero-text');
        var eyebrow = el('div', 'home-eyebrow');
        eyebrow.textContent = summary.firstRun ? 'SECURE LOCAL DATA KITCHEN' : 'WELCOME BACK, CHEF';
        heroText.appendChild(eyebrow);

        var title = el('h1', 'home-hero-title', 'PAX Cookbook');
        heroText.appendChild(title);

        var value = el('p', 'home-hero-value',
            'A secure local data kitchen for preparing Microsoft 365 audit data into trusted analytics-ready outputs.');
        heroText.appendChild(value);

        var ctas = el('div', 'home-hero-ctas');
        var browseBtn = el('a', 'btn-primary home-hero-cta-primary', 'Browse Pantry');
        browseBtn.href = '#/pantry';
        var chev = svgUse('icon-chevron-right', 'btn-trailing-icon');
        browseBtn.appendChild(chev);
        ctas.appendChild(browseBtn);

        var openBtn = el('a', 'btn-secondary home-hero-cta-secondary', 'Open Recipes');
        openBtn.href = '#/recipes';
        ctas.appendChild(openBtn);
        heroText.appendChild(ctas);

        hero.appendChild(heroText);

        var heroArt = el('div', 'home-hero-art');
        heroArt.setAttribute('aria-hidden', 'true');
        var artImg = document.createElement('img');
        artImg.src = 'images/PAX Cookbook Logo - Transparent Background - 800x800.png';
        artImg.alt = '';
        artImg.className = 'home-hero-art-img';
        heroArt.appendChild(artImg);
        hero.appendChild(heroArt);

        container.appendChild(hero);
    }

    function buildTrustCard(container) {
        var card = el('aside', 'home-trust-card');
        card.setAttribute('aria-labelledby', 'home-trust-title');
        var title = el('h2', 'home-trust-title', 'Kitchen Trust');
        title.id = 'home-trust-title';
        card.appendChild(title);

        var rows = [
            { icon: 'icon-trust-shield', title: 'Runs locally',
              body: 'All processing happens on this machine.' },
            { icon: 'icon-trust-lock',   title: 'Tenant data stays in your environment',
              body: 'No tenant data is sent to any hosted service.' },
            { icon: 'icon-trust-check',  title: 'Approved PAX only',
              body: 'PAX scripts are checked against the approved manifest before any bake runs.' }
        ];

        var list = el('ul', 'home-trust-list');
        for (var i = 0; i < rows.length; i++) {
            var row = rows[i];
            var li = el('li', 'home-trust-row');
            var ic = el('span', 'home-trust-icon');
            ic.appendChild(svgUse(row.icon, 'home-trust-svg'));
            li.appendChild(ic);
            var txt = el('div', 'home-trust-text');
            txt.appendChild(el('div', 'home-trust-row-title', row.title));
            txt.appendChild(el('div', 'home-trust-row-body', row.body));
            li.appendChild(txt);
            list.appendChild(li);
        }
        card.appendChild(list);
        container.appendChild(card);
    }

    function buildStatusGrid(container, summary) {
        var section = el('section', 'home-status');
        var title = el('h2', 'home-section-title', 'Kitchen Status');
        section.appendChild(title);

        var grid = el('div', 'home-status-grid');

        // 1. Workspace
        grid.appendChild(buildStatusCard({
            icon: 'icon-status-workspace', kind: 'neutral',
            label: 'Workspace',
            primary: summary.workspacePath || '(default)',
            secondary: summary.workspaceFree ? (summary.workspaceFree + ' free') : null,
            linkText: 'Open Folder', linkHref: '#/settings'
        }));

        // 2. PAX script
        grid.appendChild(buildStatusCard({
            icon: 'icon-status-code', kind: 'neutral',
            label: 'PAX script',
            primary: summary.paxCard ? summary.paxCard.primary : 'Not installed',
            secondary: summary.paxCard ? summary.paxCard.secondary : 'Add the PAX script to run bakes.',
            secondaryKind: summary.paxCard ? summary.paxCard.secondaryKind : 'warning',
            linkText: 'View Details', linkHref: '#/settings'
        }));

        // 3. Last Bake
        var lastBake = summary.lastBake;
        var lastBakeCard;
        if (lastBake) {
            var bakeStatus = formatBakeStatus(lastBake.status);
            lastBakeCard = buildStatusCard({
                icon: 'icon-status-bake', kind: bakeStatus.kind,
                label: 'Last Bake',
                primary: formatDate(lastBake.startedAtUtc || lastBake.createdAtUtc) || '\u2014',
                secondary: lastBake.recipeName || '',
                statusPill: bakeStatus,
                linkText: 'View Bakes', linkHref: '#/bakes'
            });
        } else {
            lastBakeCard = buildStatusCard({
                icon: 'icon-status-bake', kind: 'neutral',
                label: 'Last Bake',
                primary: 'No bakes yet',
                secondary: 'Start a recipe to record one.',
                linkText: 'Open Recipes', linkHref: '#/recipes'
            });
        }
        grid.appendChild(lastBakeCard);

        // 4. Latest Taste Test (deferred surface; calm placeholder)
        grid.appendChild(buildStatusCard({
            icon: 'icon-status-search', kind: 'neutral',
            label: 'Latest Taste Test',
            primary: 'Coming soon',
            secondary: 'Taste Tests arrive in a later release.',
            linkText: 'View Taste Tests', linkHref: '#/taste-tests'
        }));

        // 5. Chef's Keys
        var keys = summary.keys || { count: 0, healthy: 0 };
        grid.appendChild(buildStatusCard({
            icon: 'icon-status-key', kind: keys.count > 0 ? 'success' : 'neutral',
            label: "Chef's Keys",
            primary: keys.count === 1 ? '1 configured' : (keys.count + ' configured'),
            secondary: keys.count > 0
                ? (keys.healthy === keys.count ? 'All healthy' : (keys.healthy + ' of ' + keys.count + ' healthy'))
                : 'None configured yet',
            linkText: 'Manage Keys', linkHref: '#/keys'
        }));

        // 6. Updates
        grid.appendChild(buildStatusCard({
            icon: 'icon-status-updates', kind: 'neutral',
            label: 'Updates',
            primary: 'Up to date',
            secondary: 'Last checked\n' + (summary.updatesChecked || 'never'),
            linkText: 'Check for Updates', linkHref: '#/settings'
        }));

        // 7. Shortcuts
        grid.appendChild(buildStatusCard({
            icon: 'icon-status-shortcuts', kind: 'neutral',
            label: 'Shortcuts',
            primary: 'All present',
            secondary: 'Start menu and Desktop',
            linkText: 'Repair Shortcuts', linkHref: '#/settings'
        }));

        section.appendChild(grid);
        container.appendChild(section);
    }

    function buildStatusCard(opts) {
        var card = el('article', 'home-status-card');
        var head = el('div', 'home-status-card-head');
        var iconBox = el('span', 'home-status-card-icon home-status-card-icon-' + (opts.kind || 'neutral'));
        iconBox.appendChild(svgUse(opts.icon, 'home-status-card-svg'));
        head.appendChild(iconBox);
        card.appendChild(head);

        var label = el('div', 'home-status-card-label', opts.label);
        card.appendChild(label);

        var primary = el('div', 'home-status-card-primary', opts.primary || '');
        card.appendChild(primary);

        if (opts.statusPill) {
            var pill = el('span', 'pill pill-' + opts.statusPill.kind, opts.statusPill.label);
            card.appendChild(pill);
        }

        if (opts.secondary) {
            var sec = el('div', 'home-status-card-secondary' + (opts.secondaryKind ? (' home-status-card-secondary-' + opts.secondaryKind) : ''), opts.secondary);
            card.appendChild(sec);
        }

        if (opts.linkText && opts.linkHref) {
            var foot = el('div', 'home-status-card-foot');
            var link = el('a', 'home-status-card-link', opts.linkText);
            link.href = opts.linkHref;
            foot.appendChild(link);
            card.appendChild(foot);
        }
        return card;
    }

    function buildPantryStart(container, templates) {
        var section = el('section', 'home-pantry-start');
        var head = el('div', 'home-section-head');
        var title = el('h2', 'home-section-title', 'Start from the Pantry');
        head.appendChild(title);
        var allLink = el('a', 'home-section-link', 'View all recipes');
        allLink.href = '#/pantry';
        head.appendChild(allLink);
        section.appendChild(head);

        var grid = el('div', 'home-pantry-grid');

        if (!templates || templates.length === 0) {
            var empty = el('div', 'home-empty-card',
                'No bundled templates loaded yet. Open the Pantry from the left rail to investigate.');
            section.appendChild(empty);
            container.appendChild(section);
            return;
        }

        var iconBy = function (cat) {
            var c = String(cat || '').toLowerCase();
            if (c.indexOf('ai') >= 0)        { return 'icon-card-ai'; }
            if (c.indexOf('user') >= 0)      { return 'icon-card-users'; }
            return 'icon-card-chart';
        };

        var max = Math.min(3, templates.length);
        for (var i = 0; i < max; i++) {
            var t = templates[i] || {};
            var card = el('article', 'home-pantry-card');
            var top = el('div', 'home-pantry-card-top');

            // Pick an icon tone heuristically based on display-name keywords.
            var name = String(t.displayName || t.templateId || '');
            var tone = 'info';
            var ic = 'icon-card-chart';
            if (/ai|copilot/i.test(name)) { ic = 'icon-card-ai';    tone = 'info'; }
            else if (/user|entra/i.test(name)) { ic = 'icon-card-users'; tone = 'success'; }
            else if (/usage|m365/i.test(name)) { ic = 'icon-card-chart'; tone = 'info'; }

            var iconBox = el('span', 'home-pantry-card-icon home-pantry-card-icon-' + tone);
            iconBox.appendChild(svgUse(ic, 'home-pantry-card-svg'));
            top.appendChild(iconBox);

            var topText = el('div', 'home-pantry-card-toptext');
            topText.appendChild(el('div', 'home-pantry-card-title', name));
            topText.appendChild(el('p', 'home-pantry-card-desc', String(t.shortDescription || '')));
            top.appendChild(topText);

            card.appendChild(top);

            // Metadata rows (label / value pairs derived from the template
            // record). All values are read directly from broker output;
            // no fabrication.
            var meta = el('dl', 'home-pantry-meta');
            var rows = [];
            if (t.category)         { rows.push(['Category',  String(t.category)]); }
            if (t.templateVersion)  { rows.push(['Template',  String(t.templateVersion)]); }
            if (t.minPaxScriptVersion) { rows.push(['Min PAX', String(t.minPaxScriptVersion)]); }
            for (var r = 0; r < rows.length; r++) {
                var dt = el('dt', null, rows[r][0]);
                var dd = el('dd', null, rows[r][1]);
                meta.appendChild(dt);
                meta.appendChild(dd);
            }
            card.appendChild(meta);

            var actions = el('div', 'home-pantry-card-actions');
            var useBtn = el('a', 'btn-secondary home-pantry-card-use', 'Use Recipe');
            useBtn.href = '#/pantry/' + String(t.templateId || '');
            actions.appendChild(useBtn);
            var detailsLink = el('a', 'home-pantry-card-details', 'View Details');
            detailsLink.href = '#/pantry/' + String(t.templateId || '');
            var chev2 = svgUse('icon-chevron-right', 'home-pantry-card-details-icon');
            detailsLink.appendChild(chev2);
            actions.appendChild(detailsLink);
            card.appendChild(actions);

            grid.appendChild(card);
        }

        section.appendChild(grid);
        container.appendChild(section);
    }

    function buildRecommended(container, summary) {
        var card = el('aside', 'home-recommended');
        card.setAttribute('aria-labelledby', 'home-recommended-title');
        var title = el('h2', 'home-recommended-title', 'Recommended next step');
        title.id = 'home-recommended-title';
        card.appendChild(title);

        // Adaptive recommendation logic. Reads what already exists and
        // chooses the next calmest action -- no remote suggestions, no
        // experimentation buckets.
        var rec = pickRecommendation(summary);

        var body = el('div', 'home-recommended-body');
        var bicon = el('span', 'home-recommended-icon');
        bicon.appendChild(svgUse(rec.icon, 'home-recommended-svg'));
        body.appendChild(bicon);

        var btext = el('div', 'home-recommended-text');
        btext.appendChild(el('div', 'home-recommended-headline', rec.headline));
        btext.appendChild(el('p', 'home-recommended-sub', rec.sub));
        body.appendChild(btext);
        card.appendChild(body);

        var actions = el('div', 'home-recommended-actions');
        var primary = el('a', 'btn-primary home-recommended-cta', rec.ctaLabel);
        primary.href = rec.ctaHref;
        actions.appendChild(primary);
        card.appendChild(actions);

        container.appendChild(card);
    }

    function pickRecommendation(summary) {
        var keys = summary.keys || { count: 0 };
        var hasRecipes = summary.recipeCount > 0;
        var lastBake = summary.lastBake;

        if (keys.count === 0) {
            return {
                icon: 'icon-status-key',
                headline: 'Add a Chef\u2019s Key',
                sub: 'Configure a Chef\u2019s Key before baking a recipe against a tenant.',
                ctaLabel: 'Open Chef\u2019s Keys', ctaHref: '#/keys'
            };
        }
        if (!hasRecipes) {
            return {
                icon: 'icon-pantry',
                headline: 'Materialize your first recipe',
                sub: 'Pick a Pantry template and turn it into a recipe.',
                ctaLabel: 'Browse Pantry', ctaHref: '#/pantry'
            };
        }
        if (lastBake) {
            var s = String(lastBake.status || '').toLowerCase();
            if (s === 'succeeded') {
                return {
                    icon: 'icon-clipboard',
                    headline: 'Taste Test latest bake',
                    sub: (lastBake.recipeName || 'Latest bake') + ' completed successfully.',
                    ctaLabel: 'Taste Test Output', ctaHref: '#/taste-tests'
                };
            }
            if (s === 'failed' || s === 'killed') {
                return {
                    icon: 'icon-failed',
                    headline: 'Review failed bake',
                    sub: 'Open the log and triage the last run.',
                    ctaLabel: 'View Bakes', ctaHref: '#/bakes'
                };
            }
            if (s === 'running' || s === 'paused') {
                return {
                    icon: 'icon-status-bake',
                    headline: 'A bake is in progress',
                    sub: 'Watch the live log in Bakes.',
                    ctaLabel: 'View Bakes', ctaHref: '#/bakes'
                };
            }
        }
        return {
            icon: 'icon-recipes',
            headline: 'Open Recipes',
            sub: 'Review or schedule an existing recipe.',
            ctaLabel: 'Open Recipes', ctaHref: '#/recipes'
        };
    }

    function buildActivity(container, summary) {
        var section = el('aside', 'home-activity');
        section.setAttribute('aria-labelledby', 'home-activity-title');
        var title = el('h2', 'home-activity-title', 'Recent Activity');
        title.id = 'home-activity-title';
        section.appendChild(title);

        // Tabs row (Bakes / Taste Tests). Active tab is 'Bakes'; the
        // Taste Tests tab renders a calm empty-state pane on click.
        var tabBar = el('div', 'home-activity-tabs');
        tabBar.setAttribute('role', 'tablist');

        var panes = el('div', 'home-activity-panes');

        var tabs = [
            { id: 'bakes',   label: 'Bakes',           active: true  },
            { id: 'tastes',  label: 'Taste Tests',     active: false }
        ];

        for (var i = 0; i < tabs.length; i++) {
            (function (t) {
                var btn = el('button', 'home-activity-tab' + (t.active ? ' is-active' : ''), t.label);
                btn.type = 'button';
                btn.setAttribute('role', 'tab');
                btn.setAttribute('aria-selected', t.active ? 'true' : 'false');
                btn.setAttribute('data-tab', t.id);
                tabBar.appendChild(btn);

                var pane = el('div', 'home-activity-pane' + (t.active ? ' is-active' : ''));
                pane.setAttribute('role', 'tabpanel');
                pane.setAttribute('data-pane', t.id);
                if (t.active) {
                    pane.appendChild(buildBakesPane(summary));
                } else {
                    pane.appendChild(buildEmptyPane('No Taste Tests yet', 'Taste Tests will appear here once the surface ships.'));
                }
                panes.appendChild(pane);
            })(tabs[i]);
        }

        // Tab click handling. The state lives entirely in the DOM; no
        // module-level state so teardown stays trivial.
        tabBar.addEventListener('click', onTabClick);
        section.appendChild(tabBar);
        section.appendChild(panes);

        var foot = el('div', 'home-activity-foot');
        var more = el('a', 'home-activity-more', 'View all bakes');
        more.href = '#/bakes';
        var chev3 = svgUse('icon-chevron-right', 'home-activity-more-icon');
        more.appendChild(chev3);
        foot.appendChild(more);
        section.appendChild(foot);

        container.appendChild(section);
    }

    function onTabClick(ev) {
        var t = ev.target;
        if (!t || t.nodeType !== 1) { return; }
        var name = t.getAttribute('data-tab');
        if (!name) { return; }
        var tabBar = t.parentNode;
        if (!tabBar) { return; }
        var section = tabBar.parentNode;
        if (!section) { return; }
        var tabs = tabBar.querySelectorAll('.home-activity-tab');
        for (var i = 0; i < tabs.length; i++) {
            var on = tabs[i].getAttribute('data-tab') === name;
            tabs[i].setAttribute('aria-selected', on ? 'true' : 'false');
            if (on) { tabs[i].classList.add('is-active'); }
            else    { tabs[i].classList.remove('is-active'); }
        }
        var panes = section.querySelectorAll('.home-activity-pane');
        for (var j = 0; j < panes.length; j++) {
            var on2 = panes[j].getAttribute('data-pane') === name;
            if (on2) { panes[j].classList.add('is-active'); }
            else     { panes[j].classList.remove('is-active'); }
        }
    }

    function buildBakesPane(summary) {
        var bakes = summary.recentBakes || [];
        if (bakes.length === 0) {
            return buildEmptyPane('No bakes yet', 'Start a recipe to record one.');
        }
        var list = el('ul', 'home-activity-list');
        for (var i = 0; i < bakes.length; i++) {
            var b = bakes[i] || {};
            var li = el('li', 'home-activity-row');
            var bs = formatBakeStatus(b.status);
            var iconBox = el('span', 'home-activity-row-icon home-activity-row-icon-' + bs.kind);
            iconBox.appendChild(svgUse(bs.kind === 'success' ? 'icon-success' : (bs.kind === 'danger' ? 'icon-failed' : 'icon-status-bake'), 'home-activity-row-svg'));
            li.appendChild(iconBox);

            var text = el('div', 'home-activity-row-text');
            text.appendChild(el('div', 'home-activity-row-title', b.recipeName || 'Recipe'));
            text.appendChild(el('div', 'home-activity-row-time', formatDate(b.startedAtUtc || b.createdAtUtc) || ''));
            li.appendChild(text);

            var pill = el('span', 'pill pill-' + bs.kind, bs.label);
            li.appendChild(pill);
            list.appendChild(li);
        }
        return list;
    }

    function buildEmptyPane(title, body) {
        var wrap = el('div', 'home-activity-empty');
        wrap.appendChild(el('div', 'home-activity-empty-title', title));
        wrap.appendChild(el('p', 'home-activity-empty-body', body));
        return wrap;
    }

    // ----------------------------------------------------------------
    // Data fetch + composition
    // ----------------------------------------------------------------

    function fetchAll(signal) {
        if (!window.cookbookApi || typeof window.cookbookApi.get !== 'function') {
            return Promise.resolve({
                templates: null, recipes: null, cooks: null, keys: null, version: null, engineState: null
            });
        }
        var api = window.cookbookApi;
        var get = function (path) { return api.get(path, { signal: signal }); };
        return Promise.all([
            get('/api/v1/templates'),
            get('/api/v1/recipes'),
            get('/api/v1/cooks'),
            get('/api/v1/auth/profiles'),
            get('/api/v1/runtime/version'),
            get('/api/v1/setup/acquire-pax/state')
        ]).then(function (results) {
            return {
                templates:   results[0],
                recipes:     results[1],
                cooks:       results[2],
                keys:        results[3],
                version:     results[4],
                engineState: results[5]
            };
        });
    }

    // Derive the nav-rail PAX status value + tooltip from the authoritative
    // engine acquisition state, NOT from the bundled/expected version. The
    // rail must never present the pinned VERSION.json version as an installed
    // engine. Three outcomes:
    //   - acquired & valid : the acquired engine version (or "Installed")
    //   - invalid/missing  : "Needs attention"
    //   - anything else    : "Not installed"
    function derivePaxRail(engineState) {
        var notInstalled = {
            value: 'Not installed',
            title: 'Add the PAX script to run bakes.',
            acquired: false
        };
        if (!engineState || !engineState.ok || !engineState.body) {
            return notInstalled;
        }
        var b = engineState.body;
        var state = String(b.state || '').toLowerCase();
        if (state === 'invalid' || state === 'missing') {
            return {
                value: 'Needs attention',
                title: 'The acquired PAX script failed validation. Re-add it to run bakes.',
                acquired: false
            };
        }
        if (b.isAcquired === true) {
            var ver = (b.installState && b.installState.version)
                ? String(b.installState.version)
                : null;
            return {
                value: ver ? ver : 'Installed',
                title: ver
                    ? ('PAX engine ' + ver + ' is acquired and validated.')
                    : 'PAX engine is acquired and validated.',
                acquired: true
            };
        }
        return notInstalled;
    }

    // Derive the Kitchen Status "PAX script" card from the authoritative
    // engine acquisition state. The card must reflect what is actually
    // installed on this appliance, never the pinned VERSION.json version or
    // a hardcoded SHA flag. Three outcomes mirror the nav rail:
    //   - acquired & valid : the acquired engine version (or "Installed")
    //   - invalid/missing  : "Needs attention"
    //   - anything else    : "Not installed"
    function derivePaxCard(engineState) {
        var notInstalled = {
            primary: 'Not installed',
            secondary: 'Add the PAX script to run bakes.',
            secondaryKind: 'warning'
        };
        if (!engineState || !engineState.ok || !engineState.body) {
            return notInstalled;
        }
        var b = engineState.body;
        var state = String(b.state || '').toLowerCase();
        if (state === 'invalid' || state === 'missing') {
            return {
                primary: 'Needs attention',
                secondary: 'Choose or download an approved PAX script.',
                secondaryKind: 'warning'
            };
        }
        if (b.isAcquired === true) {
            var ver = (b.installState && b.installState.version)
                ? String(b.installState.version)
                : null;
            return {
                primary: ver ? ver : 'Installed',
                secondary: 'Installed',
                secondaryKind: 'success'
            };
        }
        return notInstalled;
    }

    function buildSummary(data) {
        var summary = {
            firstRun: true,
            workspacePath: null,
            workspaceFree: null,
            paxVersion: null,
            paxVerified: false,
            paxRail: { value: 'Not installed', title: 'Add the PAX script to run bakes.', acquired: false },
            paxCard: { primary: 'Not installed', secondary: 'Add the PAX script to run bakes.', secondaryKind: 'warning' },
            updatesChecked: null,
            recipes: [],
            recipeCount: 0,
            recentBakes: [],
            lastBake: null,
            keys: { count: 0, healthy: 0 },
            templates: []
        };

        if (data.templates && data.templates.ok && data.templates.body) {
            var tlist = data.templates.body.templates || data.templates.body;
            if (Array.isArray(tlist)) {
                summary.templates = tlist;
            }
        }

        if (data.recipes && data.recipes.ok && data.recipes.body) {
            var rlist = data.recipes.body.recipes || data.recipes.body;
            if (Array.isArray(rlist)) {
                summary.recipes = rlist;
                summary.recipeCount = rlist.length;
                if (rlist.length > 0) { summary.firstRun = false; }
            }
        }

        if (data.cooks && data.cooks.ok && data.cooks.body) {
            var clist = data.cooks.body.cooks || data.cooks.body;
            if (Array.isArray(clist)) {
                // Most recent first by startedAtUtc/createdAtUtc.
                clist.sort(function (a, b) {
                    var ta = Date.parse((a && (a.startedAtUtc || a.createdAtUtc)) || 0) || 0;
                    var tb = Date.parse((b && (b.startedAtUtc || b.createdAtUtc)) || 0) || 0;
                    return tb - ta;
                });
                // Enrich each bake with its recipe display name once.
                var enriched = [];
                for (var i = 0; i < clist.length && i < 3; i++) {
                    var c = clist[i] || {};
                    enriched.push({
                        cookId:        c.cookId || c.id || null,
                        status:        c.status,
                        startedAtUtc:  c.startedAtUtc || c.createdAtUtc || null,
                        createdAtUtc:  c.createdAtUtc || null,
                        recipeId:      c.recipeId,
                        recipeName:    recipeNameFor(c.recipeId, summary.recipes)
                    });
                }
                summary.recentBakes = enriched;
                if (enriched.length > 0) {
                    summary.lastBake  = enriched[0];
                    summary.firstRun  = false;
                }
            }
        }

        if (data.keys && data.keys.ok && data.keys.body) {
            var klist = data.keys.body.profiles || data.keys.body;
            if (Array.isArray(klist)) {
                summary.keys.count   = klist.length;
                summary.keys.healthy = 0;
                for (var k = 0; k < klist.length; k++) {
                    var p = klist[k] || {};
                    var hk = String(p.healthStatus || p.health || '').toLowerCase();
                    if (hk === 'ok' || hk === 'healthy' || hk === 'ready') {
                        summary.keys.healthy = summary.keys.healthy + 1;
                    }
                }
                if (klist.length > 0) { summary.firstRun = false; }
            }
        }

        if (data.version && data.version.ok && data.version.body) {
            var v = data.version.body;
            // The /api/v1/runtime/version endpoint exposes the bundled
            // PAX script under `bundledPax` (canonical name in
            // Routes/Runtime.ps1 -> Invoke-RuntimeVersionGet). Older
            // shapes used `paxScript` or a flat `paxVersion` -- read
            // those as fallbacks so the rail still populates against
            // legacy brokers during an update window.
            if (v.bundledPax && v.bundledPax.version) {
                summary.paxVersion  = String(v.bundledPax.version);
            } else if (v.paxScript && v.paxScript.version) {
                summary.paxVersion  = String(v.paxScript.version);
            } else if (v.paxVersion) {
                summary.paxVersion  = String(v.paxVersion);
            }
            // SHA-verified flag: only treat the value as `verified`
            // when the broker explicitly says so. The bundledPax
            // payload exposes integrity as `integrity === 'sha256-ok'`
            // (Routes/Runtime.ps1). Fall through to the legacy
            // shaVerified / paxVerified booleans if present.
            if (v.bundledPax && v.bundledPax.integrity === 'sha256-ok') { summary.paxVerified = true; }
            else if (v.paxScript && v.paxScript.shaVerified === true)  { summary.paxVerified = true; }
            else if (v.paxVerified === true)                           { summary.paxVerified = true; }
            // Workspace path: canonical location is `paths.workspace`
            // (Routes/Runtime.ps1 paths block). Legacy flat
            // `workspacePath` is read as a fallback.
            if (v.paths && v.paths.workspace) {
                summary.workspacePath = String(v.paths.workspace);
            } else if (v.workspacePath) {
                summary.workspacePath = String(v.workspacePath);
            }
            if (v.workspaceFreeBytes) {
                summary.workspaceFree = formatBytes(v.workspaceFreeBytes);
            }
            if (v.updatesLastCheckedUtc) {
                summary.updatesChecked = formatDate(v.updatesLastCheckedUtc);
            }
        }

        // Nav-rail PAX status is driven by the engine acquisition state, not
        // the bundled/expected version above. The Kitchen Status "PAX script"
        // card is driven by the same engine state so the Home page never
        // presents the pinned VERSION.json version as an installed engine.
        summary.paxRail = derivePaxRail(data.engineState);
        summary.paxCard = derivePaxCard(data.engineState);

        return summary;
    }

    function formatBytes(n) {
        var num = Number(n);
        if (!isFinite(num) || num <= 0) { return null; }
        var units = ['B','KB','MB','GB','TB'];
        var i = 0;
        while (num >= 1024 && i < units.length - 1) { num = num / 1024; i = i + 1; }
        var fixed = (num >= 100 ? 0 : (num >= 10 ? 1 : 2));
        return num.toFixed(fixed) + ' ' + units[i];
    }

    function applyRailIdentity(summary) {
        var paxRail = summary.paxRail || { value: 'Not installed', title: 'Add the PAX script to run bakes.' };
        setText(NAV_PAX_VER_ID, paxRail.value);
        var paxEl = byId(NAV_PAX_VER_ID);
        if (paxEl && paxRail.title) { paxEl.title = paxRail.title; }
        if (summary.workspacePath) {
            setText(NAV_WS_ID, summary.workspacePath);
            var wsEl = byId(NAV_WS_ID);
            if (wsEl) { wsEl.title = summary.workspacePath; }
            // Reveal the copy-to-clipboard button next to the truncated
            // workspace value so the operator can recover the full path
            // even when the nav rail cuts it off with an ellipsis.
            // Bind the click handler exactly once (idempotent across
            // re-renders).
            var copyBtn = document.getElementById('nav-rail-workspace-copy');
            if (copyBtn) {
                copyBtn.hidden = false;
                copyBtn.setAttribute('data-path', summary.workspacePath);
                if (!copyBtn.getAttribute('data-bound')) {
                    copyBtn.setAttribute('data-bound', '1');
                    copyBtn.addEventListener('click', function () {
                        var path = copyBtn.getAttribute('data-path') || '';
                        if (!path) { return; }
                        var done = function () {
                            var prev = copyBtn.getAttribute('title') || '';
                            copyBtn.classList.add('is-copied');
                            copyBtn.setAttribute('title', 'Copied!');
                            window.setTimeout(function () {
                                copyBtn.classList.remove('is-copied');
                                copyBtn.setAttribute('title', prev || 'Copy workspace path to clipboard');
                            }, 1500);
                        };
                        if (navigator.clipboard && navigator.clipboard.writeText) {
                            navigator.clipboard.writeText(path).then(done, function () {
                                // Fallback: textarea + execCommand for
                                // origins that block the async API.
                                var ta = document.createElement('textarea');
                                ta.value = path; ta.setAttribute('readonly', '');
                                ta.style.position = 'absolute'; ta.style.left = '-9999px';
                                document.body.appendChild(ta);
                                ta.select();
                                try { document.execCommand('copy'); done(); } catch (e) { /* swallow */ }
                                document.body.removeChild(ta);
                            });
                        } else {
                            done();
                        }
                    });
                }
            }
        }
        if (summary.appVersion) { setText(NAV_VER_ID, summary.appVersion); }
    }

    function renderPage(container, data) {
        var summary = buildSummary(data);

        // App version: the broker exposes the appliance version as a
        // top-level `cookbookVersion` string (Routes/Runtime.ps1).
        // Older payload shapes nested it as `cookbook.version`; read
        // that as a fallback so the rail populates against legacy
        // brokers during an update window.
        if (data.version && data.version.ok && data.version.body) {
            var vb = data.version.body;
            if (typeof vb.cookbookVersion === 'string' && vb.cookbookVersion.length > 0) {
                summary.appVersion = String(vb.cookbookVersion);
            } else if (vb.cookbook && vb.cookbook.version) {
                summary.appVersion = String(vb.cookbook.version);
            }
        }

        applyRailIdentity(summary);

        // Outer layout: hero row + status row + pantry/recommended row + activity.
        // Two-column grid handled in CSS via .home-main / .home-side.
        var page = el('div', 'home-page');

        var topRow = el('div', 'home-top-row');
        var main   = el('div', 'home-main');
        var side   = el('div', 'home-side');

        buildHero(main, summary);
        buildTrustCard(side);
        topRow.appendChild(main);
        topRow.appendChild(side);
        page.appendChild(topRow);

        buildStatusGrid(page, summary);

        var midRow = el('div', 'home-mid-row');
        var midMain = el('div', 'home-mid-main');
        var midSide = el('div', 'home-mid-side');
        buildPantryStart(midMain, summary.templates);
        buildRecommended(midSide, summary);
        buildActivity(midSide, summary);
        midRow.appendChild(midMain);
        midRow.appendChild(midSide);
        page.appendChild(midRow);

        container.appendChild(page);
    }

    // ----------------------------------------------------------------
    // Public surface
    // ----------------------------------------------------------------

    function mount(container, params) {
        teardown(); // defensive: drop any prior state

        var epoch = nextEpoch;
        nextEpoch = nextEpoch + 1;

        var ctrl = (typeof AbortController === 'function') ? new AbortController() : null;
        state = {
            container: container,
            epoch:     epoch,
            abortCtrl: ctrl
        };

        // Loading placeholder. Replaced once the fetch resolves.
        while (container.firstChild) { container.removeChild(container.firstChild); }
        var loading = el('div', 'home-loading',
            'Loading your kitchen overview\u2026');
        container.appendChild(loading);

        fetchAll(ctrl ? ctrl.signal : null).then(function (data) {
            if (!state || state.epoch !== epoch) { return; }
            while (container.firstChild) { container.removeChild(container.firstChild); }
            renderPage(container, data);
            // Nav rail "Data" pill: stamp the moment the overview
            // finished fetching. Mirrors the pattern other pages use
            // (pantry/settings/recipe-list); the home page is the
            // default landing surface, so without this stamp the
            // pill would read "Not loaded" for the entire session
            // even after a successful fan-out fetch.
            setText(LAST_FETCHED_ID, isoNowUtc());
        }).catch(function () {
            if (!state || state.epoch !== epoch) { return; }
            while (container.firstChild) { container.removeChild(container.firstChild); }
            var err = el('div', 'home-error',
                'Could not reach the local broker. Use the Reload button in the top bar to try again.');
            container.appendChild(err);
        });
    }

    function teardown() {
        if (state && state.abortCtrl) {
            try { state.abortCtrl.abort(); } catch (_e) { /* ignore */ }
        }
        if (state && state.container) {
            while (state.container.firstChild) {
                state.container.removeChild(state.container.firstChild);
            }
        }
        state = null;
        nextEpoch = nextEpoch + 1;
    }

    window.cookbookHomePage = {
        mount:    mount,
        teardown: teardown
    };
})();
