// Minimal forward proxy for tests: absolute-URI HTTP requests (marked with x-veil-proxied) and CONNECT tunnels.
// `seen` lists every request path and CONNECT target in order.
import { createServer, request as httpRequest } from 'node:http';
import { connect } from 'node:net';

export function startProxy() {
    const seen = [];
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
        const tunnel = connect(Number(port), host, () => { socket.write('HTTP/1.1 200 OK\r\n\r\n'); tunnel.pipe(socket); socket.pipe(tunnel); });
        tunnel.on('error', () => socket.destroy());
        socket.on('error', () => tunnel.destroy());
    });
    return new Promise(resolveProxy => proxy.listen(0, '127.0.0.1', () => resolveProxy({ proxy, seen, port: proxy.address().port })));
}
