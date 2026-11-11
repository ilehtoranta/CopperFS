param(
    [Parameter(Mandatory)][string]$ArtifactRoot
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ArtifactRoot).Path
$sizes = @(64, 512, 4096, 65536)
$rows = @()
foreach ($delay in @(0, 100, 500, 1000, 2000, 4000)) {
    $dir = Join-Path $root "phase-$delay"
    $manifest = Get-Content -LiteralPath (Join-Path $dir 'benchmark.json') -Raw | ConvertFrom-Json
    if ($manifest.PhaseDelay -ne $delay -or @($manifest.Pairs).Count -ne 1 -or $manifest.Pairs[0].Name -ne 'ffs') {
        throw "Invalid phase manifest: $dir"
    }
    $trxPath = Join-Path $dir 'kickstart.trx'
    if (!(Test-Path -LiteralPath $trxPath)) { $trxPath = Join-Path $root "results/phase-$delay.trx" }
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $cases = @($trx.TestRun.Results.UnitTestResult)
    if ($cases.Count -ne 2 -or @($cases | Where-Object { $_.outcome -ne 'Passed' -or
            $_.testName -notmatch '^CopperFS\.Kickstart\.Tests\.SequentialReadThroughputTests\.ReadThroughKickstartDosAndLoadedHandler\(profile: "ffs", variant: "(before|after)"\)$' }).Count -ne 0 -or
        @($cases | ForEach-Object testName | Select-Object -Unique).Count -ne 2) {
        throw "Incomplete phase tests: $delay"
    }
    $arms = @{}
    foreach ($variant in @('before', 'after')) {
        $run = Join-Path $dir "kickstart/ffs-$variant"
        $items = @(Get-Content -LiteralPath "$run.json" -Raw | ConvertFrom-Json)
        if ($items.Count -ne 12) { throw "Incomplete trials: $run" }
        $path = if ($variant -eq 'before') { $manifest.Pairs[0].Before } else { $manifest.Pairs[0].After }
        $handler = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $media = (Get-FileHash -LiteralPath (Join-Path $run 'read.hdf') -Algorithm SHA256).Hash
        $probe = (Get-FileHash -LiteralPath (Join-Path $run 'boot/PROBE') -Algorithm SHA256).Hash
        if ((Get-FileHash -LiteralPath (Join-Path $run 'boot/HANDLER') -Algorithm SHA256).Hash -ne $handler) {
            throw "Handler mismatch: $run"
        }
        foreach ($item in $items) {
            if ($item.Profile -ne 'ffs' -or $item.Variant -ne $variant -or $item.HandlerSha256 -ne $handler -or
                $item.MediaSha256 -ne $media -or $item.ProbeSha256 -ne $probe -or $item.SessionBusCycles -le 0) {
                throw "Trial provenance mismatch: $run"
            }
        }
        $arms[$variant] = $items
    }
    foreach ($size in $sizes) {
        $before = @($arms.before | Where-Object PacketBytes -eq $size | Sort-Object Trial)
        $after = @($arms.after | Where-Object PacketBytes -eq $size | Sort-Object Trial)
        if ($before.Count -ne 3 -or $after.Count -ne 3 -or
            ($before.Trial -join ',') -ne '0,1,2' -or ($after.Trial -join ',') -ne '0,1,2') {
            throw "Incomplete workload $size at phase $delay"
        }
        foreach ($field in @('BufferAddress', 'DataSha256', 'MediaSha256', 'ProbeSha256',
                'KickstartSha256', 'ReadRequests', 'DeviceReadBytes')) {
            if (@(($before + $after).$field | Select-Object -Unique).Count -ne 1) {
                throw "Mismatch in $field for $size at phase $delay"
            }
        }
        $left = @($before.SessionBusCycles | Sort-Object)[1]
        $right = @($after.SessionBusCycles | Sort-Object)[1]
        $rows += [pscustomobject]@{ Phase = $delay; PacketBytes = $size; BeforeCycles = $left;
            AfterCycles = $right; ChangePercent = 100.0 * ($right / $left - 1.0) }
    }
}
$rows | Format-Table -AutoSize
foreach ($size in $sizes) {
    $values = @($rows | Where-Object PacketBytes -eq $size | ForEach-Object ChangePercent)
    $limits = $values | Measure-Object -Minimum -Maximum
    '{0,6} bytes: {1:+0.000;-0.000;0.000}% to {2:+0.000;-0.000;0.000}%' -f $size, $limits.Minimum, $limits.Maximum
}
