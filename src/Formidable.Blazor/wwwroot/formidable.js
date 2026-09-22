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
