param([string]$Destination = '')

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lockRoot = Join-Path $repo 'qualification/dependencies'
$lock = Get-Content (Join-Path $lockRoot 'lock.json') -Raw | ConvertFrom-Json
if ((dotnet --version).Trim() -ne $lock.dotnetSdk) { throw "Qualification requires .NET SDK $($lock.dotnetSdk)." }
if (!$Destination) { $Destination = Join-Path $repo 'artifacts/qualification-dependencies' }
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

foreach ($entry in $lock.repositories.PSObject.Properties) {
    $name = $entry.Name; $spec = $entry.Value; $path = Join-Path $Destination $name
    if (!(Test-Path -LiteralPath $path)) {
        git clone --no-checkout -- $spec.url $path
        if ($LASTEXITCODE -ne 0) { throw "Clone failed: $name" }
        git -C $path checkout --detach $spec.revision
        if ($LASTEXITCODE -ne 0) { throw "Checkout failed: $name" }
    }
    if (!(Test-Path -LiteralPath (Join-Path $path '.git'))) { throw "Not a Git checkout: $path" }
    $head = (git -C $path rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ne $spec.revision) { throw "Unexpected revision: $name" }
    $unstaged = @(git -C $path diff --name-only)
    $untracked = @(git -C $path ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or $unstaged.Count -or $untracked.Count) { throw "Unexpected worktree changes: $name" }
    $staged = @(git -C $path diff --cached --name-only)
    if ($spec.patch) {
        $patch = Join-Path $lockRoot $spec.patch
        if ((Get-FileHash -LiteralPath $patch).Hash -ne $spec.patchSha256) { throw 'Qualification patch hash mismatch.' }
        if (!$staged.Count) {
            git -C $path apply --index $patch
            if ($LASTEXITCODE -ne 0) { throw "Patch failed: $name" }
        }
        $tree = (git -C $path write-tree).Trim()
        if ($LASTEXITCODE -ne 0 -or $tree -ne $spec.patchedTree) { throw "Unexpected patched tree: $name" }
    } elseif ($staged.Count) { throw "Unexpected staged changes: $name" }
    Write-Output "$name $head verified at $path"
}
