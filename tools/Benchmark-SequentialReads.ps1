param(
    [string]$BaselineRef = 'HEAD',
    [string]$ArtifactRoot = '',
    [string]$CopperSharpRoot = ''
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$ArtifactRoot) { $ArtifactRoot = Join-Path $repository 'artifacts/sequential-benchmark' }
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
if (!$CopperSharpRoot) { $CopperSharpRoot = [IO.Path]::GetFullPath((Join-Path $repository '../CopperSharp68k')) }
New-Item -ItemType Directory -Force -Path $ArtifactRoot | Out-Null

# Retain all other working-tree changes in both snapshots. Only the reader
# changes are replaced from the requested pre-optimization revision.
$readerPaths = @(
    'src/CopperFFS.Core/AmigaDiskFileSystemCore.cs',
    'src/CopperSFS.Core/AmigaSmartFileSystemReadOnlyCore.cs',
    'src/CopperFS.Amiga/NativeAmigaFileSystem.cs',
    'src/CopperFS.Amiga/NativeSfsExtension.cs',
    'src/CopperFS.Amiga/NativeSfs2Bridge.cs',
    'src/CopperFS.Amiga/NativeSfs2Bridge.Disabled.cs'
)
$newReader = 'src/CopperFS.Amiga/NativeFileSystemReadCore.cs'
$baselineCommit = (git -C $repository rev-parse $BaselineRef).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve baseline revision.' }
$sourceFiles = @(git -C $repository ls-files --cached --others --exclude-standard -- src build Directory.Build.props LICENSE AGENTS.md)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate source inputs.' }
foreach ($variant in @('before', 'after')) {
    $snapshot = Join-Path $ArtifactRoot "$variant-source"
    if (Test-Path -LiteralPath $snapshot) { throw "Use a fresh artifact directory; snapshot already exists: $snapshot" }
    foreach ($relative in $sourceFiles) {
        if ($variant -eq 'before' -and $relative -eq $newReader) { continue }
        $target = Join-Path $snapshot $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        if ($variant -eq 'before' -and $readerPaths -contains $relative) {
            $original = git -C $repository show "${baselineCommit}:$relative"
            if ($LASTEXITCODE -ne 0) { throw "Cannot recover baseline source: $relative" }
            [IO.File]::WriteAllText($target, (($original -join "`n") + "`n"))
        }
        else { Copy-Item -LiteralPath (Join-Path $repository $relative) -Destination $target }
    }
}

$sourceManifest = foreach ($variant in @('before', 'after')) {
    $snapshot = Join-Path $ArtifactRoot "$variant-source"
    foreach ($relative in $sourceFiles) {
        $path = Join-Path $snapshot $relative
        if (Test-Path -LiteralPath $path) {
            [ordered]@{ Variant = $variant; Path = $relative; SHA256 = (Get-FileHash -LiteralPath $path).Hash }
        }
    }
}
$sourceManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'source-manifest.json')

dotnet build (Join-Path $CopperSharpRoot 'Compiler.Cli/CopperSharp.Compiler.Cli.csproj') -c Release --verbosity quiet -m:1
if ($LASTEXITCODE -ne 0) { throw 'Compiler build failed.' }
$frozenCompiler = Join-Path $ArtifactRoot 'compiler'
New-Item -ItemType Directory -Force -Path $frozenCompiler | Out-Null
Get-ChildItem -LiteralPath (Join-Path $CopperSharpRoot 'Compiler.Cli/bin/Release/net10.0') -File |
    Copy-Item -Destination $frozenCompiler

$profiles = @(
    @{ Name = 'ffs'; Sfs2 = $false; Trst = $false; Entry = 'FfsEntry'; File = 'CopperFFS' },
    @{ Name = 'sfs2-csun'; Sfs2 = $true; Trst = $false; Entry = 'Entry'; File = 'CopperSFS' },
    @{ Name = 'sfs2-trst'; Sfs2 = $true; Trst = $true; Entry = 'Entry'; File = 'CopperSFS' }
)
$compiles = @()
$sdkHashes = @{}
$buildRecipe = Get-Content -LiteralPath (Join-Path $repository 'build/Build-Handler.ps1') -Raw
$passes = [regex]::Match($buildRecipe, '\$codeSizePasses = ''([^'']+)''').Groups[1].Value
if (!$passes) { throw 'Cannot read the canonical handler compiler passes.' }
foreach ($variant in @('before', 'after')) {
    foreach ($selection in $profiles) {
        $outputRoot = Join-Path $ArtifactRoot "$variant/$($selection.Name)"
        $inputs = Join-Path $outputRoot 'inputs'
        New-Item -ItemType Directory -Force -Path $inputs | Out-Null
        $project = Join-Path $ArtifactRoot "$variant-source/src/CopperFS.Amiga/CopperFS.Amiga.csproj"
        dotnet build $project -t:Rebuild -c Release "-p:CopperSharp68kRoot=$CopperSharpRoot" `
            '-p:CopperStartSfsStandalone=true' '-p:CopperStartNativeSfsWrites=true' `
            '-p:CopperStartSfsStrictWrites=false' `
            '-p:CopperStartSfsTrstWrites=true' "-p:CopperStartSfsTrstBuffered=$((!$selection.Sfs2).ToString().ToLowerInvariant())" `
            "-p:CopperStartNativeSfs2=$($selection.Sfs2.ToString().ToLowerInvariant())" `
            "-p:CopperStartSfs2TrstWrites=$($selection.Trst.ToString().ToLowerInvariant())" `
            --output $inputs --verbosity quiet -m:1
        if ($LASTEXITCODE -ne 0) { throw "Input build failed: $variant/$($selection.Name)" }
        $arguments = @((Join-Path $frozenCompiler 'CopperSharp.Compiler.Cli.dll'), (Join-Path $inputs 'CopperFS.Amiga.dll'),
            '--entry', "CopperStart.Dos.NativeSfsStandaloneHandler::$($selection.Entry)",
            '--platform', 'amiga', '--cpu', '68000', '--clr', 'always', '--peephole', 'fixed-point',
            '--fpu', 'disabled', '--exceptions', 'yolo', '--code-size-optimizations', 'on',
            '--code-size-passes', $passes, '--format', 'hunk', '--runtime', 'freestanding',
            '--memory', 'none', '--exports', 'none', '--symbols', 'off',
            '--output', (Join-Path $outputRoot $selection.File),
            '--compatibility-report', (Join-Path $outputRoot 'compatibility.json'))
        foreach ($assembly in @('CopperSFS.Core.dll', 'CopperSharp.Sdk.Amiga.dll', 'CopperSharp.Sdk.Amiga.Support.dll', 'CopperSharp.Compiler.dll')) {
            $input = Join-Path $inputs $assembly
            if ($assembly -ne 'CopperSFS.Core.dll') {
                $hash = (Get-FileHash -LiteralPath $input).Hash
                if ($sdkHashes.ContainsKey($assembly) -and $sdkHashes[$assembly] -ne $hash) {
                    throw "SDK/compiler metadata changed between input builds: $assembly"
                }
                $sdkHashes[$assembly] = $hash
            }
            $arguments += @('--managed-assembly', $input)
        }
        $compiles += @{ Variant = $variant; Profile = $selection.Name; Arguments = $arguments; Root = $outputRoot }
    }
}
$compiles | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'compile-commands.json')

# Each compiler process consumes an immutable, separate input directory.
$active = @()
foreach ($compile in $compiles) {
    while ($active.Count -ge 2) {
        $finished = Wait-Job -Job $active -Any
        Receive-Job -Job $finished
        if ($finished.State -ne 'Completed') { throw 'Native compilation failed.' }
        $active = @($active | Where-Object Id -ne $finished.Id)
        Remove-Job -Job $finished
    }
    $active += Start-Job -ScriptBlock {
        param($nativeArguments, $nativeRoot)
        & dotnet @nativeArguments *> (Join-Path $nativeRoot 'compile.log')
        if ($LASTEXITCODE -ne 0) { throw "Native compiler failed: $nativeRoot" }
        Write-Output "Built $nativeRoot"
    } -ArgumentList (,$compile.Arguments), $compile.Root
}
foreach ($job in $active) {
    Wait-Job -Job $job | Out-Null
    Receive-Job -Job $job
    if ($job.State -ne 'Completed') { throw 'Native compilation failed.' }
    Remove-Job -Job $job
}

$pairs = foreach ($selection in $profiles) {
    foreach ($variant in @('before', 'after')) {
        $compatibility = Get-Content -LiteralPath (Join-Path $ArtifactRoot "$variant/$($selection.Name)/compatibility.json") -Raw | ConvertFrom-Json
        if (!$compatibility.IsCompatible -or @($compatibility.ManagedAllocationSites).Count -ne 0) { throw 'Handler is not allocation-free and native compatible.' }
    }
    [ordered]@{ Name = $selection.Name; Sfs2 = $selection.Sfs2; CacheEntries = $(if ($selection.Sfs2) { 32 } else { 16 })
        Before = Join-Path $ArtifactRoot "before/$($selection.Name)/$($selection.File)"
        After = Join-Path $ArtifactRoot "after/$($selection.Name)/$($selection.File)" }
}
[ordered]@{
    BaselineCommit = $baselineCommit; Pairs = @($pairs); Trials = 3; CpuModels = @('M68000', 'M68020')
    FileBytes = 1048576; CacheBlocks = 128; MaxTransfer = 65536
    Workloads = @(
        @{ PacketBytes = 64; ReadBytes = 65536 }, @{ PacketBytes = 512; ReadBytes = 262144 },
        @{ PacketBytes = 4096; ReadBytes = 1048576 }, @{ PacketBytes = 65536; ReadBytes = 1048576 })
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'benchmark.json')
Get-ChildItem -LiteralPath $frozenCompiler -File | Get-FileHash | Select-Object Path,Hash |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'compiler-manifest.json')
Write-Output "Prepared benchmark manifest: $(Join-Path $ArtifactRoot 'benchmark.json')"
