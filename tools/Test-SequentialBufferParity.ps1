param([Parameter(Mandatory)][string]$ManifestPath)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$root = Split-Path -Parent $ManifestPath
foreach ($pair in $manifest.Pairs) {
    $addresses = @()
    foreach ($variant in @('before', 'after')) {
        $path = Join-Path $root "kickstart/$($pair.Name)-$variant.json"
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing benchmark data: $path" }
        $rows = @(Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
        if (!$rows.Count) { throw "No benchmark trials: $path" }
        foreach ($row in $rows) {
            if (!$row.BufferAddress -or ($row.BufferAddress -band 0xffff) -ne 0) { throw "Invalid aligned buffer: $path" }
            $addresses += [uint32]$row.BufferAddress
        }
    }
    if (@($addresses | Select-Object -Unique).Count -ne 1) { throw "Buffer addresses differ between trials or handler arms: $($pair.Name)" }
    Write-Output "$($pair.Name): common buffer $($addresses[0])"
}
if (!$manifest.Pairs.Count) { throw 'No benchmark pairs supplied.' }
