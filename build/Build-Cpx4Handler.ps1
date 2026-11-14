param(
    [string]$CopperSharpRoot = '', [string]$Configuration = 'Release',
    [string]$ArtifactRoot = '', [ValidateSet('68000','68020')][string]$Cpu = '68000',
    [switch]$SkipCompilerBuild
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$CopperSharpRoot) { $CopperSharpRoot = [IO.Path]::GetFullPath((Join-Path $repo '../CopperSharp68k')) }
if (!$ArtifactRoot) { $ArtifactRoot = Join-Path $repo 'artifacts/native-cpx4-handler' }
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
$inputs = Join-Path $ArtifactRoot 'inputs'
New-Item -ItemType Directory -Force -Path $inputs | Out-Null
function Get-CpxSourceHashes {
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src/CopperCPX.Core'),(Join-Path $repo 'src/CopperFS.Amiga/Cpx4') -Filter '*.cs' -File)
    $sources += Get-Item -LiteralPath (Join-Path $repo 'prototypes/CopperFS.Prototype/WideMath32.cs')
    @($sources | Sort-Object FullName | ForEach-Object { [ordered]@{ Path=[IO.Path]::GetRelativePath($repo,$_.FullName); Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
}
$sourceInputs = Get-CpxSourceHashes
if (!$SkipCompilerBuild) {
    dotnet build (Join-Path $CopperSharpRoot 'Compiler.Cli/CopperSharp.Compiler.Cli.csproj') -c $Configuration --verbosity quiet -m:1
    if ($LASTEXITCODE -ne 0) { throw 'CPX4 compiler build failed.' }
}
dotnet build (Join-Path $repo 'src/CopperFS.Amiga/CopperFS.Amiga.csproj') -c $Configuration '-p:CopperFsHandler=Cpx4' "-p:CopperSharp68kRoot=$CopperSharpRoot" --artifacts-path (Join-Path $ArtifactRoot 'managed') --output $inputs --verbosity quiet -m:1
if ($LASTEXITCODE -ne 0) { throw 'CPX4 managed input build failed.' }
$output = Join-Path $ArtifactRoot 'CopperCPX4'
$arguments = @((Join-Path $CopperSharpRoot "Compiler.Cli/bin/$Configuration/net10.0/CopperSharp.Compiler.Cli.dll"),
    (Join-Path $inputs 'CopperFS.Amiga.dll'), '--entry', 'CopperCPX.Amiga.NativeCpxHandler::Entry',
    '--platform','amiga','--cpu',$Cpu,'--clr','always','--peephole','fixed-point',
    '--fpu','disabled','--exceptions','yolo','--format','hunk','--runtime','freestanding',
    '--memory','none','--exports','none','--symbols','off','--output',$output,
    '--compatibility-report',(Join-Path $ArtifactRoot 'CopperCPX4.compatibility.json'))
foreach ($assembly in @('CopperCPX.Core.dll','CopperSharp.Sdk.Amiga.dll','CopperSharp.Compiler.dll')) {
    $file = Join-Path $inputs $assembly
    if (!(Test-Path -LiteralPath $file)) { throw "Missing CPX4 build input: $assembly" }
    $arguments += @('--managed-assembly',$file)
}
dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'CPX4 native compilation failed.' }
$report = Get-Content -LiteralPath (Join-Path $ArtifactRoot 'CopperCPX4.compatibility.json') -Raw | ConvertFrom-Json
if ($report.IsCompatible -ne $true -or !$report.PSObject.Properties['ManagedAllocationSites'] -or
    $null -eq $report.ManagedAllocationSites -or @($report.ManagedAllocationSites).Count -ne 0) {
    throw 'CPX4 native handler must be compatible and contain no reachable managed allocation sites.'
}
$sourceOutputs = Get-CpxSourceHashes
if (($sourceInputs | ConvertTo-Json -Depth 5 -Compress) -ne ($sourceOutputs | ConvertTo-Json -Depth 5 -Compress)) {
    throw 'CPX4 sources changed during native compilation. The generated HUNK is unqualified; rebuild from stable sources.'
}
@{ Cpu=$Cpu; HunkSha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash;
   CompatibilitySha256=(Get-FileHash -LiteralPath (Join-Path $ArtifactRoot 'CopperCPX4.compatibility.json') -Algorithm SHA256).Hash;
   ManagedAllocationSiteCount=@($report.ManagedAllocationSites).Count;
   Sources=$sourceInputs;
   ManagedInputs=@(Get-ChildItem -LiteralPath $inputs -Filter '*.dll' -File | Sort-Object Name | ForEach-Object { @{ Name=$_.Name; Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } });
   CompilerSha256=(Get-FileHash -LiteralPath (Join-Path $CopperSharpRoot "Compiler.Cli/bin/$Configuration/net10.0/CopperSharp.Compiler.Cli.dll") -Algorithm SHA256).Hash
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'source-manifest.json')
Write-Output "Built experimental $output. Consult the packet matrix for unsupported operations."
