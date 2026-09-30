(() => {
    const card = document.getElementById('whatsapp-sender');
    if (!card) return;
    const el = name => card.querySelector(`[data-wa-${name}]`);
    let timer, busy = false, closed = false, abort;
    const states = {
        qr: ['Scan to connect', 'Scan the QR code', 'QR codes refresh automatically. Keep this page open while linking.'],
        starting: ['Starting', 'Starting WhatsApp', 'Preparing your connection. The first start can take about a minute.'],
        connecting: ['Connecting', 'Phone linked', 'Finishing the connection. You do not need to scan again.'],
        ready: ['Connected', 'Ready to send alerts', 'Your WhatsApp sender is connected.'],
        disconnecting: ['Disconnecting', 'Disconnecting WhatsApp', 'Logging out your sender. Please wait.'],
        disconnected: ['Disconnected', 'Reconnect your sender', 'The sender was disconnected. Reconnect to continue sending alerts.'],
        auth_failure: ['Link expired', 'Link WhatsApp again', 'Your linked session expired. Reconnect and scan a new QR code.'],
        error: ['Needs attention', 'Could not start WhatsApp', 'Try reconnecting. If this continues, ask the server operator to check WhatsApp setup.'],
        offline: ['Unavailable', 'Waiting for WhatsApp', 'The background service is unavailable. SaleTrack will retry starting it automatically.'],
        setup_required: ['Preparing', 'Preparing WhatsApp', 'Waiting for the background service. If this persists, complete the one-time server setup.'],
        unavailable: ['Needs attention', 'Connection unavailable', 'Check the server setup, then refresh the connection.'],
        bridge_outdated: ['Update required', 'An older WhatsApp service is running', 'Stop the old standalone WhatsApp bridge once. SaleTrack will start its integrated service and restore your saved connection.'],
        disabled: ['Disabled', 'WhatsApp is disabled', 'Enable the WhatsApp sender in the application configuration to connect.']
    };
    function clearQr() {
        el('qr').hidden = true;
        el('qr').removeAttribute('src');
        el('placeholder').hidden = false;
    }
    function render(status) {
        const connected = status.connected === true && status.state === 'ready';
        const state = connected ? 'ready' : status.state === 'ready' ? 'connecting' : status.state;
        const [badge, title, detail] = states[state] || states.unavailable;
        card.dataset.state = state;
        el('badge').textContent = badge;
        el('preview-title').textContent = title;
        el('preview-detail').textContent = detail;
        el('status').textContent = detail;
        el('icon').textContent = connected ? '✓' : ['starting', 'connecting', 'setup_required'].includes(state) ? '↻' : '▦';
        el('preview').setAttribute('aria-busy', ['starting', 'connecting', 'setup_required'].includes(state).toString());
        const qr = state === 'qr' && /^data:image\/png;base64,[A-Za-z0-9+/]+=*$/.test(status.qr || '') ? status.qr : null;
        if (qr) {
            if (el('qr').getAttribute('src') !== qr) el('qr').src = qr;
            el('qr').hidden = false;
            el('placeholder').hidden = true;
        } else clearQr();
        el('steps').hidden = connected;
        el('number-box').hidden = !connected;
        el('number').textContent = connected ? status.senderNumber || 'WhatsApp account linked' : '';
        el('title').textContent = connected ? 'Your sender is ready' : 'Link your sender in a few seconds';
        el('description').textContent = connected
            ? 'SaleTrack will use this account for customer price alerts. You can continue using the app.'
            : 'Use the phone with the WhatsApp account you want to send alerts from.';
        el('reconnect').hidden = !['disconnected', 'auth_failure', 'error'].includes(state);
        el('disconnect').hidden = !connected;
        if (!connected) el('confirmation').hidden = true;
    }
    function expired() {
        closed = true;
        clearQr();
        el('number-box').hidden = true;
        el('badge').textContent = 'Sign-in required';
        el('preview-title').textContent = 'Admin session ended';
        el('preview-detail').textContent = 'Sign in again to manage this connection.';
        el('status').textContent = 'Reload the page and sign in with your admin account.';
        el('reconnect').hidden = true;
        el('disconnect').hidden = true;
        el('confirmation').hidden = true;
    }
    async function request(url, options = {}, timeoutMs = 10000) {
        const controller = new AbortController();
        abort = controller;
        const timeout = setTimeout(() => controller.abort(), timeoutMs);
        try {
            return await fetch(url, { credentials: 'same-origin', cache: 'no-store', signal: controller.signal, ...options });
        } finally { clearTimeout(timeout); }
    }
    async function refresh() {
        clearTimeout(timer);
        if (busy || closed || document.hidden) return;
        busy = true;
        el('refresh').disabled = true;
        try {
            const response = await request(card.dataset.statusUrl);
            if (response.status === 401 || response.status === 403 || response.redirected) return expired();
            if (!response.ok) throw new Error('Connection unavailable');
            const status = await response.json();
            if (!document.hidden && !closed) render(status);
        } catch {
            if (!closed) render({ state: 'offline' });
        } finally {
            busy = false;
            el('refresh').disabled = false;
            if (!closed && !document.hidden) timer = setTimeout(refresh, card.dataset.state === 'ready' ? 12000 : 3000);
        }
    }
    el('refresh').addEventListener('click', () => { if (closed) location.reload(); else void refresh(); });
    async function changeConnection(action) {
        if (busy || closed) return;
        const disconnect = action === 'disconnect';
        el('confirmation').hidden = true;
        busy = true;
        clearTimeout(timer);
        el('reconnect').disabled = true;
        el('disconnect').disabled = true;
        el('refresh').disabled = true;
        el('error').hidden = true;
        clearQr();
        if (disconnect) render({ state: 'disconnecting' });
        try {
            const token = el('form').querySelector('input[name="__RequestVerificationToken"]').value;
            const response = await request(disconnect ? card.dataset.disconnectUrl : card.dataset.reconnectUrl, {
                method: 'POST', body: new URLSearchParams({ __RequestVerificationToken: token })
            }, disconnect ? 25000 : 10000);
            if (response.status === 401 || response.status === 403 || response.redirected) return expired();
            if (!response.ok) {
                const error = await response.json().catch(() => ({}));
                throw new Error(error.error || `Could not ${action}. Refresh the status and try again.`);
            }
            if (!closed) render({ state: disconnect ? 'disconnected' : 'starting' });
        } catch (error) {
            if (closed) return;
            el('error').textContent = error.message || `Could not ${action}. Please try again.`;
            el('error').hidden = false;
        } finally {
            busy = false;
            el('reconnect').disabled = false;
            el('disconnect').disabled = false;
            el('refresh').disabled = false;
            if (!closed) timer = setTimeout(refresh, 1000);
        }
    }
    el('reconnect').addEventListener('click', () => void changeConnection('reconnect'));
    el('disconnect').addEventListener('click', () => {
        if (busy || closed) return;
        el('confirmation').hidden = false;
        el('confirm-disconnect').focus();
    });
    el('confirm-disconnect').addEventListener('click', () => void changeConnection('disconnect'));
    el('cancel-disconnect').addEventListener('click', () => {
        el('confirmation').hidden = true;
        el('disconnect').focus();
    });
    document.addEventListener('visibilitychange', () => {
        if (document.hidden) { clearTimeout(timer); clearQr(); }
        else void refresh();
    });
    window.addEventListener('pagehide', () => { closed = true; clearTimeout(timer); abort?.abort(); clearQr(); });
    window.addEventListener('pageshow', event => { if (event.persisted) { closed = false; void refresh(); } });
    void refresh();
})();
