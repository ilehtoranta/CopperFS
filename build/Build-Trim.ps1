param(
    [string]$CopperSharpRoot = '',
    [string]$Configuration = 'Release',
    [string]$ArtifactRoot = '',
    [switch]$SkipCompilerBuild
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $CopperSharpRoot) { $CopperSharpRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '../CopperSharp68k')) }
if (-not $ArtifactRoot) { $ArtifactRoot = Join-Path $repositoryRoot 'artifacts/native-sfs-trim' }
$inputs = Join-Path $ArtifactRoot 'inputs'
if (-not $SkipCompilerBuild) {
    dotnet build (Join-Path $CopperSharpRoot 'Compiler.Cli/CopperSharp.Compiler.Cli.csproj') -c $Configuration -m:1 --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Compiler build failed.' }
}
dotnet build (Join-Path $repositoryRoot 'tools/CopperSFS.Trim/CopperSFS.Trim.csproj') -c $Configuration "-p:CopperSharp68kRoot=$CopperSharpRoot" --output $inputs -m:1 --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'TRIM command input build failed.' }
$arguments = @((Join-Path $CopperSharpRoot "Compiler.Cli/bin/$Configuration/net10.0/CopperSharp.Compiler.Cli.dll"),
    (Join-Path $inputs 'CopperSFS.Trim.dll'), '--entry', 'CopperSFS.Tools.TrimCommand::Main',
    '--platform', 'amiga', '--cpu', '68000', '--format', 'hunk', '--runtime', 'freestanding',
    '--memory', 'none', '--exceptions', 'yolo', '--exports', 'none', '--symbols', 'off',
    '--fpu', 'disabled', '--clr', 'always', '--peephole', 'fixed-point', '--output', (Join-Path $ArtifactRoot 'CopperTrim'))
foreach ($assembly in @('CopperSFS.Core.dll','CopperSharp.Sdk.Amiga.dll','CopperSharp.Sdk.Amiga.Support.dll','CopperSharp.Compiler.dll')) {
    $arguments += @('--managed-assembly', (Join-Path $inputs $assembly))
}
dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'TRIM command native compilation failed.' }
# Preserve the old command name as a byte-identical compatibility alias.
Copy-Item -LiteralPath (Join-Path $ArtifactRoot 'CopperTrim') -Destination (Join-Path $ArtifactRoot 'CopperSFSTrim') -Force
