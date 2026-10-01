// Moves keyboard focus to an element by id (scrolled into view) — used to point one section's action at the control that owns it.
window.birknextFocusElement = function (id) {
    const element = document.getElementById(id);
    if (!element) return;
    element.scrollIntoView({ block: "center", behavior: "auto" });
    element.focus({ preventScroll: true });
};
