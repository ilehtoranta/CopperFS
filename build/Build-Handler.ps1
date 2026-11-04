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
    [switch]$Sfs2,
    [switch]$Sfs2TrstWrites,
    [switch]$SkipCompilerBuild
)

$ErrorActionPreference = 'Stop'
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
if ($Sfs2TrstWrites -and !$Sfs2) { throw 'Sfs2TrstWrites requires the Sfs2 integration profile.' }
New-Item -ItemType Directory -Force -Path $inputRoot | Out-Null
$cliProject = Join-Path $CopperSharpRoot 'Compiler.Cli/CopperSharp.Compiler.Cli.csproj'
$dosProject = Join-Path $repositoryRoot 'src/CopperFS.Amiga/CopperFS.Amiga.csproj'
if (!$SkipCompilerBuild) {
    dotnet build $cliProject -c $Configuration --verbosity quiet -m:1
    if ($LASTEXITCODE -ne 0) { throw 'CopperSharp compiler build failed.' }
}
dotnet build $dosProject -t:Rebuild -c $Configuration "-p:CopperSharp68kRoot=$CopperSharpRoot" '-p:CopperStartSfsStandalone=true' '-p:CopperStartNativeSfsWrites=true' "-p:CopperStartSfsStrictWrites=$($StrictWrites.IsPresent.ToString().ToLowerInvariant())" "-p:CopperStartSfsTrstWrites=$($useTrst.ToString().ToLowerInvariant())" "-p:CopperStartSfsTrstBuffered=$($useBufferedTrst.ToString().ToLowerInvariant())" "-p:CopperStartNativeSfs2=$($Sfs2.IsPresent.ToString().ToLowerInvariant())" "-p:CopperStartSfs2TrstWrites=$($Sfs2TrstWrites.IsPresent.ToString().ToLowerInvariant())" --output $inputRoot --verbosity quiet -m:1
if ($LASTEXITCODE -ne 0) { throw 'Standalone SFS handler input build failed.' }
$compiler = Join-Path $CopperSharpRoot "Compiler.Cli/bin/$Configuration/net10.0/CopperSharp.Compiler.Cli.dll"
$dos = Join-Path $inputRoot 'CopperFS.Amiga.dll'
$output = Join-Path $ArtifactRoot $(if ($Ffs) { 'CopperFFS' } else { 'CopperSFS' })
$entry = if ($Ffs) { 'CopperStart.Dos.NativeSfsStandaloneHandler::FfsEntry' } else { 'CopperStart.Dos.NativeSfsStandaloneHandler::Entry' }
# The handler is a freestanding HUNK. ROM-only policy does not enable these
# passes for this output. Keep the qualified selection explicit: optimize
# transport and memory accesses without adding shared-return branches.
$codeSizePasses = 'ElideUnusedRegisterArguments,ReuseIncomingArgumentHomes,ClusterInternalCalls,InlineSingleUseMethods,ForwardReadOnlyAggregateLocals,RemoveRedundantTransport,CompactPrivateStackCopies,CompactGuestMemory,NarrowOperations,EliminateRedundantInitialization,InlineMemoryHelpers,SimplifyLoopPhis'
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
$map = "$output.map"
$requiredMethods = @('NativeSfsExtension::Entry', 'AmigaSmartFileSystemWriteCore::Write', 'AmigaSmartFileSystemWriteCore::Recover')
if ($Sfs2) {
    $requiredMethods += @('AmigaSmartFileSystemReadOnlyCore::Read', 'AmigaSmartFileSystem2WriteCore::Recover')
    if ($Sfs2TrstWrites) { $requiredMethods += 'AmigaSmartFileSystem2WriteCore::ExecuteTrst' }
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
