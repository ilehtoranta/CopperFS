param(
    [Parameter(Mandatory=$true)][string]$NativeArtifactRoot,
    [Parameter(Mandatory=$true)][string]$ArtifactRoot,
    [string]$OriginalFfsHandler='',
    [string]$OriginalSfs2Handler='',
    [string]$A1200Rom=$env:COPPERSCREEN_UPGRADE_A1200_ROM,
    [string]$LegacyRom=$env:COPPERSHARP_KICKSTART31_ROM,
    [ValidateSet('3.0','3.1','Both')][string]$A1200Kickstart='3.0',
    [string]$A1200Rom31=$env:COPPERSCREEN_UPGRADE_A1200_ROM31,
    [ValidateSet('All','Ffs','Sfs2','Sfs2CSUN','Sfs2TRST')][string]$FileSystemProfile='All',
    [ValidateSet('All','ZeroLatency','TimedCopperHdf','A1200Ide')][string]$Backend='All',
    [ValidateSet('All','Legacy','A1200Stock','A1200Fast')][string]$MachineProfile='All',
    [ValidateSet('Both','Hdd','Cf')][string]$DriveProfile='Both',
    [ValidateSet('Both','Cached','Bypass')][string]$PayloadCachePolicy='Both',
    [string]$TimingProfiles='', [string]$TestAssembly='', [switch]$Smoke,
    [ValidateRange(1,16)][int]$Workers=4, [string]$ReferenceProvenance=''
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$forward=@('run','--project',(Join-Path $repo 'tools/CopperFS.StorageBench/CopperFS.StorageBench.csproj'),'-c','Release','--','matrix','--RepositoryRoot',$repo)
foreach($entry in $PSBoundParameters.GetEnumerator()) {
    if($entry.Value -is [switch]) {
        if($entry.Value.IsPresent) { $forward+='--'+$entry.Key }
    } elseif([string]$entry.Value -ne '') {
        $forward+='--'+$entry.Key
        $forward+=[string]$entry.Value
    }
}
& dotnet @forward
if($LASTEXITCODE -ne 0) { throw 'Storage matrix failed; partial results are not acceptance.' }
