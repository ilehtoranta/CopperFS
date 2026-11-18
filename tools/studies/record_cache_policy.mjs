// Record source-only evidence; never copy ROMs, images or native build products.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
const root=process.cwd(), artifacts='artifacts/cache-policy';
const read=p=>JSON.parse(fs.readFileSync(p,'utf8').replace(/^\uFEFF/,''));
const hash=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex').toUpperCase();
const require=(ok,message)=>{if(!ok)throw Error(message);};
const baseline=read('artifacts/nova-p4-handler/source-manifest.json');
const current=read(`${artifacts}/final-handler/source-manifest.json`);
require(baseline.CompilerSha256===current.CompilerSha256,'Compiler changed');
require(current.ManagedAllocationSites===0,'Native allocations');
require(hash(`${artifacts}/final-handler/CopperNovaPrototype`)===current.HunkSha256,'Handler changed');
for(const entry of current.Sources) require(hash(entry.Path)===entry.Sha256,'Final source mismatch '+entry.Path);
for(const file of fs.readdirSync(`${artifacts}/baseline-source`).filter(p=>p.endsWith('.cs'))) {
 const entry=baseline.Sources.find(e=>e.Path.replaceAll('\\','/')===`prototypes/CopperNova.Prototype/${file}`);
 require(entry&&hash(`${artifacts}/baseline-source/${file}`)===entry.Sha256,'Baseline source mismatch');
}
const result=folder=>{
 const r=read(`${artifacts}/${folder}/CopperNova-prototype/trial-1/result.json`);
 require(r.Passed&&r.IndependentCheck,'Guest/checker failure '+folder);
 return {artifact:folder,phases:r.PhaseCycles,total:Object.values(r.PhaseCycles).reduce((a,b)=>a+b,0),
 readRequests:r.ReadRequests,writeRequests:r.WriteRequests,flushes:r.Flushes,
 initialMediaSha256:r.InitialMediaSha256,resultMediaSha256:r.ResultMediaSha256};
};
const median=a=>{a=[...a].sort((x,y)=>x-y);return (a[Math.floor((a.length-1)/2)]+a[Math.floor(a.length/2)])/2;};
const originals={};
for(const mode of ['mixed','stream']) {
 const b=result(`baseline-${mode}`),trials=[1,2].map(i=>result(`final-${mode}-${i}`));
 for(const t of trials) require(t.initialMediaSha256===b.initialMediaSha256&&t.resultMediaSha256===b.resultMediaSha256,'Original workload media mismatch');
 const refs=read(`${artifacts}/clock-${mode}/benchmark.json`);
 require(refs.Summary.every(s=>s.AllPassed&&s.Trials===2),'Reference comparison incomplete');
 const total=median(trials.map(t=>t.total));
 originals[mode]={baselineFreshTrials:[b],finalTrials:trials,baselineTotal:b.total,finalMedianTotal:total,
 changePercent:(total/b.total-1)*100,
 referenceTrials:refs.Trials.filter(t=>t.Name!=='CopperNova prototype').map(t=>({name:t.Name,passed:t.Passed,phases:t.PhaseCycles,independentCheck:t.IndependentCheck})),
 references:refs.Summary.filter(s=>s.Name!=='CopperNova prototype'),referenceInputHashes:refs.Inputs};
}
const pressureBaseline=read(`${artifacts}/pressure/pressure.json`);
const pressureFinal=read(`${artifacts}/final-pressure/pressure.json`);
require(pressureBaseline.SeedSha256===pressureFinal.SeedSha256&&pressureBaseline.ProbeSha256===pressureFinal.ProbeSha256,'Pressure inputs changed');
const pressure={baseline:pressureBaseline.Trials.filter(t=>t.Variant==='baseline'),final:pressureFinal.Trials};
require(pressure.baseline.length===2&&pressure.final.length===2,'Pressure trial count');
for(const [kind,trials] of Object.entries(pressure)) for(const t of trials) {
 require(t.Result.Passed&&t.Result.IndependentCheck,'Pressure failure');
 require(t.HandlerSha256===(kind==='baseline'?baseline.HunkSha256:current.HunkSha256),'Pressure handler mismatch');
 require(t.Result.InitialMediaSha256===pressureFinal.SeedSha256&&t.Result.ResultMediaSha256===pressureFinal.SeedSha256,'Pressure media mutated');
}
const basePressure=median(pressure.baseline.map(t=>t.Result.PhaseCycles.mutationCycles));
const finalPressure=median(pressure.final.map(t=>t.Result.PhaseCycles.mutationCycles));
const hostBaseline=read(`${artifacts}/baseline-host.json`),hostFinal=read(`${artifacts}/final-host.json`);
require(hostBaseline.Passed&&hostFinal.Passed&&hostBaseline.SeedSha256===hostFinal.SeedSha256,'Host study failure');
require(hostFinal.SeedSha256===pressureFinal.SeedSha256&&hostFinal.DataSectorReads===1512&&hostFinal.WriteRequests===0&&hostFinal.Flushes===0,'Host pressure inputs/results');
const gate=read(`${artifacts}/final-rom-gate/qualification.json`);
require(gate.Passed&&gate.IndependentOwnershipAndShutdownPayloadVerified&&gate.HandlerSha256===current.HunkSha256,'Final ROM qualification failed');
const trx=folder=>fs.readdirSync(folder).filter(f=>f.endsWith('.trx')).map(f=>{
 const text=fs.readFileSync(path.join(folder,f),'utf8'),counter=text.match(/<Counters\b([^>]+)\/?\s*>/);
 require(counter,'Missing TRX counters');
 const c=Object.fromEntries([...counter[1].matchAll(/(\w+)="(\d+)"/g)].map(m=>[m[1],Number(m[2])]));
 require(c.failed===0&&c.error===0&&c.timeout===0&&c.aborted===0&&c.executed===c.passed,'TRX failure '+f);
 const assembly=text.match(/<UnitTest\b[^>]*storage="([^"]+\.dll)"/i)?.[1].split(/[\\/]/).pop();
 require(assembly,'Missing assembly identity');
 // VSTest/xUnit leaves Counters.notExecuted at zero for skipped cases.
 const skipped=[...text.matchAll(/<UnitTestResult\b[^>]*outcome="NotExecuted"/g)].length;
 require(c.total-c.executed===skipped,'TRX skip count '+f);
 return {file:f,assembly,...c,skipped};
});
require(fs.readFileSync(`${artifacts}/solution-test-exit.txt`,'utf8').trim()==='0','Full suite incomplete');
require(fs.readFileSync(`${artifacts}/replay-exit.txt`,'utf8').trim()==='0','Replay incomplete');
const tests={solution:trx(`${artifacts}/solution-results`),finalPrototype:trx(`${artifacts}/final3-prototype-results`),copperStartReplay:trx(`${artifacts}/replay-results`)};
require(tests.solution.reduce((s,t)=>s+t.passed,0)===2747&&tests.solution.reduce((s,t)=>s+t.skipped,0)===10,'Full solution count');
require(tests.finalPrototype.reduce((s,t)=>s+t.passed,0)===21,'Final prototype tests incomplete');
require(tests.copperStartReplay.reduce((s,t)=>s+t.passed,0)===169,'Replay count');
for(const name of ['final-solution-build.log','final-native-build.log']) require(fs.readFileSync(`${artifacts}/${name}`,'utf8').includes('Build succeeded.'),'Build incomplete');
const report={schema:1,date:'2026-11-17',scope:'Isolated CNM1 prototype cache policy; production CPX4 unchanged',
 nativeTiming:'68020 end-to-end emulated bus cycles; host counters are a separate portable-engine experiment',
 baselineHandlerSha256:baseline.HunkSha256,finalHandlerSha256:current.HunkSha256,compilerSha256:current.CompilerSha256,
 finalHunkBytes:fs.statSync(`${artifacts}/final-handler/CopperNovaPrototype`).size,sources:current.Sources,
 policy:{metadataPages:64,metadataBytes:32768,arenaBytes:98304,maxVictimVisits:128,hitPathUnchanged:true,dirtyAndCurrentPacketPagesProtected:true,durabilityUnchanged:true,crcUnchanged:true},
 host:{baseline:hostBaseline,final:hostFinal,metadataReadReductionPercent:(1-hostFinal.MetadataSectorReads/hostBaseline.MetadataSectorReads)*100},
 nativePressure:{baselineMedianCycles:basePressure,finalMedianCycles:finalPressure,reductionPercent:(1-finalPressure/basePressure)*100,
 seedSha256:pressureFinal.SeedSha256,probeSha256:pressureFinal.ProbeSha256,trials:pressure},originals,tests,romGate:gate,
 coverageNotes:['Full solution began with CLOCK revision 1; all unchanged legacy suites completed. Final revision 3 rebuilt and all 21 prototype tests reran separately.',
 'Native pressure has two trials per handler; original workloads have one fresh P4 baseline trial and two final trials each. Reference FFS/SFS each have two trials.',
 'Reference trials were collected in the revision-1 comparison using the same frozen mixed/stream guest HUNKs and independent reference handlers.',
 '169 CopperStart native replay tests cover existing integration paths, not the new prototype adapter.',
 'Actual final HUNK separately booted on MC68000/512 KiB Chip/zero Fast/4 KiB handler stack; mixed/shutdown persistence checked. Pressure timing probe uses 32 KiB handler stack.',
 'Ten optional legacy ROM/media tests remain skipped. No physical-device or broad-platform qualification.']};
fs.mkdirSync('benchmarks/coppernova-prototype',{recursive:true});
fs.writeFileSync('benchmarks/coppernova-prototype/2026-11-17-cache-policy.json',JSON.stringify(report,null,2)+'\n');
console.log(JSON.stringify({host:report.host.metadataReadReductionPercent,native:report.nativePressure.reductionPercent,
 originals:Object.fromEntries(Object.entries(originals).map(([k,v])=>[k,{baseline:v.baselineTotal,final:v.finalMedianTotal,changePercent:v.changePercent}])),
 tests:Object.fromEntries(Object.entries(tests).map(([k,v])=>[k,{passed:v.reduce((s,t)=>s+t.passed,0),skipped:v.reduce((s,t)=>s+t.skipped,0)}]))},null,2));
