import assert from 'node:assert/strict';
import test from 'node:test';
import { candidate, judgeCapture, judgeProxyCandidates, plan } from './internet-batch.mjs';

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

test('dns-path needs the control name in the capture and fails when the secure-DNS name leaks', () => {
    const names = { systemName: 'veil-aaaa.example.com', dohName: 'veil-bbbb.example.com' };
    const packet = (...labels) => Buffer.concat(labels.map(label => Buffer.concat([Buffer.from([label.length]), Buffer.from(label)])));
    assert.equal(judgeCapture(packet('veil-aaaa', 'example', 'com'), names).status, 'PASS');
    assert.equal(judgeCapture(packet('veil-aaaa', 'example', 'com', 'veil-bbbb'), names).status, 'FAIL');
    assert.match(judgeCapture(Buffer.alloc(10), names).detail, /not trustworthy/);
    assert.deepEqual(judgeCapture(packet('veil-aaaa', 'cloudflare-dns', 'com'), names).bootstrap, ['cloudflare-dns']);
});
