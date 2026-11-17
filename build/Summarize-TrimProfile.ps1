param([Parameter(Mandatory = $true)][string]$ArtifactRoot, [switch]$AssertBounded)
$ErrorActionPreference = 'Stop'

function Read-Samples([string]$Directory, [string]$Pattern) {
    @(Get-ChildItem -LiteralPath $Directory -Filter $Pattern -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
}
function Sum-Property($Samples, [string]$Property) {
    [long](($Samples | Measure-Object -Property $Property -Sum).Sum)
}
function Sum-Delta($Samples, [string]$Property) {
    [long](($Samples | ForEach-Object { $_.ScopeEnd.$Property - $_.ScopeStart.$Property } | Measure-Object -Sum).Sum)
}

$rows = @(foreach ($profile in @('ofs', 'ffs', 'sfs', 'sfs2-csun', 'sfs2-trst')) {
    $directory = Join-Path $ArtifactRoot "automatic/$profile/poison"
    if (!(Test-Path -LiteralPath $directory)) { throw "Missing completed profile: $profile" }
    $service = Read-Samples $directory 'deferred-*-service-profile.json'
    $tracking = Read-Samples $directory 'setup-*-tracking-profile.json'
    $cold = Read-Samples $directory 'cold-*-initialization-profile.json'
    if ($service.Count -eq 0 -or $tracking.Count -eq 0) { throw "Incomplete TRIM scopes: $profile" }
    $foreground = Join-Path $ArtifactRoot "automatic/$profile/foreground.json"
    $delete = if (Test-Path -LiteralPath $foreground) {
        (Get-Content -LiteralPath $foreground -Raw | ConvertFrom-Json).ForegroundDeleteCycles
    } else { $null }
    $methods = @($service | ForEach-Object { $_.InclusiveMethods })
    $recoveries = @($methods | Where-Object { $_.Name -match '::Recover' })
    if ($AssertBounded -and ($recoveries.Count -ne 0 -or
        @($service | Where-Object { $_.ScopeEnd.Discards - $_.ScopeStart.Discards -gt 1 }).Count -ne 0)) {
        throw "Recovery or more than one discard in an automatic service turn: $profile"
    }
    [pscustomobject]@{
        Profile = $profile
        InitializationCycles = if ($cold.Count -eq 0) { $null } else { Sum-Property $cold 'GuestCycles' }
        ForegroundDeleteCycles = $delete
        ObserverCalls = $tracking.Count
        ObserverCycles = Sum-Property $tracking 'GuestCycles'
        ServiceTurns = $service.Count
        ServiceCycles = Sum-Property $service 'GuestCycles'
        Reads = Sum-Delta $service 'Reads'
        Flushes = Sum-Delta $service 'Flushes'
        ScsiCommands = Sum-Delta $service 'Scsi'
        Discards = Sum-Delta $service 'Discards'
        ReclaimedBytes = Sum-Delta $service 'DiscardBytes'
        RecoveryCalls = Sum-Property $recoveries 'Calls'
    }
})
$rows | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'trim-summary.json')
$rows | Format-Table Profile, InitializationCycles, ForegroundDeleteCycles, ObserverCalls, ServiceCycles, Reads, Discards, ReclaimedBytes, RecoveryCalls -AutoSize
