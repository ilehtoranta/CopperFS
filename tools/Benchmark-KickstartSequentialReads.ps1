param(
    [Parameter(Mandatory = $true)][string]$NativeArtifactRoot,
    [Parameter(Mandatory = $true)][string]$ArtifactRoot,
    [string]$KickstartRom = $env:COPPERSHARP_KICKSTART31_ROM,
    [string]$OriginalFfsHandler = '',
    [string]$OriginalSfs2Handler = '',
    [string]$ReferenceProvenance = '',
    [ValidateSet('Cached', 'Bypass', 'Both')][string]$PayloadCachePolicy = 'Cached',
    [switch]$Matrix,
    [ValidateSet('All','ZeroLatency','TimedCopperHdf','A1200Ide')][string]$StorageBackend='ZeroLatency',
    [ValidateSet('All','Legacy','A1200Stock','A1200Fast')][string]$MachineProfile='Legacy',
    [ValidateSet('Both','Hdd','Cf')][string]$DriveProfile='Both',
    [string]$A1200Rom=$env:COPPERSCREEN_UPGRADE_A1200_ROM,
    [ValidateSet('3.0','3.1','Both')][string]$A1200Kickstart='3.0',
    [string]$A1200Rom31=$env:COPPERSCREEN_UPGRADE_A1200_ROM31,
    [ValidateSet('All','Ffs','Sfs2','Sfs2CSUN','Sfs2TRST')][string]$FileSystemProfile='All',
    [string]$TimingProfiles='',
    [switch]$NoBuild,
    [string]$TestAssembly = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if($Matrix -or $StorageBackend -ne 'ZeroLatency' -or $MachineProfile -ne 'Legacy' -or $PayloadCachePolicy -eq 'Both' -or $A1200Kickstart -ne '3.0' -or $FileSystemProfile -ne 'All') {
    $matrixArguments=@{
        NativeArtifactRoot=$NativeArtifactRoot;ArtifactRoot=$ArtifactRoot;OriginalFfsHandler=$OriginalFfsHandler
        OriginalSfs2Handler=$OriginalSfs2Handler;A1200Rom=$A1200Rom;LegacyRom=$KickstartRom
        Backend=$(if($Matrix -and !$PSBoundParameters.ContainsKey('StorageBackend')){'All'}else{$StorageBackend})
        MachineProfile=$(if(($Matrix -or $A1200Kickstart -ne '3.0') -and !$PSBoundParameters.ContainsKey('MachineProfile')){'All'}else{$MachineProfile})
        DriveProfile=$DriveProfile;PayloadCachePolicy=$(if($Matrix -and !$PSBoundParameters.ContainsKey('PayloadCachePolicy')){'Both'}else{$PayloadCachePolicy})
        TimingProfiles=$TimingProfiles;TestAssembly=$TestAssembly;ReferenceProvenance=$ReferenceProvenance
        A1200Kickstart=$A1200Kickstart;A1200Rom31=$A1200Rom31;FileSystemProfile=$FileSystemProfile
    }
    & (Join-Path $PSScriptRoot 'Benchmark-KickstartStorageMatrix.ps1') @matrixArguments
    return
}
$nativeManifest = (Resolve-Path (Join-Path $NativeArtifactRoot 'benchmark.json')).Path
$romPath = (Resolve-Path $KickstartRom).Path
if ($TestAssembly) { $TestAssembly = (Resolve-Path -LiteralPath $TestAssembly).Path }
$destination = [IO.Path]::GetFullPath($ArtifactRoot)
if (Test-Path $destination) { throw "Use a new artifact directory: $destination" }
$native = Get-Content $nativeManifest -Raw | ConvertFrom-Json
if ([bool]$OriginalFfsHandler -ne [bool]$OriginalSfs2Handler) {
    throw 'Supply both original handlers for an original-versus-Copper comparison.'
}
$references = @()
if ($OriginalFfsHandler) {
    $ffsReference = (Resolve-Path $OriginalFfsHandler).Path
    $sfs2Reference = (Resolve-Path $OriginalSfs2Handler).Path
    foreach ($pair in $native.Pairs) { $pair.Before = if ($pair.Sfs2) { $sfs2Reference } else { $ffsReference } }
    foreach ($path in @($ffsReference, $sfs2Reference)) {
        $bytes = [IO.File]::ReadAllBytes($path)
        $text = [Text.Encoding]::Latin1.GetString($bytes)
        $version = [regex]::Match($text, '\$VER: [^\x00\r\n]+').Value
        if (!$version) { $version = [regex]::Match($text, 'SmartFilesystem [0-9.]+ \([^)]*\)').Value }
        $references += @{ Path = $path; Bytes = $bytes.Length; Sha256 = (Get-FileHash $path).Hash; Version = $version }
    }
}
if ($ReferenceProvenance) { $ReferenceProvenance = (Resolve-Path $ReferenceProvenance).Path }
foreach ($pair in $native.Pairs) {
    $pair | Add-Member -NotePropertyName BeforeControl -NotePropertyValue 0 -Force
    $pair | Add-Member -NotePropertyName AfterControl -NotePropertyValue $(if ($PayloadCachePolicy -eq 'Bypass') { 0x43504301 } else { 0 }) -Force
    foreach ($variant in @('Before', 'After')) {
        if (!(Test-Path $pair.$variant)) { throw "Missing frozen handler: $($pair.$variant)" }
    }
}
New-Item $destination -ItemType Directory | Out-Null
if ($ReferenceProvenance) { Copy-Item -LiteralPath $ReferenceProvenance -Destination (Join-Path $destination 'reference-provenance.json') }
$manifest = [ordered]@{
    Comparison = $(if ($OriginalFfsHandler) { 'original-handlers' } else { 'copper-before-after' })
    ReferenceHandlers = $references
    NativeManifest = $nativeManifest
    BaselineCommit = $native.BaselineCommit
    Pairs = $native.Pairs
    Trials = 3
    Workloads = @(
        @{ PacketBytes = 64; ReadBytes = 65536 },
        @{ PacketBytes = 512; ReadBytes = 262144 },
        @{ PacketBytes = 4096; ReadBytes = 1048576 },
        @{ PacketBytes = 65536; ReadBytes = 1048576 }
    )
    Kickstart = @{
        Cpu = 'M68020'; BusHz = 7093790; ChipRamBytes = 524288; FastRamBytes = 2097152
        VolumeBytes = 16777216; FileBytes = 1048576; BlockBytes = 512
        MaxTransfer = 65536; ReadAheadBytes = 65536
        CacheReset = 'Inhibit/remount before each open'; Backend = 'CopperHDF synchronous read-only'
        PayloadCachePolicy = $PayloadCachePolicy
        RomSha256 = (Get-FileHash $romPath -Algorithm SHA256).Hash
    }
}
$manifestPath = Join-Path $destination 'benchmark.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content $manifestPath -Encoding utf8
$savedRom = $env:COPPERSHARP_KICKSTART31_ROM
$savedManifest = $env:COPPERFS_KICKSTART_READ_MANIFEST
$savedSmoke = $env:COPPERFS_KICKSTART_SMOKE
$savedProfile = $env:COPPERFS_KICKSTART_PROFILE
Push-Location $repo
try {
    $env:COPPERSHARP_KICKSTART31_ROM = $romPath
    $env:COPPERFS_KICKSTART_READ_MANIFEST = $manifestPath
    $env:COPPERFS_KICKSTART_SMOKE = $null
    $env:COPPERFS_KICKSTART_PROFILE = $null
    $buildArguments = @()
    $testTarget = 'tests/CopperFS.Kickstart.Tests/CopperFS.Kickstart.Tests.csproj'
    $testBin = 'tests/CopperFS.Kickstart.Tests/bin/Release/net10.0'
    if ($TestAssembly) { $testTarget = $TestAssembly; $testBin = Split-Path $TestAssembly }
    else {
        $buildArguments += @('-c', 'Release')
        if ($NoBuild) { $buildArguments += '--no-build' }
    }
    dotnet test $testTarget @buildArguments `
        --filter 'FullyQualifiedName~CopperFS.Kickstart.Tests.SequentialReadThroughputTests' `
        --logger 'trx;LogFileName=kickstart.trx' --results-directory $destination
    if ($LASTEXITCODE -ne 0) { throw "Kickstart benchmark failed ($LASTEXITCODE)." }
    $assemblies = Get-ChildItem $testBin -Filter 'Copper*.dll' |
        ForEach-Object { @{ Name = $_.Name; Sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } }
    @{ DotnetSdk = (dotnet --version); Assemblies = @($assemblies) } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $destination 'environment.json') -Encoding utf8
    Write-Host "Checked raw trials: $(Join-Path $destination 'kickstart')"
    Write-Host "Summarize with tools/Summarize-KickstartSequentialReads.py '$destination' <report.md>"
}
finally {
    $env:COPPERSHARP_KICKSTART31_ROM = $savedRom
    $env:COPPERFS_KICKSTART_READ_MANIFEST = $savedManifest
    $env:COPPERFS_KICKSTART_SMOKE = $savedSmoke
    $env:COPPERFS_KICKSTART_PROFILE = $savedProfile
    Pop-Location
}
