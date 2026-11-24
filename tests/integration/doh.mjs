// Local DNS-over-HTTPS responder for engine tests (RFC 8484, GET and POST).
// A throwaway self-signed certificate for 127.0.0.1 is trusted only through the Debug-only
// VEIL_TEST_TRUSTED_SPKI hook. *.test names resolve to 127.0.0.1; every other name gets NXDOMAIN.
// Every query name is recorded so tests can see which names the engine looked up.
import { createServer } from 'node:https';
import { testCertificate } from './test-cert.mjs';

function answer(query, queried) {
    let offset = 12;
    const labels = [];
    while (query[offset] !== 0) { labels.push(query.subarray(offset + 1, offset + 1 + query[offset]).toString('ascii')); offset += query[offset] + 1; }
    const name = labels.join('.').toLowerCase();
    const type = query.readUInt16BE(offset + 1);
    queried.push(`${name}/${type}`);
    const question = query.subarray(12, offset + 5);
    const ours = name.endsWith('.test');
    const header = Buffer.alloc(12);
    query.copy(header, 0, 0, 2); // id
    header.writeUInt16BE(ours ? 0x8180 : 0x8183, 2); // response, RD+RA, NOERROR / NXDOMAIN
    header.writeUInt16BE(1, 4);
    const records = ours && type === 1
        ? Buffer.from([0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 127, 0, 0, 1]) : Buffer.alloc(0);
    header.writeUInt16BE(records.length ? 1 : 0, 6);
    return Buffer.concat([header, question, records]);
}

/** Starts the responder. Needs openssl on PATH (e.g. Git for Windows) or a shipped VEIL_TEST_CERT_DIR. */
export async function startDoh(directory) {
    const { key, cert, spki } = await testCertificate(directory, 'doh');
    const queried = [];
    const server = createServer({ key, cert }, (request, response) => {
        const send = query => {
            try { response.writeHead(200, { 'Content-Type': 'application/dns-message', 'Cache-Control': 'max-age=0' }).end(answer(query, queried)); }
            catch { response.writeHead(400).end(); }
        };
        if (request.method === 'GET') {
            const dns = new URL(request.url, 'https://127.0.0.1').searchParams.get('dns') ?? '';
            return send(Buffer.from(dns.replace(/-/g, '+').replace(/_/g, '/'), 'base64'));
        }
        const chunks = [];
        request.on('data', chunk => chunks.push(chunk)).on('end', () => send(Buffer.concat(chunks)));
    });
    await new Promise(done => server.listen(0, '127.0.0.1', done));
    const template = `https://127.0.0.1:${server.address().port}/dns-query`;
    const stop = async () => { await new Promise(done => server.close(done)); server.closeAllConnections?.(); };
    return { server, template, spki, queried, stop };
}
