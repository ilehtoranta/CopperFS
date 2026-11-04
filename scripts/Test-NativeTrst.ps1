param(
    [Parameter(Mandatory)][string]$Handler,
    [Parameter(Mandatory)][string]$NativeTestAssembly,
    [Parameter(Mandatory)][string]$FixtureManifest,
    [Parameter(Mandatory)][string]$Python,
    [Parameter(Mandatory)][string]$LegacyHandler,
    [string]$OutputDirectory,
    [switch]$StrictWrites,
    [string]$Filter
)
$ErrorActionPreference = 'Stop'
if (!$Filter) {
    $Filter = 'FullyQualifiedName~NativeTrst|FullyQualifiedName~CopperWritesBaseIndependently|FullyQualifiedName~SharedCorrectnessWorkloadAndCleanRemount|FullyQualifiedName~SharedHandlesObserveWritesAndFileSizes|FullyQualifiedName~ConcurrentGuestProcessesAndFreshRemount|FullyQualifiedName~NativeSfsAmigaOsTests|FullyQualifiedName~SequentialReadsPreserveBytes'
    if (!$StrictWrites) { $Filter += '|FullyQualifiedName~BufferedTrstGroup|FullyQualifiedName~CopperTimerCommitsWithoutAnotherDosPacket' }
}
$repository = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repository ('artifacts/native-trst/run-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'tests.trx')) { throw 'Choose a fresh output directory for each qualification run.' }
$settings = @{
    COPPERSFS_HANDLER_PATH = [IO.Path]::GetFullPath($Handler)
    COPPERSFS_AB_MANIFEST = [IO.Path]::GetFullPath($FixtureManifest)
    COPPERSFS_AB_PYTHON = [IO.Path]::GetFullPath($Python)
    COPPERSFS_LEGACY_HANDLER = [IO.Path]::GetFullPath($LegacyHandler)
    COPPERSFS_NATIVE_TRST = '1'
    COPPERSFS_NATIVE_TRST_EVIDENCE = $OutputDirectory
    COPPERSFS_AB_RUN_ID = 'native-trst-' + (Split-Path -Leaf $OutputDirectory)
    COPPERSFS_AB_CRASH_SAMPLE = $null
    COPPERSFS_AB_KEEP_FAILED = '1'
}
$previous = @{}
try {
    foreach ($name in $settings.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $settings[$name])
    }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    dotnet test $NativeTestAssembly --nologo --filter $Filter --logger 'trx;LogFileName=tests.trx' --results-directory $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Native TRST qualification failed: $OutputDirectory" }
    [xml]$result = Get-Content -LiteralPath (Join-Path $OutputDirectory 'tests.trx') -Raw
    $tests = @($result.TestRun.Results.UnitTestResult)
    if (!$tests.Count -or @($tests | Where-Object outcome -NE 'Passed').Count) { throw 'Qualification requires every selected test to pass without skips.' }
    [ordered]@{
        handlerSha256 = (Get-FileHash -LiteralPath $Handler -Algorithm SHA256).Hash
        nativeTestsSha256 = (Get-FileHash -LiteralPath $NativeTestAssembly -Algorithm SHA256).Hash
        passed = $tests.Count; skipped = 0; filter = $Filter
        boundary = 'Compiled MC68000 writer on emulated Amiga; atomic 512-byte sectors and reliable flushes. Equivalent durable cut states are grouped with complete sector/flush coverage receipts.'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
}
finally { foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) } }
