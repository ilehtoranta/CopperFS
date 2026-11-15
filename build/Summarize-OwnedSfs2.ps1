param([string]$ArtifactRoot = 'artifacts/owned-sfs2', [string]$CandidatePrefix = 'durable')
$ErrorActionPreference = 'Stop'

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Means($Samples) {
    @($Samples | Group-Object Arm, Phase | ForEach-Object {
        $first = $_.Group[0]
        [pscustomobject]@{
            Arm = $first.Arm; Phase = $first.Phase; Trials = $_.Count
            Cycles = [math]::Round(($_.Group | Measure-Object Cycles -Average).Average, 3)
            Reads = [math]::Round(($_.Group | Measure-Object Reads -Average).Average, 3)
            Writes = [math]::Round(($_.Group | Measure-Object Writes -Average).Average, 3)
            Flushes = [math]::Round(($_.Group | Measure-Object Flushes -Average).Average, 3)
        }
    })
}

$profiles = @(foreach ($budget in @(0, 16, 64, 256)) {
    $binary = Join-Path $ArtifactRoot "$CandidatePrefix-$budget/CopperSFS"
    $root = Join-Path $ArtifactRoot "$CandidatePrefix-qualification-$budget"
    $owned = Join-Path $root 'owned-performance'
    $memory = @(Read-Json (Join-Path $owned 'fast-32-32-16-False/0-candidate/owned-mount-memory.json'))
    if ($memory.Count -ne 1 -or $memory[0].AdditionalPerMountBytes -ne $budget * 1024) { throw "Invalid RAM observation: $budget" }
    $measurements = @(Get-ChildItem -LiteralPath $owned -Directory | Sort-Object Name | ForEach-Object {
        $cycles = Join-Path $_.FullName 'cycles.json'
        if (Test-Path -LiteralPath $cycles) {
            $data = Read-Json $cycles
            if ($data.CandidateSha256 -ne (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash) { throw "Stale candidate measurement: $cycles" }
            $ram = @(foreach ($arm in @('original', 'grouped', 'candidate')) {
                $probe = (Read-Json (Join-Path $_.FullName "0-$arm/result.json")).ProbeData
                [pscustomobject]@{ Arm = $arm; AfterMountBytes = $probe[0] - $probe[1]; AfterMutationBytes = $probe[0] - $probe[2]; AfterReadBytes = $probe[0] - $probe[3] }
            })
            [pscustomobject]@{ Case = $_.Name; Means = Means $data.Samples; MountRam = $ram }
        }
    })
    $gates = @(foreach ($placement in @('original-performance', 'original-performance-chip')) {
        $data = Read-Json (Join-Path $root "$placement/sfs2-trst/cycles.json")
        if ($data.CandidateSha256 -ne (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash) { throw "Stale parity measurement: $root/$placement" }
        [pscustomobject]@{ Placement = $placement; Means = Means $data.Samples }
    })
    [pscustomobject]@{
        ExtraKiB = $budget; HandlerBytes = (Get-Item -LiteralPath $binary).Length
        HandlerSha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
        Memory = $memory[0]; Measurements = $measurements; ReleaseGates = $gates
    }
})
$profile = Read-Json (Join-Path $ArtifactRoot "$CandidatePrefix-qualification-0/owned-cpu-profile/candidate/mutate-profile.json")
$report = [pscustomobject]@{
    Profiles = $profiles
    WarmCpuProfile = [pscustomobject]@{
        GuestCycles = $profile.GuestCycles
        Inclusive = @($profile.InclusiveMethods | Where-Object Name -Match 'Root|Recover|Prepare|TransactionBuffer|Copy|BlockSum|MetadataStore')
        Exclusive = @($profile.Methods | Sort-Object Cycles -Descending | Select-Object -First 25)
        BlockCopyCallers = $profile.BlockCopyCallers
    }
}
$destination = Join-Path $ArtifactRoot "$CandidatePrefix-summary.json"
$report | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $destination
Write-Output $destination
