param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$VersionTag
)
$ErrorActionPreference = 'Stop'

# CopperSharp emits a CODE hunk, optionally followed by a BSS hunk. Append
# the tag inside CODE so both the file and the loaded segment contain it.
$file = (Get-Item -LiteralPath $Path).FullName
$image = [IO.File]::ReadAllBytes($file)
function Read-HunkWord([int]$offset) {
    $bytes = [byte[]]::new(4)
    [Array]::Copy($image, $offset, $bytes, 0, 4)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($bytes) }
    [BitConverter]::ToUInt32($bytes, 0)
}
# Workbench 3.1 Version reads numeric tag years as years since 1900 (126 for 2026).
if ($VersionTag -cnotmatch '^\$VER: [\x20-\x7e]+$') { throw 'Expected an ASCII Amiga version tag.' }
if ($image.Length -lt 36 -or (Read-HunkWord 0) -ne 1011 -or (Read-HunkWord 4) -ne 0) {
    throw "Invalid CopperSharp HUNK header in $Path"
}
$count = Read-HunkWord 8
if ($count -notin @(1, 2) -or (Read-HunkWord 12) -ne 0 -or (Read-HunkWord 16) -ne ($count - 1)) {
    throw "Unexpected CopperSharp HUNK table in $Path"
}
$code = 20 + 4 * $count
$words = Read-HunkWord ($code + 4)
$end = $code + 8 + 4L * $words
if ((Read-HunkWord $code) -ne 1001 -or (Read-HunkWord 20) -ne $words -or $end -gt ($image.Length - 4)) {
    throw "Invalid CODE hunk in $Path"
}
if ([Text.Encoding]::ASCII.GetString($image, $code + 8, [int](4 * $words)).Contains('$VER:')) {
    throw "CODE hunk already contains a version tag in $Path"
}
$tag = [Text.Encoding]::ASCII.GetBytes([char]0 + $VersionTag + [char]0)
$extraWords = [int][Math]::Ceiling($tag.Length / 4.0)
$extra = 4 * $extraWords
$stamped = [byte[]]::new($image.Length + $extra)
[Array]::Copy($image, $stamped, $end)
[Array]::Copy($tag, 0, $stamped, $end, $tag.Length)
[Array]::Copy($image, $end, $stamped, $end + $extra, $image.Length - $end)
$size = [BitConverter]::GetBytes([uint32]($words + $extraWords))
if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($size) }
$size.CopyTo($stamped, 20)
$size.CopyTo($stamped, $code + 4)
[IO.File]::WriteAllBytes($file, $stamped)
