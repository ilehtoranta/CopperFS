param(
    [string]$CopperSharpRoot = '',
    [string]$Configuration = 'Release',
    [string]$ArtifactRoot = '',
    [ValidateSet('68000', '68020')]
    [string]$Cpu = '68000',
    [switch]$StrictWrites,
    [switch]$TrstWrites,
    [switch]$BufferedTrst,
    [switch]$LegacyCsUn,
    [switch]$Ffs,
    [switch]$Cpx4,
    [switch]$FfsOnly,
    [switch]$SmartOnly,
    [switch]$NoSpecialization,
    [switch]$Sfs2,
    [switch]$Sfs2TrstWrites,
    [switch]$SharedDispatch,
    [switch]$AutoTrim,
    [switch]$FfsTrimLayoutControl,
    [ValidateSet(64, 256, 1024)] [int]$FfsIndexKiB = 256,
    [switch]$SkipCompilerBuild,
    [switch]$SkipCilica,
    [switch]$CilicaInlining,
    [switch]$GroupedSfs2Trst,
    [switch]$GroupedSfsTrst,
    [switch]$SharedSfsPool,
    [switch]$SfsInPlaceOverwrite,
    [ValidateSet(0, 16)] [int]$SfsExtraKiB = 0,
    [switch]$OwnedSfs2,
    [ValidateSet(0, 16, 64, 256)] [int]$Sfs2ExtraKiB = 0,
    [switch]$Sfs2CsunWrites
)

$ErrorActionPreference = 'Stop'
if ($FfsTrimLayoutControl -and (!$Ffs -or $NoSpecialization -or $SharedDispatch -or $Cpx4)) { throw 'FfsTrimLayoutControl requires the specialized FFS entry.' }
if ($CilicaInlining -and ($SkipCilica -or $Cpx4)) { throw 'CilicaInlining requires this script''s CILica stage and excludes Cpx4.' }
if ($SfsInPlaceOverwrite -and (!$SharedSfsPool -or $Cpx4)) { throw 'SfsInPlaceOverwrite requires the SFS shared-pool profile.' }
if ($SfsExtraKiB -ne 0 -and (!$SharedSfsPool -or $Cpx4)) { throw 'SfsExtraKiB requires the SFS shared-pool profile.' }
if ($Cpx4) {
    if ($Ffs -or $Sfs2 -or $FfsOnly -or $SmartOnly -or $OwnedSfs2 -or $GroupedSfs2Trst) { throw 'Cpx4 is an isolated handler profile.' }
    & (Join-Path $PSScriptRoot 'Build-Cpx4Handler.ps1') -CopperSharpRoot $CopperSharpRoot -Configuration $Configuration -ArtifactRoot $ArtifactRoot -Cpu $Cpu -SkipCompilerBuild:$SkipCompilerBuild
    return
}
if ($SharedSfsPool) { $GroupedSfsTrst = [switch]::new($true) }
if ($OwnedSfs2) { $GroupedSfs2Trst = [switch]::new($true) }
if ($GroupedSfsTrst -and ($Ffs -or $Sfs2 -or $StrictWrites -or $LegacyCsUn)) { throw 'GroupedSfsTrst requires buffered SFS TRST.' }
if ($Sfs2ExtraKiB -ne 0 -and !$OwnedSfs2) { throw 'Sfs2ExtraKiB requires -OwnedSfs2.' }
if ($SharedDispatch) { $NoSpecialization = $true }
if ($Ffs -and $Sfs2) { throw 'Choose the FFS or SFS2 handler, not both.' }
if ($FfsOnly -and (!$Ffs -or $Sfs2)) { throw 'FfsOnly requires -Ffs and excludes -Sfs2.' }
if ($SmartOnly -and ($Ffs -or $FfsOnly)) { throw 'SmartOnly requires an SFS or SFS2 entry.' }
if ($NoSpecialization -and ($FfsOnly -or $SmartOnly)) { throw 'NoSpecialization cannot be combined with an explicit specialized profile.' }
if ($TrstWrites -and $LegacyCsUn) { throw 'Choose TRST or legacy CSUN, not both.' }
if ($BufferedTrst -and ($LegacyCsUn -or $Sfs2 -or $StrictWrites)) { throw 'BufferedTrst requires classic standalone TRST without StrictWrites.' }
$useTrst = !$LegacyCsUn
$useBufferedTrst = $useTrst -and !$StrictWrites -and !$Sfs2
$filesystemRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = $filesystemRoot
if ([string]::IsNullOrWhiteSpace($CopperSharpRoot)) {
    $CopperSharpRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '../CopperSharp68k'))
}
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) { $ArtifactRoot = Join-Path $filesystemRoot 'artifacts/native-sfs-handler' }
$inputRoot = Join-Path $ArtifactRoot 'inputs'
if (($Sfs2TrstWrites -or $Sfs2CsunWrites) -and !$Sfs2) { throw 'SFS2 write-policy switches require -Sfs2.' }
if ($Sfs2TrstWrites -and $Sfs2CsunWrites) { throw 'Choose one SFS2 write policy.' }
if ($GroupedSfs2Trst -and (!$Sfs2 -or $Sfs2CsunWrites -or $StrictWrites)) { throw 'GroupedSfs2Trst requires SFS2 TRST without StrictWrites.' }
$useSfs2Trst = $Sfs2 -and !$Sfs2CsunWrites -and ($Sfs2TrstWrites -or !$PSBoundParameters.ContainsKey('Sfs2TrstWrites'))
# Only the qualified default policies opt in automatically. Other write-policy
# combinations retain their existing combined handler until qualified.
$useFfsOnly = $FfsOnly -or ($Ffs -and !$Sfs2 -and !$NoSpecialization -and !$StrictWrites -and !$LegacyCsUn)
$useSmartOnly = $SmartOnly -or (!$Ffs -and !$NoSpecialization -and !$Sfs2CsunWrites -and !$StrictWrites -and !$LegacyCsUn)
New-Item -ItemType Directory -Force -Path $inputRoot | Out-Null
$cliProject = Join-Path $CopperSharpRoot 'Compiler.Cli/CopperSharp.Compiler.Cli.csproj'
$dosProject = Join-Path $repositoryRoot 'src/CopperFS.Amiga/CopperFS.Amiga.csproj'
if (!$SkipCompilerBuild) {
    dotnet build $cliProject -c $Configuration --verbosity quiet -m:1
    if ($LASTEXITCODE -ne 0) { throw 'CopperSharp compiler build failed.' }
}
dotnet build $dosProject -t:Rebuild -c $Configuration "-p:CopperSharp68kRoot=$CopperSharpRoot" "-p:CopperFsFfsTrimLayoutControl=$($FfsTrimLayoutControl.IsPresent.ToString().ToLowerInvariant())" "-p:CopperFsFfsIndexKiB=$FfsIndexKiB" "-p:CopperFsAutoTrim=$($AutoTrim.IsPresent.ToString().ToLowerInvariant())" '-p:CopperStartSfsStandalone=true' '-p:CopperStartNativeSfsWrites=true' "-p:CopperFsNativeFfsReadAhead=$($Ffs.IsPresent.ToString().ToLowerInvariant())" "-p:CopperFsFfsOnly=$($useFfsOnly.ToString().ToLowerInvariant())" "-p:CopperFsSmartOnly=$($useSmartOnly.ToString().ToLowerInvariant())" "-p:CopperStartSfsStrictWrites=$($StrictWrites.IsPresent.ToString().ToLowerInvariant())" "-p:CopperStartSfsTrstWrites=$($useTrst.ToString().ToLowerInvariant())" "-p:CopperStartSfsTrstBuffered=$($useBufferedTrst.ToString().ToLowerInvariant())" "-p:CopperFsSfsInPlace=$($SfsInPlaceOverwrite.IsPresent.ToString().ToLowerInvariant())" "-p:CopperFsSfsExtraKiB=$SfsExtraKiB" "-p:CopperFsSfsPool=$($SharedSfsPool.IsPresent.ToString().ToLowerInvariant())" "-p:CopperFsSfsGrouped=$($GroupedSfsTrst.IsPresent.ToString().ToLowerInvariant())" "-p:CopperStartNativeSfs2=$($Sfs2.IsPresent.ToString().ToLowerInvariant())" "-p:CopperFsSfs2Owned=$($OwnedSfs2.IsPresent.ToString().ToLowerInvariant())" "-p:CopperFsSfs2ExtraKiB=$Sfs2ExtraKiB" "-p:CopperStartSfs2TrstGrouped=$($GroupedSfs2Trst.IsPresent.ToString().ToLowerInvariant())" "-p:CopperStartSfs2TrstWrites=$($useSfs2Trst.ToString().ToLowerInvariant())" --artifacts-path (Join-Path $ArtifactRoot 'managed-build') --output $inputRoot --verbosity quiet -m:1
if ($LASTEXITCODE -ne 0) { throw 'Standalone SFS handler input build failed.' }
$compiler = Join-Path $CopperSharpRoot "Compiler.Cli/bin/$Configuration/net10.0/CopperSharp.Compiler.Cli.dll"
$dos = Join-Path $inputRoot 'CopperFS.Amiga.dll'
if (!$SkipCilica) {
    # This build drives copper68kc directly rather than importing CopperSharp.Sdk.
    # Run the SDK's restricted CILica stage explicitly, retaining original inputs
    # and the optimizer's hash/provenance report. Do not silently fall back.
    $optimizedDos = Join-Path $ArtifactRoot 'cilica/CopperFS.Amiga.dll'
    $activeSdkVersion = (& dotnet --version).Trim()
    & (Join-Path $CopperSharpRoot 'scripts/optimize-il.ps1') -InputAssembly $dos -OutputAssembly $optimizedDos -ResolverPaths $inputRoot -SdkVersion $activeSdkVersion
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath "$optimizedDos.cilica.json")) { throw 'Standalone handler CILica optimization failed.' }
    # Compare inlining independently of scalar replacement. Both modes consume
    # the original input and retain the optimizer's provenance report.
    $optimizer = Join-Path $CopperSharpRoot '.artifacts/CilicaOptimizer/build/bin/CilicaOptimizer/release/CilicaOptimizer.dll'
    $inliningMode = if ($CilicaInlining) { 'on' } else { 'off' }
    dotnet $optimizer $dos $optimizedDos --resolver $inputRoot --inlining $inliningMode
    if ($LASTEXITCODE -ne 0) { throw 'Standalone CILica optimization failed.' }
    $report = Get-Content -LiteralPath "$optimizedDos.cilica.json" -Raw | ConvertFrom-Json
    if ($report.EnableInlining -ne [bool]$CilicaInlining -or $report.EnableScalarReplacement -ne $true) { throw 'Unexpected CILica optimization profile.' }
    $dos = $optimizedDos
}
$output = Join-Path $ArtifactRoot $(if ($Ffs) { 'CopperFFS' } else { 'CopperSFS' })
$entry = if ($Ffs) { 'CopperStart.Dos.NativeSfsStandaloneHandler::FfsEntry' } else { 'CopperStart.Dos.NativeSfsStandaloneHandler::Entry' }
# The handler is a freestanding HUNK. ROM-only policy does not enable these
# passes for this output. Keep the qualified selection explicit: optimize
# transport and memory accesses without adding shared-return branches.
$codeSizePasses = 'ElideUnusedRegisterArguments,ReuseIncomingArgumentHomes,ClusterInternalCalls,InlineSingleUseMethods,ForwardReadOnlyAggregateLocals,RemoveRedundantTransport,CompactPrivateStackCopies,CompactGuestMemory,NarrowOperations,EliminateRedundantInitialization,InlineMemoryHelpers,SimplifyLoopPhis'
# The FFS-only call clustering layout measurably slows the qualified read
# workloads. Keep every other native pass fixed when comparing CILica profiles.
if ($useFfsOnly) { $codeSizePasses = $codeSizePasses.Replace('ClusterInternalCalls,', '') }
$arguments = @($compiler, $dos, '--entry', $entry,
    '--platform', 'amiga', '--cpu', $Cpu, '--clr', 'always', '--peephole', 'fixed-point',
    '--fpu', 'disabled', '--exceptions', 'yolo', '--code-size-optimizations', 'on',
    '--code-size-passes', $codeSizePasses,
    '--format', 'hunk', '--runtime', 'freestanding', '--memory', 'none', '--exports', 'none',
    '--symbols', 'off', '--output', $output,
    '--compatibility-report', (Join-Path $ArtifactRoot 'CopperSFS.compatibility.json'))
foreach ($assembly in @('CopperSFS.Core.dll', 'CopperSharp.Sdk.Amiga.dll',
    'CopperSharp.Sdk.Amiga.Support.dll', 'CopperSharp.Compiler.dll')) {
    $file = Join-Path $inputRoot $assembly
    if (!(Test-Path -LiteralPath $file)) { throw "Missing standalone handler input $file" }
    $arguments += @('--managed-assembly', $file)
}
dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Standalone SFS handler compilation failed.' }
$report = Get-Content -LiteralPath (Join-Path $ArtifactRoot 'CopperSFS.compatibility.json') -Raw | ConvertFrom-Json
if ($report.IsCompatible -ne $true -or !$report.PSObject.Properties['ManagedAllocationSites'] -or
    $null -eq $report.ManagedAllocationSites -or @($report.ManagedAllocationSites).Count -ne 0) {
    throw 'Standalone handler must be compatible and contain no reachable managed allocation sites.'
}
$map = "$output.map"
$requiredMethods = @('NativeSfsExtension::Entry', 'AmigaSmartFileSystemWriteCore::Write', 'AmigaSmartFileSystemWriteCore::Recover')
if ($Sfs2) {
    $requiredMethods += @('AmigaSmartFileSystemReadOnlyCore::Read', 'AmigaSmartFileSystem2WriteCore::Recover')
    if ($useSfs2Trst) { $requiredMethods += 'AmigaSmartFileSystem2WriteCore::ExecuteTrst' }
    else { $requiredMethods += 'AmigaSmartFileSystem2WriteCore::WriteBatched' }
}
if ($useTrst) { $requiredMethods += 'AmigaSmartFileSystemWriteCore::ExecuteTrst' }
else { $requiredMethods += @('AmigaSmartFileSystemWriteCore::WriteBatched', 'AmigaSmartFileSystemWriteCore::RelocateOverwrite') }
if ($Ffs) { $requiredMethods = @('AmigaFfsWriteCore::Trim', 'AmigaFfsWriteCore::Write') }
foreach ($method in $requiredMethods) {
    if (!(Select-String -LiteralPath $map -SimpleMatch $method -Quiet)) {
        throw "Standalone SFS handler is missing $method; check the standalone build profile."
    }
}
Write-Output "Built $output; install the selected handler in L: on an AmigaOS boot volume."
