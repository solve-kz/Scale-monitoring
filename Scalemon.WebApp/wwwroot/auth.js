// wwwroot/auth.js
window.scalemon = {
    login: async function (username, password) {
        const r = await fetch('/auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'include',               // <-- важно!
            body: JSON.stringify({ username, password })
        });
        return r.ok;
    },
    logout: async function () {
        await fetch('/auth/logout', {
            method: 'POST',
            credentials: 'include'                // <-- важно!
        });
    }
};

