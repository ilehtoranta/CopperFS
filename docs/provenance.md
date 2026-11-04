# Provenance

The implementation and tests in this repository are MIT-licensed original
work. No original Smart File System C files, ROM images, disk images or game
media are included.

SFS2 read-only layout verification used pinned WinUAE and Aaru readers as
references and a disposable volume formatted/populated by the original
SmartFilesystem 1.279 handler. The original-handler image also established
SFS2's different object-node branch-pointer shift. No third-party source or
binary fixture is included. References, hashes, reproduction commands and
validation boundaries are in [SFS2 read-only support](sfs2-read-only.md).

The [SFS2 writer](sfs2-writes.md) specializes this repository's original classic
mutation algorithms for version 4. Recovery and clean handoffs were checked
against the original 1.279 binary in the emulator; no external implementation
source is included. The expanded independent checker uses separate code and
the verified format facts, including native reserved nodes and bitmap placement.

The on-disk layout work used the original Smart File System structures as
references, especially `blockstructure.h`, `objects.h`, `nodes.h`,
`btreenodes.h`, `bitmap.h`, `adminspaces.h`, `transactions.h` and
`support.c` in the
[AROS SFS tree](https://github.com/aros-development-team/AROS/tree/master/rom/filesys/SFS/FS).
The [original Smart File System source project](https://sourceforge.net/projects/smartfilesystem/)
publishes its C implementation under LGPLv2. Its name and license do not
describe this repository's ownership or license.

Compatibility checks also used independently formatted SFS images and
[amidisk](https://github.com/alfishe/amidisk) for structural checks.
Contributors should state the origin and license of any imported source or
fixture before adding it.

`tools/CopperSFS.Check` is a separate, original implementation of the subset
documented in [reliability testing](reliability.md). Its layout references are
the same published AROS SFS structures above; no C implementation was copied.
The reliability fixtures are generated locally from this repository's
synthetic traversal fixture, with fully accounted admin ranges, directory
hashes and relocated data. They are not independently formatted SFS media.
