// SPDX-License-Identifier: MPL-2.0
import {test} from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,writeFileSync,readFileSync,rmSync,mkdirSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {validateSeries,checkSeries,sameDirectory} from '../../scripts/chromium/patch-series.mjs';
const origin='https://example.invalid/chromium.git';
const sha=b=>createHash('sha256').update(b).digest('hex');
function fixture(t){
  const root=mkdtempSync(join(tmpdir(),'veil-patch-test-'));t.after(()=>rmSync(root,{recursive:true,force:true}));
  const repo=join(root,'repo'),dir=join(root,'patches');mkdirSync(repo);mkdirSync(dir);
  const git=(...a)=>execFileSync('git',['-C',repo,...a],{encoding:'utf8',stdio:['ignore','pipe','pipe']}).trim();
  git('init');git('config','user.email','synthetic@example.invalid');git('config','user.name','Fixture');git('config','core.autocrlf','false');
  writeFileSync(join(repo,'a.txt'),'first\n');git('add','a.txt');git('commit','-m','base');git('remote','add','origin',origin);
  const revision=git('rev-parse','HEAD');
  const make=(n,before,after)=>{const file=n+'.patch',bytes=Buffer.from('diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-'+before+'\n+'+after+'\n');writeFileSync(join(dir,file),bytes);return {file,sha256:sha(bytes),purpose:'synthetic'};};
  return {repo,dir,git,revision,make,manifest:patches=>({schemaVersion:1,baseRevision:revision,patches})};
}
test('ordered patches checked in isolation; original HEAD, index and working file unchanged',t=>{
  const f=fixture(t),a=f.make('0001-first','first','second'),b=f.make('0002-second','second','third');
  const index=readFileSync(join(f.repo,'.git','index')),baseTree=f.git('rev-parse','HEAD^{tree}');
  const result=checkSeries(f.repo,f.revision,origin,validateSeries(f.manifest([a,b]),f.revision,f.dir));
  assert.equal(result.status,'SERIES_CHECKED_ONLY');assert.equal(result.patchCount,2);assert.notEqual(result.tree,baseTree);assert.equal(result.build,'NOT RUN');
  assert.equal(f.git('rev-parse','HEAD'),f.revision);assert.equal(readFileSync(join(f.repo,'a.txt'),'utf8'),'first\n');assert.deepEqual(readFileSync(join(f.repo,'.git','index')),index);
});
test('conflict refuses and leaves original checkout unchanged',t=>{
  const f=fixture(t),a=f.make('0001-conflict','wrong','second');
  assert.throws(()=>checkSeries(f.repo,f.revision,origin,validateSeries(f.manifest([a]),f.revision,f.dir)),/git.*apply/i);
  assert.equal(f.git('status','--porcelain'),'');assert.equal(readFileSync(join(f.repo,'a.txt'),'utf8'),'first\n');
});
test('bad identity, dirty tracked source, hash, order and traversal rejected',t=>{
  const f=fixture(t),a=f.make('0001-first','first','second'),b=f.make('0002-second','second','third');
  for(const patches of [[{...a,sha256:'0'.repeat(64)}],[b,a],[a,a],[{...a,file:'../bad.patch'}]]) assert.throws(()=>validateSeries(f.manifest(patches),f.revision,f.dir));
  assert.throws(()=>validateSeries(f.manifest([]),'0'.repeat(40),f.dir));
  assert.throws(()=>checkSeries(f.repo,f.revision,'wrong',[]),/Checkout identity mismatch/);
  writeFileSync(join(f.repo,'a.txt'),'dirty\n');assert.throws(()=>checkSeries(f.repo,f.revision,origin,[]),/Tracked checkout is dirty/);
});

test('directory identity accepts equivalent path spelling but refuses nested or different roots',t=>{
  const f=fixture(t);
  assert.equal(sameDirectory(f.repo,f.git('rev-parse','--show-toplevel')),true);
  assert.equal(sameDirectory(f.repo,f.dir),false);
  const nested=join(f.repo,'nested');mkdirSync(nested);
  assert.throws(()=>checkSeries(nested,f.revision,origin,[]),/Expected checkout root/);
  if(process.platform==='win32'){
    const alternate=f.repo.replaceAll('\\','/').replace(/^[A-Za-z]:/,m=>m[0]===m[0].toUpperCase()?m.toLowerCase():m.toUpperCase());
    assert.equal(sameDirectory(f.repo,alternate),true);
    assert.equal(checkSeries(alternate,f.revision,origin,[]).status,'SERIES_CHECKED_ONLY');
  }
});
