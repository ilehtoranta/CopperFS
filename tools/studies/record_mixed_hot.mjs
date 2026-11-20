import fs from 'node:fs';
import crypto from 'node:crypto';

const root='artifacts/mixed-hot';
const read=file=>JSON.parse(fs.readFileSync(file,'utf8').replace(/^\uFEFF/,''));
const hash=file=>crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex').toUpperCase();
const check=(v,label)=>{if(!v)throw Error(label);};
const native=read(root+'/final/source-manifest.json');
check(native.ManagedAllocationSites===0&&hash(root+'/final/CopperNovaPrototype')===native.HunkSha256,'native build');
for(const src of native.Sources)check(hash(src.Path)===src.Sha256,'source mismatch '+src.Path);
const prior=read('benchmarks/coppernova-prototype/2026-11-17-packed-root.json');
const workloads={},profiles={},gates={};
for(const phase of ['mutation','flush','remount','read']){
  const before=read(root+'/profile-'+phase+'/profile.json'),after=read(root+'/final-profile-'+phase+'/profile.json');
  check(before.HandlerSha256===prior.native.manifest.HunkSha256&&after.HandlerSha256===native.HunkSha256&&before.ProbeSha256===after.ProbeSha256,'profile controls');
  profiles[phase]={before,after};
}
for(const name of ['gate','extended']){
  const g=read(root+'/'+name+'/qualification.json');check(g.Passed&&g.IndependentOwnershipAndShutdownPayloadVerified&&g.OperationAllocations===0&&g.ShutdownBarriers===2&&g.HandlerSha256===native.HunkSha256,'ROM gate '+name);gates[name]=g;
}
for(const name of ['mixed','stream']){
  const r=read(root+'/final-'+name+'/benchmark.json');
  check(r.Inputs.PrototypeHandlerSha256===native.HunkSha256,'wrong candidate');
  for(const k of ['Sfs1279HandlerSha256','FfsHandlerSha256','ProbeSha256','KickstartSha256','SfsBaseSha256'])check(r.Inputs[k]===prior.workloads[name].inputs[k],k);
  check(r.Trials.length===6&&r.Summary.length===3&&r.Summary.every(s=>s.Trials===2&&s.AllPassed)&&r.Trials.every(t=>t.Passed&&t.IndependentCheck&&Object.values(t.PhaseCycles).every(c=>c>0)),'comparison '+name);
  const frozen=prior.workloads[name].trials.find(t=>t.Name==='CopperNova prototype');
  const base=fs.readFileSync('artifacts/packed-aligned/'+name+'/CopperNova-prototype/trial-1/disk.hdf');
  check(frozen.ResultMediaSha256===crypto.createHash('sha256').update(base).digest('hex').toUpperCase(),'control image');
  for(const t of r.Trials.filter(t=>t.Name==='CopperNova prototype')){
    check(t.IndependentCheck===frozen.IndependentCheck&&t.Flushes===frozen.Flushes,'durable ownership or barrier mismatch '+name);
    const file=root+'/final-'+name+'/CopperNova-prototype/trial-'+t.Trial+'/disk.hdf';
    const disk=fs.readFileSync(file),diff=[];
    check(disk.length===base.length&&hash(file)===t.ResultMediaSha256,'candidate image');
    for(let i=0;i<base.length;i++)if(base[i]!==disk[i])diff.push(i);
    check(diff.length===5&&diff.every((v,i)=>v===195*512+[8,9,10,11,207][i])&&disk[195*512+207]+4===base[195*512+207],'unexpected disk changes');
  }
  workloads[name]=r;
}
const qualification={};
for(const [name,expected] of [['host',42],['replay',169]]){
  const dir=root+'/'+name+'-results',files=fs.readdirSync(dir).filter(f=>f.endsWith('.trx'));
  check(files.length===1,name+' results count');
  const xml=fs.readFileSync(dir+'/'+files[0],'utf8'),counts=xml.match(/<Counters\s+[^>]+>/)?.[0]??'';
  check(counts.includes('passed="'+expected+'"')&&counts.includes('total="'+expected+'"')&&counts.includes('failed="0"'),name+' counts');
  qualification[name]={passed:expected,trxSha256:hash(dir+'/'+files[0])};
}
const inspection=read(root+'/hunk-inspection/inspection.json');
check(inspection.HunkSha256===native.HunkSha256&&inspection.Methods.find(m=>m.Name.endsWith('::Crc')).CallSites.length===0,'emitted checksum loop');
fs.writeFileSync('benchmarks/coppernova-prototype/2026-11-17-mixed-hot.json',JSON.stringify({native,profiles,gates,qualification,inspection,workloads},null,2)+'\n');
