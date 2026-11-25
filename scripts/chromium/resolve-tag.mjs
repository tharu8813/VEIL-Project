// SPDX-License-Identifier: MPL-2.0
// Resolve an exact upstream version tag, without cloning or executing upstream code.
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
export const upstream = 'https://chromium.googlesource.com/chromium/src.git';
export function validateVersion(version) {
  if (typeof version !== 'string' || !/^\d{1,4}\.\d{1,4}\.\d{1,8}\.\d{1,6}$/.test(version)) throw new Error('Expected numeric Chromium version');
  return version;
}
export function parseTagRefs(version, stdout) {
  const tag = 'refs/tags/' + validateVersion(version);
  const refs = new Map();
  for (const line of stdout.trim().split(/\r?\n/).filter(Boolean)) {
    const m = /^([0-9a-f]{40})\t(\S+)$/.exec(line);
    if (!m || ![tag, tag+'^{}'].includes(m[2]) || refs.has(m[2])) throw new Error('Unexpected or duplicate remote ref');
    refs.set(m[2],m[1]);
  }
  if (!refs.has(tag)) throw new Error('Exact upstream tag not found');
  return {version, upstream, tag, tagObject:refs.get(tag), revision:refs.get(tag+'^{}') ?? refs.get(tag), status:'TAG_RESOLVED_ONLY', sourceCheckout:'NOT RUN', toolchain:'NOT RUN', build:'NOT RUN', sandbox:'NOT RUN'};
}
export function resolveTag(version) {
  const tag = 'refs/tags/' + validateVersion(version);
  const env = {...process.env, GIT_TERMINAL_PROMPT:'0', GIT_CONFIG_NOSYSTEM:'1', GIT_CONFIG_GLOBAL:process.platform==='win32'?'NUL':'/dev/null'};
  // Avoid executing repository-local git config; run outside the VEIL checkout.
  const stdout = execFileSync('git',['ls-remote','--exit-code',upstream,tag,tag+'^{}'],{cwd:process.platform==='win32'?process.env.SystemRoot ?? 'C:\\Windows':'/',env,encoding:'utf8',timeout:90000,maxBuffer:1024*1024});
  return parseTagRefs(version,stdout);
}
if (process.argv[1] && import.meta.url===pathToFileURL(process.argv[1]).href) {
  try { if(process.argv.length!==3) throw new Error('Usage: node resolve-tag.mjs <version>'); console.log(JSON.stringify(resolveTag(process.argv[2]),null,2)); }
  catch(error) { console.error(error.message); process.exitCode=1; }
}
