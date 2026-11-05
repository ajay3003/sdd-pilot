// Sidebar navigation helpers (NavMenu): focus moves for flyouts, the search palette and the mobile drawer, arrow-key movement between rows,
// and the drawer's focus trap and scroll lock. Presentation only — the navigation state lives in NavigationSectionState.
window.birknextNav = (function () {
    const focusable = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';
    const visible = el => el.offsetParent !== null || el.getClientRects().length > 0;
    let trapHandler = null;

    function items(container, selector) {
        return container ? Array.from(container.querySelectorAll(selector || focusable)).filter(visible) : [];
    }

    return {
        // Focus the first focusable element (or the element matching selector) inside the container.
        focusFirst(id, selector) {
            const container = document.getElementById(id);
            const first = items(container, selector)[0];
            if (first) first.focus({ preventScroll: true });
        },
        focusId(id) {
            const el = document.getElementById(id);
            if (el) el.focus({ preventScroll: true });
        },
        // Move focus by delta among the container's items (wraps); Home/End pass delta -Infinity/Infinity.
        move(id, selector, delta) {
            const list = items(document.getElementById(id), selector);
            if (list.length === 0) return;
            let index = list.indexOf(document.activeElement);
            if (delta === Infinity) index = list.length - 1;
            else if (delta === -Infinity) index = 0;
            else index = index < 0 ? (delta > 0 ? 0 : list.length - 1) : (index + delta + list.length) % list.length;
            list[index].focus({ preventScroll: false });
        },
        // Keep a flyout inside the viewport (it opens beside its group button and may run past the bottom edge).
        place(id) {
            const el = document.getElementById(id);
            if (!el) return;
            el.style.top = '';
            const rect = el.getBoundingClientRect();
            const overflow = rect.bottom - (window.innerHeight - 8);
            if (overflow > 0) el.style.top = `${Math.max(-rect.top + 8, -overflow)}px`;
        },
        // Arrow keys move between the sidebar's rows and group headings (Tab still moves through everything).
        attachArrows(id, selector) {
            const container = document.getElementById(id);
            if (!container || container.dataset.navArrows === 'true') return;
            container.dataset.navArrows = 'true';
            container.addEventListener('keydown', e => {
                if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(e.key)) return;
                if (!e.target.matches || !e.target.matches(selector)) return;
                e.preventDefault();
                const delta = e.key === 'ArrowDown' ? 1 : e.key === 'ArrowUp' ? -1 : e.key === 'Home' ? -Infinity : Infinity;
                window.birknextNav.move(id, selector, delta);
            });
        },
        // Mobile drawer: lock page scroll, move focus into the drawer and keep Tab inside it until it closes.
        openDrawer(id) {
            const drawer = document.getElementById(id);
            if (!drawer) return;
            document.body.classList.add('nav-drawer-scroll-lock');
            if (trapHandler) document.removeEventListener('keydown', trapHandler, true);
            trapHandler = e => {
                if (e.key !== 'Tab') return;
                const list = items(drawer);
                if (list.length === 0) return;
                const first = list[0], last = list[list.length - 1];
                if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
                else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
                else if (!drawer.contains(document.activeElement)) { e.preventDefault(); first.focus(); }
            };
            document.addEventListener('keydown', trapHandler, true);
            const first = items(drawer)[0];
            if (first) first.focus({ preventScroll: true });
        },
        closeDrawer(returnFocusId) {
            document.body.classList.remove('nav-drawer-scroll-lock');
            if (trapHandler) document.removeEventListener('keydown', trapHandler, true);
            trapHandler = null;
            if (returnFocusId) {
                const el = document.getElementById(returnFocusId);
                if (el && visible(el)) el.focus({ preventScroll: true });
            }
        },
        // Scroll the active row into view inside the sidebar (after a deep link or a group reveal), without moving the page.
        revealActive(id) {
            const container = document.getElementById(id);
            const active = container && container.querySelector('a.active');
            if (!active) return;
            const c = container.getBoundingClientRect(), r = active.getBoundingClientRect();
            if (r.top < c.top || r.bottom > c.bottom) container.scrollTop += r.top - c.top - c.height / 3;
        }
    };
})();
