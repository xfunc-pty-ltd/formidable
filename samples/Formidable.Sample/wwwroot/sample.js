// Sample-local helper: scrolls the virtualized panel so Virtualize renders the target row.
window.formidableSample = {
    scrollPanelTo: function (selector, top) {
        const panel = document.querySelector(selector);
        if (panel) {
            panel.scrollTop = top;
        }
    },
    // The chosen culture has to outlive the reload that applies it, so it lives in
    // localStorage rather than in component state.
    getCulture: function () {
        return localStorage.getItem('formidable.culture');
    },
    setCulture: function (culture) {
        localStorage.setItem('formidable.culture', culture);
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
