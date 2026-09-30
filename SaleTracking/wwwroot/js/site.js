// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

document.querySelectorAll('input[type="password"]').forEach(input => {
    if (input.closest('.password-field')) return;

    const field = document.createElement('div');
    field.className = 'password-field';
    input.before(field);
    field.append(input);

    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'password-toggle';
    if (input.id) toggle.setAttribute('aria-controls', input.id);

    const eyeIcon = '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"></path><circle cx="12" cy="12" r="3"></circle></svg>';
    const eyeOffIcon = '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24"></path><line x1="1" y1="1" x2="23" y2="23"></line></svg>';

    const label = input.labels?.[0]?.textContent.trim().toLowerCase() || 'password';
    const updateVisibility = visible => {
        input.type = visible ? 'text' : 'password';
        toggle.innerHTML = visible ? eyeOffIcon : eyeIcon;
        toggle.setAttribute('aria-label', `${visible ? 'Hide' : 'Show'} ${label}`);
        toggle.setAttribute('aria-pressed', String(visible));
    };

    updateVisibility(false);
    toggle.addEventListener('click', () => updateVisibility(input.type === 'password'));
    field.append(toggle);

    // Keep restored pages and reset forms masked as well as newly loaded forms.
    window.addEventListener('pagehide', () => updateVisibility(false));
    input.form?.addEventListener('submit', () => updateVisibility(false), { capture: true });
    input.form?.addEventListener('reset', () => updateVisibility(false));
});

// Reject letters in typed or pasted numbers while preserving phone formatting.
const cleanPhone = value => value.replace(/[^0-9+ -]/g, '').replace(/(?!^)\+/g, '');
document.querySelectorAll('input[data-phone-number]').forEach(input => {
    input.addEventListener('input', () => {
        const original = input.value;
        const cleaned = cleanPhone(original);
        if (cleaned === original) return;
        const caret = cleanPhone(original.slice(0, input.selectionStart ?? original.length)).length;
        input.value = cleaned;
        input.setSelectionRange(caret, caret);
    });
});

const confirmation = document.querySelector('.track-success[data-return-url]');
if (confirmation?.dataset.returnUrl) {
    const destination = new URL(confirmation.dataset.returnUrl, window.location.href);
    if (destination.origin === window.location.origin) {
        const timer = window.setTimeout(() => window.location.replace(destination.href),
            Number(confirmation.dataset.returnDelay) || 4000);
        window.addEventListener('pagehide', () => window.clearTimeout(timer), { once: true });
    }
}
