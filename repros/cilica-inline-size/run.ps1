param([string]$OutputRoot = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (!$OutputRoot) {
    $OutputRoot = Join-Path $root ('artifacts/cilica-inline-issue-' + [guid]::NewGuid().ToString('N').Substring(0,8))
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw "Choose a new output directory: $OutputRoot" }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null

dotnet build (Join-Path $root 'Runner/Runner.csproj') -c Release -v:q
if ($LASTEXITCODE -ne 0) { throw 'Runner build failed' }
dotnet build (Join-Path $root 'Optimizer/Optimizer.csproj') -c Release -v:q
if ($LASTEXITCODE -ne 0) { throw 'Optimizer build failed' }
$runner = Join-Path $root 'Runner/bin/Release/net10.0'
$optimizer = Join-Path $root 'Optimizer/bin/Release/net10.0/Optimizer.dll'
$inputDll = Join-Path $runner 'Workload.dll'
$originalEnvironment = @{}
foreach ($name in 'CILICA_TRACE','DOTNET_TieredCompilation','DOTNET_ReadyToRun','DOTNET_JitDisasm') {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    $env:CILICA_TRACE = '1'
    $env:DOTNET_TieredCompilation = '0'
    $env:DOTNET_ReadyToRun = '0'
    $env:DOTNET_JitDisasm = 'InlineSize.Filesystem:*'
    $results = @{}
    foreach ($arm in @('off','on','on-cap200')) {
        $dir = Join-Path $OutputRoot $arm
        New-Item -ItemType Directory -Path $dir | Out-Null
        Copy-Item -Path (Join-Path $runner '*') -Destination $dir
        $optArgs = @($optimizer, $inputDll, (Join-Path $dir 'Workload.dll'), $(if ($arm -eq 'off') { 'off' } else { 'on' }))
        if ($arm -eq 'on-cap200') { $optArgs += '200' }
        & dotnet @optArgs *> (Join-Path $dir 'optimizer-trace.txt')
        if ($LASTEXITCODE -ne 0) { throw "CILica failed: $arm" }
        & dotnet (Join-Path $dir 'Runner.dll') *> (Join-Path $dir 'jit.txt')
        if ($LASTEXITCODE -ne 0) { throw "Runner failed: $arm" }
        $log = Get-Content -LiteralPath (Join-Path $dir 'jit.txt')
        $results[$arm] = @($log | Where-Object { $_ -like 'RESULT *' })
        if ($results[$arm].Count -ne 4) { throw "Missing correctness results: $arm" }
        $sizes = @($log | Where-Object { $_ -match '^; Total bytes of code (\d+)' } | ForEach-Object { [int]([regex]::Match($_, '\d+$').Value) })
        if (!$sizes.Count) { throw "No JIT disassembly found: $arm. Run an x64 runtime with JIT disassembly support." }
        $callerIL = $log | Where-Object { $_ -match '^IL Write \d+$' } | Select-Object -First 1
        $sum = ($sizes | Measure-Object -Sum).Sum
        Write-Output "$arm`: $callerIL; emitted Filesystem methods = $($sizes -join ' + ') = $sum bytes"
    }
    foreach ($arm in 'on','on-cap200') {
        if (Compare-Object $results['off'] $results[$arm]) { throw "Results differ for $arm" }
    }
    Write-Output "Four correctness cases match in all arms. Logs: $OutputRoot"
}
finally {
    foreach ($name in $originalEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process') }
}
