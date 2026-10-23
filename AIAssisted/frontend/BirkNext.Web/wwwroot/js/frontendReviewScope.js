export function open(dialog) {
    if (!dialog.dataset.focusBound) {
        dialog.addEventListener('keydown', event => {
            if (event.key !== 'Tab') return;
            // A radio group is one tab stop: its checked option, or its first option when none is checked yet.
            const radios = [...dialog.querySelectorAll('input[type=radio]:not(:disabled)')];
            const radioStop = radios.find(r => r.checked) ?? radios[0];
            const controls = [...dialog.querySelectorAll('button:not(:disabled), input[type=radio]:not(:disabled)')]
                .filter(c => c.type !== 'radio' || c === radioStop);
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
