// SPDX-License-Identifier: MPL-2.0
// Explicit source-only patch application on a second, dedicated checkout.
// Never builds, launches, installs, syncs DEPS, or approves a release.
import {execFileSync} from 'node:child_process';
import {readFileSync, lstatSync, realpathSync} from 'node:fs';
import {resolve, dirname, join, relative, isAbsolute, sep} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {readPin} from './verify-candidate.mjs';
import {validateSeries,checkSeries,sameDirectory} from './patch-series.mjs';

function ordinaryDirectory(input) {
  const absolute=resolve(input);
  for(let p=absolute;;p=dirname(p)){
    const st=lstatSync(p);
    if(!st.isDirectory()||st.isSymbolicLink())throw Error('Ordinary directories required: '+p);
    if(dirname(p)===p)break;
  }
  return realpathSync(absolute);
}
function within(a,b){
  const rel=relative(a,b);
  return rel===''||(!isAbsolute(rel)&&rel!=='..'&&!rel.startsWith('..'+sep));
}
function envForGit(){
  return {...Object.fromEntries(Object.entries(process.env).filter(([k])=>!k.toUpperCase().startsWith('GIT_'))),
    GIT_CONFIG_NOSYSTEM:'1',GIT_CONFIG_GLOBAL:process.platform==='win32'?'NUL':'/dev/null',GIT_TERMINAL_PROMPT:'0',GIT_OPTIONAL_LOCKS:'0'};
}
function runGit(cwd,args,input){
  return execFileSync('git',['--no-replace-objects','-C',cwd,...args],{
    env:envForGit(),input,encoding:'utf8',timeout:120000,maxBuffer:16*1024*1024,stdio:['pipe','pipe','pipe']
  }).trim();
}
function checkRoot(root,revision,origin){
  if(!sameDirectory(root,runGit(root,['rev-parse','--show-toplevel'])))throw Error('Expected Git checkout root');
  if(runGit(root,['rev-parse','HEAD'])!==revision||runGit(root,['remote','get-url','origin'])!==origin)throw Error('Checkout identity mismatch');
  if(runGit(root,['status','--porcelain','--untracked-files=all','--ignore-submodules=none']))throw Error('Checkout must be clean, including untracked files');
  // Require independent ordinary .git directories, not a worktree or submodule.
  const gitDir=ordinaryDirectory(join(root,'.git'));
  const common=runGit(root,['rev-parse','--git-common-dir']);
  if(!sameDirectory(gitDir,resolve(root,common)))throw Error('Shared Git metadata refused');
  const alternates=runGit(root,['rev-parse','--git-path','objects/info/alternates']);
  try { lstatSync(resolve(root,alternates)); throw Error('Object alternates refused'); }
  catch(e){if(e.code!=='ENOENT')throw e;}
  return gitDir;
}

export function materialize({upstream,target,revision,origin,patches,execute=false}){
  if(!patches.length)throw Error('No registered patches');
  const source=ordinaryDirectory(upstream),dest=ordinaryDirectory(target);
  if(within(source,dest)||within(dest,source)||sameDirectory(source,dest))throw Error('Dedicated separate checkout required');
  const a=checkRoot(source,revision,origin),b=checkRoot(dest,revision,origin);
  if(sameDirectory(a,b))throw Error('Shared Git metadata refused');
  // Validate the whole ordered series in temporary indexes before any real write.
  const sourceTree=checkSeries(source,revision,origin,patches).tree;
  const targetTree=checkSeries(dest,revision,origin,patches).tree;
  if(sourceTree!==targetTree)throw Error('Patched tree mismatch between checkouts');
  const result={status:'MATERIALIZATION_PLAN_ONLY',source,destination:dest,baseRevision:revision,
    expectedTree:sourceTree,patches:patches.map(({file,sha256})=>({file,sha256})),
    build:'NOT RUN',runtime:'NOT RUN',sandbox:'NOT RUN',dependencyGraph:'NOT VERIFIED',releaseApproved:false};
  if(!execute)return result;
  // Recheck immediately before mutation; never automatically reset or delete a failed target.
  checkRoot(source,revision,origin);checkRoot(dest,revision,origin);
  try{
    for(const p of patches){
      runGit(dest,['apply','--index','--check','--whitespace=error-all','-'],p.bytes);
      runGit(dest,['apply','--index','--whitespace=error-all','-'],p.bytes);
    }
    if(runGit(dest,['write-tree'])!==targetTree)throw Error('Materialized tree mismatch');
    if(runGit(dest,['diff','--name-only'])!=='')throw Error('Unstaged changes detected');
    if(runGit(dest,['rev-parse','HEAD'])!==revision)throw Error('Destination HEAD changed');
    checkRoot(source,revision,origin);
    return {...result,status:'PATCHED_SOURCE_OBSERVED',destinationIndexTree:targetTree,commitCreated:false};
  }catch(e){
    throw Error('Materialization interrupted or failed; preserve destination for review, do not build: '+e.message);
  }
}

if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){
  try{
    const args=process.argv.slice(2);
    if((args.length!==2&&args.length!==3)||(args.length===3&&args[2]!=='--execute'))throw Error('Usage: node materialize-patches.mjs <clean upstream checkout> <separate clean target checkout> [--execute]');
    const pin=readPin(),dir=fileURLToPath(new URL('../../chromium/patches/',import.meta.url));
    const manifest=JSON.parse(readFileSync(join(dir,'series.json'),'utf8'));
    const patches=validateSeries(manifest,pin.revision,dir);
    console.log(JSON.stringify(materialize({upstream:args[0],target:args[1],revision:pin.revision,origin:pin.upstream,patches,execute:args.length===3}),null,2));
  }catch(e){console.error(e.message);process.exitCode=1;}
}
