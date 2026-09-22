// Sample-local helper: scrolls the virtualized panel so Virtualize renders the target row.
window.formidableSample = {
    scrollPanelTo: function (selector, top) {
        const panel = document.querySelector(selector);
        if (panel) {
            panel.scrollTop = top;
        }
    },
    // The chosen culture has to outlive the reload that applies it, so it lives in
    // localStorage rather than in component state; FormidableCultureBootstrap reads it back
    // through the library's own JS module, so this object only needs to write it.
    setCulture: function (culture) {
        localStorage.setItem('formidable.culture', culture);
    },
    // A dialog saying aria-modal="true" promises assistive technology that nothing outside it
    // can be reached, and only a key handler can keep that promise: CSS can paint the page
    // behind an overlay but cannot take it out of the tab order, and neither can markup. Tab off
    // either end of the panel comes back to the other end. The focusables are read at each press
    // rather than once, since what the panel holds changes while it is open. Nothing here can
    // intercept a PROGRAMMATIC focus move - focus() raises no key event - so a form that focuses
    // a field behind the overlay still does, which is the failure the page exists to show.
    trapTabWithin: panel => {
        panel.addEventListener('keydown', event => {
            if (event.key !== 'Tab') { return; }

            const focusable = panel.querySelectorAll(
                'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])');
            if (focusable.length === 0) { return; }

            const first = focusable[0];
            const last = focusable[focusable.length - 1];
            // The panel itself is the third case: it holds focus from the moment it opens, and a
            // backwards Tab from there would leave without ever reaching an entry.
            if (event.shiftKey && (document.activeElement === first || document.activeElement === panel)) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first.focus();
            }
        });
    },
    // matchMedia returns a NEW MediaQueryList per call, so the watcher keeps the instance it
    // registered on - removeEventListener needs the same object back to unregister it.
    _bsWatches: {},
    watchBsTheme: id => {
        const mq = window.matchMedia('(prefers-color-scheme: dark)');
        const apply = () => {
            const el = document.getElementById(id);
            if (el) { el.setAttribute('data-bs-theme', mq.matches ? 'dark' : 'light'); }
        };
        window.formidableSample._bsWatches[id] = { mq, apply };
        mq.addEventListener('change', apply);
        apply();
    },
    unwatchBsTheme: id => {
        const w = window.formidableSample._bsWatches[id];
        if (w) { w.mq.removeEventListener('change', w.apply); delete window.formidableSample._bsWatches[id]; }
    }
};
