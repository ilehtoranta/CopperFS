param(
    [Parameter(Mandatory)][string]$TrstHandler,
    [Parameter(Mandatory)][string]$CsUnStrictHandler,
    [Parameter(Mandatory)][string]$CsUnBufferedHandler,
    [Parameter(Mandatory)][string]$NativeTestAssembly,
    [Parameter(Mandatory)][string]$FixtureManifest,
    [Parameter(Mandatory)][string]$Python,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1, 5)][int]$Trials = 5
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path (Join-Path $OutputDirectory 'measurements.json')) { throw 'Use a fresh benchmark output directory.' }
$settings = @{
    COPPERSFS_HANDLER_PATH = [IO.Path]::GetFullPath($TrstHandler)
    COPPERSFS_BENCH_CSUN_STRICT = [IO.Path]::GetFullPath($CsUnStrictHandler)
    COPPERSFS_BENCH_CSUN_BUFFERED = [IO.Path]::GetFullPath($CsUnBufferedHandler)
    COPPERSFS_AB_MANIFEST = [IO.Path]::GetFullPath($FixtureManifest)
    COPPERSFS_AB_PYTHON = [IO.Path]::GetFullPath($Python)
    COPPERSFS_AB_PERF = '1'
    COPPERSFS_BENCH_OUTPUT = $OutputDirectory
    COPPERSFS_BENCH_TRIALS = "$Trials"
    COPPERSFS_AB_PROFILE_MAP = $null
    COPPERSFS_AB_NO_HOOK = $null
}
$previous = @{}
try {
    foreach ($key in $settings.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $settings[$key])
    }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    dotnet test $NativeTestAssembly --nologo --filter 'FullyQualifiedName~CompiledWritePolicyComparison' --logger 'trx;LogFileName=tests.trx' --results-directory $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Compiled write-policy comparison failed.' }
    $measurements = Get-Content (Join-Path $OutputDirectory 'measurements.json') -Raw | ConvertFrom-Json
    if (!$measurements.passed -or @($measurements.rawTrials).Count -ne $Trials * 12) { throw 'Incomplete benchmark.' }
    $assemblyDirectory = Split-Path ([IO.Path]::GetFullPath($NativeTestAssembly))
    [ordered]@{
        completedUtc = [DateTime]::UtcNow.ToString('o')
        trialsPerHandlerAndWorkload = $Trials
        completedTrials = @($measurements.rawTrials).Count
        inputs = @('CopperStart.Sfs.Native.Tests.dll', 'CopperMod.Amiga.Lightweight.dll', 'Copper68k.dll', 'CopperSharp.Compiler.dll') | ForEach-Object {
            $inputPath = Join-Path $assemblyDirectory $_
            @{name = $_; sha256 = (Get-FileHash -LiteralPath $inputPath).Hash}
        }
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'run.json') -Encoding utf8
}
finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
}
