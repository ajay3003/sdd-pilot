export function open(dialog) {
    if (!dialog.dataset.focusBound) {
        dialog.addEventListener('keydown', event => {
            if (event.key !== 'Tab') return;
            const controls = [...dialog.querySelectorAll('button:not(:disabled), input:checked:not(:disabled)')];
            if (!controls.length) { event.preventDefault(); return; }
            const first = controls[0], last = controls[controls.length - 1];
            if (event.shiftKey && document.activeElement === first) {
                event.preventDefault(); last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault(); first.focus();
            }
        });
        dialog.dataset.focusBound = 'true';
    }
    dialog.showModal();
}
export function close(dialog, trigger) { dialog.close(); trigger.focus(); }
