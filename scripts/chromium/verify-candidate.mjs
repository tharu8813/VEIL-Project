// SPDX-License-Identifier: MPL-2.0
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { resolveTag, upstream, validateVersion } from './resolve-tag.mjs';
export function validatePin(pin) {
  if (!pin || pin.schemaVersion !== 1 || pin.upstream !== upstream || pin.tag !== 'refs/tags/'+validateVersion(pin.version) || !/^[a-f0-9]{40}$/.test(pin.revision) || pin.status !== 'MIRROR_OBSERVED_REMOTE_CHECK_REQUIRED') throw new Error('Invalid candidate pin');
  return pin;
}
export function compareRemote(pin, observed) {
  validatePin(pin);
  if (!observed || observed.upstream !== pin.upstream || observed.version !== pin.version || observed.tag !== pin.tag || observed.revision !== pin.revision || observed.status !== 'TAG_RESOLVED_ONLY') throw new Error('Upstream tag does not match reviewed candidate');
  return {version:pin.version, revision:pin.revision, status:'REMOTE_MATCH_ONLY', checkout:'NOT RUN', build:'NOT RUN', sandbox:'NOT RUN'};
}
export function readPin() {
  return validatePin(JSON.parse(readFileSync(new URL('../../chromium/candidate.json',import.meta.url),'utf8')));
}
if(process.argv[1] && import.meta.url===pathToFileURL(process.argv[1]).href) {
  try {
    const pin=readPin();
    if(process.argv.length===2) console.log(JSON.stringify({...pin,checkout:'NOT RUN',build:'NOT RUN',sandbox:'NOT RUN'},null,2));
    else if(process.argv.length===3 && process.argv[2]==='--verify-remote') console.log(JSON.stringify(compareRemote(pin,resolveTag(pin.version)),null,2));
    else throw new Error('Usage: node verify-candidate.mjs [--verify-remote]');
  } catch(error) { console.error(error.message); process.exitCode=1; }
}
