window.scalemon = window.scalemon || {};

window.scalemon.registerCloseOnScroll = function (dotnet) {
    const onScroll = () => dotnet.invokeMethodAsync('CloseContextMenu');
    window.addEventListener('scroll', onScroll, true); // capture на всём дереве
    window._scOnScroll = onScroll;
};

window.scalemon.unregisterCloseOnScroll = function () {
    if (window._scOnScroll) {
        window.removeEventListener('scroll', window._scOnScroll, true);
        window._scOnScroll = null;
    }
};

window.scalemon.registerEscToClose = function (dotnet) {
    const onKey = (e) => { if (e.key === 'Escape') dotnet.invokeMethodAsync('CloseContextMenu'); };
    window.addEventListener('keydown', onKey, true);
    window._scOnKey = onKey;
};

window.scalemon.unregisterEscToClose = function () {
    if (window._scOnKey) {
        window.removeEventListener('keydown', window._scOnKey, true);
        window._scOnKey = null;
    }
};

window.scalemon = window.scalemon || {};
window.scalemon.saveTheme = function (value) {
    try {
        const yearMs = 365 * 24 * 60 * 60 * 1000;
        document.cookie =
            "MyApplicationTheme=" + encodeURIComponent(value) +
            "; expires=" + new Date(Date.now() + yearMs).toUTCString() +
            "; path=/; samesite=lax";
    } catch (e) {
        console.error("saveTheme cookie error:", e);
    }
};

window.scalemon = {
    setCookie: function (name, value, days) {
        const d = new Date();
        d.setTime(d.getTime() + (days * 24 * 60 * 60 * 1000));
        const expires = "expires=" + d.toUTCString();
        document.cookie = name + "=" + encodeURIComponent(value) + ";" + expires + ";path=/;samesite=lax";
    },
    getCookie: function (name) {
        const key = name + "=";
        const arr = document.cookie.split(';');
        for (let c of arr) {
            while (c.charAt(0) === ' ') c = c.substring(1);
            if (c.indexOf(key) === 0) return decodeURIComponent(c.substring(key.length, c.length));
        }
        return "";
    },
    delCookie: function (name) {
        document.cookie = name + "=; expires=Thu, 01 Jan 1970 00:00:01 GMT; path=/; samesite=lax";
    }
};
