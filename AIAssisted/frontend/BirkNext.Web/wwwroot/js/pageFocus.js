// Moves keyboard focus to an element by id (scrolled into view) — used to point one section's action at the control that owns it.
window.birknextFocusElement = function (id) {
    const element = document.getElementById(id);
    if (!element) return;
    element.scrollIntoView({ block: "center", behavior: "auto" });
    element.focus({ preventScroll: true });
};

// Brings a section heading to the top of the view and focuses it (a step or area action). Scrolls instantly (the page's smooth scrolling would
// aim at a position that is stale once the newly shown section finishes rendering) and checks again after layout has settled.
window.birknextRevealSection = function (id) {
    const reveal = () => {
        const element = document.getElementById(id);
        if (!element) return;
        const r = element.getBoundingClientRect();
        if (r.top < 0 || r.bottom > window.innerHeight) element.scrollIntoView({ block: "start", behavior: "instant" });
        if (document.activeElement !== element) element.focus({ preventScroll: true });
    };
    reveal();
    requestAnimationFrame(() => requestAnimationFrame(reveal));
    setTimeout(reveal, 350);
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

// Drag to reorder (Plan & prepare): browsers such as Firefox only start a drag when data is set; Blazor's drag events cannot set it, so any
// element marked data-drag sets a plain, empty payload here. The order itself is handled by the page (drop target + Save draft).
// While such a drag is in progress, dragover and drop are default-prevented so the browser allows the drop (Blazor's :preventDefault alone did
// not enable it in Chromium) and never pastes the payload into an input under the pointer; the page's own drop handlers still run.
var birknextReorderDrag = false;
document.addEventListener('dragstart', function (e) {
    var source = e.target && e.target.closest ? e.target.closest('[data-drag]') : null;
    if (!source || !e.dataTransfer) return;
    birknextReorderDrag = true;
    try { e.dataTransfer.setData('text/plain', source.getAttribute('data-drag') || ''); e.dataTransfer.effectAllowed = 'move'; } catch (_) { }
}, true);
document.addEventListener('dragover', function (e) {
    if (!birknextReorderDrag) return;
    e.preventDefault();
    if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
}, true);
document.addEventListener('drop', function (e) { if (birknextReorderDrag) e.preventDefault(); }, true);
document.addEventListener('dragend', function () { birknextReorderDrag = false; }, true);

// Tells a component whether an element is at least minPx wide, now and whenever that changes (2026-11-27: Finn og forstå's three columns
// follow the page's own width, so a collapsed sidebar or a narrow window is measured, not guessed from the viewport).
window.birknextWatchWidth = function (element, dotnet, minPx) {
    if (!element || typeof ResizeObserver === "undefined") return;
    let last = null;
    const check = () => {
        const wide = element.getBoundingClientRect().width >= minPx;
        if (wide === last) return;
        last = wide;
        dotnet.invokeMethodAsync("OnLayoutWidth", wide).catch(() => observer.disconnect());
    };
    const observer = new ResizeObserver(check);
    observer.observe(element);
    check();
};

// V12 (2026-11-27, APP-4): one accessible-dialog behaviour for every modal — keyboard focus stays inside the dialog (Tab and Shift+Tab wrap),
// the first focusable control (or the dialog itself) receives focus when it opens, and focus returns to the control that opened it when it
// closes. Escape is handled by the dialog component itself (it may ask first when there is unsaved work).
window.birknextDialog = (function () {
    const focusable = 'a[href], button:not([disabled]), input:not([disabled]):not([type=hidden]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
    const open = new Map();
    function items(dialog) { return Array.from(dialog.querySelectorAll(focusable)).filter(e => e.offsetParent !== null || e === document.activeElement); }
    return {
        trap: function (dialog, returnFocusId) {
            if (!dialog || open.has(dialog)) return;
            const previous = returnFocusId ? document.getElementById(returnFocusId) : document.activeElement;
            const onKey = function (e) {
                if (e.key !== 'Tab') return;
                const list = items(dialog);
                if (list.length === 0) { e.preventDefault(); dialog.focus(); return; }
                const first = list[0], last = list[list.length - 1];
                if (e.shiftKey && (document.activeElement === first || !dialog.contains(document.activeElement))) { e.preventDefault(); last.focus(); }
                else if (!e.shiftKey && (document.activeElement === last || !dialog.contains(document.activeElement))) { e.preventDefault(); first.focus(); }
            };
            dialog.addEventListener('keydown', onKey);
            open.set(dialog, { onKey: onKey, previous: previous });
            if (!dialog.contains(document.activeElement)) { const list = items(dialog); (list[0] || dialog).focus(); }
        },
        release: function (dialog) {
            const state = open.get(dialog);
            if (!state) return;
            dialog.removeEventListener('keydown', state.onKey);
            open.delete(dialog);
            if (state.previous && document.contains(state.previous) && typeof state.previous.focus === 'function') state.previous.focus();
        },
        releaseAll: function () { Array.from(open.keys()).forEach(d => window.birknextDialog.release(d)); }
    };
})();
