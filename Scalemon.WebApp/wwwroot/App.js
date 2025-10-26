(function () {
    // гарантируем объект
    window.scalemon = window.scalemon || {};

    // добавляем (или переопределяем) методы ПОВЕРХ существующих,
    // но не затираем весь объект
    Object.assign(window.scalemon, {
        // --- контекстное меню: закрывать на скролл/ESC ---
        registerCloseOnScroll(dotnet) {
            const onScroll = () => dotnet.invokeMethodAsync('CloseContextMenu');
            window.addEventListener('scroll', onScroll, true);
            this._onScroll = onScroll;
        },
        unregisterCloseOnScroll() {
            if (this._onScroll) {
                window.removeEventListener('scroll', this._onScroll, true);
                this._onScroll = null;
            }
        },
        registerEscToClose(dotnet) {
            const onKey = (e) => { if (e.key === 'Escape') dotnet.invokeMethodAsync('CloseContextMenu'); };
            document.addEventListener('keydown', onKey, true);
            this._onKey = onKey;
        },
        unregisterEscToClose() {
            if (this._onKey) {
                document.removeEventListener('keydown', this._onKey, true);
                this._onKey = null;
            }
        },

        // --- тема/куки ---
        saveTheme(value) {
            try {
                const yearMs = 365 * 24 * 60 * 60 * 1000;
                document.cookie =
                    "MyApplicationTheme=" + encodeURIComponent(value) +
                    "; expires=" + new Date(Date.now() + yearMs).toUTCString() +
                    "; path=/; samesite=lax";
            } catch (e) { console.error("saveTheme cookie error:", e); }
        },
        setCookie(name, value, days) {
            const d = new Date();
            d.setTime(d.getTime() + (days * 24 * 60 * 60 * 1000));
            const expires = "expires=" + d.toUTCString();
            document.cookie = name + "=" + encodeURIComponent(value) + ";" + expires + ";path=/;samesite=lax";
        },
        getCookie(name) {
            const key = name + "=";
            const arr = document.cookie.split(';');
            for (let c of arr) {
                while (c.charAt(0) === ' ') c = c.substring(1);
                if (c.indexOf(key) === 0) return decodeURIComponent(c.substring(key.length, c.length));
            }
            return "";
        },
        delCookie(name) {
            document.cookie = name + "=; expires=Thu, 01 Jan 1970 00:00:01 GMT; path=/; samesite=lax";
        },
        logsGet: function (key) {
            try {
                var v = sessionStorage.getItem(key);
                return v == null ? "" : v;
            } catch (e) {            // ← обязательно с параметром
                return "";
            }
        },

        logsSet: function (key, val) {
            try {
                sessionStorage.setItem(key, val);
            } catch (e) { }
        },

        triggerFileDialog(element) {
            if (element) {
                element.click();
            }
        },
        resetFileInput(element) {
            if (element) {
                element.value = "";
            }
        },
        downloadFile(fileName, contentType, base64Data) {
            try {
                const link = document.createElement('a');
                link.style.display = 'none';
                link.download = fileName || 'download';

                const dataUrl = `data:${contentType || 'application/octet-stream'};base64,${base64Data}`;
                link.href = dataUrl;

                document.body.appendChild(link);
                link.click();
                document.body.removeChild(link);
            } catch (e) {
                console.error('downloadFile error', e);
            }
        },
        localTodayIso: function () {
            var d = new Date();
            var y = d.getFullYear();
            var m = String(d.getMonth() + 1).padStart(2, '0');
            var day = String(d.getDate()).padStart(2, '0');
            return y + "-" + m + "-" + day;
        }
    });
})();
