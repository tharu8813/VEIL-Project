// SPDX-License-Identifier: MPL-2.0
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseTagRefs, validateVersion } from '../../scripts/chromium/resolve-tag.mjs';
const version='158.0.8127.89', tag='refs/tags/'+version, a='a'.repeat(40), b='b'.repeat(40);
test('lightweight exact tag resolves without claiming build',()=>{const x=parseTagRefs(version,a+'\t'+tag+'\n');assert.equal(x.revision,a);assert.equal(x.build,'NOT RUN');assert.equal(x.status,'TAG_RESOLVED_ONLY');});
test('annotated tag resolves peeled ref irrespective of order',()=>{const x=parseTagRefs(version,b+'\t'+tag+'^{}\n'+a+'\t'+tag);assert.equal(x.revision,b);assert.equal(x.tagObject,a);});
test('missing or wrong tags, duplicates, malformed hash and arguments fail closed',()=>{for(const s of ['',a+'\t'+tag+'0',a+'\t'+tag+'^{}',a+'\t'+tag+'\n'+a+'\t'+tag,'xyz\t'+tag])assert.throws(()=>parseTagRefs(version,s));for(const s of ['main','--help','1.2.3','158.0.8127.89^{commit}'])assert.throws(()=>validateVersion(s));});
