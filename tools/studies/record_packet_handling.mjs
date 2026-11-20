// Rebuild the public evidence from local receipts. Keep ROMs, disks and HUNKs local.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createHash } from 'node:crypto';

const root = 'artifacts/packet-core';
const json = file => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
const hash = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex').toUpperCase();
const previous = json('benchmarks/coppernova-prototype/2026-11-17-mixed-hot.json');
const manifest = json(root + '/final/source-manifest.json');
const candidate = manifest.HunkSha256;
assert.equal(hash(root + '/final/CopperNovaPrototype'), candidate);
assert.equal(hash(root + '/regions/CopperNovaPrototype'), candidate);
assert.equal(manifest.ManagedAllocationSites, 0);
for (const source of manifest.Sources) assert.equal(hash(source.Path.replaceAll('\\', '/')), source.Sha256);

const workloads = {};
for (const name of ['mixed', 'stream']) {
    const run = json(root + '/comparison-' + name + '/benchmark.json');
    const before = previous.workloads[name];
    assert.equal(run.Inputs.PrototypeHandlerSha256, candidate);
    for (const key of ['ProbeSha256', 'FfsHandlerSha256', 'Sfs1279HandlerSha256', 'KickstartSha256', 'SfsBaseSha256'])
        assert.equal(run.Inputs[key], before.Inputs[key]);
    assert.equal(run.Trials.length, 6);
    assert(run.Summary.every(s => s.Trials === 2 && s.AllPassed));
    assert(run.Trials.every(t => t.Passed && t.IndependentCheck && Object.values(t.PhaseCycles).every(v => v > 0)));
    const control = before.Trials.find(t => t.Name === 'CopperNova prototype');
    for (const trial of run.Trials.filter(t => t.Name === control.Name)) {
        assert.equal(trial.ResultMediaSha256, control.ResultMediaSha256);
        assert.equal(trial.IndependentCheck, control.IndependentCheck);
        assert.equal(trial.Flushes, control.Flushes);
        const image = root + '/comparison-' + name + '/CopperNova-prototype/trial-' + trial.Trial + '/disk.hdf';
        assert.equal(hash(image), trial.ResultMediaSha256);
    }
    workloads[name] = run;
}

const mixedConfirmation = json(root + '/confirmation-mixed/benchmark.json');
assert.deepEqual(mixedConfirmation.Inputs, workloads.mixed.Inputs);
assert.equal(mixedConfirmation.Trials.length, 6);
assert(mixedConfirmation.Summary.every(s => s.Trials === 2 && s.AllPassed));
assert(mixedConfirmation.Trials.every(t => t.Passed && t.IndependentCheck));
for (const trial of mixedConfirmation.Trials.filter(t => t.Name === 'CopperNova prototype'))
    assert.equal(trial.ResultMediaSha256, workloads.mixed.Trials[0].ResultMediaSha256);

const profiles = {};
for (const phase of ['mutation', 'flush', 'remount', 'read']) {
    const before = previous.profiles[phase].after;
    const after = json(root + '/final-profile-' + phase + '/profile.json');
    assert.equal(after.HandlerSha256, candidate);
    assert.equal(after.ProbeSha256, before.ProbeSha256);
    profiles[phase] = { previousHandlerCycles: before.TaskCycles, ...after };
}

const startupBefore = json(root + '/previous-profile-startup/profile.json');
const startupAfter = json(root + '/final-profile-startup/profile.json');
assert.equal(startupBefore.HandlerSha256, previous.native.HunkSha256);
assert.equal(startupAfter.HandlerSha256, candidate);
assert.equal(startupBefore.ProbeSha256, startupAfter.ProbeSha256);
profiles.startup = { previousHandlerCycles: startupBefore.TaskCycles, ...startupAfter };

const gates = {};
for (const gate of ['gate', 'extended']) {
    const result = json(root + '/final-' + gate + '/qualification.json');
    assert.equal(result.HandlerSha256, candidate);
    assert(result.Passed && result.GuardIntact && result.SourceInputsMatched && result.IndependentOwnershipAndShutdownPayloadVerified);
    assert.equal(result.OperationAllocations, 0);
    assert.equal(result.ShutdownBarriers, 2);
    assert.equal(result.LargestStartupAllocMemRequest, 115200);
    gates[gate] = result;
}
const inspection = json(root + '/hunk-inspection/inspection.json');
assert.equal(inspection.HunkSha256, candidate);
for (const name of ['SectorCrc', 'PatchedCrc', 'Find'])
    assert.equal(inspection.Methods.find(m => m.Name.endsWith('::' + name)).CallSites.length, 0);

function testReceipts(directory) {
    return fs.readdirSync(directory).filter(name => name.endsWith('.trx')).map(name => {
        const file = directory + '/' + name;
        const xml = fs.readFileSync(file, 'utf8');
        const counters = xml.match(/<Counters\s+([^>]+)>/)[1];
        const get = key => Number(counters.match(new RegExp('\\b' + key + '="(\\d+)"'))[1]);
        assert.equal(get('failed'), 0);
        const assembly = xml.match(/<UnitTest [^>]*storage="([^"]+)"/i)[1].split(/[\\/]/).pop();
        return { assembly, passed: get('passed'), skipped: get('total') - get('executed'), sha256: hash(file) };
    });
}
const suite = testReceipts(root + '/final-suite');
assert.equal(new Set(suite.map(r => r.assembly)).size, 7);
const replay = testReceipts(root + '/replays');
assert.equal(replay[0].passed, 169);
const focused = testReceipts(root + '/focused');
assert.equal(focused[0].passed, 43);
const directIoReplay = json(root + '/io-replay.json');
assert.equal(directIoReplay.HandlerSha256, candidate);
assert.equal(new Set(directIoReplay.Cases).size, 12);
const output = { directIoReplay, previousHandlerSha256: previous.native.HunkSha256, manifest, workloads, mixedConfirmation, profiles, gates, inspection, suite, focused, replay };
fs.writeFileSync('benchmarks/coppernova-prototype/2026-11-18-packet-handling.json', JSON.stringify(output, null, 2) + '\n');
