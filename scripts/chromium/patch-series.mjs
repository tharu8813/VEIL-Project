// SPDX-License-Identifier: MPL-2.0
// Check ordered patches against a pinned Git tree in a temporary index/object store.
import {execFileSync} from 'node:child_process';
import {readFileSync, lstatSync, statSync, realpathSync, mkdtempSync, mkdirSync, rmSync} from 'node:fs';
import {resolve, join, isAbsolute} from 'node:path';
import {tmpdir} from 'node:os';
import {fileURLToPath, pathToFileURL} from 'node:url';
import {createHash} from 'node:crypto';
import {readPin} from './verify-candidate.mjs';

const root=fileURLToPath(new URL('../../chromium/patches/',import.meta.url));
const hex=/^[0-9a-f]{40}$/;
const hash=b=>createHash('sha256').update(b).digest('hex');
export function validateSeries(manifest, revision, directory) {
  if(!hex.test(revision)||manifest?.schemaVersion!==1||manifest.baseRevision!==revision||!Array.isArray(manifest.patches)) throw Error('Patch series base/schema mismatch');
  if(!lstatSync(directory).isDirectory()||lstatSync(directory).isSymbolicLink()) throw Error('Patch directory must be ordinary');
  const seen=new Set();
  let previous='';
  return manifest.patches.map(p=>{
    if(typeof p?.file!=='string'||!/^[0-9]{4}-[a-z0-9][a-z0-9-]*\.patch$/.test(p.file)||p.file<=previous||seen.has(p.file)||!/^[0-9a-f]{64}$/.test(p.sha256)||typeof p.purpose!=='string'||!p.purpose.trim()) throw Error('Invalid patch metadata or order');
    seen.add(p.file);previous=p.file;
    const path=join(directory,p.file), st=lstatSync(path);
    if(!st.isFile()||st.isSymbolicLink()||st.size>16*1024*1024) throw Error('Invalid patch file');
    const bytes=readFileSync(path);
    if(hash(bytes)!==p.sha256) throw Error('Patch hash mismatch: '+p.file);
    return {file:p.file,sha256:p.sha256,purpose:p.purpose,bytes};
  });
}
function cleanGitEnv() {
  const env=Object.fromEntries(Object.entries(process.env).filter(([k])=>!k.toUpperCase().startsWith('GIT_')));
  return {...env,GIT_CONFIG_NOSYSTEM:'1',GIT_CONFIG_GLOBAL:process.platform==='win32'?'NUL':'/dev/null',GIT_TERMINAL_PROMPT:'0',GIT_OPTIONAL_LOCKS:'0'};
}
// Git for Windows and Node can spell the same directory differently.
// Compare actual filesystem identity instead of folding case (case-sensitive NTFS is possible).
export function sameDirectory(a,b) {
  const left=realpathSync(a),right=realpathSync(b);
  const x=statSync(left,{bigint:true}),y=statSync(right,{bigint:true});
  if(!x.isDirectory()||!y.isDirectory())return false;
  if(left===right)return true;
  return x.ino!==0n&&y.ino!==0n&&x.dev===y.dev&&x.ino===y.ino;
}
export function checkSeries(checkout, revision, origin, patches) {
  const cwd=realpathSync(checkout), env=cleanGitEnv();
  const git=(args, extra={},input)=>execFileSync('git',['--no-replace-objects','-C',cwd,...args],{env:{...env,...extra},input,encoding:'utf8',timeout:120000,maxBuffer:16*1024*1024,stdio:['pipe','pipe','pipe']}).trim();
  if(!sameDirectory(git(['rev-parse','--show-toplevel']),cwd)) throw Error('Expected checkout root');
  if(git(['rev-parse','HEAD'])!==revision||git(['remote','get-url','origin'])!==origin) throw Error('Checkout identity mismatch');
  if(git(['status','--porcelain','--untracked-files=no'])) throw Error('Tracked checkout is dirty');
  const objectPath=git(['rev-parse','--git-path','objects']);
  const objects=realpathSync(isAbsolute(objectPath)?objectPath:resolve(cwd,objectPath));
  // Git's alternate path list has platform delimiters. Fail rather than misparse.
  if(objects.includes(process.platform==='win32'?';':':')||/[\r\n"]/u.test(objects)) throw Error('Unsupported object path');
  const temp=mkdtempSync(join(tmpdir(),'veil-patches-'));
  try {
    const isolatedObjects=join(temp,'objects');mkdirSync(isolatedObjects);
    const gitPath=p=>process.platform==='win32'?p.replaceAll('\\','/'):p;
    const isolated={GIT_INDEX_FILE:gitPath(join(temp,'index')),GIT_OBJECT_DIRECTORY:gitPath(isolatedObjects),GIT_ALTERNATE_OBJECT_DIRECTORIES:gitPath(objects)};
    git(['read-tree',revision],isolated);
    for(const p of patches){
      // stdin binds the exact bytes already hashed. No worktree write, no hooks, no three-way fallback.
      git(['apply','--cached','--check','--whitespace=error-all','-'],isolated,p.bytes);
      git(['apply','--cached','--whitespace=error-all','-'],isolated,p.bytes);
    }
    const tree=git(['write-tree'],isolated);
    return {status:'SERIES_CHECKED_ONLY',baseRevision:revision,patchCount:patches.length,tree,checkoutModified:false,build:'NOT RUN',runtime:'NOT RUN',sandbox:'NOT RUN',releaseApproved:false};
  } finally {rmSync(temp,{recursive:true,force:true});}
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){
  try{
    const pin=readPin(),manifest=JSON.parse(readFileSync(join(root,'series.json'),'utf8'));
    const patches=validateSeries(manifest,pin.revision,root);
    if(process.argv.length===2) console.log(JSON.stringify({status:'MANIFEST_VALIDATED_ONLY',baseRevision:pin.revision,patches:patches.map(({bytes,...p})=>p),upstreamCheck:'NOT RUN',build:'NOT RUN',releaseApproved:false},null,2));
    else if(process.argv.length===4&&process.argv[2]==='--check') console.log(JSON.stringify(checkSeries(process.argv[3],pin.revision,pin.upstream,patches),null,2));
    else throw Error('Usage: node patch-series.mjs [--check <Chromium checkout root>]');
  }catch(e){console.error(e.message);process.exitCode=1;}
}
