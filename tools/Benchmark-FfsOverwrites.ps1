param(
    [string]$Rom = $env:COPPERSHARP_KICKSTART31_ROM,
    [string]$CopperSharpRoot = "$PSScriptRoot/../../CopperSharp68k",
    [string]$ArtifactRoot = "$PSScriptRoot/../artifacts/ffs-overwrite-$(Get-Date -Format yyyyMMdd-HHmmss)",
    [switch]$ExploreCutoff
)
$ErrorActionPreference = 'Stop'
if (!$Rom -or !(Test-Path -LiteralPath $Rom -PathType Leaf)) { throw 'Supply a licensed Kickstart 3.1 ROM.' }
if (Test-Path -LiteralPath $ArtifactRoot) { throw 'Select a fresh artifact directory.' }
$repository = (Resolve-Path "$PSScriptRoot/..").Path
$compiler = (Resolve-Path -LiteralPath $CopperSharpRoot).Path
$output = (New-Item -ItemType Directory -Path $ArtifactRoot).FullName
$snapshot = Join-Path $output 'source'
foreach ($path in 'src/CopperFFS.Core','src/CopperFS.Amiga','src/CopperSFS.Core','build') {
    $dest = New-Item -Force -ItemType Directory -Path "$snapshot/$path"
    Get-ChildItem "$repository/$path/*" -Include *.cs,*.csproj,*.props,Build-Handler.ps1 -File |
        Copy-Item -Destination $dest.FullName
}
Copy-Item "$repository/Directory.Build.props" $snapshot
$adapter = "$snapshot/src/CopperFS.Amiga/NativeAmigaFileSystem.cs"
$writer = "$snapshot/src/CopperFFS.Core/AmigaFfsWriteCore.cs"
$native = [IO.File]::ReadAllText($adapter)
$core = [IO.File]::ReadAllText($writer)
function Replace-Once([string]$body, [string]$needle, [string]$value) {
    if ($body.IndexOf($needle) -lt 0 -or $body.IndexOf($needle) -ne $body.LastIndexOf($needle)) {
        throw "Benchmark source differs at '$needle'. Review the comparison arms."
    }
    $body.Replace($needle, $value)
}
& "$snapshot/build/Build-Handler.ps1" -Ffs -CopperSharpRoot $compiler -ArtifactRoot "$output/candidate"
[IO.File]::WriteAllText($adapter, (Replace-Once $native 'APTR.ReadUInt32(state, FixedHardDiskOffset) != 0' 'false'))
& "$snapshot/build/Build-Handler.ps1" -Ffs -SkipCompilerBuild -CopperSharpRoot $compiler -ArtifactRoot "$output/baseline"
[IO.File]::WriteAllText($adapter, $native)
if ($ExploreCutoff) {
    $trial = Replace-Once $core 'if (count < 8)' 'if (count < 2)'
    [IO.File]::WriteAllText($writer, (Replace-Once $trial 'count >= 32 ? 3u : 4u' '1u'))
    & "$snapshot/build/Build-Handler.ps1" -Ffs -SkipCompilerBuild -CopperSharpRoot $compiler -ArtifactRoot "$output/trial"
    [IO.File]::WriteAllText($writer, $core)
}
$settings = @{
    COPPERSHARP_KICKSTART31_ROM = (Resolve-Path -LiteralPath $Rom).Path
    COPPERFS_FFS_HANDLER = "$output/candidate/CopperFFS"
    COPPERFS_FFS_BASELINE = "$output/baseline/CopperFFS"
    COPPERFS_FFS_TRIAL = $(if ($ExploreCutoff) { "$output/trial/CopperFFS" })
    COPPERFS_OVERWRITE_RESULTS = "$output/results"
}
$previous = @{}
try {
    foreach ($key in $settings.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $settings[$key])
    }
    $filter = 'FullyQualifiedName~FragmentedOverwriteThenFreshBootSequentialRead|FullyQualifiedName~OfsHardDiskPlacement|FullyQualifiedName~TrackdiskOverwritePreservesPointersAcrossFreshBoot|FullyQualifiedName~UnknownFixedDeviceOverwritesInPlace'
    dotnet test "$repository/tests/CopperFS.Kickstart.Tests" -c Release "-p:CopperSharp68kRoot=$compiler" --artifacts-path "$output/harness" --filter $filter
    if ($LASTEXITCODE) { throw 'Overwrite qualification failed.' }
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
}
