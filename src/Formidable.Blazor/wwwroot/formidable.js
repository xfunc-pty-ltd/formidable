export function focusField(id, scrollId) {
    const element = document.getElementById(id);
    if (!element) {
        return false;
    }

    const scrollTarget = (scrollId && document.getElementById(scrollId)) || element;

    // Centring works for an input, but a tall container's centre is the middle of its
    // contents, which is nowhere near the message that was clicked. 60% is comfortably above
    // an ordinary field wrapper and comfortably below a container spanning the viewport, so it
    // separates the two without being sensitive to small layout changes.
    const tall = scrollTarget.getBoundingClientRect().height > window.innerHeight * 0.6;
    scrollTarget.scrollIntoView({ behavior: "smooth", block: tall ? "start" : "center" });
    element.focus({ preventScroll: true });
    return true;
}

export function syncValue(id, value) {
    const element = document.getElementById(id);
    if (element) {
        element.value = value ?? "";
    }
}

export function orderFields(ids) {
    const found = [];
    for (const id of ids) {
        const element = document.getElementById(id);
        if (element) {
            found.push({ id, element });
        }
    }

    found.sort((a, b) => {
        const position = a.element.compareDocumentPosition(b.element);
        if (position & Node.DOCUMENT_POSITION_FOLLOWING) {
            return -1;
        }
        if (position & Node.DOCUMENT_POSITION_PRECEDING) {
            return 1;
        }
        return 0;
    });

    return found.map(entry => entry.id);
}

export function getStoredCulture(key) {
    return window.localStorage.getItem(key);
}

// One entry per observed form, keyed by the form element's id: the id is what the caller holds
// and what survives the element itself being torn down, so a form that has already left the page
// can still be forgotten.
const layoutObservers = new Map();

export function observeLayout(formId, dotNetRef) {
    const form = document.getElementById(formId);
    if (!form) {
        return;
    }

    // Re-observing a form replaces whatever was watching it: a form rebuilt over a new model
    // renders a fresh element under the same id, and the observer left on the old one would
    // otherwise watch a detached node forever.
    disconnectLayoutObserver(formId);

    let frame = 0;
    const observer = new MutationObserver(() => {
        // One notification per frame. Moving a single field can produce a burst of childList
        // records, and each one taken on its own would cost an interop round trip and a fresh
        // order resolve. This cannot feed itself: an observer reports DOM changes, and resolving
        // an order only reads where elements sit; it writes nothing back. So the render the
        // notification provokes either changes the DOM because the order really did change, which
        // settles on the next pass, or changes nothing and produces no records at all.
        if (frame) {
            return;
        }

        frame = requestAnimationFrame(() => {
            frame = 0;
            // A form torn down between this frame being scheduled and its callback running has
            // already released the reference it would report to, and the page it would have
            // re-ordered is gone with it.
            dotNetRef.invokeMethodAsync("NotifyLayoutMoved").catch(() => { });
        });
    });

    observer.observe(form, { childList: true, subtree: true });
    layoutObservers.set(formId, { observer, frame: () => frame });
}

export function disconnectLayoutObserver(formId) {
    const entry = layoutObservers.get(formId);
    if (!entry) {
        return;
    }

    const pending = entry.frame();
    if (pending) {
        cancelAnimationFrame(pending);
    }

    entry.observer.disconnect();
    layoutObservers.delete(formId);
}
