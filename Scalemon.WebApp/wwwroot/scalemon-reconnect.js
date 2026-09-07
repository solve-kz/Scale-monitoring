// После перезапуска службы старый Blazor circuit восстановить нельзя: обновляем страницу,
// когда сервер снова готов. Обычная работа страницы не вызывает фоновых запросов.
(() => {
    let checking = false;
    window.setInterval(async () => {
        const modal = document.getElementById('components-reconnect-modal');
        if (!modal || checking || !['components-reconnect-show', 'components-reconnect-failed', 'components-reconnect-rejected'].some(c => modal.classList.contains(c))) return;
        checking = true;
        try {
            const response = await fetch('/health/ready', { cache: 'no-store', signal: AbortSignal.timeout(4000) });
            if (response.ok) window.location.reload();
        } catch { /* Служба ещё запускается или сеть недоступна. */ }
        finally { checking = false; }
    }, 5000);
})();
