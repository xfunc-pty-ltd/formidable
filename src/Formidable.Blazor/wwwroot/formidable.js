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
    // "auto" hands the motion to each scrolling box's own scroll-behavior, which is where a
    // visitor's prefers-reduced-motion can reach it. Naming "smooth" here would animate the
    // scroll whatever the page and the visitor asked for, and how a page moves is styling,
    // which this library does not ship.
    scrollTarget.scrollIntoView({ behavior: "auto", block: tall ? "start" : "center" });
    element.focus({ preventScroll: true });
    // Whether the element took focus, not merely whether it was found. An element can be on the
    // page and still refuse focus, and that field is the whole reason the caller has a recovery
    // path: a fallback that makes the target reachable and retries, or a diagnostic when none is
    // wired. Reporting "found" as "focused" is what leaves that path unreachable, so the answer is
    // read back from the document rather than assumed from the call.
    return document.activeElement === element;
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

// One entry per registered Formidable root, keyed by the id its owner holds: the model-level
// field id, which is deterministic and survives the element itself being replaced, so a root
// rebuilt over a new model can be forgotten by the same key that registered it. The value is the
// resolved element rather than the id, because a root reached by the second route below — the
// nearest <form> ancestor of a registered field — need not carry an id of its own.
const clickRecoveryRoots = new Map();

// The listeners sit on the document, in the capture phase, because the click this guard exists
// for is dispatched on the nearest common ancestor of the down and up targets, and that ancestor
// is routinely OUTSIDE the form: a message LEAVING the page as the last error is fixed lifts the
// button and retargets the click to <main>. A form-scoped listener would never see it. Scoping is
// done by testing the pressed BUTTON against the registered roots instead.
let clickRecoveryListening = false;

// The gesture in progress: which button it began on, where the pointer was, and where the
// button's border box was at that moment. Null whenever no press is outstanding.
let pressedButton = null;

const recoverableButtons = "button, input[type=submit], input[type=image]";

// How far the pointer may drift between press and release and still count as a press that never
// left — about the tremor an unsteady hand adds to a deliberate click, and well under any
// movement that reads as dragging off to cancel.
const pointerDriftLimit = 6;

export function registerClickRecovery(rootId, fieldIds) {
    // Re-registering replaces, the same way re-observing a form does: a root rebuilt over a new
    // model renders a fresh element under the same id, and the entry left behind would scope the
    // guard to a detached node forever.
    clickRecoveryRoots.delete(rootId);

    // Route 1: whatever element carries the model-level id, taken when it is a <form> or when a
    // button is actually under it.
    //
    // The form clause is what keeps a root that renders its own id safe at a moment when it holds
    // neither. Content behind a condition that has not opened yet still arrives inside that form
    // later, and until it does there is no registered field for route 2 to walk up from — so
    // declining here would lose the guard for the life of a root that is asked once.
    //
    // The button clause is for an id a consumer placed by hand, which can name something narrower
    // than the form: an id left on a wrapper. Taking that would install a guard with nothing in
    // reach and tell nobody, which is the shape this whole mechanism exists to end, so a non-form
    // element with no button falls through rather than standing.
    const named = document.getElementById(rootId);
    let root = named instanceof HTMLFormElement || named?.querySelector(recoverableButtons)
        ? named
        : null;

    if (!root) {
        // Route 2: the <form> a registered field sits in, which is the same boundary a root that
        // renders one of its own would have drawn. Deliberately NOT button-checked. A form is the
        // structural answer whatever it happens to hold at this moment, and a button rendered
        // into it later is inside it either way — so this is where a form with no button yet is
        // caught, including one whose own id route 1 has just declined.
        for (const fieldId of fieldIds || []) {
            const form = document.getElementById(fieldId)?.closest("form");
            if (form) {
                root = form;
                break;
            }
        }
    }

    if (root) {
        clickRecoveryRoots.set(rootId, root);
    }

    syncClickRecoveryListeners();
    return Boolean(root);
}

export function releaseClickRecovery(rootId) {
    clickRecoveryRoots.delete(rootId);
    syncClickRecoveryListeners();
}

// Refcounted by the registered roots themselves rather than by a counter: the map is the count,
// so a re-registration that replaces an entry cannot drift it.
function syncClickRecoveryListeners() {
    const wanted = clickRecoveryRoots.size > 0;
    if (wanted === clickRecoveryListening) {
        return;
    }

    clickRecoveryListening = wanted;
    const bind = wanted ? "addEventListener" : "removeEventListener";
    document[bind]("pointerdown", onClickRecoveryPointerDown, true);
    document[bind]("click", onClickRecoveryClick, true);

    if (!wanted) {
        pressedButton = null;
    }
}

function onClickRecoveryPointerDown(event) {
    // Every press starts the record over, so a gesture that never produced a click leaves nothing
    // behind for the next one to be judged against.
    pressedButton = null;

    // The primary button of the primary pointer is the only one that produces a click at all; a
    // secondary press opens a context menu and a second finger belongs to a gesture, not a press.
    if (event.button !== 0 || !event.isPrimary) {
        return;
    }

    const button = event.target instanceof Element
        ? event.target.closest(recoverableButtons)
        : null;
    if (!button || !isInsideRegisteredRoot(button)) {
        return;
    }

    const rect = button.getBoundingClientRect();
    pressedButton = {
        button,
        x: event.clientX,
        y: event.clientY,
        top: rect.top,
        left: rect.left,
        width: rect.width,
        height: rect.height,
    };
}

function onClickRecoveryClick(event) {
    const press = pressedButton;

    // Cleared ahead of everything below, so the click this handler may go on to deliver finds no
    // press outstanding and cannot start a second round of the same reasoning on itself.
    pressedButton = null;

    if (!press || !press.button.isConnected) {
        return;
    }

    // The click reached the button, or something inside it: an ordinary click, already delivered.
    // Everything this guard acts on is a click the browser dispatched somewhere else instead.
    if (press.button.contains(event.target)) {
        return;
    }

    // A retarget specifically, not merely a click elsewhere: the browser dispatches on the
    // nearest common ancestor of the down and up targets, so the pressed button has to sit under
    // whatever received this click.
    if (!(event.target instanceof Node) || !event.target.contains(press.button)) {
        return;
    }

    // The pointer stayed where it was pressed. This is the whole discrimination the platform
    // cannot make on its own: cancel-on-different-target is written in terms of targets, and
    // dragging off a button and a button moving out from under a still pointer produce the same
    // different target. The intent lives in the pointer, so that is what is read.
    if (Math.abs(event.clientX - press.x) > pointerDriftLimit ||
        Math.abs(event.clientY - press.y) > pointerDriftLimit) {
        return;
    }

    // ...and the button is what moved. Viewport-relative on purpose: the question is whether the
    // button left the pointer, and the pointer's frame is the viewport — so a scroll under a
    // still pointer counts, and recovering there is right for the same reason. A border box that
    // has not changed at all means something merely covered the button, and intercepting a click
    // is exactly what an element opened over one is for.
    const rect = press.button.getBoundingClientRect();
    if (rect.top === press.top && rect.left === press.left &&
        rect.width === press.width && rect.height === press.height) {
        return;
    }

    // The mis-delivered click stops here, so one press stays one click at every level. These
    // listeners sit at document capture, so leaving it to run would let the re-delivered click
    // finish its whole path and then send the original down to its own target and back up:
    // everything above the button would see two. A consumer's delegated click handler, an
    // analytics listener, a click-outside-to-close guard would each fire twice for one press.
    // Nobody loses a click by this. The retarget condition above already established that the
    // click's target CONTAINS the button, so the re-delivered click bubbles through that very
    // element and every ancestor of it — the intended click arrives exactly where the misdirected
    // one would have, and replaces it. stopPropagation rather than stopImmediatePropagation:
    // other listeners on the document itself are not this guard's business.
    event.stopPropagation();

    // click() rather than requestSubmit(): it also reaches a button that submits nothing at all —
    // an ordinary handler on a form the library only attached to — and it bubbles, which the
    // framework's delegated handlers need.
    press.button.click();
}

function isInsideRegisteredRoot(element) {
    for (const root of clickRecoveryRoots.values()) {
        if (root.isConnected && root.contains(element)) {
            return true;
        }
    }

    return false;
}
