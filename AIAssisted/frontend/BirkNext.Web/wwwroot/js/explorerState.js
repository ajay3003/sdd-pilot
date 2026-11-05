// Work-item explorer: scroll position per explorer key (kept for the browser tab's session, restored when the user comes back), keyboard keys
// of a grid that must not scroll the page, and keeping the active row of a virtualized list in view.
window.birknextExplorer = (function () {
    const positions = {};
    function track(element, key) {
        if (!element) return;
        element.dataset.scrollKey = key;
        if (element.dataset.scrollTracked === "true") return;
        element.dataset.scrollTracked = "true";
        element.addEventListener("scroll", function () { positions[element.dataset.scrollKey] = element.scrollTop; }, { passive: true });
    }
    // A virtualized list reaches its full height only after its first measurements: keep applying the position for a few frames until the
    // content is tall enough (or ~1 s has passed), instead of stopping at whatever height the first render had.
    function restore(element, key) {
        if (!element) return false;
        const top = positions[key];
        if (typeof top !== "number") return false;
        let frames = 0;
        const apply = () => {
            element.scrollTop = top;
            if (Math.abs(element.scrollTop - top) > 2 && frames++ < 60) requestAnimationFrame(apply);
        };
        apply();
        return true;
    }
    // Arrow keys, Home/End, Page keys and Space drive the grid's active row: they must not also scroll the container.
    function gridKeys(grid) {
        if (!grid || grid.dataset.gridKeys === "true") return;
        grid.dataset.gridKeys = "true";
        grid.addEventListener("keydown", function (e) {
            if (["ArrowUp", "ArrowDown", "Home", "End", "PageUp", "PageDown", " "].includes(e.key)) e.preventDefault();
        });
    }
    // Rows have a fixed height, so the active row's position is known even when the virtualized list has not rendered it yet.
    function rowIntoView(container, index, rowHeight) {
        if (!container || index < 0) return;
        const head = container.querySelector("thead");
        const headHeight = head ? head.offsetHeight : 0;
        const table = container.querySelector("table");
        const offset = table ? table.offsetTop + headHeight : headHeight;
        const top = offset + index * rowHeight;
        if (top - headHeight < container.scrollTop) container.scrollTop = top - headHeight;
        else if (top + rowHeight > container.scrollTop + container.clientHeight) container.scrollTop = top + rowHeight - container.clientHeight;
    }
    function selectedIntoView(container, selector) {
        if (!container) return;
        const row = container.querySelector(selector);
        if (row) row.scrollIntoView({ block: "nearest" });
    }
    // A page with a sticky header marks it data-sticky-offset: its height is kept in --page-sticky-offset, so sticky panes below it (explorer
    // details) start under it instead of behind it. 0 when the header is not sticky (narrow screens) or absent.
    function stickyOffset() {
        const header = document.querySelector("[data-sticky-offset]");
        const set = () => {
            const h = header && header.isConnected && getComputedStyle(header).position === "sticky" ? header.getBoundingClientRect().height : 0;
            document.documentElement.style.setProperty("--page-sticky-offset", Math.round(h) + "px");
        };
        set();
        if (header && !header.dataset.stickyObserved && typeof ResizeObserver !== "undefined") {
            header.dataset.stickyObserved = "true";
            new ResizeObserver(set).observe(header);
            window.addEventListener("resize", set);
        }
    }
    return { track: track, restore: restore, gridKeys: gridKeys, rowIntoView: rowIntoView, selectedIntoView: selectedIntoView, stickyOffset: stickyOffset };
})();
