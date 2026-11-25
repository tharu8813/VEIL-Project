// SPDX-License-Identifier: MPL-2.0
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readPin, validatePin, compareRemote } from '../../scripts/chromium/verify-candidate.mjs';
const pin=readPin();
const observed={version:pin.version,upstream:pin.upstream,tag:pin.tag,revision:pin.revision,status:'TAG_RESOLVED_ONLY'};
test('candidate is only a source identity observation',()=>{assert.match(pin.depotToolsRevision,/^[a-f0-9]{40}$/);assert.equal(compareRemote(pin,observed).build,'NOT RUN');});
test('mismatch rejects checkout authorization',()=>{for(const patch of [{revision:'0'.repeat(40)},{tag:'refs/tags/other'},{upstream:'https://example.org'},{version:'1.2.3.4'},{status:'PASS'}])assert.throws(()=>compareRemote(pin,{...observed,...patch}));});
test('malformed candidate fails closed',()=>{for(const patch of [{schemaVersion:2},{depotToolsRevision:null},{depotToolsRevision:'main'},{depotToolsUpstream:'https://example.org'},{depotToolsStatus:'PASS'},{revision:'main'},{tag:'refs/heads/main'},{upstream:'https://example.org'},{status:'PASS'}])assert.throws(()=>validatePin({...pin,...patch}));});
