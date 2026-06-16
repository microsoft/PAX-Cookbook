// PAX Cookbook -- Taste Tests page (#/taste-tests).
//
// Placeholder surface. The full Taste Test workflow (output validation,
// schema check, sample preview) is deferred to a later UX slice. This
// module exists so that the nav-rail 'Taste Tests' item resolves to a
// real, calm page instead of landing on an unknown-route fallback.
//
// No data fetches, no broker interaction, no scheduled work.
(function () {
    'use strict';

    var container = null;

    function el(tag, className, text) {
        var n = document.createElement(tag);
        if (className) { n.className = className; }
        if (text !== undefined && text !== null) { n.textContent = String(text); }
        return n;
    }

    function mount(c, params) {
        teardown();
        container = c;

        var page = el('section', 'page taste-tests-page');

        var header = el('header', 'page-header');
        var headText = el('div', 'page-header-text');
        headText.appendChild(el('h1', 'page-title', 'Taste Tests'));
        headText.appendChild(el('p', 'page-lede',
            'Inspect bake output, compare runs, and confirm an analytics-ready deliverable before handing it off.'));
        header.appendChild(headText);
        page.appendChild(header);

        var empty = el('div', 'taste-tests-empty');
        empty.appendChild(el('div', 'taste-tests-empty-title', 'Taste Tests arrive in a later release'));
        empty.appendChild(el('p', 'taste-tests-empty-body',
            'This surface is reserved for output validation, sample preview, and run-to-run comparison. It will light up once the Taste Test engine ships.'));
        var actions = el('div', 'taste-tests-empty-actions');
        var bakeLink = el('a', 'btn-secondary', 'View Bakes');
        bakeLink.href = '#/bakes';
        actions.appendChild(bakeLink);
        empty.appendChild(actions);
        page.appendChild(empty);

        container.appendChild(page);
    }

    function teardown() {
        if (container) {
            while (container.firstChild) { container.removeChild(container.firstChild); }
        }
        container = null;
    }

    window.cookbookTasteTestsPage = {
        mount:    mount,
        teardown: teardown
    };
})();
