export function focusField(id) {
    const element = document.getElementById(id);
    if (!element) {
        return false;
    }

    element.scrollIntoView({ behavior: "smooth", block: "center" });
    element.focus({ preventScroll: true });
    return true;
}

export function syncValue(id, value) {
    const element = document.getElementById(id);
    if (element) {
        element.value = value ?? "";
    }
}
