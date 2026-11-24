// Throwaway loopback TLS certificate for engine tests (trusted only through the Debug-only
// VEIL_TEST_TRUSTED_SPKI hook). Generated with openssl, or taken from VEIL_TEST_CERT_DIR when the
// offline VM bundle ships a certificate made on the host (the guest has no openssl).
//
//   node tests/integration/test-cert.mjs <dir>     writes test-key.pem and test-cert.pem into <dir>
import { execFile } from 'node:child_process';
import { X509Certificate, createHash } from 'node:crypto';
import { copyFile, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { promisify } from 'node:util';

const run = promisify(execFile);

/** Returns { key, cert, spki } for 127.0.0.1, written as <prefix>-key.pem / <prefix>-cert.pem in directory. */
export async function testCertificate(directory, prefix = 'test') {
    const key = join(directory, `${prefix}-key.pem`), cert = join(directory, `${prefix}-cert.pem`);
    const shipped = process.env.VEIL_TEST_CERT_DIR;
    if (shipped) {
        await copyFile(join(shipped, 'test-key.pem'), key);
        await copyFile(join(shipped, 'test-cert.pem'), cert);
    } else {
        await run('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', cert, '-days', '7',
            '-subj', '/CN=127.0.0.1', '-addext', 'subjectAltName=IP:127.0.0.1']);
    }
    const pem = await readFile(cert);
    const certificate = new X509Certificate(pem);
    if (!certificate.checkIP('127.0.0.1') || new Date(certificate.validTo) < new Date())
        throw new Error('The test certificate must be valid for 127.0.0.1 and not expired.');
    const spki = createHash('sha256').update(certificate.publicKey.export({ type: 'spki', format: 'der' })).digest('base64');
    return { key: await readFile(key), cert: pem, spki };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const directory = process.argv[2];
    if (!directory) throw new Error('Usage: node tests/integration/test-cert.mjs <dir>');
    delete process.env.VEIL_TEST_CERT_DIR;
    const { spki } = await testCertificate(directory);
    console.log(`test certificate written to ${directory} (SPKI ${spki})`);
}
