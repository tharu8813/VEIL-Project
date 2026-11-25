// SPDX-License-Identifier: MPL-2.0
import {test} from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync,writeFileSync,readFileSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {materialize} from '../../scripts/chromium/materialize-patches.mjs';
const origin='https://example.invalid/chromium.git';
function fixture(t){
  const dir=mkdtempSync(join(tmpdir(),'veil-materialize-'));t.after(()=>rmSync(dir,{recursive:true,force:true}));
  const upstream=join(dir,'upstream'),target=join(dir,'target');mkdirSync(upstream);
  const git=(cwd,...args)=>execFileSync('git',['-C',cwd,...args],{encoding:'utf8',stdio:['ignore','pipe','pipe']}).trim();
  git(upstream,'init');git(upstream,'config','user.name','Fixture');git(upstream,'config','user.email','fixture@example.invalid');git(upstream,'config','core.autocrlf','false');
  writeFileSync(join(upstream,'a.txt'),'first\n');git(upstream,'add','a.txt');git(upstream,'commit','-m','base');git(upstream,'remote','add','origin',origin);
  // Configure before the initial checkout; changing autocrlf afterwards leaves CRLF bytes on disk.
  git(dir,'clone','--no-hardlinks','--config','core.autocrlf=false',upstream,target);git(target,'remote','set-url','origin',origin);
  assert.equal(readFileSync(join(upstream,'a.txt'),'utf8'),'first\n');
  assert.equal(readFileSync(join(target,'a.txt'),'utf8'),'first\n');
  assert.equal(git(upstream,'status','--porcelain'),'');
  assert.equal(git(target,'status','--porcelain'),'');
  const revision=git(upstream,'rev-parse','HEAD');
  const make=(n,old,next)=>{const bytes=Buffer.from('diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-'+old+'\n+'+next+'\n');return {file:n+'.patch',sha256:createHash('sha256').update(bytes).digest('hex'),bytes};};
  return {dir,upstream,target,revision,origin,git,make,patches:[make('0001','first','second'),make('0002','second','third')]};
}
test('plan leaves both original indexes and files untouched; execution changes only target',t=>{
  const f=fixture(t),sourceIndex=readFileSync(join(f.upstream,'.git','index')),targetIndex=readFileSync(join(f.target,'.git','index'));
  const plan=materialize(f);assert.equal(plan.status,'MATERIALIZATION_PLAN_ONLY');
  assert.deepEqual(readFileSync(join(f.upstream,'.git','index')),sourceIndex);assert.deepEqual(readFileSync(join(f.target,'.git','index')),targetIndex);
  const result=materialize({...f,execute:true});assert.equal(result.status,'PATCHED_SOURCE_OBSERVED');assert.equal(result.destinationIndexTree,plan.expectedTree);
  assert.equal(result.build,'NOT RUN');assert.equal(result.releaseApproved,false);
  assert.equal(readFileSync(join(f.upstream,'a.txt'),'utf8'),'first\n');assert.equal(readFileSync(join(f.target,'a.txt'),'utf8'),'third\n');
  assert.deepEqual(readFileSync(join(f.upstream,'.git','index')),sourceIndex);
  assert.equal(f.git(f.target,'rev-parse','HEAD'),f.revision);
  assert.equal(f.git(f.target,'diff','--cached','--name-only'),'a.txt');
});
test('same checkout, nested checkout, dirty target and wrong origin refused',t=>{
  const f=fixture(t);assert.throws(()=>materialize({...f,target:f.upstream,execute:true}),/separate checkout/);
  const nested=join(f.upstream,'nested');mkdirSync(nested);
  assert.throws(()=>materialize({...f,target:nested,execute:true}),/separate checkout/);
  rmSync(nested,{recursive:true});writeFileSync(join(f.target,'untracked.txt'),'x');
  assert.throws(()=>materialize({...f,execute:true}),/must be clean/);
  rmSync(join(f.target,'untracked.txt'));
  assert.throws(()=>materialize({...f,origin:'wrong',execute:true}),/identity mismatch/);
});
test('conflicting series is refused before mutation',t=>{
  const f=fixture(t);const index=readFileSync(join(f.target,'.git','index'));
  assert.throws(()=>materialize({...f,patches:[f.patches[0],f.make('0002','wrong','third')],execute:true}),/git.*apply/i);
  assert.deepEqual(readFileSync(join(f.target,'.git','index')),index);
  assert.equal(readFileSync(join(f.target,'a.txt'),'utf8'),'first\n');
  assert.equal(f.git(f.target,'status','--porcelain'),'');
});

test('unexpected CRLF bytes are refused without rewriting target or source',t=>{
  const f=fixture(t);
  writeFileSync(join(f.target,'a.txt'),'first\r\n');
  const before=readFileSync(join(f.target,'a.txt')),index=readFileSync(join(f.target,'.git','index'));
  assert.throws(()=>materialize({...f,execute:true}),/must be clean/);
  assert.deepEqual(readFileSync(join(f.target,'a.txt')),before);
  assert.deepEqual(readFileSync(join(f.target,'.git','index')),index);
  assert.equal(readFileSync(join(f.upstream,'a.txt'),'utf8'),'first\n');
});

for(const flag of ['--assume-unchanged','--skip-worktree']){
  for(const side of ['upstream','target']){
    test(`${flag} on ${side} refuses hidden changes without mutation`,t=>{
      const f=fixture(t),root=f[side],other=f[side==='upstream'?'target':'upstream'];
      f.git(root,'update-index',flag,'a.txt');
      writeFileSync(join(root,'a.txt'),'hidden change\n');
      assert.equal(f.git(root,'status','--porcelain'),'');
      const before=readFileSync(join(root,'.git','index')),otherIndex=readFileSync(join(other,'.git','index'));
      for(const execute of [false,true]){
        assert.throws(()=>materialize({...f,execute}),/index entry refused/);
        assert.deepEqual(readFileSync(join(root,'.git','index')),before);
        assert.deepEqual(readFileSync(join(other,'.git','index')),otherIndex);
        assert.equal(readFileSync(join(root,'a.txt'),'utf8'),'hidden change\n');
        assert.equal(readFileSync(join(other,'a.txt'),'utf8'),'first\n');
      }
    });
  }
}
