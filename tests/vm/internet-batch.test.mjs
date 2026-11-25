import assert from 'node:assert/strict';
import test from 'node:test';
import { candidate, judgeCapture, judgeProxyCandidates, judgeWebRtc, judgeQuic, judgeProxyDohRoute, judgeProxyDohCapture, matchesDowngradeControl, judgeDowngrade, plan } from './internet-batch.mjs';

test('plan lists every Gate D item as NOT RUN and rejects unknown IDs', () => {
    const items = plan();
    assert.ok(items.every(item => item.status === 'NOT RUN'));
    for (const id of ['dns-path', 'webrtc-proxy', 'smartscreen-demo', 'site-compat', 'sleep-network-switch'])
        assert.ok(items.some(item => item.id === id), id);
    assert.throws(() => plan(['nope']));
    assert.equal(plan(['site-compat']).find(item => item.id === 'dns-path').reason, 'Excluded by --only.');
});

test('ICE candidates are parsed and any UDP candidate fails proxy mode', () => {
    const host = 'candidate:1 1 udp 2122260223 192.168.1.5 54321 typ host generation 0';
    const srflx = 'candidate:2 1 udp 1686052607 203.0.113.9 54321 typ srflx raddr 192.168.1.5 rport 54321';
    const tcp = 'candidate:3 1 tcp 1518280447 192.168.1.5 9 typ host tcptype active';
    assert.deepEqual(candidate(srflx), { transport: 'udp', address: '203.0.113.9', type: 'srflx' });
    assert.equal(judgeProxyCandidates([tcp]).status, 'PASS');
    assert.equal(judgeProxyCandidates([]).status, 'PASS');
    assert.equal(judgeProxyCandidates([tcp, host]).status, 'FAIL');
    assert.equal(judgeProxyCandidates([srflx]).status, 'FAIL');
    assert.equal(judgeProxyCandidates(null).status, 'FAIL');
});

const wire = name => Buffer.concat([...name.split('.').map(label => Buffer.concat([
    Buffer.from([label.length]), Buffer.from(label, 'ascii'),
])), Buffer.from([0])]);

test('dns-path needs a full control name and rejects a case-insensitive protected name leak', () => {
    const names = { systemName: 'veil-aaaa.example.com', dohName: 'veil-bbbb.example.com' };
    const control = wire(names.systemName);
    assert.equal(judgeCapture(control, names).status, 'PASS');
    assert.equal(judgeCapture(Buffer.concat([control, wire('VEIL-BBBB.Example.COM')]), names).status, 'FAIL');
    assert.equal(judgeCapture(wire(names.dohName), names).status, 'FAIL');
    assert.match(judgeCapture(Buffer.from('veil-aaaa'), names).detail, /not trustworthy/);
    assert.match(judgeCapture(wire('veil-aaaa.other.com'), names).detail, /not trustworthy/);
    assert.deepEqual(judgeCapture(Buffer.concat([control, wire('cloudflare-dns.com'), wire('dns.quad9.net')]), names).bootstrap,
        ['cloudflare-dns.com', 'dns.quad9.net']);
    assert.throws(() => judgeCapture(control, { ...names, dohName: names.systemName }));
});

test('proxy DoH requires successful site and tunnel, and distinguishes missing provider evidence', () => {
    const target = 'https://www.wikipedia.org/', dohName = 'veil-bbbb.example.com', systemName = 'veil-aaaa.example.com';
    const site = { error: null, navError: 'none', url: target, eval: '"h2"' };
    const tunnels = ['CONNECT www.wikipedia.org:443'], seen = ['CONNECT ' + dohName + ':443', ...tunnels];
    assert.equal(judgeProxyDohRoute(site, target, [], seen, dohName).status, 'FAIL');
    assert.equal(judgeProxyDohRoute({ ...site, navError: 'Timeout' }, target, tunnels, seen, dohName).status, 'FAIL');
    assert.equal(judgeProxyDohRoute(site, target, tunnels, [], dohName).status, 'FAIL');
    const missing = { ...judgeProxyDohRoute(site, target, tunnels, seen, dohName), names: { dohName } };
    assert.equal(judgeProxyDohCapture(wire(systemName), systemName, missing).status, 'OBSERVED');
    const established = { ...judgeProxyDohRoute(site, target, [...tunnels, 'CONNECT cloudflare-dns.com:443'], seen, dohName), names: { dohName } };
    assert.equal(judgeProxyDohCapture(wire(systemName), systemName, established).status, 'PASS');
    assert.equal(judgeProxyDohCapture(Buffer.concat([wire(systemName), wire(dohName)]), systemName, established).status, 'FAIL');
    assert.equal(judgeProxyDohCapture(wire(dohName), systemName, missing).status, 'FAIL');
});

test('ICE errors, malformed candidates, missing control and direct requests cannot pass', () => {
    const udp = 'candidate:1 1 udp 2122260223 192.168.1.5 54321 typ host';
    for (const lines of [['error OperationError'], ['garbage'], [null], ['candidate:1 1 udp']])
        assert.equal(judgeProxyCandidates(lines).status, 'FAIL');
    assert.equal(judgeWebRtc([udp], [], { viaProxy: true, directRequests: 0 }).status, 'PASS');
    assert.equal(judgeWebRtc([], [], { viaProxy: true, directRequests: 0 }).status, 'OBSERVED');
    assert.equal(judgeWebRtc([udp], [], { viaProxy: true, directRequests: 1 }).status, 'FAIL');
    assert.equal(judgeWebRtc([udp], [], { viaProxy: false, directRequests: 0 }).status, 'FAIL');
});
test('QUIC requires a real HTTP/3 control, successful proxy load, route and measured protocol', () => {
    const target = 'https://www.google.com/';
    const page = protocol => ({ error: null, url: target, navError: 'none', eval: JSON.stringify(protocol) });
    assert.equal(judgeQuic(page('h3'), page('h2'), target, true).status, 'PASS');
    assert.equal(judgeQuic(page('h3-29'), page('http/1.1'), target, true).status, 'PASS');
    assert.equal(judgeQuic(page('h3'), page('h3-29'), target, true).status, 'FAIL');
    assert.equal(judgeQuic(page('h2'), page('h2'), target, true).status, 'OBSERVED');
    for (const bad of [page(null), page(''), page('unknown'), { ...page('h2'), navError: 'Timeout' },
        { ...page('h2'), url: 'veil://https-warning' }, { ...page('h2'), error: 'failed' },
        { ...page('h2'), url: 'https://other.test/' }])
        assert.equal(judgeQuic(page('h3'), bad, target, true).status, 'FAIL');
    assert.equal(judgeQuic(page('h3'), page('h2'), target, false).status, 'FAIL');
});


test('downgrade control must be an actual HTTPS redirect to the exact HTTP target', () => {
    const source = 'https://httpbin.org/redirect', target = 'http://neverssl.com/';
    assert.equal(matchesDowngradeControl(302, target, source, target), true);
    assert.equal(matchesDowngradeControl(308, target, source, target), true);
    for (const [status, location, from] of [[200, target, source], [302, null, source],
        [302, 'https://neverssl.com/', source], [302, 'http://other.test/', source],
        [302, target, 'http://httpbin.org/redirect'], [302, 'not a URL', source]])
        assert.equal(matchesDowngradeControl(status, location, from, target), false);
});

test('downgrade warning alone is insufficient; require cancellation, native bar and no exception', () => {
    const target = 'http://neverssl.com/';
    const control = { matched: true };
    const page = { error: null, url: 'veil://https-warning', pendingHttp: target, cancelledHttpRedirects: '1',
        nativeHttpBar: 'True', httpExceptions: '0' };
    assert.equal(judgeDowngrade(control, page, target).status, 'PASS');
    assert.equal(judgeDowngrade({ matched: false }, page, target).status, 'OBSERVED');
    for (const change of [{ cancelledHttpRedirects: '0' }, { cancelledHttpRedirects: undefined },
        { pendingHttp: 'http://other.test/' }, { nativeHttpBar: 'False' }, { httpExceptions: '1' },
        { httpExceptions: undefined }, { error: 'failed' }, { url: target }])
        assert.equal(judgeDowngrade(control, { ...page, ...change }, target).status, 'FAIL');
    assert.equal(judgeDowngrade(control, { ...page, url: 'https://neverssl.com/' }, target).status, 'OBSERVED');
    assert.equal(judgeDowngrade({ matched: false }, { ...page, url: target }, target).status, 'FAIL');
});


test('live proxy transition requires the same engine, exact origin traffic and no direct requests', async () => {
    const { validateTransition } = await import('../phase5/proxy-transition.mjs');
    const records = [
        { phase: 'before', pid: 101, loaded: true, blocked: false, stored: true, entries: 1 },
        { phase: 'blocked', pid: 101, loaded: false, blocked: true, stored: false, entries: 1, failureName: 'HttpRequestException' },
        { phase: 'after', pid: 101, loaded: true, blocked: false, stored: true, entries: 2 },
    ];
    const routes = [{ page: 1, vault: 1 }, { page: 0, vault: 0 }, { page: 1, vault: 1 }];
    assert.equal(validateTransition(records, 0, routes).status, 'PASS');
    assert.throws(() => validateTransition(records, 1, routes));
    assert.throws(() => validateTransition(records.slice(0, 2), 0, routes));
    for (const change of [{ pid: 102 }, { loaded: true }, { blocked: false }, { stored: true }, { entries: 2 }, { failureName: null }])
        assert.throws(() => validateTransition([records[0], { ...records[1], ...change }, records[2]], 0, routes));
    assert.throws(() => validateTransition(records, 0, [routes[0], { page: 1, vault: 0 }, routes[2]]));
    assert.throws(() => validateTransition(records, 0, [routes[0], routes[1], { page: 0, vault: 1 }]));
});
