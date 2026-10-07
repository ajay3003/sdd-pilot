// Moves keyboard focus to an element by id (scrolled into view) — used to point one section's action at the control that owns it.
window.birknextFocusElement = function (id) {
    const element = document.getElementById(id);
    if (!element) return;
    element.scrollIntoView({ block: "center", behavior: "auto" });
    element.focus({ preventScroll: true });
};

// Roving tab stop for a list of buttons (the Task Explorer Map): only one button is in the tab order; arrow keys move
// focus. Up/Down = previous/next item, PageUp/PageDown or Left/Right = first item of the previous/next group
// (data-map-phase), Home/End = first/last. Enter/Space keep their native button behaviour (selection).
// Idempotent per container; birknextRovingSync re-applies the single tab stop after a re-render.
window.birknextRovingSync = function (container, itemSelector) {
    if (!container) return;
    const items = Array.from(container.querySelectorAll(itemSelector));
    if (items.length === 0) return;
    const focused = items.find(item => item === document.activeElement);
    const current = focused
        || items.find(item => item.getAttribute("aria-current") === "true")
        || items.find(item => item.getAttribute("tabindex") === "0")
        || items[0];
    items.forEach(item => item.setAttribute("tabindex", item === current ? "0" : "-1"));
};

window.birknextRovingAttach = function (container, itemSelector) {
    if (!container) return;
    window.birknextRovingSync(container, itemSelector);
    if (container.dataset.rovingAttached === "true") return;
    container.dataset.rovingAttached = "true";

    const moveTo = (items, target) => {
        if (!target) return;
        items.forEach(item => item.setAttribute("tabindex", item === target ? "0" : "-1"));
        target.focus({ preventScroll: true });
        target.scrollIntoView({ block: "nearest", behavior: "auto" });
    };

    container.addEventListener("focusin", event => {
        const target = event.target.closest(itemSelector);
        if (!target || !container.contains(target)) return;
        container.querySelectorAll(itemSelector).forEach(item => item.setAttribute("tabindex", item === target ? "0" : "-1"));
    });

    container.addEventListener("keydown", event => {
        if (event.altKey || event.ctrlKey || event.metaKey) return;
        const current = event.target.closest(itemSelector);
        if (!current || !container.contains(current)) return;
        const items = Array.from(container.querySelectorAll(itemSelector));
        const index = items.indexOf(current);
        const group = current.dataset.mapPhase;
        let target = null;
        switch (event.key) {
            case "ArrowDown": target = items[index + 1]; break;
            case "ArrowUp": target = items[index - 1]; break;
            case "Home": target = items[0]; break;
            case "End": target = items[items.length - 1]; break;
            case "PageDown":
            case "ArrowRight":
                target = items.slice(index + 1).find(item => item.dataset.mapPhase !== group);
                break;
            case "PageUp":
            case "ArrowLeft": {
                const firstOfGroup = items.findIndex(item => item.dataset.mapPhase === group);
                if (firstOfGroup < index) { target = items[firstOfGroup]; break; }
                const previous = items[firstOfGroup - 1];
                if (previous) target = items.find(item => item.dataset.mapPhase === previous.dataset.mapPhase);
                break;
            }
            default: return;
        }
        // Keys at an edge are consumed too, so the region does not scroll away from the focused task.
        event.preventDefault();
        moveTo(items, target);
    });
};
