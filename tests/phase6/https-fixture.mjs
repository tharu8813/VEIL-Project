// Two loopback HTTPS origins (different ports, same throwaway certificate) for Phase 6 smoke stages.
// The certificate is trusted only through the Debug-only VEIL_TEST_TRUSTED_SPKI hook. Synthetic data only.
import { createServer } from 'node:https';
import { testCertificate } from '../integration/test-cert.mjs';
const html = (title, body) => `<!doctype html><meta charset="utf-8"><title>${title}</title><body>${body}</body>`;

const storage = `<script>
function db() {
    return new Promise((ok, fail) => {
        const open = indexedDB.open('p6', 1);
        open.onupgradeneeded = () => open.result.createObjectStore('kv');
        open.onsuccess = () => ok(open.result);
        open.onerror = () => fail(open.error);
    });
}
async function seed(value) {
    localStorage.setItem('p6', value);
    const d = await db();
    await new Promise((ok, fail) => { const t = d.transaction('kv', 'readwrite'); t.objectStore('kv').put(value, 'k'); t.oncomplete = ok; t.onerror = fail; });
    d.close();
    document.cookie = 'p6' + location.port + '=' + value + '; max-age=3600; path=/; secure';
    return true;
}
async function state() {
    const d = await db();
    const idb = await new Promise(ok => { const r = d.transaction('kv').objectStore('kv').get('k'); r.onsuccess = () => ok(r.result ?? null); r.onerror = () => ok(null); });
    d.close();
    return { local: localStorage.getItem('p6'), idb, cookie: document.cookie.includes('p6') };
}
</script>`;

const permissions = `
<button id="notify" onclick="Notification.requestPermission().then(r => window.notifyResult = r)">알림</button>
<button id="geo" onclick="navigator.geolocation.getCurrentPosition(() => window.geoResult = 'ok', e => window.geoResult = 'err' + e.code, { timeout: 4000 })">위치</button>
<script>
if (new URLSearchParams(location.search).has('auto') && window !== window.top)
    Notification.requestPermission().then(r => parent.postMessage(r, '*'), e => parent.postMessage('error', '*'));
</script>`;

function loginForm(action, extra = '') {
    return `<form method="post" action="${action}">
<input name="user" autocomplete="username">${extra}
<button type="submit">로그인</button></form>
<script>
async function formState() {
    const user = document.querySelector('input[name=user]');
    const passes = [...document.querySelectorAll('input[type=password]')];
    const submitted = await (await fetch('/submits')).json();
    return { user: user ? user.value : null, pass: passes.some(p => p.value.length > 0), submitted };
}
</script>`;
}

export async function startHttpsFixtures(directory) {
    const { key, cert, spki } = await testCertificate(directory, 'p6');
    const tls = { key, cert };
    const servers = [];
    const origins = [];
    const requests = [];
    for (let i = 0; i < 2; i++) {
        let submits = 0;
        const server = createServer(tls, (request, response) => {
            const url = new URL(request.url, 'https://127.0.0.1');
            requests.push(`${i}:${request.method} ${url.pathname}${url.search}`);
            const send = (body, type = 'text/html; charset=utf-8') => response.writeHead(200, { 'Content-Type': type }).end(body);
            const other = origins[1 - i];
            switch (url.pathname) {
                case '/': return send(html('P6 HTTPS', 'P6'));
                case '/perm': return send(html('P6 권한', permissions));
                case '/perm-frame': return send(html('P6 권한 프레임', `<iframe src="${url.searchParams.get('child')}?auto=1"></iframe>
<script>addEventListener('message', e => { window.frameResult = String(e.data); });</script>`));
                case '/storage': return send(html('P6 저장소', storage));
                case '/login': return send(html('P6 로그인', loginForm('/login-submit', '<input type="password" name="pass">')));
                case '/login-cross': return send(html('P6 교차 로그인', loginForm(`${other}/login-submit`, '<input type="password" name="pass">')));
                case '/login-two': return send(html('P6 이중', loginForm('/login-submit', '<input type="password" name="pass"><input type="password" name="pass2">')));
                case '/login-new': return send(html('P6 새 암호', loginForm('/login-submit', '<input type="password" name="pass" autocomplete="new-password">')));
                case '/login-hidden': return send(html('P6 숨김', loginForm('/login-submit', '<input type="password" name="pass" style="display:none">')));
                case '/login-frame': return send(html('P6 로그인 프레임', `<iframe src="${url.searchParams.get('child')}"></iframe>
<script>async function formState() { return { user: null, pass: false, submitted: 0 }; }</script>`));
                case '/login-submit': submits++; return send(html('P6 제출됨', 'submitted'));
                case '/submits': return send(String(submits), 'application/json');
                default: return response.writeHead(404).end();
            }
        });
        await new Promise(done => server.listen(0, '127.0.0.1', done));
        servers.push(server);
        origins.push(`https://127.0.0.1:${server.address().port}`);
    }
    // Child frames on /perm with ?auto=1 request without a gesture and report to the parent.
    const stop = async () => { for (const server of servers) { server.closeAllConnections?.(); await new Promise(done => server.close(done)); } };
    return { origins, spki, requests, stop };
}
