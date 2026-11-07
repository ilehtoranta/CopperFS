param(
    [Parameter(Mandatory)][string]$OriginalFfsHandler,
    [Parameter(Mandatory)][string]$OriginalSfsHandler,
    [Parameter(Mandatory)][string]$ClassicSfsImage,
    [string]$KickstartRom = $env:COPPERSHARP_KICKSTART31_ROM,
    [string]$OutputRoot = '',
    [string]$ReferenceProvenance = '',
    [string]$FrozenHandlerRoot = ''
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputRoot) { $OutputRoot = Join-Path $repository ('artifacts/correctness-ab-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath (Join-Path $OutputRoot 'manifest.json')) { throw 'Choose a fresh output directory.' }
foreach ($inputPath in @($OriginalFfsHandler, $OriginalSfsHandler, $ClassicSfsImage, $KickstartRom)) {
    if (!(Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Missing input: $inputPath" }
}
$OriginalFfsHandler = (Resolve-Path -LiteralPath $OriginalFfsHandler).Path
$OriginalSfsHandler = (Resolve-Path -LiteralPath $OriginalSfsHandler).Path
$ClassicSfsImage = (Resolve-Path -LiteralPath $ClassicSfsImage).Path
$KickstartRom = (Resolve-Path -LiteralPath $KickstartRom).Path
$ffsHash = '9992D843E3936EF81F92311404BCA557E3727DA3A10F39B586B94B02921B3773'
$sfsHash = 'C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205'
if ((Get-FileHash -LiteralPath $OriginalFfsHandler).Hash -ne $ffsHash) { throw 'Expected original FFS 40.1.' }
if ((Get-FileHash -LiteralPath $OriginalSfsHandler).Hash -ne $sfsHash) { throw 'Expected original SmartFilesystem 1.279.' }
if ((Get-Item -LiteralPath $ClassicSfsImage).Length -ne 16777216) { throw 'Expected a clean 16 MiB SFS v3 partition image.' }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$buildSource = [ordered]@{}
Push-Location $repository
try {
    foreach ($path in (& rg --files src build -g '*.cs' -g '*.props' -g '*.csproj' -g '*.ps1')) {
        $buildSource[$path] = (Get-FileHash -LiteralPath $path).Hash
    }
    if (!$FrozenHandlerRoot) {
        $FrozenHandlerRoot = Join-Path $OutputRoot 'handlers'
        foreach ($profile in @('ffs', 'sfs', 'sfs2-csun', 'sfs2-trst')) {
            $options = @{ ArtifactRoot = (Join-Path $FrozenHandlerRoot $profile) }
            if ($profile -eq 'ffs') { $options.Ffs = $true }
            if ($profile.StartsWith('sfs2')) { $options.Sfs2 = $true }
            if ($profile -eq 'sfs2-trst') { $options.Sfs2TrstWrites = $true }
            & (Join-Path $repository 'build/Build-Handler.ps1') @options *> (Join-Path $OutputRoot "build-$profile.log")
            if ($LASTEXITCODE -ne 0) { throw "Handler build failed: $profile" }
        }
    }
    $FrozenHandlerRoot = (Resolve-Path -LiteralPath $FrozenHandlerRoot).Path
    $profiles = foreach ($profile in @('ofs', 'ffs', 'ofs-dd', 'ofs-hd', 'ffs-dd', 'ffs-hd', 'sfs', 'sfs2-csun', 'sfs2-trst')) {
        $dos = !$profile.StartsWith('sfs')
        $candidate = if ($dos) { Join-Path $FrozenHandlerRoot 'ffs/CopperFFS' } else { Join-Path $FrozenHandlerRoot "$profile/CopperSFS" }
        if (!(Test-Path -LiteralPath $candidate)) { throw "Missing frozen candidate: $candidate" }
        [ordered]@{ Name = $profile; Original = $(if ($dos) { $OriginalFfsHandler } else { $OriginalSfsHandler });
            Candidate = $candidate; OriginalSha256 = $(if ($dos) { $ffsHash } else { $sfsHash }) }
    }
    $manifest = [ordered]@{ Profiles = @($profiles); ClassicBase = $ClassicSfsImage;
        ClassicBaseSha256 = (Get-FileHash -LiteralPath $ClassicSfsImage).Hash }
    $manifestPath = Join-Path $OutputRoot 'manifest.json'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath
    $receipt = [ordered]@{
        KickstartSha256 = (Get-FileHash -LiteralPath $KickstartRom).Hash
        OriginalFfsVersion = '40.1 (15.2.93)'; OriginalSfsVersion = '1.279 (23.12.2007)'
        CandidateHashes = @($profiles | ForEach-Object { @{ Name = $_.Name; Sha256 = (Get-FileHash -LiteralPath $_.Candidate).Hash } })
        SourceAtInvocation = $buildSource
        FrozenInputsSupplied = [bool]$PSBoundParameters.ContainsKey('FrozenHandlerRoot')
        Scope = 'Real Kickstart Exec/DOS; native MC68000 HUNKs on emulated 68020; synchronous CopperHDF sectors. DD/HD geometry is partition geometry, not floppy DMA.'
    }
    if ($ReferenceProvenance) { $receipt.ReferenceProvenance = Get-Content -LiteralPath $ReferenceProvenance -Raw | ConvertFrom-Json }
    $receipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputRoot 'provenance.json')
    $oldManifest = $env:COPPERFS_CORRECTNESS_MANIFEST; $oldRom = $env:COPPERSHARP_KICKSTART31_ROM
    try {
        $env:COPPERFS_CORRECTNESS_MANIFEST = $manifestPath; $env:COPPERSHARP_KICKSTART31_ROM = $KickstartRom
        dotnet test (Join-Path $repository 'tests/CopperFS.Kickstart.Tests') -c Release --artifacts-path (Join-Path $OutputRoot 'managed') `
            --filter 'FullyQualifiedName~OriginalHandlerCorrectnessTests' --logger 'trx;LogFileName=correctness.trx' `
            --results-directory (Join-Path $OutputRoot 'results') *> (Join-Path $OutputRoot 'run.log')
        if ($LASTEXITCODE -ne 0) { throw "Original-handler comparison failed; see $OutputRoot/run.log" }
    }
    finally { $env:COPPERFS_CORRECTNESS_MANIFEST = $oldManifest; $env:COPPERSHARP_KICKSTART31_ROM = $oldRom }
    Write-Output "Original-handler comparisons passed: $OutputRoot"
}
finally { Pop-Location }
