import { createServer } from 'node:http';
import { randomUUID } from 'node:crypto';
import { pathToFileURL } from 'node:url';

export const marker = 'VEIL_PHASE0_SYNTHETIC_5723';

const page = `<!doctype html>
<meta charset="utf-8"><title>VEIL 로컬 테스트</title>
<h1>VEIL 로컬 테스트</h1>
<p>합성 데이터로 로그인, 사이트 저장소, 탐색, 다운로드를 시험합니다.</p>
<a href="/second">다음 페이지</a> · <a href="/login">테스트 로그인</a> · <a href="/session">로그인 상태</a>
<p><a href="/download?kind=get">GET 다운로드</a> · <a href="/download?kind=auth">인증 다운로드</a>
· <a href="/redirect">리다이렉트 다운로드</a> · <a href="/download?kind=large">대용량 다운로드</a></p>
<form action="/download?kind=post" method="post"><input name="probe" value="synthetic"><button>POST 다운로드</button></form>
<form action="/upload" enctype="multipart/form-data" method="post"><input type="file" name="sample">
<button>테스트 파일 업로드</button></form>
<p><button id="storage">사이트 데이터 저장</button> <output id="result"></output></p>
<script>
document.querySelector('#storage').onclick = async () => {
  try { document.querySelector('#result').textContent = JSON.stringify(await probe('write')); }
  catch (error) { document.querySelector('#result').textContent = String(error); }
};
</script>
<script src="/probe.js"></script>`;

const probe = `async function probe(action) {
  const marker = ${JSON.stringify(marker)};
  const db = await new Promise((resolve, reject) => {
    const request = indexedDB.open('veil-probe', 1);
    request.onupgradeneeded = () => request.result.createObjectStore('probe');
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
  const cache = await caches.open('veil-probe');
  if (action === 'write') {
    await fetch('/login');
    localStorage.setItem('probe', marker);
    await new Promise((resolve, reject) => {
      const tx = db.transaction('probe', 'readwrite');
      tx.objectStore('probe').put(marker, 'value');
      tx.oncomplete = resolve;
      tx.onabort = () => reject(tx.error);
    });
    await navigator.serviceWorker.register('/sw.js');
    await navigator.serviceWorker.ready;
    await cache.put('/cached', new Response(marker));
  }
  const value = await new Promise((resolve, reject) => {
    const request = db.transaction('probe').objectStore('probe').get('value');
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
  db.close();
  return {
    cookie: await (await fetch('/session')).text() === marker,
    localStorage: localStorage.getItem('probe') === marker,
    indexedDB: value === marker,
    serviceWorker: Boolean((await navigator.serviceWorker.getRegistration())?.active),
    cacheStorage: await (await cache.match('/cached'))?.text() === marker
  };
}`;

// Phase 4-0: unique per fixture start, only ever sent in vault download bodies, so any copy found
// on disk afterwards is a plaintext leak.
export const vaultPayload = `VEIL-VAULT-PAYLOAD-${randomUUID()}-`;

// Phase 3 engine-kill test: one numbered IndexedDB record per transaction (64 KiB each) plus a
// localStorage counter, written as fast as the engine commits them.
const stormPage = `<!doctype html><meta charset="utf-8"><title>VEIL storm</title><script>
function openStorm() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open('veil-storm', 1);
    request.onupgradeneeded = () => request.result.createObjectStore('gen', { keyPath: 'n' });
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}
window.stormRead = async () => {
  const db = await openStorm();
  const keys = await new Promise((resolve, reject) => {
    const query = db.transaction('gen').objectStore('gen').getAllKeys();
    query.onsuccess = () => resolve(query.result);
    query.onerror = () => reject(query.error);
  });
  db.close();
  const max = keys.length ? Math.max(...keys) : 0;
  return { count: keys.length, max, contiguous: keys.length === max, ls: Number(localStorage.getItem('storm') ?? 0) };
};
window.stormGen = 0;
window.stormStart = async from => {
  const db = await openStorm();
  const pad = 'x'.repeat(65536);
  for (let n = from + 1; ; n++) {
    await new Promise((resolve, reject) => {
      const transaction = db.transaction('gen', 'readwrite');
      transaction.objectStore('gen').put({ n, pad });
      transaction.oncomplete = resolve;
      transaction.onerror = () => reject(transaction.error);
    });
    localStorage.setItem('storm', String(n));
    window.stormGen = n;
  }
};
</script><p>storm</p>`;

export async function startFixture(port = 0, { ipv6 = false } = {}) {
    const host = ipv6 ? '::1' : '127.0.0.1';
    const authority = ipv6 ? '[::1]' : host;
    const requests = new Map();
    const direct = new Map();
    const visitTitleMarker = `VEIL synthetic visit ${randomUUID()}`;
    const server = createServer(async (request, response) => {
        const url = new URL(request.url, 'http://127.0.0.1');
        const hasCookie = (request.headers.cookie ?? '').split(/;\s*/).includes(`veil=${marker}`);
        const send = (body, type = 'text/plain; charset=utf-8') => {
            response.setHeader('Content-Type', type);
            response.end(body);
        };
        requests.set(url.pathname, (requests.get(url.pathname) ?? 0) + 1);
        // Phase 5: the test proxy marks what it forwards; anything unmarked reached this server directly.
        if (!request.headers['x-veil-proxied']) direct.set(url.pathname, (direct.get(url.pathname) ?? 0) + 1);
        switch (url.pathname) {
            case '/network-control': return send('VEIL_DIRECT_CONTROL');
            case '/': return send(page, 'text/html; charset=utf-8');
            case '/second': return send('<title>VEIL 두 번째 페이지</title><a href="/">홈</a>', 'text/html; charset=utf-8');
            // Gate D: gathers WebRTC ICE candidates (STUN server from ?stun=, none by default) into window.veilCandidates.
            case '/webrtc': return send(`<title>VEIL WebRTC</title><script>
                window.veilCandidates = null;
                (async () => {
                    const stun = new URLSearchParams(location.search).get('stun');
                    const found = [];
                    let pc;
                    try {
                        pc = new RTCPeerConnection({ iceServers: stun ? [{ urls: stun }] : [] });
                        pc.createDataChannel('veil');
                        pc.onicecandidate = event => { if (event.candidate?.candidate) found.push(event.candidate.candidate); };
                        await pc.setLocalDescription(await pc.createOffer());
                        await new Promise((done, reject) => {
                            const timer = setTimeout(() => reject(new Error('ICE gathering timeout')), 15000);
                            const completed = () => {
                                if (pc.iceGatheringState === 'complete') { clearTimeout(timer); done(); }
                            };
                            pc.onicegatheringstatechange = completed;
                            completed();
                        });
                        window.veilCandidates = found;
                    } catch (error) { window.veilCandidates = ['error ' + error.name]; }
                    finally { pc?.close(); }
                })();
            </script>`, 'text/html; charset=utf-8');
            case '/storm': return send(stormPage, 'text/html; charset=utf-8');
            case '/vault-dl': {
                // Phase 4-0 vault capture: distinct sizes and failure modes, each body filled with vaultPayload.
                const kind = url.searchParams.get('kind');
                const sizes = { small: 1024, large: 8 * 1024 ** 2, huge: 64 * 1024 ** 2, auth: 4096, redirected: 2048,
                    drop: 4 * 1024 ** 2, post: 2048, slow: 32 * 1024 ** 2, 'slow-unknown': 32 * 1024 ** 2,
                    'slow-queued': 32 * 1024 ** 2, 'slow-power': 32 * 1024 ** 2 };
                if (kind === 'redirect') return response.writeHead(302, { Location: '/vault-dl?kind=redirected' }).end();
                if (!(kind in sizes)) return response.writeHead(400).end();
                if (kind === 'auth' && !hasCookie) return response.writeHead(401).end();
                if (kind === 'post' && request.method !== 'POST') return response.writeHead(405).end();
                request.resume();
                const size = sizes[kind];
                response.writeHead(200, {
                    'Content-Type': 'application/octet-stream',
                    'Content-Disposition': `attachment; filename="veil-vault-${kind}.bin"`,
                    ...(kind === 'slow-unknown' ? {} : { 'Content-Length': size }),
                });
                const block = Buffer.alloc(64 * 1024, vaultPayload);
                const limit = kind === 'drop' ? size / 4 : size; // drop: cut the connection a quarter of the way in
                for (let sent = 0; sent < limit;) {
                    if (kind.startsWith('slow')) await new Promise(resolve => setTimeout(resolve, kind === 'slow-power' ? 1000 : 35));
                    if (response.destroyed) return;
                    const chunk = block.subarray(0, Math.min(block.length, limit - sent));
                    sent += chunk.length;
                    if (!response.write(chunk)) await new Promise(resolve => {
                        function resume() {
                            response.off('drain', resume);
                            response.off('close', resume);
                            resolve();
                        }
                        response.once('drain', resume);
                        response.once('close', resume);
                    });
                }
                if (kind === 'drop') { await new Promise(resolve => setTimeout(resolve, 200)); return response.destroy(); }
                return response.end();
            }
            case '/visit': {
                // Distinct synthetic pages for the long-session jump-list trace (SEC-004).
                const n = Number.parseInt(url.searchParams.get('n') ?? '0', 10) || 0;
                return send(`<title>${visitTitleMarker} ${n}</title><p>visit ${n}</p>`, 'text/html; charset=utf-8');
            }
            case '/probe.js': return send(probe, 'text/javascript');
            case '/login':
                response.setHeader('Set-Cookie', `veil=${marker}; HttpOnly; SameSite=Lax; Path=/; Max-Age=3600`);
                return send('테스트 로그인 완료');
            case '/session': return send(hasCookie ? marker : '로그인 필요');
            case '/sw.js':
                return send(`self.addEventListener('install', () => self.skipWaiting());
                    self.addEventListener('activate', event => event.waitUntil(clients.claim()));
                    self.addEventListener('fetch', event => {
                        if (new URL(event.request.url).pathname === '/sw-file') {
                            event.respondWith(new Response('${marker}', {headers: {
                                'Content-Type': 'application/octet-stream',
                                'Content-Disposition': 'attachment; filename="veil-phase0-sw.bin"'
                            }}));
                        }
                    });`, 'text/javascript');
            case '/redirect': return response.writeHead(302, { Location: '/download?kind=redirect' }).end();
            case '/upload':
                request.resume();
                await new Promise(resolve => request.once('end', resolve).once('close', resolve));
                return send('테스트 업로드 요청 수신');
            case '/download': {
                const kind = url.searchParams.get('kind');
                if (kind === 'auth' && !hasCookie) return response.writeHead(401).end();
                if (kind === 'post' && request.method !== 'POST') return response.writeHead(405).end();
                if (!['auth', 'post', 'redirect', 'large', 'get'].includes(kind)) return response.writeHead(400).end();
                request.resume();
                response.setHeader('Content-Disposition', `attachment; filename="veil-phase0-${kind}.bin"`);
                const size = kind === 'large' ? 8 * 1024 * 1024 : marker.length;
                response.setHeader('Content-Length', size);
                return send(Buffer.alloc(size, marker), 'application/octet-stream');
            }
            default: return response.writeHead(404).end();
        }
    });
    await new Promise((resolve, reject) => server.once('error', reject).listen(port, host, resolve));
    return { server, requests, direct, origin: `http://${authority}:${server.address().port}`, visitTitleMarker };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const { origin } = await startFixture(Number(process.argv[2] ?? 8765));
    console.log(origin);
}
