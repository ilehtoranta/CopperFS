"""Publish source-only reports after validating the selected prototype evidence.

Generated ROM/media/HUNKs remain ignored. Fails closed on incomplete benchmarks,
hash/source mismatch, incomplete tests, allocation/stack or shutdown gate failure.
The long solution run tested P2's new tests; P4 has a separate latest 19-case run.
Unchanged legacy projects are reported separately, without adding duplicate tests.
"""
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import subprocess
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
artifacts = root / 'artifacts'
evidence = artifacts / 'metadata-study'


def require(value, message):
    if not value:
        raise RuntimeError(message)


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def trx(path):
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    xml = ET.parse(path).getroot()
    counts = xml.find('t:ResultSummary/t:Counters', ns).attrib
    unit = xml.find('t:TestDefinitions/t:UnitTest', ns)
    name = unit.attrib['storage'].replace('\\', '/').split('/')[-1]
    outputs = [node.text or '' for node in xml.findall('.//t:StdOut', ns)]
    outcomes = [node.attrib['outcome'] for node in xml.findall('t:Results/t:UnitTestResult', ns)]
    require(all(value in ('Passed', 'Failed', 'NotExecuted') for value in outcomes), 'Unexpected TRX outcome')
    skipped = outcomes.count('NotExecuted')
    require(skipped == int(counts['total']) - int(counts['executed']), 'TRX skip count mismatch')
    return {'assembly': name, 'passed': int(counts['passed']),
            'failed': int(counts['failed']), 'skipped': skipped,
            'output': '\n'.join(outputs)}


manifest_path = artifacts / 'nova-p4-handler/source-manifest.json'
manifest = read(manifest_path)
handler = manifest_path.parent / 'CopperNovaPrototype'
require(sha(handler) == manifest['HunkSha256'], 'Selected handler changed')
require(manifest['ManagedAllocationSites'] == 0, 'Native allocation sites')
for source in manifest['Sources']:
    require(sha(root / source['Path'].replace('\\', '/')) == source['Sha256'], 'Source changed: ' + source['Path'])

legacy_paths = ['src/CopperCPX.Core', 'src/CopperFS.Amiga/Cpx4',
                'src/CopperFFS.Core', 'src/CopperSFS.Core',
                'src/CopperFS.Amiga/CopperFS.Amiga.csproj', 'build/Build-Cpx4Handler.ps1',
                'tests/CopperCPX.Core.Tests', 'tests/CopperCPX.InstructionTests']
subprocess.run(['git', 'diff', '--exit-code', '6a4380d', '--', *legacy_paths], cwd=root, check=True, capture_output=True)

benchmarks = {}
for workload, probe_hash in [('mixed', '0984E37B49A652D20E62ED24A524713C228E1071B8422A5676A78E177C0AD5B1'),
                             ('stream', '31AF34F1FF54A5AB4EBFF9D74C612C3BA00CB20C845DEB8947896E619656F906')]:
    data = read(artifacts / f'nova-p4-comparison-{workload}/benchmark.json')
    require(len(data['Trials']) == 6 and len(data['Summary']) == 3, 'Incomplete comparison')
    require(all(t['Passed'] and t['IndependentCheck'] for t in data['Trials']), 'A guest/checker failed')
    require(all(s['Trials'] == 2 and s['AllPassed'] for s in data['Summary']), 'Incomplete trials')
    inputs = data['Inputs']
    require(inputs['PrototypeHandlerSha256'] == manifest['HunkSha256'], 'Benchmark handler mismatch')
    require(inputs['ProbeSha256'] == probe_hash, 'Measured guest changed')
    require(inputs['FfsHandlerSha256'] == '9992D843E3936EF81F92311404BCA557E3727DA3A10F39B586B94B02921B3773', 'FFS changed')
    require(inputs['Sfs1279HandlerSha256'] == 'C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205', 'SFS changed')
    require(inputs['KickstartSha256'] == '8C8A0CF04F91B88EAF0C4F1126041987067E2286A8EE590BDBAE447A8000C5EE', 'ROM changed')
    require(inputs['SfsBaseSha256'] == '18170E0D35A5307AA29F17751B50BC848EB7B47211EB76FC1A88E95091409137', 'SFS fixture changed')
    benchmarks[workload] = data

gate = read(artifacts / 'nova-p4-rom-gate/qualification.json')
require(gate['Passed'] and gate['IndependentOwnershipAndShutdownPayloadVerified'], 'Incomplete gate')
gate_check = read(artifacts / 'nova-p4-rom-gate/independent-check.json')
gate_temp = next(e for e in gate_check['Entries'] if e['Path'] == '/Bench/Temp.bin')
require(gate_temp['Size'] == 1024 and gate_temp['Sha256'] == hashlib.sha256(bytes([0x5A]) * 1024).hexdigest().upper(), 'Gate temporary payload mismatch')
require(gate['HandlerSha256'] == manifest['HunkSha256'] and gate['SourceInputsMatched'], 'Gate/source mismatch')
require(gate['OperationAllocations'] == 0 and gate['StartupAllocations'] > 0 and gate['ManagedAllocationSites'] == 0, 'Invalid allocation gate')
require(gate['HandlerTaskStackBytes'] == 4096 and gate['GuardIntact'] and 0 < gate['Peak'] <= 4096, 'Invalid stack gate')
require(gate['ShutdownBarriers'] == 4, 'Shutdown durability gate')

require((evidence / 'solution-test-exit.txt').read_text().strip() == '0', 'Solution suite incomplete/failed')
suite = [trx(path) for path in (evidence / 'solution-results').glob('*.trx')]
require(len(suite) == 7 and all(t['failed'] == 0 for t in suite), 'Unexpected solution results')
baseline_prototype = next(t for t in suite if t['assembly'].lower() == 'coppernova.tests.dll')
legacy = [t for t in suite if t['assembly'].lower() != 'coppernova.tests.dll']
require(sum(t['passed'] for t in legacy) == 2726 and sum(t['skipped'] for t in legacy) == 10, 'Unexpected legacy coverage')
latest = max((evidence / 'final-prototype-results').glob('*.trx'), key=lambda p: p.stat().st_mtime_ns)
prototype = trx(latest)
require(prototype['passed'] == 19 and prototype['failed'] == 0 and prototype['skipped'] == 0, 'Latest prototype tests failed')
replay = [trx(path) for path in (evidence / 'replay-results').glob('*.trx')]
require(len(replay) == 1 and replay[0]['passed'] == 169 and replay[0]['failed'] == 0 and replay[0]['skipped'] == 0, 'Replay failed')
require('Build succeeded.' in (evidence / 'final-solution-build.log').read_text(encoding='utf-8-sig'), 'Final build failed')


def summary(workload):
    return {row['Name']: row for row in benchmarks[workload]['Summary']}


mixed, stream = summary('mixed'), summary('stream')
names = ['CopperNova prototype', 'Kickstart FFS 40.1', 'SmartFilesystem 1.279']
totals = {w: {n: summary(w)[n]['TotalMeasuredCyclesMedian'] for n in names} for w in benchmarks}
beats_both = all(totals[w][names[0]] < min(totals[w][names[1]], totals[w][names[2]]) for w in totals)
result = {
    'schema': 1, 'date': '2026-11-16', 'generatedAtUtc': datetime.now(timezone.utc).isoformat(),
    'candidate': 'CNM1-P4', 'status': 'isolated-prototype',
    'performanceObjectiveAchieved': beats_both, 'existingCpxSourcesUnchanged': True,
    'bufferedDurability': 'successful ACTION_FLUSH, inhibit/unmount and ACTION_DIE after all ordered barriers',
    'native': {'handlerBytes': handler.stat().st_size, 'handlerSha256': manifest['HunkSha256'],
               'compilerSha256': manifest['CompilerSha256'], 'managedAllocationSites': 0,
               'arenaBytes': 98304, 'sourceCount': len(manifest['Sources']), 'sources': manifest['Sources']},
    'coverage': {'finalReleaseBuild': 'passed with .NET 10 target overrides',
                 'unchangedLegacyProjects': [{k: v for k, v in t.items() if k != 'output'} for t in legacy],
                 'solutionInvocationPrototypeRevision': 'P2; superseded by the separately rerun final P4 tests',
                 'solutionInvocationPrototypePassed': baseline_prototype['passed'],
                 'latestPrototype': {k: v for k, v in prototype.items() if k != 'output'},
                 'physicalCrashRetryStates': 74, 'physicalRecoveryCrashStates': 32,
                 'failedRequestPositions': 13, 'shortOrFailedRequestPositions': 13,
                 'copperStartReplay': {k: v for k, v in replay[0].items() if k != 'output'},
                 'actualHunkRomGate': gate, 'physicalDevicesAndBroaderPlatformMatrix': 'not run'},
    'benchmarks': benchmarks,
    'limits': ['one group, 2–32 MiB, 512-byte sectors', 'ASCII inline directories, eight inline extents',
               'partial DOS packet and attribute coverage; no feature parity claim',
               'no production CPX4 conversion/integration or publication'],
    'history': {'P1': {w: read(artifacts / f'nova-p1-comparison-{w}/benchmark.json')['Summary'] for w in ('mixed', 'stream')},
                'P2stream': 'interrupted after P4 superseded it; not an acceptance result'}
}
out = root / 'benchmarks/coppernova-prototype/2026-11-16-metadata.json'
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')

lines = ['# CopperNova metadata prototype: benchmark and qualification, 16 November 2026', '',
         'CNM1-P4 implements the common direct-metadata path in an isolated prototype.',
         '**It does not beat both original handlers in both workloads.**' if not beats_both else '**It beats both references in both measured workloads, within the scope below.**',
         'Existing CPX4/CPX5, FFS and SFS source and native build selections remain unchanged.',
         'The [prototype README](../prototypes/CopperNova.Prototype/README.md) records the layout,',
         'supported operations, format and feature limits. This is not a production replacement.', '',
         '## Fresh complete-workload measurements', '',
         'Two-trial median emulated bus cycles; lower is better. Same frozen mixed and',
         'streaming guest HUNKs, Kickstart 3.1, emulated 68020, 512 KiB Chip plus 2 MiB',
         'Fast, 16 MiB images, 512-byte sectors, 32 requested DOS buffers and interleaved',
         'prototype/FFS/SFS/SFS/prototype/FFS order. All twelve trials pass guest checks',
         'and independent persisted checks. The prototype decoder verifies full ownership,',
         'bitmaps/counters and both retained file payloads. Reference inputs are original',
         'FFS 40.1 and SmartFilesystem 1.279, unchanged and hash-checked.', '',
         '| Workload | CNM1-P4 | FFS 40.1 | SFS 1.279 |', '| --- | ---: | ---: | ---: |']
for w, title in [('mixed', 'Mixed'), ('stream', 'Streaming')]:
    lines.append('| ' + title + ' | ' + ' | '.join(f'{totals[w][n]:,.0f}' for n in names) + ' |')
lines += ['', 'The backend does not charge physical seek/rotation/transfer latency. Host elapsed',
          'time is not compared. The 32-buffer setting does not equalize internal cache',
          'memory. The prototype has a 96 KiB arena and a narrower feature set than the',
          'references; these results do not establish full filesystem or physical-device parity.', '',
          '## Phase accounting', '', '| Workload / phase | CNM1-P4 | FFS | SFS |', '| --- | ---: | ---: | ---: |']
for w, title in [('mixed', 'Mixed'), ('stream', 'Streaming')]:
    for key, label in [('MutationCyclesMedian', 'mutation'), ('FlushCyclesMedian', 'flush'), ('RemountCyclesMedian', 'remount'), ('VerifyCyclesMedian', 'read/verify')]:
        lines.append('| ' + title + ' ' + label + ' | ' + ' | '.join(f'{summary(w)[n][key]:,.0f}' for n in names) + ' |')
gap = totals['mixed'][names[0]] - totals['mixed'][names[1]]
boundary_gap = sum(mixed[names[0]][key] - mixed[names[1]][key] for key in ('FlushCyclesMedian', 'RemountCyclesMedian'))
lines += ['', f'Flush/remount account for {boundary_gap:,.0f} of the {gap:,.0f}-cycle mixed gap',
          f'to FFS ({boundary_gap/gap:.1%}). Direct inodes and inline directories remove',
          'selection and name-page work, but do not make the current publication path cheap.',
          'Mixed changes seven final metadata images. Retirement seals and journals them,',
          'writes descriptor/commit/clean controls, installs every image at home, and orders',
          'four persistence barriers. Its CRC kernel still visits zero padding; the older',
          'qualified CPX path had additional sparse-zero CRC shortcuts. These are code-level',
          'costs to profile, not exclusive-cycle attribution. Fewer metadata reads alone',
          'are insufficient evidence of a faster native implementation.', '',
          'A short FFS ACTION_FLUSH phase does not prove omitted persistence: earlier',
          'operations can perform work before the marker. Acceptance compares the full sequence.', '',
          '## Durability and execution evidence', '',
          f'- Latest prototype host tests: **{prototype["passed"]} passed, zero failed/skipped**.',
          '- Physical byte-sector power cuts: 74 crash/changed-retry states after three',
          '  committed warmup transactions, 32 interrupted-recovery states, and failures',
          '  or short transfers at each of 13 journal request positions. The model tests',
          '  loss, reordered persistence, torn writes, bank reuse and idempotent replay.',
          '- Final Release solution build passed with zero warnings/errors. The solution',
          '  suite run started on P2: 2,726 unchanged legacy cases plus 11 prototype cases',
          '  passed, zero failed, ten skipped. After the counter/ownership fixes, the',
          '  final P4 build and all 19 prototype cases were rerun. The old projects do not',
          '  depend on the new prototype; their source/tests and shared boundaries are unchanged.',
          '- The legacy solution coverage separates 2,587 host cases from 139 compiled',
          '  native cases. Seven default-skipped ROM methods and three unavailable SFS',
          '  media cases remain skips, not passes.',
          '- Fresh CopperStart geometry/buffer replay: **169 passed**, zero failed/skipped.',
          '  This is separate legacy source-integration coverage, not prototype adapter coverage.',
          f'- Final {handler.stat().st_size:,}-byte MC68000 HUNK: **zero reachable managed allocation sites**;',
          '  manifest hashes match every source input and both comparisons and the ROM gate.',
          f'- Actual-HUNK ROM gate: MC68000, 512 KiB Chip, no Fast RAM, 4 KiB handler stack;',
          f'  **{gate["Peak"]}-byte measured peak**, intact guard, {gate["StartupAllocations"]} observed startup Exec allocations',
          '  and **zero operation-time Exec allocations**. The test runs mixed operations,',
          '  remount/readback, then appends without client flush and invokes ACTION_DIE.',
          '  It observes four shutdown barriers and independently checks all 7,027 final',
          '  main-file bytes, the temporary file and complete ownership after shutdown.',
          '- Broader packet/media/platform coverage, physical Amigas and physical storage',
          '  were not run. One targeted ROM gate is not a full platform qualification.', '',
          'Successful ACTION_FLUSH, inhibit/unmount and shutdown wait for full clean',
          'retirement. Earlier replies may precede persistence. New data is ordered before',
          'commit, home writes follow the durable commit, and uncommitted frees are',
          'quarantined. Existing in-place-overwrite atomicity limitations remain. The',
          'native core/transport owns fixed caller storage and performs no operation-time',
          'allocation or recursive traversal. Each continuation has fixed bounds and I/O yields.', '',
          '## Provenance and reproduction', '',
          f'Handler SHA-256: `{manifest["HunkSha256"]}`.',
          f'Compiler CLI SHA-256: `{manifest["CompilerSha256"]}`.', '',
          'The installed .NET 10.0.401 SDK cannot target the neighboring compiler\'s new',
          '.NET 11 framework. Builds select its existing .NET 10 target using',
          '`-p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0`; no compiler/SDK source',
          'was edited. The separate native project consumes only portable cores and CopperSharp.', '',
          'Use `build/Build-NovaPrototype.ps1`, `build/Benchmark-NovaPrototype.ps1` and the',
          'qualification command in the README. Selected raw receipts live under ignored',
          '`artifacts/nova-p4-*` and `artifacts/metadata-study`. P1 has complete historical',
          'comparisons; P2 streaming was interrupted when superseded and is not an acceptance',
          'result. No benchmark guest was edited or recompiled for the measured comparisons.', '',
          'The [machine-readable report](../benchmarks/coppernova-prototype/2026-11-16-metadata.json)',
          'retains phase ranges, input hashes, qualification and source hashes. Original',
          'ROMs, handler binaries, generated media and build artifacts are not committed.',
          'No production CPX4 replacement, conversion, package or ROM was published.', '']
(root / 'docs/coppernova-prototype-benchmark-2026-11-16.md').write_text('\n'.join(lines), encoding='utf-8')
print(json.dumps({'candidate': 'P4', 'totals': totals, 'beatsBothInBoth': beats_both,
                  'legacyPassed': sum(t['passed'] for t in legacy), 'latestPrototypePassed': prototype['passed'],
                  'nativeGate': gate['Passed'], 'report': str(out)}, indent=2))
