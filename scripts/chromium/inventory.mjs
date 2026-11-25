// SPDX-License-Identifier: MPL-2.0
// Read-only inventory of tracked text; heuristic, not a security gate.
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const git = (...args) => execFileSync('git', args, { cwd: root, maxBuffer: 32 * 1024 * 1024 }).toString('utf8');
const files = git('ls-files', '-z', '--', 'apps', 'crates', 'scripts', 'tests', '.github').split('\0').filter(Boolean);
const patterns = [
  ['webview2', /webview2|CoreWebView2|msedgewebview2/i],
  ['profile', /ProtectedProfile|BitLocker|ProfileScratch|ProfileGate|userDataFolder/i],
  ['vault', /veil-vault|DownloadVault|DownloadStarting/i],
  ['network', /NetworkPolicy|DoH|SmartScreen|WebRTC|proxy/i],
  ['broker', /NamedPipe|VolumeBroker|BrokerClient/i]
];
const extensions = /\.(?:cs|csproj|xaml|rs|toml|ps1|mjs|js|cjs|ts|json|yml|yaml|xml|props|targets|cmd|bat|py|cpp|h|gn|gni)$/i;
const results = [];
const unreadable = [];
for (const path of files) {
  if (!extensions.test(path) || path === relative(root, fileURLToPath(import.meta.url)).replaceAll('\\','/')) continue;
  const matches = {};
  let lines;
  try { lines = readFileSync(resolve(root, path), 'utf8').split(/\r?\n/); }
  catch (error) { unreadable.push({path, code:error.code ?? 'READ_ERROR'}); continue; }
  for (const [id, pattern] of patterns) {
    const hits = [];
    for (let i = 0; i < lines.length; i++) if (pattern.test(lines[i])) hits.push(i + 1);
    if (hits.length) matches[id] = hits;
  }
  if (Object.keys(matches).length) results.push({path, matches});
}
console.log(JSON.stringify({commit:git('rev-parse','HEAD').trim(), dirty:git('status','--porcelain').length>0, heuristic:true, tracked:files.length, unreadable, matches:results}, null, 2));
if (unreadable.length) process.exitCode = 1;
