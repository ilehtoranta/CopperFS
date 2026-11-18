// Source-only, fail-closed qualification record for the isolated execution core.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
const a='artifacts/execution-core',read=p=>JSON.parse(fs.readFileSync(p,'utf8').replace(/^\uFEFF/,''));
const hash=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex').toUpperCase();
const require=(ok,message)=>{if(!ok)throw Error(message);};
const baseline=read(`${a}/baseline-handler/source-manifest.json`),current=read(`${a}/final-handler/source-manifest.json`);
require(baseline.CompilerSha256===current.CompilerSha256,'Compiler changed');
require(current.ManagedAllocationSites===0,'Native allocation sites');
const rebuiltBaseline=read(`${a}/baseline-rebuild-receipt.json`);
require(rebuiltBaseline.ByteIdentical&&rebuiltBaseline.FrozenSha256===baseline.HunkSha256&&rebuiltBaseline.RebuiltSha256===baseline.HunkSha256,'Baseline dependency-control rebuild');
require(hash(`${a}/baseline-rebuild-handler/CopperNovaPrototype`)===baseline.HunkSha256,'Rebuilt baseline bytes');
for(const input of current.ManagedInputs.filter(i=>i.Name!=='CopperNova.Prototype.dll'))
 require(rebuiltBaseline.ManagedInputs.find(i=>i.Name===input.Name)?.Sha256===input.Sha256,'Controlled dependency input '+input.Name);
for(const [variant,m] of [['baseline',baseline],['final',current]]) {
 require(hash(`${a}/${variant}-handler/CopperNovaPrototype`)===m.HunkSha256,'HUNK changed');
 for(const source of m.Sources) {
  const p=source.Path.replaceAll('\\','/');
  const frozen=p.startsWith('prototypes/CopperNova.Prototype/')?`${a}/${variant}-source/${path.basename(p)}`:p;
  require(hash(frozen)===source.Sha256,'Frozen source mismatch '+p);
  if(variant==='final') require(hash(p)===source.Sha256,'Current source mismatch '+p);
 }
}
const inspections={baseline:read(`${a}/baseline-inspection/inspection.json`),final:read(`${a}/final-inspection/inspection.json`)};
for(const [v,i] of Object.entries(inspections)) {
 require(i.HunkSha256===(v==='baseline'?baseline:current).HunkSha256,'Inspection HUNK');
 require(i.MapSha256===hash(`${a}/${v}-handler/CopperNovaPrototype.map`),'Inspection map');
}
const crc=inspections.final.Methods.find(m=>m.Name.endsWith('::Crc'));
require(crc&&crc.CallSites.length===0&&crc.AllNonCallBranchesWithinMethod,'CRC still calls/tail-transfers to helper');
require(!/\s(?:jmp|jsr|bsr)[.\t ]/i.test(fs.readFileSync(`${a}/final-inspection/Crc.asm`,'utf8')),'Unexpected CRC control transfer');
require(!inspections.final.Methods.some(m=>m.Name.endsWith('::CrcFour')),'Old per-word helper remains');
const profiles={};
for(const [v,m] of [['baseline',baseline],['final',current]]) {
 const p=read(`${a}/${v}-flush-profile/profile.json`),gate=read(`${a}/${v}-flush-profile/qualification.json`);
 require(gate.Passed&&gate.IndependentOwnershipAndShutdownPayloadVerified&&p.HandlerSha256===m.HunkSha256&&gate.HandlerSha256===m.HunkSha256,'Profile qualification');
 profiles[v]=p;
}
const calls=(p,name)=>p.Methods.find(m=>m.Name.endsWith('::'+name))?.Calls??0;
require(calls(profiles.baseline,'Crc')===12&&calls(profiles.final,'Crc')===12,'Changed CRC frequency');
require(calls(profiles.baseline,'CrcFour')===1653&&calls(profiles.final,'CrcFour')===0,'Per-word calls');
require(calls(profiles.baseline,'Zero')===19&&calls(profiles.final,'Zero')===3,'Quarantine clearing not eliminated');
const names={baseline:'Current CopperNova prototype (CLOCK)',final:'CopperNova prototype',ffs:'Kickstart FFS 40.1',sfs:'SmartFilesystem 1.279'};
const comparisons={};
const median=a=>{a=[...a].sort((x,y)=>x-y);return (a[Math.floor((a.length-1)/2)]+a[Math.floor(a.length/2)])/2;};
for(const mode of ['mixed','stream']) {
 const q=read(`${a}/comparison-${mode}/benchmark.json`);
 require(q.Trials.length===8&&q.Trials.every(t=>t.Passed&&t.IndependentCheck),'Comparison incomplete/failed');
 require(q.Inputs.PrototypeHandlerSha256===current.HunkSha256&&q.Inputs.BaselinePrototypeHandlerSha256===baseline.HunkSha256,'Comparison handlers');
 require(q.Inputs.ProbeSha256===(mode==='mixed'?'0984E37B49A652D20E62ED24A524713C228E1071B8422A5676A78E177C0AD5B1':'31AF34F1FF54A5AB4EBFF9D74C612C3BA00CB20C845DEB8947896E619656F906'),'Frozen guest changed');
 require(q.Inputs.FfsHandlerSha256==='9992D843E3936EF81F92311404BCA557E3727DA3A10F39B586B94B02921B3773'&&q.Inputs.Sfs1279HandlerSha256==='C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205','Reference handlers changed');
 const summaries={};
 for(const [kind,name] of Object.entries(names)) {
  const trials=q.Trials.filter(t=>t.Name===name); require(trials.length===2,'Trial count '+name);
  const values=trials.map(t=>Object.values(t.PhaseCycles).reduce((s,v)=>s+v,0));
  const summary=q.Summary.find(s=>s.Name===name);
  require(summary?.AllPassed&&summary.TotalMeasuredCyclesMedian===median(values),'Summary mismatch');
  summaries[kind]=summary;
 }
 const nova=q.Trials.filter(t=>t.Name===names.baseline||t.Name===names.final);
 for(const t of nova) {
  require(t.InitialMediaSha256===nova[0].InitialMediaSha256&&t.ResultMediaSha256===nova[0].ResultMediaSha256,'Changed resulting disk image');
  for(const field of ['ReadRequests','WriteRequests','ReadBytes','WriteBytes','Flushes']) require(t[field]===nova[0][field],'Changed I/O '+field);
 }
 const old=summaries.baseline.TotalMeasuredCyclesMedian,now=summaries.final.TotalMeasuredCyclesMedian;
 comparisons[mode]={...q,NamedSummary:summaries,ImprovementPercent:(1-now/old)*100,
  FlushImprovementPercent:(1-summaries.final.FlushCyclesMedian/summaries.baseline.FlushCyclesMedian)*100,
  OriginalProbeAndIoAndDiskBytesPreserved:true};
}
const gate=read(`${a}/rom-gate/qualification.json`);
require(gate.Passed&&gate.HandlerSha256===current.HunkSha256&&gate.SourceInputsMatched&&gate.IndependentOwnershipAndShutdownPayloadVerified,'ROM qualification');
require(gate.OperationAllocations===0&&gate.Peak<=4096&&gate.GuardIntact&&gate.ShutdownBarriers===4,'Native safety/durability');
const trx=folder=>fs.readdirSync(folder).filter(f=>f.endsWith('.trx')).map(file=>{
 const text=fs.readFileSync(path.join(folder,file),'utf8'),counter=text.match(/<Counters\b([^>]+)\/?\s*>/);
 require(counter,'TRX counters');
 const c=Object.fromEntries([...counter[1].matchAll(/(\w+)="(\d+)"/g)].map(m=>[m[1],Number(m[2])]));
 require(c.failed===0&&c.error===0&&c.timeout===0&&c.aborted===0&&c.executed===c.passed,'TRX failure');
 const skipped=[...text.matchAll(/<UnitTestResult\b[^>]*outcome="NotExecuted"/g)].length;
 require(c.total-c.executed===skipped,'TRX skipped count');
 const assembly=text.match(/<UnitTest\b[^>]*storage="([^"]+\.dll)"/i)?.[1].split(/[\\/]/).pop();
 require(assembly,'TRX assembly'); return {file,assembly,...c,skipped};
});
require(fs.readFileSync(`${a}/solution-test-exit.txt`,'utf8').trim()==='0','Full suite incomplete');
require(fs.readFileSync(`${a}/replay-exit.txt`,'utf8').trim()==='0','Replay incomplete');
const tests={solution:trx(`${a}/solution-results`),copperStartReplay:trx(`${a}/replay-results`)};
require(tests.solution.reduce((s,t)=>s+t.passed,0)===2754&&tests.solution.reduce((s,t)=>s+t.skipped,0)===10,'Solution count');
require(tests.copperStartReplay.reduce((s,t)=>s+t.passed,0)===169,'Replay count');
require(fs.readFileSync(`${a}/final-solution-build.log`,'utf8').includes('Build succeeded.'),'Release build');
const report={schema:1,date:'2026-11-17',scope:'Item 1 in isolated CNM1; production CPX4/FFS/SFS untouched; existing prototype feature limits unchanged',
 timing:{acceptance:'68020 emulated end-to-end bus-cycle medians; two trials per filesystem per workload',profile:'Separate MC68000 scalar instruction cycles attributed to handler task; not acceptance timing'},
 baseline:{hunkSha256:baseline.HunkSha256,compilerSha256:baseline.CompilerSha256,bytes:fs.statSync(`${a}/baseline-handler/CopperNovaPrototype`).size,sources:baseline.Sources},
 final:{hunkSha256:current.HunkSha256,compilerSha256:current.CompilerSha256,bytes:fs.statSync(`${a}/final-handler/CopperNovaPrototype`).size,managedAllocationSites:current.ManagedAllocationSites,sources:current.Sources},
 contract:{arenaBytes:98304,metadataCacheBytes:32768,crcCoverageUnchanged:true,formatAndPacketAbiUnchanged:true,bufferedDurabilityUnchanged:true,
  retirementRecords:32,retirementRecordBytes:128,retirementWindowInitBytes:256,retirementEpochWrapResetBytes:128,
  quarantineBitmapBytesRetained:8192,normalRetirement:'O(1) epoch advance only after fourth successful durability barrier',maxCrcWordsPerCall:256,maxCacheVictimVisits:128,maxSkippedAllocationCandidatesPerContinuation:256,maxAllocationRunSectors:128,maxRetirementWindowSectors:2048},
 rebuiltBaseline,inspections,profiles,comparisons,tests,romGate:gate,
 coverageNotes:['All final prototype source changes were frozen before the full solution build/test; 28 prototype cases are part of that invocation.',
 'Prototype cases include 32 independent bitwise CRC comparisons, normal/wrapping cross-window retirement, 84 free/allocate crash states, 74 changed-retry transaction states, 32 recovery states and 13 failed/13 short I/O positions.',
 '139 compiled-native legacy tests and 169 CopperStart integration replays are separate from the actual final prototype HUNK ROM gate.',
 'The ROM/profile gates cover mixed operations and shutdown with MC68000/512 KiB Chip/zero Fast/4 KiB stack; benchmark guest handlers use 32 KiB stacks.',
 'Ten optional legacy ROM/media cases remain skipped. No physical-device or broad-platform qualification.',
 'Per-window initialization is charged to mutation. Acceptance totals include all four marked phases; no work was removed from the measured workloads.']};
fs.mkdirSync('benchmarks/coppernova-prototype',{recursive:true});
fs.writeFileSync('benchmarks/coppernova-prototype/2026-11-17-execution-core.json',JSON.stringify(report,null,2)+'\n');
console.log(JSON.stringify({mixed:comparisons.mixed.ImprovementPercent,stream:comparisons.stream.ImprovementPercent,
 tests:Object.fromEntries(Object.entries(tests).map(([k,v])=>[k,{passed:v.reduce((s,t)=>s+t.passed,0),skipped:v.reduce((s,t)=>s+t.skipped,0)}]))},null,2));
