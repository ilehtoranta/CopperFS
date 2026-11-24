param([string]$ArtifactRoot='',[string]$CopperSharpRoot='', [switch]$SkipCompilerBuild)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if(!$ArtifactRoot) { $ArtifactRoot=Join-Path $repo 'artifacts/nova-prototype-handler' }
if(!$CopperSharpRoot) { $CopperSharpRoot=Join-Path (Split-Path -Parent $repo) 'CopperSharp68k' }
$ArtifactRoot=[IO.Path]::GetFullPath($ArtifactRoot)
New-Item -ItemType Directory -Path $ArtifactRoot -Force | Out-Null
$inputs=Join-Path $ArtifactRoot 'inputs'
$sourcePaths=@(Get-ChildItem -LiteralPath (Join-Path $repo 'prototypes/CopperNova.Prototype'),(Join-Path $repo 'prototypes/CopperNova.Native') -Filter '*.cs' -File)
$sourcePaths+=@('NativeCpxMemory.cs','NativeCpxIo.cs','NativeCpxTransport.cs','NativeCpxVolume.cs') | ForEach-Object { Get-Item -LiteralPath (Join-Path $repo "src/CopperFS.Amiga/Cpx4/$_") }
$sourcePaths+=Get-ChildItem -LiteralPath (Join-Path $repo 'src/CopperCPX.Core') -Filter '*.cs' -File
$sourcePaths+=Get-Item -LiteralPath (Join-Path $repo 'prototypes/CopperFS.Prototype/WideMath32.cs')
function SourceHashes { @($sourcePaths | Sort-Object FullName | ForEach-Object { @{Path=[IO.Path]::GetRelativePath($repo,$_.FullName);Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} }) }
$before=SourceHashes
if(!$SkipCompilerBuild) {
    dotnet build (Join-Path $CopperSharpRoot 'Compiler.Cli/CopperSharp.Compiler.Cli.csproj') -c Release -p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0 --verbosity quiet -m:1
    if($LASTEXITCODE -ne 0) { throw 'Compiler build failed' }
}
dotnet build (Join-Path $repo 'src/CopperFS.Amiga/CopperNova.Prototype.Amiga.csproj') -c Release -p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0 "-p:CopperSharp68kRoot=$CopperSharpRoot" --artifacts-path (Join-Path $ArtifactRoot 'managed') --output $inputs --verbosity quiet -m:1
if($LASTEXITCODE -ne 0) { throw 'Prototype managed build failed' }
$cli=Join-Path $CopperSharpRoot 'Compiler.Cli/bin/Release/net10.0/CopperSharp.Compiler.Cli.dll'
$output=Join-Path $ArtifactRoot 'CopperNovaPrototype'
$nativeArguments=@($cli,(Join-Path $inputs 'CopperNova.Prototype.Amiga.dll'),'--entry','CopperNova.Amiga.NativeNovaHandler::Entry','--platform','amiga','--cpu','68000','--clr','always','--peephole','fixed-point','--fpu','disabled','--exceptions','yolo','--format','hunk','--runtime','freestanding','--memory','none','--include-export','coppernova.handler','--symbols','off','--code-size-optimizations','on','--code-size-passes','ElideUnusedRegisterArguments,ReuseIncomingArgumentHomes,InlineSingleUseMethods,ForwardReadOnlyAggregateLocals,RemoveRedundantTransport,CompactPrivateStackCopies,CompactGuestMemory,NarrowOperations,EliminateRedundantInitialization,InlineMemoryHelpers,SimplifyLoopPhis,CompactFrameHomeInitialization,ShareArithmeticCores,ShareReturnSequences,ShareIdenticalMethods,ClusterInternalCalls,SizeFirstCosts','--output',$output,'--compatibility-report',(Join-Path $ArtifactRoot 'compatibility.json'))
foreach($name in @('CopperNova.Prototype','CopperCPX.Core','CopperSharp.Sdk.Amiga','CopperSharp.Compiler')) { $nativeArguments+=@('--managed-assembly',(Join-Path $inputs "$name.dll")) }
dotnet @nativeArguments
if($LASTEXITCODE -ne 0) { throw 'Native prototype compilation failed' }
$report=Get-Content -LiteralPath (Join-Path $ArtifactRoot 'compatibility.json') -Raw | ConvertFrom-Json
if($report.IsCompatible -ne $true -or !$report.PSObject.Properties['ManagedAllocationSites'] -or $null -eq $report.ManagedAllocationSites -or @($report.ManagedAllocationSites).Count -ne 0) { throw 'Prototype is not allocation-free native code' }
$formatter=Join-Path $ArtifactRoot 'CopperNovaFormat'
$formatterReport=Join-Path $ArtifactRoot 'formatter-compatibility.json'
$formatterArguments=@($cli,(Join-Path $inputs 'CopperNova.Prototype.Amiga.dll'),'--entry','CopperNova.Amiga.NativeNovaFormat::Entry','--platform','amiga','--cpu','68000','--clr','always','--fpu','disabled','--exceptions','yolo','--format','hunk','--runtime','application','--exports','none','--output',$formatter,'--compatibility-report',$formatterReport)
foreach($name in @('CopperNova.Prototype','CopperCPX.Core','CopperSharp.Sdk.Amiga','CopperSharp.Compiler')) { $formatterArguments+=@('--managed-assembly',(Join-Path $inputs "$name.dll")) }
dotnet @formatterArguments
if($LASTEXITCODE -ne 0) { throw 'Native formatter compilation failed' }
$formatReport=Get-Content -LiteralPath $formatterReport -Raw | ConvertFrom-Json
if($formatReport.IsCompatible -ne $true -or @($formatReport.ManagedAllocationSites).Count -ne 0) { throw 'Formatter is not allocation-free managed code' }
if(($before|ConvertTo-Json -Depth 4 -Compress) -ne ((SourceHashes)|ConvertTo-Json -Depth 4 -Compress)) { throw 'Source changed during build' }
@{Sources=$before;HunkSha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash;FormatterSha256=(Get-FileHash -LiteralPath $formatter -Algorithm SHA256).Hash;CompilerSha256=(Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash;ManagedAllocationSites=@($report.ManagedAllocationSites).Count;ManagedInputs=@(Get-ChildItem -LiteralPath $inputs -Filter '*.dll' -File | Sort-Object Name | ForEach-Object { @{Name=$_.Name;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'source-manifest.json')
Write-Output "Built $output and $formatter"
