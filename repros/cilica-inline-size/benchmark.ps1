param(
    [ValidateRange(1, 100)][int]$Rounds = 9,
    [ValidateRange(1, 100000000)][int]$Iterations = 3000000,
    [string]$OutputRoot = ''
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (!$OutputRoot) {
    $OutputRoot = Join-Path $root ('artifacts/cilica-inline-benchmark-' + [guid]::NewGuid().ToString('N').Substring(0,8))
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
& (Join-Path $root 'run.ps1') -OutputRoot $OutputRoot
dotnet build (Join-Path $root 'Bench/Bench.csproj') -c Release -v:q
if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed' }
$bench = Join-Path $root 'Bench/bin/Release/net10.0'
$arms = @('off','on','on-cap200')
foreach ($arm in $arms) {
    $dir = Join-Path $OutputRoot "benchmark-$arm"
    New-Item -ItemType Directory -Path $dir | Out-Null
    Copy-Item -Path (Join-Path $bench '*') -Destination $dir
    Copy-Item -LiteralPath (Join-Path $OutputRoot "$arm/Workload.dll") -Destination $dir -Force
}
$environment = @{}
foreach ($name in 'DOTNET_TieredCompilation','DOTNET_ReadyToRun','DOTNET_JitDisasm','DOTNET_JitNoInline') {
    $environment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    $env:DOTNET_TieredCompilation='0'; $env:DOTNET_ReadyToRun='0'; $env:DOTNET_JitNoInline='0'
    [Environment]::SetEnvironmentVariable('DOTNET_JitDisasm', $null, 'Process')
    $samples = @()
    for ($round = 0; $round -lt $Rounds; $round++) {
        for ($index = 0; $index -lt $arms.Count; $index++) {
            $arm = $arms[($round + $index) % $arms.Count]
            $output = & dotnet (Join-Path $OutputRoot "benchmark-$arm/Bench.dll") $Iterations
            if ($LASTEXITCODE -ne 0) { throw "Benchmark failed: $arm round $round" }
            $result = $output | ConvertFrom-Json
            if ($result.Error -ne 0 -or ($result.words -join ',') -ne '42,-69,2,3,4,5,6,7') {
                throw "Unexpected benchmark result: $arm round $round"
            }
            $samples += [pscustomobject]@{ Round=$round; Arm=$arm; NsPerOperation=$result.nsPerOperation; Ticks=$result.elapsedTicks }
        }
    }
    $samples | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputRoot 'benchmark-samples.json')
    function Median($values) {
        $sorted = @($values | Sort-Object)
        if ($sorted.Count % 2) { return $sorted[[int][Math]::Floor($sorted.Count / 2)] }
        return ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2
    }
    foreach ($arm in $arms) {
        $values = @($samples | Where-Object Arm -eq $arm | ForEach-Object NsPerOperation)
        $reference = @($samples | Where-Object Arm -eq 'off' | Sort-Object Round)
        $current = @($samples | Where-Object Arm -eq $arm | Sort-Object Round)
        $ratios = for ($i = 0; $i -lt $Rounds; $i++) { $current[$i].NsPerOperation / $reference[$i].NsPerOperation }
        '{0}: median {1:N2} ns/op; range {2:N2}–{3:N2}; median paired time change vs off {4:+0.00;-0.00;0.00}%' -f $arm,(Median $values),($values | Measure-Object -Minimum).Minimum,($values | Measure-Object -Maximum).Maximum,(100 * ((Median $ratios) - 1))
    }
    Write-Output "Each sample times $Iterations successful writes after warmup; $Rounds rotated rounds. Results: $OutputRoot"
}
finally {
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process') }
}
