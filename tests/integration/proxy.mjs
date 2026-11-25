// Minimal forward proxy for tests: absolute-URI HTTP requests (marked with x-veil-proxied) and CONNECT tunnels.
// `seen` lists every request path and CONNECT target in order.
import { createServer, request as httpRequest } from 'node:http';
import { connect } from 'node:net';

export function startProxy({ rejectConnectHosts = [], listenPort = 0 } = {}) {
    const seen = [], connected = [];
    const rejected = new Set(rejectConnectHosts.map(host => host.toLowerCase()));
    const proxy = createServer((request, response) => {
        const target = new URL(request.url);
        seen.push(target.pathname);
        const upstream = httpRequest(target, { method: request.method, headers: { ...request.headers, 'x-veil-proxied': '1' } }, reply => {
            response.writeHead(reply.statusCode, reply.headers);
            reply.pipe(response);
        });
        upstream.on('error', () => response.destroy());
        request.pipe(upstream);
    });
    proxy.on('connect', (request, socket) => {
        seen.push(`CONNECT ${request.url}`);
        const [host, port] = request.url.split(':');
        // Synthetic negative DNS controls must never be resolved by this test proxy itself.
        if (rejected.has(host.toLowerCase())) {
            socket.end('HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n');
            return;
        }
        const tunnel = connect(Number(port), host, () => {
            connected.push('CONNECT ' + request.url);
            socket.write('HTTP/1.1 200 OK\r\n\r\n');
            tunnel.pipe(socket);
            socket.pipe(tunnel);
        });
        tunnel.on('error', () => socket.destroy());
        socket.on('error', () => tunnel.destroy());
    });
    return new Promise((resolveProxy, reject) => {
        proxy.once('error', reject);
        proxy.listen(listenPort, '127.0.0.1', () => resolveProxy({ proxy, seen, connected, port: proxy.address().port }));
    });
}
