param(
    [Parameter(Mandatory)][string]$NativeTestAssembly,
    [Parameter(Mandatory)][string]$FixtureManifest,
    [Parameter(Mandatory)][string]$CopperHandler,
    [Parameter(Mandatory)][string]$Python,
    [string]$Rom = $env:COPPERSHARP_KICKSTART31_ROM,
    [string]$TestAssembly,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$TestAssembly) { $TestAssembly = Join-Path $repository 'tests/CopperSFS.Core.Tests/bin/Release/net10.0/CopperSFS.Core.Tests.dll' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repository ('artifacts/trst/original-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$settings = @{
    COPPERSFS_NATIVE_TEST_ASSEMBLY = [IO.Path]::GetFullPath($NativeTestAssembly)
    COPPERSFS_AB_MANIFEST = [IO.Path]::GetFullPath($FixtureManifest)
    COPPERSFS_HANDLER_PATH = [IO.Path]::GetFullPath($CopperHandler)
    COPPERSFS_AB_PYTHON = [IO.Path]::GetFullPath($Python)
    COPPERSHARP_KICKSTART31_ROM = [IO.Path]::GetFullPath($Rom)
    COPPERSFS_TRST_EVIDENCE = Join-Path $OutputDirectory 'evidence'
    COPPERSFS_AB_RUN_ID = 'portable-trst-original'
}
$previous = @{}
try {
    foreach ($name in $settings.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $settings[$name])
    }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    dotnet test $TestAssembly --nologo --filter 'FullyQualifiedName~TrstOriginalHandlerTests' --logger 'trx;LogFileName=tests.trx' --results-directory $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Original-handler TRST tests failed: $OutputDirectory" }
}
finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
}
