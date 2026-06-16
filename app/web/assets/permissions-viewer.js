// PAX Cookbook -- Required Permissions viewer (V1.S19).
//
// Renders the resolved permission/prerequisite list as a compact-card
// section. Caller passes in a container element and either a full
// recipe object or a flat options object; the viewer hands it to
// window.PaxPermissions.resolve() and replaces the container's content.
//
// The viewer NEVER reads the DOM beyond the container it owns. It does
// NOT participate in extractPayloadFromDom and does NOT modify the
// recipe payload sent to the broker. It is a transparency view only.
//
// Public surface:
//   window.PaxPermissions.viewer.render(containerEl, recipe[, options])
//     - Renders the section header, the help-hook button (data-help-topic
//       = 'permissions.required'), and one card per resolved entry,
//       grouped by type in catalog.typeOrder order.
//     - options.compact (default false): omit the section header
//       (caller already supplies one). Used by recipe-list/detail rows.
//     - options.helpTopic (default 'permissions.required'): override
//       the help topic id.
(function () {
    'use strict';

    if (!window.PaxPermissions ||
        !window.PaxPermissions.catalog ||
        typeof window.PaxPermissions.resolve !== 'function') {
        return;
    }

    var TYPE_ORDER       = window.PaxPermissions.typeOrder;
    var TYPE_GROUP_LABEL = window.PaxPermissions.typeGroupLabels;

    function makeEl(tag, className, text) {
        var el = document.createElement(tag);
        if (className) { el.className = className; }
        if (text != null) { el.textContent = String(text); }
        return el;
    }

    function renderCard(entry) {
        var card = makeEl('div', 'permission-card permission-card--' + entry.type);
        card.setAttribute('data-permission-id', entry.id);
        card.setAttribute('data-permission-type', entry.type);

        var header = makeEl('div', 'permission-card-header');
        header.appendChild(makeEl('span', 'permission-card-name', entry.name));
        var badge = makeEl('span', 'permission-card-badge permission-card-badge--' + entry.type, entry.badgeLabel);
        header.appendChild(badge);
        card.appendChild(header);

        if (entry.requiredBecause) {
            card.appendChild(makeEl('div', 'permission-card-reason', entry.requiredBecause));
        }

        if (entry.triggeredBy && entry.triggeredBy.length > 0) {
            var trig = makeEl('div', 'permission-card-triggered');
            trig.appendChild(makeEl('span', 'permission-card-triggered-label', 'Triggered by: '));
            for (var i = 0; i < entry.triggeredBy.length; i += 1) {
                if (i > 0) {
                    trig.appendChild(document.createTextNode(', '));
                }
                trig.appendChild(makeEl('span', 'permission-card-triggered-item', entry.triggeredBy[i]));
            }
            card.appendChild(trig);
        }

        return card;
    }

    function renderGroup(typeId, entries) {
        var group = makeEl('div', 'permission-group permission-group--' + typeId);
        var label = TYPE_GROUP_LABEL[typeId] || typeId;
        group.appendChild(makeEl('div', 'permission-group-label', label));
        var grid = makeEl('div', 'permission-group-grid');
        for (var i = 0; i < entries.length; i += 1) {
            grid.appendChild(renderCard(entries[i]));
        }
        group.appendChild(grid);
        return group;
    }

    function renderHeader(helpTopic) {
        var header = makeEl('div', 'permissions-section-header');
        var title = makeEl('h3', 'permissions-section-title', 'Required permissions');
        header.appendChild(title);

        var help = document.createElement('button');
        help.type = 'button';
        help.className = 'help-hook permissions-section-help';
        help.setAttribute('data-help-topic', helpTopic || 'permissions.required');
        help.setAttribute('aria-label', 'Open help: Required permissions');
        help.textContent = '?';
        header.appendChild(help);

        var blurb = makeEl(
            'p',
            'permissions-section-blurb',
            'These are the Graph permissions, runtimes, and environment ' +
            'prerequisites this recipe needs to cook successfully. ' +
            'The list updates as you change recipe options.'
        );
        header.appendChild(blurb);

        return header;
    }

    function render(container, recipe, options) {
        if (!container) { return; }
        var opts = options || {};
        var helpTopic = opts.helpTopic || 'permissions.required';

        // Build replacement content into a fragment, then atomically
        // swap into the container. No mid-render flash, no partial DOM.
        var frag = document.createDocumentFragment();

        if (!opts.compact) {
            frag.appendChild(renderHeader(helpTopic));
        }

        var entries = window.PaxPermissions.resolve(recipe);

        if (entries.length === 0) {
            frag.appendChild(makeEl(
                'p',
                'permissions-section-empty',
                'No requirements resolved. This usually means the recipe is missing required fields.'
            ));
            container.replaceChildren(frag);
            return;
        }

        // Group by catalog.typeOrder; within each group preserve the
        // resolver's insertion order.
        var byType = {};
        for (var i = 0; i < entries.length; i += 1) {
            var t = entries[i].type;
            if (!byType[t]) { byType[t] = []; }
            byType[t].push(entries[i]);
        }
        for (var k = 0; k < TYPE_ORDER.length; k += 1) {
            var typeId = TYPE_ORDER[k];
            if (byType[typeId] && byType[typeId].length > 0) {
                frag.appendChild(renderGroup(typeId, byType[typeId]));
            }
        }

        container.replaceChildren(frag);
    }

    window.PaxPermissions = window.PaxPermissions || {};
    window.PaxPermissions.viewer = { render: render };
})();
