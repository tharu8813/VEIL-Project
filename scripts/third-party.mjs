// SPDX-License-Identifier: MPL-2.0
// Third-party notices and CycloneDX SBOM for the redistributed VEIL binaries (ADR-0010 E6).
//
//   node scripts/third-party.mjs --check
//       resolve components and enforce the license policy (CI; writes nothing)
//   node scripts/third-party.mjs --notices <file.md> --sbom <file.cdx.json> --version <v> --commit <sha>
//       write both files (build-package.ps1)
//
// Components: Rust crates linked into veil-vault.exe (normal dependencies for x86_64-pc-windows-msvc,
// from Cargo.lock via `cargo metadata --locked`) and NuGet packages from the locked .NET projects.
// .NET Desktop Runtime and WebView2 Runtime are installed separately and are not redistributed.
// Reads only the local cargo registry and NuGet cache; run after a locked build or restore.
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';

const repo = resolve(dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), '..');
const args = process.argv.slice(2);
const option = name => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const check = args.includes('--check');
const noticesPath = option('--notices'), sbomPath = option('--sbom');
const version = option('--version'), commit = option('--commit');
if (!check && !(noticesPath && sbomPath && version && /^[0-9a-f]{40}$/.test(commit ?? '')))
    throw new Error('Use --check, or --notices <md> --sbom <json> --version <v> --commit <40-hex sha>.');

// Licenses VEIL may redistribute in binary form without extra obligations beyond notices.
const allowed = new Set(['MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', 'Zlib', 'Unlicense',
    'Unicode-3.0', 'Unicode-DFS-2016', 'CC0-1.0', 'MPL-2.0', 'LicenseRef-Microsoft-WebView2-SDK']);

/** True when the SPDX expression (AND/OR/parentheses; legacy "/" means OR) is satisfiable by the allowlist. */
function satisfiable(expression) {
    const tokens = expression.replace(/\//g, ' OR ').match(/\(|\)|[A-Za-z0-9.+-]+/g) ?? [];
    let i = 0;
    const primary = () => {
        if (tokens[i] === '(') { i++; const value = or(); if (tokens[i++] !== ')') throw new Error(expression); return value; }
        const id = tokens[i++];
        if (!id || ['AND', 'OR'].includes(id)) throw new Error(`bad license expression: ${expression}`);
        return allowed.has(id);
    };
    const and = () => { let value = primary(); while (tokens[i] === 'AND') { i++; value = primary() && value; } return value; };
    const or = () => { let value = and(); while (tokens[i] === 'OR') { i++; value = and() || value; } return value; };
    const result = or();
    if (i !== tokens.length) throw new Error(`bad license expression: ${expression}`);
    return result;
}

const mitText = holders => `MIT License

Copyright (c) ${holders}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.`;

const licenseFile = name => /^(LICEN[CS]E|COPYING|NOTICE|UNLICENSE)/i.test(name);
const readTexts = directory => existsSync(directory)
    ? readdirSync(directory).filter(licenseFile).sort().map(name => ({ name, text: readFileSync(join(directory, name), 'utf8') }))
    : [];

// Rust crates in veil-vault.exe.
const manifest = join(repo, 'crates/veil-security/Cargo.toml');
const metadata = JSON.parse(execFileSync('cargo', ['metadata', '--locked', '--format-version', '1', '--manifest-path', manifest,
    '--filter-platform', 'x86_64-pc-windows-msvc'], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }));
const packages = new Map(metadata.packages.map(p => [p.id, p]));
const nodes = new Map(metadata.resolve.nodes.map(n => [n.id, n]));
const reachable = new Set();
for (const stack = [metadata.resolve.root]; stack.length;) {
    const id = stack.pop();
    if (reachable.has(id)) continue;
    reachable.add(id);
    for (const dep of nodes.get(id).deps) if (dep.dep_kinds.some(kind => kind.kind === null)) stack.push(dep.pkg);
}
reachable.delete(metadata.resolve.root);
const checksums = new Map();
for (const block of readFileSync(join(repo, 'crates/veil-security/Cargo.lock'), 'utf8').split('[[package]]')) {
    const name = block.match(/^name = "(.+)"$/m)?.[1], ver = block.match(/^version = "(.+)"$/m)?.[1], sum = block.match(/^checksum = "([0-9a-f]{64})"$/m)?.[1];
    if (name && ver && sum) checksums.set(`${name}@${ver}`, sum);
}
const components = [...reachable].map(id => packages.get(id)).map(p => ({
    ecosystem: 'cargo', name: p.name, version: p.version, license: p.license ?? null,
    authors: p.authors ?? [], source: p.repository ?? `https://crates.io/crates/${p.name}`,
    purl: `pkg:cargo/${p.name}@${p.version}`, sha256: checksums.get(`${p.name}@${p.version}`) ?? null,
    texts: readTexts(dirname(p.manifest_path)),
}));

// NuGet packages redistributed with the .NET applications.
const nugetRoot = process.env.NUGET_PACKAGES ?? join(homedir(), '.nuget', 'packages');
for (const lock of ['apps/desktop/packages.lock.json', 'apps/broker/Veil.VolumeBroker/packages.lock.json']) {
    for (const deps of Object.values(JSON.parse(readFileSync(join(repo, lock), 'utf8')).dependencies)) {
        for (const [name, entry] of Object.entries(deps)) {
            if (components.some(c => c.ecosystem === 'nuget' && c.name === name && c.version === entry.resolved)) continue;
            const folder = join(nugetRoot, name.toLowerCase(), entry.resolved);
            const nuspec = existsSync(folder) ? readFileSync(join(folder, `${name.toLowerCase()}.nuspec`), 'utf8') : '';
            const expression = nuspec.match(/<license type="expression">([^<]+)<\/license>/)?.[1] ??
                (name === 'Microsoft.Web.WebView2' ? 'LicenseRef-Microsoft-WebView2-SDK' : null);
            components.push({
                ecosystem: 'nuget', name, version: entry.resolved, license: expression,
                authors: [nuspec.match(/<authors>([^<]+)<\/authors>/)?.[1] ?? ''].filter(Boolean),
                source: nuspec.match(/<projectUrl>([^<]+)<\/projectUrl>/)?.[1] ?? `https://www.nuget.org/packages/${name}`,
                purl: `pkg:nuget/${name}@${entry.resolved}`,
                sha512: entry.contentHash ? Buffer.from(entry.contentHash, 'base64').toString('hex') : null,
                texts: readTexts(folder),
            });
        }
    }
}
components.sort((a, b) => `${a.ecosystem}/${a.name}@${a.version}`.localeCompare(`${b.ecosystem}/${b.name}@${b.version}`));

// Policy: every component has an allowed license; crates without bundled texts get the SPDX text of the
// same license from another crate and are flagged for review.
const problems = [];
const fallbacks = [];
const reference = new Map();
for (const c of components) for (const t of c.texts) {
    const id = /APACHE/i.test(t.name) ? 'Apache-2.0' : /MIT/i.test(t.name) ? 'MIT' : null;
    if (id && !reference.has(id)) reference.set(id, t.text);
}
for (const c of components) {
    if (!c.license) { problems.push(`${c.purl}: no license metadata`); continue; }
    try { if (!satisfiable(c.license)) problems.push(`${c.purl}: license not allowed (${c.license})`); }
    catch (error) { problems.push(`${c.purl}: ${error.message}`); }
    if (c.texts.length === 0) {
        // Prefer Apache-2.0 (its text carries no per-project copyright line). An MIT-only crate gets the MIT
        // text with the copyright holders from its metadata, never another crate's copyright line.
        const holders = c.authors.map(a => a.replace(/\s*<[^>]*>/, '')).join(', ') || c.name + ' authors';
        let text = null, id = null;
        if (c.license.includes('Apache-2.0') && reference.has('Apache-2.0')) { id = 'Apache-2.0'; text = reference.get(id); }
        else if (c.license.includes('MIT')) { id = 'MIT'; text = mitText(holders); }
        if (!text) { problems.push(`${c.purl}: no license text`); continue; }
        c.texts = [{ name: `${id} (패키지에 파일 없음 · 표준 본문 · 저작권자: ${holders})`, text }];
        fallbacks.push(c.purl);
    }
}
if (problems.length) throw new Error(`third-party license policy failed:\n${problems.join('\n')}`);
const summary = `${components.length} components (${components.filter(c => c.ecosystem === 'cargo').length} cargo, ` +
    `${components.filter(c => c.ecosystem === 'nuget').length} nuget), ${fallbacks.length} with standard license text`;
if (check) { console.log(`third-party policy: PASS · ${summary}`); process.exit(0); }

// Notices: identical texts are printed once with every component they apply to.
const groups = new Map();
for (const c of components) for (const t of c.texts) {
    const key = createHash('sha256').update(t.text.replace(/\r\n/g, '\n').trim()).digest('hex');
    if (!groups.has(key)) groups.set(key, { text: t.text.replace(/\r\n/g, '\n').trim(), files: new Set(), users: [] });
    groups.get(key).files.add(t.name);
    groups.get(key).users.push(`${c.name} ${c.version}`);
}
const lines = [
    '# VEIL 제3자 구성 요소 고지 (Third-party notices)', '',
    `VEIL ${version} · 소스 커밋 ${commit}`, '',
    'VEIL 자체 코드는 MPL-2.0입니다(LICENSE, NOTICE). 아래 구성 요소는 각 라이선스에 따라 함께 배포됩니다.',
    '복수 라이선스(OR) 구성 요소는 허용 목록에 있는 라이선스로 사용합니다. 예: `Apache-2.0 OR GPL-2.0-only`는 Apache-2.0으로 사용합니다.', '',
    '함께 배포하지 않는 구성 요소: .NET 10 Desktop Runtime, Microsoft Edge WebView2 Runtime. 각각 Microsoft의 공식 설치 경로와 라이선스를 따릅니다.', '',
    '## 구성 요소', '', '| 구성 요소 | 버전 | 라이선스 | 출처 |', '|---|---|---|---|',
    ...components.map(c => `| ${c.name} (${c.ecosystem}) | ${c.version} | ${c.license} | ${c.source} |`), '',
];
if (fallbacks.length)
    lines.push('## 확인 필요', '', '다음 구성 요소는 패키지에 라이선스 파일이 없어, 같은 라이선스의 SPDX 표준 본문과 메타데이터상의 저작권자를 첨부했습니다.', '',
        ...fallbacks.map(p => `- ${p}`), '');
lines.push('## 라이선스 본문', '');
for (const group of groups.values())
    lines.push(`### ${[...group.files].join(', ')}`, '', `적용 대상: ${group.users.join(', ')}`, '', '```text', group.text, '```', '');
writeFileSync(noticesPath, lines.join('\n'));

const serial = createHash('sha256').update(`${commit}:${version}`).digest('hex');
const sbom = {
    bomFormat: 'CycloneDX', specVersion: '1.5', version: 1,
    serialNumber: `urn:uuid:${serial.slice(0, 8)}-${serial.slice(8, 12)}-5${serial.slice(13, 16)}-8${serial.slice(17, 20)}-${serial.slice(20, 32)}`,
    metadata: {
        tools: { components: [{ type: 'application', name: 'veil-third-party', version: '1' }] },
        component: { type: 'application', name: 'VEIL', version, licenses: [{ license: { id: 'MPL-2.0' } }],
            properties: [{ name: 'veil:sourceCommit', value: commit }, { name: 'veil:channel', value: 'internal' }] },
    },
    components: components.map(c => ({
        type: 'library', name: c.name, version: c.version, purl: c.purl,
        licenses: [{ expression: c.license }],
        hashes: [c.sha256 && { alg: 'SHA-256', content: c.sha256 }, c.sha512 && { alg: 'SHA-512', content: c.sha512 }].filter(Boolean),
        externalReferences: [{ type: 'vcs', url: c.source }],
    })),
};
writeFileSync(sbomPath, JSON.stringify(sbom, null, 2));
console.log(`third-party notices and SBOM written · ${summary}`);
