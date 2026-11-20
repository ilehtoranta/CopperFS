import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
const base='artifacts/packed-aligned';
const read=name=>JSON.parse(fs.readFileSync(name,'utf8').replace(/^﻿/,''));
const hash=name=>crypto.createHash('sha256').update(fs.readFileSync(name)).digest('hex').toUpperCase();
const require=(v,message)=>{if(!v)throw Error(message);};
const manifest=read(base+'/handler/source-manifest.json');
const control=read('artifacts/execution-core/final-handler/source-manifest.json');
require(manifest.HunkSha256===hash(base+'/handler/CopperNovaPrototype')&&manifest.ManagedAllocationSites===0,'native binary');
require(control.HunkSha256==='61EFDFBF1A8EE50BF3625F0487B3514DA56C2C1F3F2299D5E4A053D5BE1DC339'&&control.CompilerSha256===manifest.CompilerSha256,'baseline/compiler');
for(const s of manifest.Sources)require(s.Sha256===hash(s.Path),'source '+s.Path);
const rom=read(base+'/rom/qualification.json');
require(rom.Passed&&rom.IndependentOwnershipAndShutdownPayloadVerified&&rom.OperationAllocations===0&&rom.ManagedAllocationSites===0&&rom.ShutdownBarriers===2&&rom.GuardIntact&&rom.HandlerSha256===manifest.HunkSha256,'ROM gate');
const workloads={};
for(const mode of ['mixed','stream']) {
  const report=read(base+'/'+mode+'/benchmark.json');const previous=read('artifacts/execution-core/comparison-'+mode+'/benchmark.json');
  for(const name of ['FfsHandlerSha256','Sfs1279HandlerSha256','KickstartSha256','SfsBaseSha256','ProbeSha256','DiskBytes','SectorBytes'])require(report.Inputs[name]===previous.Inputs[name],name);
  require(report.Inputs.PrototypeHandlerSha256===manifest.HunkSha256&&report.Inputs.BaselinePrototypeHandlerSha256===control.HunkSha256,'benchmark executables');
  require(report.Trials.length===8&&report.Summary.length===4&&report.Summary.every(s=>s.Trials===2&&s.AllPassed),'trial count');
  require(report.Trials.every(t=>t.Passed&&t.IndependentCheck&&Object.values(t.PhaseCycles).every(c=>c>0)),'failed guest/independent check');
  const sample=report.Trials.find(t=>t.Name==='CopperNova prototype'); const file=base+'/'+mode+'/CopperNova-prototype/trial-1/disk.hdf';
  require(sample.ResultMediaSha256===hash(file),'wrong image');const disk=fs.readFileSync(file);
  const root=disk.readUInt32BE(528)>disk.readUInt32BE(16)?512:0,leaf=disk.readUInt16BE(root+160)*512;
  require(disk.readUInt16BE(leaf+6)===32,'expected direct leaf'); const records=[];const end=disk.readUInt16BE(leaf+32);
  for(let at=40;at<end;) {const length=disk.readUInt16BE(leaf+at+4);require(length>=24&&at+length<=end,'record length'); records.push({object:disk.readUInt16BE(leaf+at),type:disk[leaf+at+2],length});at+=length;}
  require(records.length===4&&sample.Flushes===3&&report.Trials.filter(t=>t.Name.startsWith('Current')).every(t=>t.Flushes===5),'unexpected layout/barrier count');
  const pages=[]; const generation=disk.readUInt32BE(root+16);
  for(let sector=0;sector<1394;sector++){let offset=sector*512;if(disk.readUInt32BE(offset)===0x434e5032&&disk.readUInt32BE(offset+16)===generation)pages.push({sector,type:disk.readUInt16BE(offset+6)});}
  require(pages.length===(mode==='mixed'?3:5),'published metadata cost');
  workloads[mode]={publishedPages:pages,machine:report.Machine,workload:report.Workload,inputs:report.Inputs,summary:report.Summary,trials:report.Trials,selectedRoot:root/512,leafSector:leaf/512,leafBytesUsed:end,records};
}
function trx(dir){return fs.readdirSync(dir).filter(n=>n.endsWith('.trx')).map(n=>{let text=fs.readFileSync(path.join(dir,n),'utf8');const c=text.match(/<Counters\s+([^>]+)/)[1];const attr=k=>Number(c.match(new RegExp(k+'="(\\d+)"'))[1]);const r={total:attr('total'),passed:attr('passed'),failed:attr('failed'),skipped:(text.match(/<UnitTestResult\b[^>]+outcome="NotExecuted"/g)||[]).length};require(r.total===r.passed+r.skipped&&r.failed===0,'test failure');return r;});}
const focused=trx(base+'/complete-host-results'),replay=trx(base+'/replay-results'); require(focused.length===1&&focused[0].passed===38&&replay.length===1&&replay[0].passed===169,'qualification counts');
const stressRom=read(base+'/stress-rom/qualification.json'); require(stressRom.Passed&&stressRom.IndependentOwnershipAndShutdownPayloadVerified&&stressRom.OperationAllocations===0&&stressRom.ShutdownBarriers===2&&stressRom.HandlerSha256===manifest.HunkSha256,'extended ROM gate');
const full=trx('artifacts/packed/full-results');
const includedAssemblies=full.length;
const suitePassed=full.reduce((sum,item)=>sum+item.passed,0),suiteSkipped=full.reduce((sum,item)=>sum+item.skipped,0);
const complete=includedAssemblies===7;
if(complete) require(suitePassed===2758&&suiteSkipped===10,'unexpected solution results');
else require(process.argv.includes('--pending-suite'),'incomplete solution run');
const hunkInspection=read(base+'/hunk-inspection/inspection.json');require(hunkInspection.HunkSha256===manifest.HunkSha256&&hunkInspection.Methods.find(m=>m.Name.endsWith('::Crc')).CallSites.length===0,'HUNK inspection');
const buildLog=fs.readFileSync(base+'/solution-build.log','utf8');require(buildLog.includes('Build succeeded.')&&buildLog.includes('0 Error(s)'),'Release build');
const coverage={suiteStatus:complete?'complete':'in progress',includedAssemblies,suitePassed,suiteSkipped,fullSuitePrototypeRevision:'32 tests before final attribute and alignment changes',finalPrototype:38,unchangedLegacyHost:complete?2587:null,unchangedCompiledNative:complete?139:null,replay:169,physicalDevices:false};
const trace=fs.readFileSync(path.join(base,'complete-host-results',fs.readdirSync(base+'/complete-host-results').find(n=>n.endsWith('.trx'))),'utf8');
require(trace.includes('unaligned hint 4194/6; aligned hint 32/128'),'bitmap trace');
const evidence={allocatorTrace:{unaligned:{hint:1394,byteReads:4194,longReads:6},aligned:{hint:1408,byteReads:32,longReads:128}},coverage,hunkInspection,stressRom,schema:1,units:'emulated 68020 bus cycles; lower is better',native:{bytes:fs.statSync(base+'/handler/CopperNovaPrototype').size,manifest},rom,focused,replay,workloads};
fs.writeFileSync('benchmarks/coppernova-prototype/2026-11-17-packed-root.json',JSON.stringify(evidence,null,2)+'\n');
