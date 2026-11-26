# CPX4 V26 experimental source

This text-only patch preserves the latest measured CPU candidate against the
default V7 source in the same commit. It is not promoted.

Apply it to a clean checkout of this checkpoint, from the repository root:

```powershell
git apply --check experiments/cpx4-v26/changes.patch
git apply experiments/cpx4-v26/changes.patch
pwsh -NoProfile -File build/Build-Cpx4Handler.ps1
dotnet test tests/CopperCPX.Core.Tests -c Release
```

Use the benchmark tool's documented local inputs to repeat both comparisons.
For native coverage run `dotnet test tests/CopperCPX.InstructionTests -c Release`;
full-ROM tests additionally require the documented licensed ROM and opt-in.
Seven compiled stack/extended-core cases and 484 host cases passed for the
measured candidate. Its full native suite and full-ROM gate remain outstanding.

The patch contains V19's private metadata-helper expansion, stable arena loads,
grouped CRC scan, split create planning and nonrecursive quick transport, followed
by V26's private static CRC kernel. It keeps the six-member memory interface and
contains no diagnostic CRC access hook. It does not include the rejected optional
default-interface experiment, metadata-view candidates or the failed 68020 build.

`source-hashes.json` identifies the base and resulting source using UTF-8/LF
hashes. Candidate trailing whitespace is removed; executable source is unchanged.
Unchanged core files are hashed too. Other native files and WideMath32.cs are
inherited from the default checkpoint. The original candidate and its generated
binaries remain under ignored artifacts; generated output is not part of this
snapshot. Rebuilding depends on the existing CopperSharp68k sibling toolchain;
source preservation does not promise byte-identical compiler output.
