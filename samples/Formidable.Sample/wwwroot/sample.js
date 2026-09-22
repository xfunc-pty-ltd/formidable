// Sample-local helper: scrolls the virtualized panel so Virtualize renders the target row.
window.formidableSample = {
    scrollPanelTo: function (selector, top) {
        const panel = document.querySelector(selector);
        if (panel) {
            panel.scrollTop = top;
        }
    }
};
