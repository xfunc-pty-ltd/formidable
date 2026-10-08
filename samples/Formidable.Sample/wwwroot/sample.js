// Sample-local helper: scrolls the virtualized panel so Virtualize renders the target row.
window.formidableSample = {
    // "instant" rather than the panel's own scroll-behavior: the fallback's whole job is to have
    // the row rendered by the time the retried focus arrives a fixed delay later, and an animated
    // jump would still be travelling. The focus that follows it is what moves smoothly.
    scrollPanelTo: function (selector, top) {
        const panel = document.querySelector(selector);
        if (panel) {
            panel.scrollTo({ top: top, behavior: 'instant' });
        }
    },
    // The chosen culture has to outlive the reload that applies it, so it lives in
    // localStorage rather than in component state. Reading it back belongs here too: where a
    // preference is kept is the app's business, not a validation library's.
    getCulture: function () {
        try {
            return localStorage.getItem('formidable.culture');
        } catch {
            // A browser configured to block site data throws on the access itself, and this read
            // runs before the host does. Letting it out would cost the whole app over a language
            // preference, and nothing stored reads exactly like nothing chosen.
            return null;
        }
    },
    // The write is deliberately left bare. A throw here stops the switch before the reload, which
    // is the honest outcome for a choice that could not be saved; swallowing it would reload into
    // the old language with nothing said.
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
    },
    // A script added once the app is running runs when its download finishes, and a component
    // that calls into it on its first render can get there first. This adds the script to the
    // head and returns a promise that settles once it has run. A later call for the same script
    // returns the same promise, so the script runs once however often a page asks for it: a
    // second run would replace the objects the first run set up.
    _scripts: {},
    loadScript: src => {
        const scripts = window.formidableSample._scripts;
        if (!scripts[src]) {
            scripts[src] = new Promise((resolve, reject) => {
                const script = document.createElement('script');
                script.src = src;
                script.onload = () => resolve();
                script.onerror = () => {
                    delete scripts[src];
                    script.remove();
                    reject(new Error('Could not load ' + src));
                };
                document.head.appendChild(script);
            });
        }
        return scripts[src];
    },
    // A stylesheet added once the app is running applies when its download finishes, and the
    // page draws without it until then. This adds the stylesheet's link to the head and returns
    // a promise that settles once it has loaded, so a page can hold back what would draw wrongly
    // without it. The link goes straight after the sample's own stylesheet, ahead of anything a
    // page's HeadContent adds, so a page's own rules still come after the library's. A link that
    // fails to load is taken out again, so the next call asks for the file afresh.
    loadStylesheet: href => new Promise((resolve, reject) => {
        const link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = href;
        link.onload = () => resolve();
        link.onerror = () => {
            link.remove();
            reject(new Error('Could not load ' + href));
        };
        document.head.querySelector('link[rel="stylesheet"]').after(link);
    }),
    // The page that loaded a stylesheet takes it out as it closes, so the next page keeps its
    // own look. A link still loading goes too, and its promise never settles: the page that
    // asked has gone.
    unloadStylesheet: href => {
        for (const link of document.head.querySelectorAll('link[rel="stylesheet"]')) {
            if (link.getAttribute('href') === href) {
                link.remove();
            }
        }
    }
};
