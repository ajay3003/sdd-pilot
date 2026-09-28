// Critical E2E run-history dialogs: a native modal (inert background, Esc), Tab kept inside it, and focus handed back to
// the control that opened it. Esc is routed to .NET so the dialog always closes the same way.
const focusable = 'button:not(:disabled), input:not(:disabled), select:not(:disabled), [href], [tabindex]:not([tabindex="-1"])';

export function open(dialog, dotnet) {
    if (!dialog) return;
    if (!dialog.dataset.bound) {
        dialog.addEventListener('keydown', event => {
            if (event.key !== 'Tab') return;
            const controls = [...dialog.querySelectorAll(focusable)];
            if (!controls.length) { event.preventDefault(); return; }
            const first = controls[0], last = controls[controls.length - 1];
            if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
            else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
        });
        dialog.addEventListener('cancel', event => {
            event.preventDefault();
            dotnet.invokeMethodAsync('CancelHistoryDialog');
        });
        dialog.dataset.bound = 'true';
    }
    if (!dialog.open) dialog.showModal();
    // Start on the safe choice: Cancel, never the confirming action.
    dialog.querySelector('[data-testid=e2e-dialog-cancel]')?.focus();
}

// Focus goes back to the opening control from .NET, after the page has re-rendered and that control is enabled again.
export function close(dialog) {
    if (dialog?.open) dialog.close();
}
