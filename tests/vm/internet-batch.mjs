// Gate D checks for an internet-connected, isolated test VM (ADR-0004 policy 4; never the offline lab VM).
//
//   node tests/vm/internet-batch.mjs --plan
//   node tests/vm/internet-batch.mjs --run --internet-vm [--desktop-dll <Veil.Desktop.dll>] [--only <id>]...
//   node tests/vm/internet-batch.mjs --analyze-capture <capture.etl> --summary <summary.json>   (elevated step)
//
// The elevated guest script (tests/vm/guest-internet.ps1) captures port 53 with pktmon around --run, then
// calls --analyze-capture, which decides dns-path from the raw capture: the system-DNS control name must be
// seen in plain DNS (proves the capture works) and the secure-DNS name must not be.
// Results: PASS / FAIL / OBSERVED (recorded, no pass rule) / NOT RUN. Uses synthetic profiles only.
import { execFile } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rename, writeFile } from 'node:fs/promises';
import { isAbsolute, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseArgs, promisify } from 'node:util';
import { startFixture } from '../integration/fixture.mjs';
import { startProxy } from '../integration/proxy.mjs';

const run = promisify(execFile);
const repo = fileURLToPath(new URL('../../', import.meta.url));

export const SITES = [
    'https://www.wikipedia.org/', 'https://www.microsoft.com/', 'https://github.com/', 'https://www.google.com/',
    'https://www.naver.com/', 'https://www.youtube.com/', 'https://www.bbc.com/', 'https://www.cloudflare.com/',
];
export const HTTP_ONLY = 'http://neverssl.com/';
export const SMARTSCREEN_DEMO = 'https://demo.smartscreen.msft.net/phishingdemo.html';
export const STUN = 'stun:stun.l.google.com:19302';

export const PLAN = [
    ['dns-path', 'Plain DNS (port 53) carries the system-DNS name but never the secure-DNS name (pktmon capture).'],
    ['webrtc-standard', 'ICE candidates in standard protection (recorded: host/srflx, IPv6).'],
    ['webrtc-proxy', 'Proxy protection: no UDP ICE candidate at all (disable_non_proxied_udp); page reached via the proxy.'],
    ['smartscreen-demo', 'Microsoft SmartScreen demo phishing page with SmartScreen on vs off.'],
    ['site-compat', 'Real sites load under HTTPS-first and strict tracking prevention; an HTTP-only site shows the warning page.'],
    ['ipv6-reachability', 'Whether the engine reaches an IPv6-only site (recorded).'],
    // ADR-0010 D2: the remaining proxy-protection items.
    ['proxy-doh', 'Proxy protection with secure DNS: the page and the DoH provider go through the proxy; the name never appears in plain DNS (capture).'],
    ['quic-proxy', 'QUIC is not used in proxy protection (second load is not h3), against a standard-mode control that does reach h3.'],
    ['https-downgrade-redirect', 'An HTTPS page redirecting to HTTP shows the HTTPS warning page instead of loading the HTTP page.'],
];
export const DOWNGRADE = 'https://httpbin.org/redirect-to?url=http%3A%2F%2Fneverssl.com%2F';
export const QUIC_SITE = 'https://www.google.com/';
const MANUAL = [
    ['sleep-network-switch', 'Sleep/resume and network change while proxy protection is on (manual).'],
    ['windows-smartscreen-override', 'Windows "SmartScreen for Microsoft Edge" off overrides VEIL (needs a guest setting change, manual).'],
    ['public-doh-bootstrap', 'Bootstrap lookup of the DoH provider host: decided from the same capture (recorded by --analyze-capture).'],
];

export function plan(only = []) {
    for (const id of only) if (!PLAN.some(([known]) => known === id)) throw new Error(`Unknown test ID: ${id}`);
    return [
        ...PLAN.map(([id, purpose]) => ({ id, purpose, status: 'NOT RUN', ...(only.length && !only.includes(id) ? { reason: 'Excluded by --only.' } : {}) })),
        ...MANUAL.map(([id, purpose]) => ({ id, purpose, status: 'NOT RUN', reason: 'Manual or capture-derived.' })),
    ];
}

/** Parses an ICE candidate line: transport, address, type. */
export function candidate(line) {
    const parts = typeof line === 'string' && /^candidate:\S+\s/.test(line) ? line.trim().split(/\s+/) : [];
    const transport = (parts[2] ?? '').toLowerCase();
    if (parts.length < 8 || !/^[1-9]\d*$/.test(parts[1]) || !/^\d+$/.test(parts[3]) ||
        !['udp', 'tcp'].includes(transport) || !parts[4] || !/^\d+$/.test(parts[5]) ||
        Number(parts[5]) > 65535 || parts[6] !== 'typ' || !['host', 'srflx', 'prflx', 'relay'].includes(parts[7]))
        return { transport: '', address: '', type: '' };
    return { transport, address: parts[4], type: parts[7] };
}

/** webrtc-proxy rule: no UDP candidates (host, srflx or relay) may be gathered. */
export function judgeProxyCandidates(lines) {
    if (!Array.isArray(lines)) return { status: 'FAIL', detail: 'no candidate list' };
    const parsed = lines.map(candidate);
    if (parsed.some(c => !c.transport)) return { status: 'FAIL', detail: 'invalid ICE candidate or gathering error' };
    const udp = parsed.filter(c => c.transport === 'udp');
    return udp.length ? { status: 'FAIL', detail: `${udp.length} UDP candidate(s): ${udp.map(c => c.type).join(',')}` } : { status: 'PASS', detail: `${lines.length} candidate(s), none UDP` };
}

// Require actual successful HTTPS navigation and an explicit measured transport.
export function measuredProtocol(page, target) {
    if (!page || page.error !== null || page.navError !== 'none') return null;
    try {
        const actual = new URL(page.url), expected = new URL(target);
        if (actual.protocol !== 'https:' || actual.origin !== expected.origin) return null;
        const protocol = JSON.parse(page.eval ?? 'null');
        return typeof protocol === 'string' && /^(h3(?:-\d+)?|h2|http\/1\.1)$/.test(protocol) ? protocol : null;
    } catch { return null; }
}

export function judgeQuic(controlPage, proxyPage, target, viaProxy) {
    const control = measuredProtocol(controlPage, target), measured = measuredProtocol(proxyPage, target);
    if (measured?.startsWith('h3')) return { status: 'FAIL', detail: 'proxy protection used HTTP/3' };
    if (!viaProxy || !measured) return { status: 'FAIL', detail: 'successful proxy navigation and measured protocol not established' };
    if (!control?.startsWith('h3')) return { status: 'OBSERVED', detail: 'standard control did not demonstrate HTTP/3; proxy ' + measured };
    return { status: 'PASS', detail: 'standard ' + control + '; proxy ' + measured };
}

export function judgeWebRtc(control, proxied, { viaProxy, directRequests }) {
    const verdict = judgeProxyCandidates(proxied);
    if (verdict.status !== 'PASS') return verdict;
    if (!viaProxy || directRequests !== 0) return { status: 'FAIL', detail: 'proxy route or zero-direct-request evidence missing' };
    if (!Array.isArray(control) || control.some(line => !candidate(line).transport) ||
        !control.some(line => candidate(line).transport === 'udp'))
        return { status: 'OBSERVED', detail: 'standard control did not demonstrate UDP; ' + verdict.detail };
    return verdict;
}

/** dns-path rule from the raw capture bytes (DNS labels are plain ASCII in port-53 packets). */
export function judgeCapture(bytes, { systemName, dohName }) {
    const has = label => bytes.includes(Buffer.from(label, 'ascii'));
    const control = has(systemName.split('.')[0]);
    const leaked = has(dohName.split('.')[0]);
    const bootstrap = ['cloudflare-dns', 'dns.quad9', 'dns.google'].filter(name => has(name));
    if (!control) return { status: 'FAIL', detail: 'control name not captured: the capture is not trustworthy', bootstrap };
    return leaked
        ? { status: 'FAIL', detail: 'secure-DNS name seen in plain DNS', bootstrap }
        : { status: 'PASS', detail: 'control seen in plain DNS; secure-DNS name absent', bootstrap };
}

async function main() {
    const { values } = parseArgs({ options: {
        plan: { type: 'boolean' }, run: { type: 'boolean' }, 'internet-vm': { type: 'boolean' },
        'desktop-dll': { type: 'string' }, only: { type: 'string', multiple: true, default: [] },
        'analyze-capture': { type: 'string' }, summary: { type: 'string' },
    } });
    if (values['analyze-capture']) {
        const summaryPath = values.summary ?? (() => { throw new Error('--summary is required'); })();
        const summary = JSON.parse(await readFile(summaryPath, 'utf8'));
        const test = summary.tests.find(t => t.id === 'dns-path');
        const bootstrapTest = summary.tests.find(t => t.id === 'public-doh-bootstrap');
        if (!test?.names) throw new Error('dns-path did not run.');
        const bytes = await readFile(values['analyze-capture']);
        const verdict = judgeCapture(bytes, test.names);
        Object.assign(test, verdict);
        // proxy-doh reuses the same capture and the same control name (the capture must be trustworthy).
        const proxyDoh = summary.tests.find(t => t.id === 'proxy-doh');
        if (proxyDoh?.status === 'PENDING-CAPTURE') {
            const result = judgeCapture(bytes, { systemName: test.names.systemName, dohName: proxyDoh.names.dohName });
            Object.assign(proxyDoh, { status: result.status, detail: `${proxyDoh.detail}; capture: ${result.detail}` });
        }
        Object.assign(bootstrapTest, { status: 'OBSERVED', detail: `provider names in plain DNS: ${verdict.bootstrap.join(', ') || 'none'}` });
        await writeFile(summaryPath + '.next', JSON.stringify(summary, null, 2));
        await rename(summaryPath + '.next', summaryPath);
        for (const t of summary.tests) console.log(`${t.status.padEnd(8)} ${t.id}${t.detail ? ' · ' + t.detail : ''}`);
        return;
    }
    const tests = plan(values.only);
    if (!values.run) { console.log(JSON.stringify({ mode: 'PLAN', tests }, null, 2)); return; }
    if (!values['internet-vm']) throw new Error('Use --run --internet-vm only inside the isolated internet test VM.');
    const desktop = values['desktop-dll'] ?? resolve('apps/desktop/bin/Debug/net10.0-windows/Veil.Desktop.dll');
    if (!isAbsolute(desktop)) throw new Error('--desktop-dll must be absolute');

    const root = join(repo, '.veil-dev');
    await mkdir(root, { recursive: true });
    const evidence = await mkdtemp(join(root, 'internet-vm-'));
    const summaryPath = join(evidence, 'summary.json');
    const save = () => writeFile(summaryPath, JSON.stringify({ scope: 'Gate D internet VM', desktop, tests }, null, 2));
    const { server: fixture, origin, direct } = await startFixture();
    const selected = id => tests.find(t => t.id === id && !t.reason);
    let n = 0;

    async function visit(label, url, { network = null, security = null, evalExpr = null, reuse = null } = {}) {
        // reuse: a profile from an earlier visit, so a second load can use what the first one learned (Alt-Svc).
        const profile = reuse ?? join(evidence, `${++n}-${label}`, 'profile');
        if (!reuse) {
            await mkdir(profile, { recursive: true });
            await writeFile(join(profile, 'browser-state.json'), JSON.stringify({ Bookmarks: [], History: [], Tabs: [], Selected: 0, Network: network, Security: security }));
        } else n++;
        const report = join(evidence, `${n}-${label}.json`);
        await run('dotnet', [desktop, '--smoke', profile, origin, 'visit', report], {
            timeout: 180_000, env: { ...process.env, VEIL_TEST_URL: url, VEIL_TEST_EVAL: evalExpr ?? '' },
        });
        const result = JSON.parse(await readFile(report, 'utf8'));
        if (result.error !== null || !Array.isArray(result.checks)) throw new Error('Desktop visit failed: ' + (result.error ?? 'missing checks'));
        const field = name => result.checks.find(c => c.startsWith(`visit: ${name}=`))?.slice(`visit: ${name}=`.length);
        return { error: result.error, url: field('url'), navError: field('error'), title: field('title'), eval: field('eval'), profile };
    }
    const record = async (id, outcome) => { Object.assign(tests.find(t => t.id === id), outcome); await save(); console.log(`${outcome.status.padEnd(8)} ${id} · ${outcome.detail ?? ''}`); };
    const guard = async (id, body) => {
        if (!selected(id)) return;
        try { await record(id, await body()); }
        catch (error) { await record(id, { status: 'FAIL', detail: `${error.name}: ${error.message}` }); }
    };

    try {
        await save();
        await guard('dns-path', async () => {
            const token = () => `veil-${randomBytes(5).toString('hex')}`;
            const systemName = `${token()}.example.com`, dohName = `${token()}.example.com`;
            const system = await visit('dns-system', `https://${systemName}/`);
            const doh = await visit('dns-doh', `https://${dohName}/`, { network: { Mode: 0, DnsTemplate: 'https://cloudflare-dns.com/dns-query' } });
            return { status: 'PENDING-CAPTURE', names: { systemName, dohName }, detail: `system ${system.navError}, doh ${doh.navError}` };
        });
        const webrtcUrl = `${origin}/webrtc?stun=${encodeURIComponent(STUN)}`;
        let controlCandidates = null;
        await guard('webrtc-standard', async () => {
            const page = await visit('webrtc-standard', webrtcUrl, { evalExpr: 'window.veilCandidates' });
            const lines = JSON.parse(page.eval ?? 'null');
            if (page.navError !== 'none' || page.url !== webrtcUrl || !Array.isArray(lines) || lines.some(line => !candidate(line).transport))
                return { status: 'FAIL', detail: 'standard ICE gathering failed or incomplete' };
            controlCandidates = lines;
            const parsed = lines.map(candidate);
            return { status: 'OBSERVED', candidates: parsed.map(c => `${c.transport}/${c.type}/${c.address.includes(':') ? 'ipv6' : c.address.endsWith('.local') ? 'mdns' : 'ipv4'}`), detail: `${lines.length} candidate(s)` };
        });
        await guard('webrtc-proxy', async () => {
            const { proxy, seen, port } = await startProxy();
            try {
                const before = direct.get('/webrtc') ?? 0;
                const page = await visit('webrtc-proxy', webrtcUrl, { network: { Mode: 1, ProxyScheme: 'http', ProxyHost: '127.0.0.1', ProxyPort: port }, evalExpr: 'window.veilCandidates' });
                if (page.navError !== 'none' || page.url !== webrtcUrl) return { status: 'FAIL', detail: 'proxy ICE page did not load' };
                return judgeWebRtc(controlCandidates, JSON.parse(page.eval ?? 'null'), {
                    viaProxy: seen.some(path => path === '/webrtc'), directRequests: (direct.get('/webrtc') ?? 0) - before,
                });
            } finally { proxy.close(); }
        });
        await guard('smartscreen-demo', async () => {
            const on = await visit('smartscreen-on', SMARTSCREEN_DEMO, { security: { SmartScreen: true } });
            const off = await visit('smartscreen-off', SMARTSCREEN_DEMO, { security: { SmartScreen: false } });
            const differs = on.navError !== off.navError || on.title !== off.title;
            return { status: differs && off.navError === 'none' ? 'PASS' : 'OBSERVED', on, off,
                detail: `on: ${on.navError} ${on.title} · off: ${off.navError} ${off.title}` };
        });
        await guard('site-compat', async () => {
            const results = [];
            for (const site of SITES) {
                // Some sites set the title after the load event: wait for it (up to 20 s) instead of reading once.
                const page = await visit(`site-${new URL(site).hostname}`, site, { evalExpr: 'document.title || null' });
                results.push({ site, ok: page.navError === 'none' && page.title !== '""', navError: page.navError });
            }
            const http = await visit('site-http-only', HTTP_ONLY);
            const warned = http.url === 'veil://https-warning';
            const failed = results.filter(r => !r.ok);
            return { status: failed.length === 0 && warned ? 'PASS' : 'FAIL', results, httpOnly: http,
                detail: `${results.length - failed.length}/${results.length} sites loaded; http-only warning ${warned}` + (failed.length ? `; failed ${failed.map(f => f.site + ' ' + f.navError).join(', ')}` : '') };
        });
        await guard('ipv6-reachability', async () => {
            const page = await visit('ipv6', 'https://ipv6.google.com/');
            return { status: 'OBSERVED', detail: `error ${page.navError}, title ${page.title}` };
        });
        await guard('proxy-doh', async () => {
            const { proxy, seen, port } = await startProxy();
            try {
                const dohName = `veil-${randomBytes(5).toString('hex')}.example.com`;
                const network = { Mode: 1, ProxyScheme: 'http', ProxyHost: '127.0.0.1', ProxyPort: port, DnsTemplate: 'https://cloudflare-dns.com/dns-query' };
                const page = await visit('proxy-doh', `https://${dohName}/`, { network });
                const site = await visit('proxy-doh-site', SITES[0], { network });
                const viaProxy = seen.some(target => target.startsWith(`CONNECT ${new URL(SITES[0]).hostname}:`));
                if (!viaProxy) return { status: 'FAIL', detail: 'real site did not go through the proxy' };
                // The synthetic name does not exist; what matters is that it was never sent as plain DNS.
                return { status: 'PENDING-CAPTURE', names: { dohName }, proxied: seen.slice(0, 20),
                    detail: `synthetic name ${page.navError}; ${SITES[0]} ${site.navError} via proxy; DoH host via proxy ${seen.some(t => t.startsWith('CONNECT cloudflare-dns.com:'))}` };
            } finally { proxy.close(); }
        });
        await guard('quic-proxy', async () => {
            const protocol = 'performance.getEntriesByType("navigation")[0]?.nextHopProtocol || null';
            const first = await visit('quic-standard-1', QUIC_SITE, { evalExpr: protocol });
            const second = await visit('quic-standard-2', QUIC_SITE, { evalExpr: protocol, reuse: first.profile });
            const { proxy, port, seen } = await startProxy();
            let proxied, viaProxy = false;
            try {
                const network = { Mode: 1, ProxyScheme: 'http', ProxyHost: '127.0.0.1', ProxyPort: port };
                const p1 = await visit('quic-proxy-1', QUIC_SITE, { network, evalExpr: protocol });
                const before = seen.length;
                proxied = await visit('quic-proxy-2', QUIC_SITE, { network, evalExpr: protocol, reuse: p1.profile });
                viaProxy = seen.slice(before).some(target => target === 'CONNECT ' + new URL(QUIC_SITE).hostname + ':443');
            } finally { proxy.close(); }
            return judgeQuic(second, proxied, QUIC_SITE, viaProxy);
        });
        await guard('https-downgrade-redirect', async () => {
            const page = await visit('https-downgrade', DOWNGRADE);
            if (page.url === 'veil://https-warning') return { status: 'PASS', detail: 'warning page shown for the HTTPS to HTTP redirect' };
            if (page.url?.startsWith('http://')) return { status: 'FAIL', detail: `HTTP page loaded silently: ${page.url}` };
            return { status: 'OBSERVED', detail: `redirect service result: ${page.url} ${page.navError}` };
        });
    } finally {
        fixture.close();
        await save();
    }
    console.log(`Evidence: ${summaryPath}`);
    if (tests.some(t => t.status === 'FAIL')) process.exitCode = 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
