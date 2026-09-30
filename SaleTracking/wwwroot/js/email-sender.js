(() => {
    const card = document.getElementById('email-sender');
    if (!card) return;

    const statusUrl = card.dataset.statusUrl;
    const saveUrl = card.dataset.saveUrl;
    const testUrl = card.dataset.testUrl;

    const badge = card.querySelector('[data-email-badge]');
    const form = document.getElementById('smtp-config-form');
    const hostInput = document.getElementById('smtp-host');
    const portInput = document.getElementById('smtp-port');
    const sslInput = document.getElementById('smtp-ssl');
    const senderEmailInput = document.getElementById('smtp-sender-email');
    const senderNameInput = document.getElementById('smtp-sender-name');
    const passwordInput = document.getElementById('smtp-password');
    const passwordHint = document.getElementById('smtp-password-hint');
    const saveBtn = document.getElementById('btn-save-smtp');
    const saveIndicator = document.getElementById('smtp-save-indicator');

    const testRecipientInput = document.getElementById('smtp-test-recipient');
    const sendTestBtn = document.getElementById('btn-send-test-email');
    const testResult = document.getElementById('smtp-test-result');

    function getAntiForgeryToken() {
        const tokenInput = card.querySelector('input[name="__RequestVerificationToken"]');
        return tokenInput ? tokenInput.value : '';
    }

    function updateBadge(isConfigured) {
        if (!badge) return;
        if (isConfigured) {
            badge.textContent = 'Configured & Active';
            badge.className = 'wa-badge';
            badge.style.backgroundColor = '#10b981';
            badge.style.color = '#ffffff';
        } else {
            badge.textContent = 'Setup Required';
            badge.className = 'wa-badge';
            badge.style.backgroundColor = '#f59e0b';
            badge.style.color = '#ffffff';
        }
    }

    async function loadStatus() {
        try {
            const response = await fetch(statusUrl, {
                headers: { 'Accept': 'application/json' },
                cache: 'no-store'
            });
            if (!response.ok) return;
            const data = await response.json();

            updateBadge(data.isConfigured);

            if (data.host) hostInput.value = data.host;
            if (data.port) portInput.value = data.port;
            sslInput.checked = !!data.enableSsl;
            if (data.senderEmail) senderEmailInput.value = data.senderEmail;
            if (data.senderName) senderNameInput.value = data.senderName;

            if (data.hasPassword) {
                passwordInput.placeholder = '•••••••••••••••• (Saved)';
                passwordHint.textContent = 'App Password is set. Enter a new one only if you want to replace it.';
            } else {
                passwordInput.placeholder = 'Enter 16-character App Password';
                passwordHint.textContent = 'Enter the 16-character App Password generated from your Google Account.';
            }
        } catch {
            updateBadge(false);
        }
    }

    if (form) {
        form.addEventListener('submit', async (e) => {
            e.preventDefault();

            saveBtn.disabled = true;
            saveIndicator.style.display = 'inline';
            saveIndicator.textContent = 'Saving…';
            saveIndicator.className = 'small text-muted';

            const payload = {
                host: hostInput.value.trim(),
                port: parseInt(portInput.value, 10) || 587,
                enableSsl: sslInput.checked,
                senderEmail: senderEmailInput.value.trim(),
                senderName: senderNameInput.value.trim(),
                password: passwordInput.value.trim()
            };

            try {
                const response = await fetch(saveUrl, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'RequestVerificationToken': getAntiForgeryToken()
                    },
                    body: JSON.stringify(payload)
                });

                if (response.redirected && response.url.includes('/Account/Login')) {
                    saveIndicator.textContent = '✗ Session expired. Please log in again as Admin.';
                    saveIndicator.className = 'small text-danger fw-bold';
                    return;
                }

                let result;
                try {
                    result = await response.json();
                } catch {
                    result = null;
                }

                if (response.ok && result?.success) {
                    saveIndicator.textContent = '✓ Saved successfully!';
                    saveIndicator.className = 'small text-success fw-bold';
                    passwordInput.value = '';
                    await loadStatus();
                } else if (result?.message) {
                    saveIndicator.textContent = '✗ ' + result.message;
                    saveIndicator.className = 'small text-danger fw-bold';
                } else if (response.status === 401 || response.status === 403) {
                    saveIndicator.textContent = '✗ Unauthorized. Please log in as Admin.';
                    saveIndicator.className = 'small text-danger fw-bold';
                } else {
                    saveIndicator.textContent = `✗ Server returned error (${response.status}).`;
                    saveIndicator.className = 'small text-danger fw-bold';
                }
            } catch (err) {
                saveIndicator.textContent = '✗ Server unreachable. Please ensure the app is running in Visual Studio.';
                saveIndicator.className = 'small text-danger fw-bold';
            } finally {
                saveBtn.disabled = false;
                setTimeout(() => {
                    if (saveIndicator.textContent.includes('Saved')) {
                        saveIndicator.style.display = 'none';
                    }
                }, 4000);
            }
        });
    }

    if (sendTestBtn) {
        sendTestBtn.addEventListener('click', async () => {
            const recipient = testRecipientInput.value.trim();
            if (!recipient) {
                showTestResult(false, 'Please enter a recipient email address.');
                testRecipientInput.focus();
                return;
            }

            sendTestBtn.disabled = true;
            const originalText = sendTestBtn.textContent;
            sendTestBtn.textContent = 'Sending Test…';
            testResult.style.display = 'none';

            try {
                const response = await fetch(testUrl, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'RequestVerificationToken': getAntiForgeryToken()
                    },
                    body: JSON.stringify({ recipientEmail: recipient })
                });

                if (response.redirected && response.url.includes('/Account/Login')) {
                    showTestResult(false, 'Your session has expired. Please refresh the page and log in as Admin.');
                    return;
                }

                let result;
                try {
                    result = await response.json();
                } catch {
                    result = null;
                }

                if (response.ok && result?.success) {
                    showTestResult(true, result.message);
                } else if (result?.message) {
                    showTestResult(false, result.message);
                } else if (response.status === 401 || response.status === 403) {
                    showTestResult(false, 'Unauthorized. Please sign in as Admin.');
                } else {
                    showTestResult(false, `Server returned error (${response.status}).`);
                }
            } catch (err) {
                showTestResult(false, 'Server is unreachable. Please ensure the app is running in Visual Studio.');
            } finally {
                sendTestBtn.disabled = false;
                sendTestBtn.textContent = originalText;
            }
        });
    }

    function showTestResult(isSuccess, message) {
        if (!testResult) return;
        testResult.style.display = 'block';
        if (isSuccess) {
            testResult.className = 'alert alert-success small py-2 px-3 mt-2';
            testResult.textContent = '✓ ' + (message || 'Test email sent successfully!');
        } else {
            testResult.className = 'alert alert-danger small py-2 px-3 mt-2';
            testResult.textContent = '✗ ' + (message || 'Failed to send test email.');
        }
    }

    loadStatus();
})();
