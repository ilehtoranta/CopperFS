param([Parameter(Mandatory)][string[]]$ArtifactsPath)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lock = Get-Content (Join-Path $repo 'qualification/dependencies/nuget-packages.json') -Raw | ConvertFrom-Json
$seenAssets = 0; $seenPackages = 0
foreach ($root in $ArtifactsPath) {
    if (!(Test-Path -LiteralPath $root -PathType Container)) { throw "Missing artifacts directory: $root" }
    foreach ($file in (Get-ChildItem -LiteralPath $root -Recurse -Filter 'project.assets.json' -File)) {
        $seenAssets++
        $assets = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        foreach ($package in $assets.libraries.PSObject.Properties) {
            if ($package.Value.type -ne 'package') { continue }
            $seenPackages++
            $expected = $lock.PSObject.Properties[$package.Name]
            if (!$expected -or !$package.Value.sha512 -or $expected.Value -cne $package.Value.sha512) {
                throw "Unexpected NuGet package or content hash: $($package.Name) in $($file.FullName)"
            }
        }
    }
}
if (!$seenAssets -or !$seenPackages) { throw 'No restored package assets were found.' }
Write-Output "Verified $seenPackages package references in $seenAssets asset files."
