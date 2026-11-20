param(
    [Parameter(Mandatory=$true)][ValidateSet('mixed','stream')][string]$Workload,
    [Parameter(Mandatory=$true)][string]$EvidenceRoot,
    [Parameter(Mandatory=$true)][string]$Rom,
    [string]$Handler='', [string]$ArtifactRoot='', [switch]$PrototypeOnly,
    [string]$BaselineHandler='', [switch]$Packed, [string]$BenchAssembly='',
    [switch]$ChipOnly, [switch]$PackedBaseline, [switch]$ReusableObjects
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if(!$Handler) { $Handler=Join-Path $repo 'artifacts/nova-prototype-handler/CopperNovaPrototype' }
if(!$ArtifactRoot) { $ArtifactRoot=Join-Path $repo "artifacts/nova-comparison-$Workload" }
$ffs=Join-Path $EvidenceRoot 'cpx4-metadata-redesign-20261115/reference-inputs/FFS'
$sfs=Join-Path $EvidenceRoot 'cpx4-metadata-redesign-20261115/reference-inputs/SFS'
$sfsImage=Join-Path $EvidenceRoot 'cpx4-cpu-redesign-20261115/retained-mixed/sfs-1.279-base-16m.hdf'
$probe=Join-Path $EvidenceRoot "cpx4-compact-redesign-20261115/architecture-a86q-comparison-$Workload/BENCHPROBE"
foreach($pair in @(@($ffs,'9992D843E3936EF81F92311404BCA557E3727DA3A10F39B586B94B02921B3773'),@($sfs,'C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205'),@($sfsImage,'18170E0D35A5307AA29F17751B50BC848EB7B47211EB76FC1A88E95091409137')))
{ if((Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash -ne $pair[1]) { throw "Reference input hash mismatch: $($pair[0])" } }
$expectedProbe=if($Workload -eq 'mixed') {'0984E37B49A652D20E62ED24A524713C228E1071B8422A5676A78E177C0AD5B1'} else {'31AF34F1FF54A5AB4EBFF9D74C612C3BA00CB20C845DEB8947896E619656F906'}
if((Get-FileHash -LiteralPath $probe -Algorithm SHA256).Hash -ne $expectedProbe) { throw 'Frozen guest probe hash mismatch' }
if((Get-FileHash -LiteralPath $Rom -Algorithm SHA256).Hash -ne '8C8A0CF04F91B88EAF0C4F1126041987067E2286A8EE590BDBAE447A8000C5EE') { throw 'Kickstart 3.1 ROM hash mismatch' }
if($PackedBaseline -and (!$Packed -or !$BaselineHandler)) { throw 'PackedBaseline requires Packed and BaselineHandler' }
if($ReusableObjects -and !$Packed) { throw 'ReusableObjects requires Packed' }
$env:NOVA_BENCH_REUSABLE_OBJECTS=if($ReusableObjects) {'1'} else {$null}
$env:NOVA_BENCH_BASELINE_PACKED=if($PackedBaseline) {'1'} else {$null}
$env:NOVA_BENCH_MACHINE=if($ChipOnly) {'68000-chip'} else {$null}
$env:NOVA_PACKED=if($Packed) {'1'} else {$null}
$env:CPX_BENCH_ROOT=[IO.Path]::GetFullPath($ArtifactRoot)
$env:CPX_BENCH_HANDLER=[IO.Path]::GetFullPath($Handler)
$env:NOVA_BENCH_BASELINE_HANDLER=if($BaselineHandler) { [IO.Path]::GetFullPath($BaselineHandler) } else { $null }
$env:CPX_BENCH_FFS_HANDLER=$ffs; $env:CPX_BENCH_SFS_HANDLER=$sfs; $env:CPX_BENCH_SFS_IMAGE=$sfsImage
$env:CPX_BENCH_ROM=[IO.Path]::GetFullPath($Rom); $env:NOVA_BENCH_PROBE=$probe
$env:CPX_ONLY=if($PrototypeOnly) {'1'} else {'0'}
if(!$BenchAssembly) { $BenchAssembly=Join-Path $repo 'tools/CopperNova.Bench/bin/Release/net10.0/CopperNova.Bench.dll' }
if($ChipOnly) {
    New-Item -ItemType Directory -Force -Path $env:CPX_BENCH_ROOT | Out-Null
    $env:NOVA_BENCH_PROBE=Join-Path $env:CPX_BENCH_ROOT "chip-$Workload.hunk"
    dotnet $BenchAssembly compile-chip $Workload $env:NOVA_BENCH_PROBE
    if($LASTEXITCODE -ne 0) { throw 'Chip-memory probe compilation failed' }
}
dotnet $BenchAssembly $Workload
if($LASTEXITCODE -ne 0) { throw "Prototype $Workload comparison failed" }
