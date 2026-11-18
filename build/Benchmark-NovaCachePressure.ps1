param(
    [Parameter(Mandatory=$true)][string]$EvidenceRoot,
    [Parameter(Mandatory=$true)][string]$Rom,
    [Parameter(Mandatory=$true)][string]$BaselineHandler,
    [Parameter(Mandatory=$true)][string]$ClockHandler,
    [Parameter(Mandatory=$true)][string]$Seed,
    [Parameter(Mandatory=$true)][string]$Probe,
    [Parameter(Mandatory=$true)][string]$ArtifactRoot,
    [switch]$ClockOnly
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$env:CPX_BENCH_FFS_HANDLER=Join-Path $EvidenceRoot 'cpx4-metadata-redesign-20261115/reference-inputs/FFS'
$env:CPX_BENCH_SFS_HANDLER=Join-Path $EvidenceRoot 'cpx4-metadata-redesign-20261115/reference-inputs/SFS'
$env:CPX_BENCH_SFS_IMAGE=Join-Path $EvidenceRoot 'cpx4-cpu-redesign-20261115/retained-mixed/sfs-1.279-base-16m.hdf'
$env:CPX_BENCH_ROM=[IO.Path]::GetFullPath($Rom)
$env:NOVA_BENCH_PROBE=[IO.Path]::GetFullPath($Probe)
$env:NOVA_CACHE_SEED=[IO.Path]::GetFullPath($Seed)
$env:CPX_ONLY='1'
$receipts=@()
$order=@(@('baseline',$BaselineHandler),@('clock',$ClockHandler),@('clock',$ClockHandler),@('baseline',$BaselineHandler))
if($ClockOnly) { $order=@(@('clock',$ClockHandler),@('clock',$ClockHandler)) }
for($trial=0;$trial -lt $order.Count;$trial++) {
    $env:CPX_BENCH_ROOT=[IO.Path]::GetFullPath((Join-Path $ArtifactRoot "$trial-$($order[$trial][0])"))
    $env:CPX_BENCH_HANDLER=[IO.Path]::GetFullPath($order[$trial][1])
    dotnet (Join-Path $repo 'tools/CopperNova.Bench/bin/Release/net10.0/CopperNova.Bench.dll') cache
    if($LASTEXITCODE -ne 0) { throw 'Native cache-pressure benchmark failed' }
    $result=Get-Content -LiteralPath (Join-Path $env:CPX_BENCH_ROOT 'CopperNova-prototype/trial-1/result.json') -Raw | ConvertFrom-Json
    if(!$result.Passed) { throw 'Native pressure guest/checker failed' }
    $receipts+=@{Variant=$order[$trial][0];Result=$result;HandlerSha256=(Get-FileHash -LiteralPath $env:CPX_BENCH_HANDLER).Hash}
}
@{Timing='68020 emulated bus-cycle deltas; mutationCycles covers 1,512 opens/read-one-byte/closes with per-read payload verification';SeedSha256=(Get-FileHash -LiteralPath $Seed).Hash;ProbeSha256=(Get-FileHash -LiteralPath $Probe).Hash;Trials=$receipts} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'pressure.json')
