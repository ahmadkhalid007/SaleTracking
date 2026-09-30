(function () {
    let latestNotifications = [];
    const bellBtn = document.getElementById('notifBellBtn');
    const badge = document.getElementById('notifBadge');
    const notifList = document.getElementById('notifList');
    const markAllBtn = document.getElementById('btnMarkAllRead');

    if (!bellBtn) return;

    async function fetchNotifications() {
        try {
            const res = await fetch('/Notifications/Recent', { credentials: 'same-origin' });
            if (!res.ok) return;
            const data = await res.json();
            latestNotifications = data.notifications || [];
            updateBadge(data.unreadCount || 0);
            renderNotifications(latestNotifications);
        } catch (e) {
            console.warn('Failed to fetch notifications', e);
        }
    }

    function updateBadge(count) {
        if (!badge) return;
        if (count > 0) {
            badge.textContent = count > 99 ? '99+' : count;
            badge.classList.remove('d-none');
        } else {
            badge.textContent = '0';
            badge.classList.add('d-none');
        }
    }

    function renderNotifications(items) {
        if (!notifList) return;
        if (!items || items.length === 0) {
            notifList.innerHTML = `
                <div class="notif-empty-state text-center py-4 px-3">
                    <div class="notif-empty-icon mb-2">🔔</div>
                    <div class="fw-bold text-dark small">No notifications yet</div>
                    <small class="text-muted d-block mt-1">When monitored prices drop to or below your target, alerts will appear here.</small>
                </div>
            `;
            return;
        }

        let html = '';
        items.forEach(n => {
            const unreadClass = n.isRead ? '' : 'is-unread';
            const msg = n.message || '';
            const isStockOnly = msg.startsWith('[Stock Alert]') || (n.targetPrice <= 0 && msg.toLowerCase().includes('stock'));
            const isBoth = msg.startsWith('[Both') || msg.toLowerCase().includes('both');

            let icon = '🎯';
            let priceInfo = `Rs. ${Number(n.currentPrice).toLocaleString()} (Target: Rs. ${Number(n.targetPrice).toLocaleString()})`;

            if (isBoth) {
                icon = '🔥';
                priceInfo = `Rs. ${Number(n.currentPrice).toLocaleString()} (Target: Rs. ${Number(n.targetPrice).toLocaleString()}) • In Stock`;
            } else if (isStockOnly) {
                icon = '📦';
                priceInfo = n.currentPrice > 0
                    ? `Rs. ${Number(n.currentPrice).toLocaleString()} • In Stock`
                    : 'In Stock';
            }

            const title = n.productName || (isBoth ? 'Both: Price & Stock Alert' : isStockOnly ? 'Back in Stock Alert' : 'Price Drop Alert');
            
            html += `
                <div class="notif-item ${unreadClass}" data-id="${n.id}" data-url="${escapeHtml(n.productUrl)}">
                    <div class="notif-icon-col">
                        <span class="notif-type-icon">${icon}</span>
                    </div>
                    <div class="notif-content-col">
                        <div class="d-flex align-items-center justify-content-between gap-1">
                            <span class="notif-title" title="${escapeHtml(title)}">${escapeHtml(title)}</span>
                            <span class="notif-time">${escapeHtml(n.timeAgo)}</span>
                        </div>
                        <div class="notif-msg">${escapeHtml(n.message)}</div>
                        <div class="notif-price-badge">${escapeHtml(priceInfo)}</div>
                    </div>
                    ${!n.isRead ? '<span class="notif-unread-dot" title="Unread"></span>' : ''}
                </div>
            `;
        });
        notifList.innerHTML = html;

        // Attach click handlers
        notifList.querySelectorAll('.notif-item').forEach(el => {
            el.addEventListener('click', async function (e) {
                const id = this.getAttribute('data-id');
                const targetUrl = this.getAttribute('data-url');
                
                // Optimistically mark as read
                this.classList.remove('is-unread');
                const dot = this.querySelector('.notif-unread-dot');
                if (dot) dot.remove();

                try {
                    await fetch('/Notifications/MarkRead', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ id: parseInt(id, 10) })
                    });
                    const currentCount = parseInt(badge?.textContent || '0', 10);
                    if (currentCount > 0) updateBadge(currentCount - 1);
                } catch (err) {
                    console.warn(err);
                }

                if (targetUrl && targetUrl !== '#' && !targetUrl.startsWith('javascript:')) {
                    window.open(targetUrl, '_blank', 'noopener,noreferrer');
                }
            });
        });
    }

    if (markAllBtn) {
        markAllBtn.addEventListener('click', async function (e) {
            e.preventDefault();
            e.stopPropagation();
            try {
                await fetch('/Notifications/MarkAllRead', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' }
                });
                updateBadge(0);
                notifList.querySelectorAll('.notif-item').forEach(item => {
                    item.classList.remove('is-unread');
                    const dot = item.querySelector('.notif-unread-dot');
                    if (dot) dot.remove();
                });
            } catch (err) {
                console.warn(err);
            }
        });
    }

    function escapeHtml(text) {
        if (!text) return '';
        return String(text)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    // Initial load and periodic refresh
    fetchNotifications();
    setInterval(fetchNotifications, 25000);
})();
