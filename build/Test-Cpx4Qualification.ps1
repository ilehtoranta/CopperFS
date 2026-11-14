[CmdletBinding()]
param(
    [ValidateSet('fast','integration','campaign')][string]$Tier = 'fast',
    [Nullable[uint32]]$Seed,
    [string]$Replay,
    [string]$ArtifactDirectory,
    [switch]$RequireRom
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$ArtifactDirectory) { $ArtifactDirectory = Join-Path $repo "artifacts/cpx4-qualification/$Tier" }
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
$ignoredRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (!$ArtifactDirectory.StartsWith($ignoredRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Qualification artifacts must stay under the repository artifacts directory.' }
if ($Replay) { $Replay = (Resolve-Path -LiteralPath $Replay).Path }
if ($PSBoundParameters.ContainsKey('Seed') -and $Seed -eq 0) { throw 'Seed zero is not supported by the specified PRNG.' }
$romRequired = $RequireRom -or $Tier -eq 'campaign'
if ($romRequired -and (!$env:COPPERSHARP_KICKSTART31_ROM -or !(Test-Path -LiteralPath $env:COPPERSHARP_KICKSTART31_ROM -PathType Leaf))) {
    throw 'Full-ROM qualification requires an available licensed Kickstart 3.1 ROM through COPPERSHARP_KICKSTART31_ROM.'
}
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$variables = @('COPPERCPX_TIER','COPPERCPX_SEED','COPPERCPX_REPLAY','COPPERCPX_ARTIFACTS','COPPERCPX_BOOT','COPPERCPX_ASYNC_DIAGNOSTIC')
$saved = @{}; foreach ($name in $variables) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$receipt = [ordered]@{ schema = 1; tier = $Tier; seed = $Seed; replay = $Replay; startedUtc = [DateTime]::UtcNow.ToString('O'); status = 'running'; host = 'not run'; compiledNative = 'not run'; fullRom = 'not requested'; physicalMedia = 'not tested'; commands = @() }
function Invoke-Checked([string]$Label, [string]$Executable, [string[]]$Arguments) {
    $log = Join-Path $ArtifactDirectory "$Label.log"
    & $Executable @Arguments 2>&1 | Tee-Object -FilePath $log | Out-Host
    $code = $LASTEXITCODE
    $receipt.commands += @{ label = $Label; executable = $Executable; arguments = $Arguments; exitCode = $code }
    if ($code -ne 0) { throw "$Label failed with exit code $code. See $log" }
}
function Source-Hashes {
    $roots = @('src/CopperCPX.Core','src/CopperFS.Amiga/Cpx4','tests/CopperCPX.Core.Tests','tests/CopperCPX.InstructionTests','prototypes/CopperFS.Cpx4.Reference')
    $paths = @(foreach ($root in $roots) { Get-ChildItem -LiteralPath (Join-Path $repo $root) -Recurse -File | Where-Object { $_.Extension -in @('.cs','.csproj','.json') -and $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } })
    $paths += Get-Item -LiteralPath (Join-Path $repo 'build/Test-Cpx4Qualification.ps1'), (Join-Path $repo 'build/Build-Cpx4Handler.ps1'), (Join-Path $repo 'build/Build-Handler.ps1'), (Join-Path $repo 'prototypes/CopperFS.Prototype/WideMath32.cs'), (Join-Path $repo 'src/CopperFS.Amiga/CopperFS.Amiga.csproj'), (Join-Path $repo 'Directory.Build.props')
    @(foreach ($file in $paths | Sort-Object FullName) { [ordered]@{ path = [IO.Path]::GetRelativePath($repo,$file.FullName); sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash } })
}
Push-Location $repo
try {
    $env:COPPERCPX_TIER = $Tier; $env:COPPERCPX_ARTIFACTS = $ArtifactDirectory
    $env:COPPERCPX_SEED = if ($PSBoundParameters.ContainsKey('Seed')) { [string]$Seed } else { $null }
    $env:COPPERCPX_REPLAY = $Replay; $env:COPPERCPX_BOOT = $null
    # A focused diagnostic boot is never the requested full-ROM gate.
    $env:COPPERCPX_ASYNC_DIAGNOSTIC = $null
    $before = Source-Hashes; $receipt.sources = $before
    $buildArtifacts = Join-Path $ArtifactDirectory 'build'
    $results = Join-Path $ArtifactDirectory 'results'
    if ($Tier -eq 'fast') {
        $filter = 'FullyQualifiedName~QualificationTests|FullyQualifiedName~OwnerTests|FullyQualifiedName~VolumeLabelTests|FullyQualifiedName~CrashModelTests|FullyQualifiedName~PacketContractTests|FullyQualifiedName~LifecyclePacketTests|FullyQualifiedName~RecordLockPacketTests|FullyQualifiedName~RecordLockModelTests|FullyQualifiedName~RecordWaitTests|FullyQualifiedName~NotificationLifecycleTests|FullyQualifiedName~AsyncSchedulingTests|FullyQualifiedName~LinkPacketTests|FullyQualifiedName~InformationPacketTests|FullyQualifiedName~ExAllPacketTests|FullyQualifiedName~InhibitLifecycleTests|FullyQualifiedName~StorageStopRecoveryTests|FullyQualifiedName~ExAllSchedulingTests|FullyQualifiedName~CorruptionMatrixTests|FullyQualifiedName~GeometryMatrixTests|FullyQualifiedName~Td64Tests|FullyQualifiedName~PoolTests|FullyQualifiedName~SchedulingTests|FullyQualifiedName~TreeCascadeTests|FullyQualifiedName~CommentInputIsOwned|FullyQualifiedName~SparseWritesClear|FullyQualifiedName~EmptyAndNonemptyInline|FullyQualifiedName~ResumableFormatterMatches|FullyQualifiedName~SoftLinksOwn|FullyQualifiedName~OverlongEncodedTarget|FullyQualifiedName~HardLinksShare|FullyQualifiedName~ExtentWriteTests|FullyQualifiedName~NamespaceTests|FullyQualifiedName~ManualTrimPacketTests|FullyQualifiedName~TrimTests|FullyQualifiedName~FortyThirdTargetRequiresAbort|FullyQualifiedName~CorruptLastCommittedImageIsRejected'
        Invoke-Checked 'host-fast' 'dotnet' @('test','tests/CopperCPX.Core.Tests/CopperCPX.Core.Tests.csproj','-c','Release','--artifacts-path',$buildArtifacts,'--filter',$filter,'--logger','trx','--results-directory',$results)
        $receipt.host = 'passed'
    } else {
        Invoke-Checked 'solution-build' 'dotnet' @('build','CopperFS.slnx','-c','Release','--artifacts-path',$buildArtifacts)
        Invoke-Checked 'standalone-handler' 'pwsh' @('-NoProfile','-File','build/Build-Handler.ps1','-Cpx4')
        Invoke-Checked 'solution-tests' 'dotnet' @('test','CopperFS.slnx','-c','Release','--no-build','--artifacts-path',$buildArtifacts,'--logger','trx','--results-directory',$results)
        $receipt.host = 'passed'; $receipt.compiledNative = 'passed'
        $replayProject = '../CopperStart/tests/CopperStart.Floppy.Native.Tests/CopperStart.Floppy.Native.Tests.csproj'
        if (!(Test-Path -LiteralPath $replayProject)) { throw 'CopperStart native replay dependency is unavailable; integration cannot pass.' }
        Invoke-Checked 'copperstart-replay' 'dotnet' @('test',$replayProject,'-c','Release','--artifacts-path',(Join-Path $ArtifactDirectory 'copperstart-build'),'--filter','FullyQualifiedName~FileSystemGeometryInstructionTests|FullyQualifiedName~NativeFileSystemBufferInstructionTests','--logger','trx','--results-directory',(Join-Path $ArtifactDirectory 'copperstart-results'))
    }
    if ($romRequired) {
        if ($Tier -eq 'fast') { Invoke-Checked 'standalone-handler' 'pwsh' @('-NoProfile','-File','build/Build-Handler.ps1','-Cpx4') }
        # The solution maps some external dependencies to Debug even under its
        # Release configuration. Build this project directly in an isolated tree
        # so ROM qualification always uses the explicitly requested Release harness.
        $env:COPPERCPX_BOOT = '1'
        Invoke-Checked 'native-rom' 'dotnet' @('test','tests/CopperCPX.InstructionTests/CopperCPX.InstructionTests.csproj','-c','Release','--artifacts-path',(Join-Path $ArtifactDirectory 'native-release'),'--logger','trx','--results-directory',(Join-Path $ArtifactDirectory 'native-results'))
        $env:COPPERCPX_BOOT = $null
        $receipt.compiledNative = 'passed'; $receipt.fullRom = 'passed'
    }
    $after = Source-Hashes
    if (($before | ConvertTo-Json -Depth 5 -Compress) -ne ($after | ConvertTo-Json -Depth 5 -Compress)) { throw 'Qualification sources changed while the campaign ran; receipts cannot qualify the final source.' }
    $hunk = Join-Path $repo 'artifacts/native-cpx4-handler/CopperCPX4'
    if (Test-Path -LiteralPath $hunk) { $receipt.hunkSha256 = (Get-FileHash -LiteralPath $hunk -Algorithm SHA256).Hash }
    $receipt.status = 'passed'
} catch {
    $receipt.status = 'failed'; $receipt.failure = $_.Exception.Message; throw
} finally {
    $receipt.finishedUtc = [DateTime]::UtcNow.ToString('O')
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'receipt.json') -Encoding utf8
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name,$saved[$name]) }
    Pop-Location
}
