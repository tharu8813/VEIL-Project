// VEIL local threat list tool (ADR-0008). Keys never belong in the repository.
//
//   node scripts/threat-list.mjs keygen <private-key.pem>      prints the public key (SPKI, base64) to embed
//   node scripts/threat-list.mjs sign <threat-list.json> <private-key.pem>   writes threat-list.json.sig
//   node scripts/threat-list.mjs url-key <url>                  prints the SHA-256 key for the list's "urls"
//   node scripts/threat-list.mjs file-key <file>                prints the SHA-256 key for the list's "files"
//
// List format: {"schema":1,"issued":ISO,"expires":ISO,"hosts":[...],"urls":[sha256...],"files":[sha256...]}.
// A listed host also covers its subdomains. The signature is ECDSA P-256 / SHA-256 (DER, base64) over the exact
// file bytes, so the list must not be edited after signing.
import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, sign } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

/** Same normalisation as LocalThreatList.UrlKey: scheme://host[:port]/path?query, no fragment. */
export function urlKey(address) {
    const url = new URL(address);
    return createHash('sha256').update(`${url.protocol}//${url.host}${url.pathname}${url.search}`).digest('hex');
}

export function newKeyPair() {
    const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
    return {
        privatePem: privateKey.export({ type: 'pkcs8', format: 'pem' }),
        spki: publicKey.export({ type: 'spki', format: 'der' }).toString('base64'),
    };
}

export function signList(bytes, privatePem) {
    return sign('sha256', bytes, { key: createPrivateKey(privatePem), dsaEncoding: 'der' }).toString('base64');
}

async function main() {
    const [command, a, b] = process.argv.slice(2);
    if (command === 'keygen' && a) {
        const pair = newKeyPair();
        await writeFile(a, pair.privatePem, { flag: 'wx', mode: 0o600 });
        console.log(pair.spki);
    } else if (command === 'sign' && a && b) {
        const pem = await readFile(b, 'utf8');
        await writeFile(`${a}.sig`, signList(await readFile(a), pem));
        console.log(`signed ${a} with ${createPublicKey(pem).export({ type: 'spki', format: 'der' }).toString('base64').slice(0, 24)}…`);
    } else if (command === 'url-key' && a) {
        console.log(urlKey(a));
    } else if (command === 'file-key' && a) {
        console.log(createHash('sha256').update(await readFile(a)).digest('hex'));
    } else {
        throw new Error('Usage: keygen <pem> | sign <list.json> <pem> | url-key <url> | file-key <file>');
    }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
