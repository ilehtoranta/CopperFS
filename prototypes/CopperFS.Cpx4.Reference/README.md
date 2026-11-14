# CPX4 host reference

This is a separate, incompatible research fork of the CPX3 reference. It writes
magic `0x43505834` and version 4. CPX3 images are not converted; preserve them and
format a separate disposable image. This project is non-packable and is **not the
native handler or the resumable engine**.

The source inventory and original workspace SHA-256 values are in
[source-provenance.json](source-provenance.json). The source is the repository's
own CPX3 implementation, with namespace/format changes and the additions below.
CPX3 itself is retained unchanged. Test fixture copies have the same provenance.

The basic control regions, A/P geometry, object/tree records, journal policy and
credits retain the [CPX3 layout](../CopperFS.Prototype/FORMAT.md), with the CPX4
magic/version substituted in every header. Additions:

- SetProtection writes the object's u32 protection field at 72.
- SetDate writes s32 days/u16 minutes/u16 ticks at 56/60/62. Minutes are below
  1440 and ticks below 3000. Negative day counts remain representable.
- SetComment accepts at most 79 Latin-1 bytes, rejects NUL and stores UTF-8.
  The object pointer at 48 owns one type-18 page. At payload offsets 64/66/68/70
  are u16 count=1, reserved=0, tag=1 (comment), UTF-8 length; bytes begin at 72.
  Header payload length is 8+UTF-8 length. Empty comments release the page.
- Symlink objects have type 3 and size equal to UTF-8 target length (1..768).
  Targets up to 160 bytes use storage 0, inline length at 88 and bytes at 96.
  Longer targets use storage 3 with a type-19 page pointer at 96. That page uses
  u16 length at 64, zero reserved at 66 and bytes at 68; payload length is 4+length.
  Targets may contain slash/colon; the separate entry name follows normal naming
  rules. Symlink targets are not resolved by this host API.
- Attribute and external symlink pages are metadata, not file payload units.
  Queued unlink releases both before removing the object. The ownership checker
  and independent test decoder account for them.

These are low-level model APIs. DOS access checks, automatic timestamps, link
resolution and notification publication still belong to the unfinished packet
layer. See [implementation status](../../docs/cpx4-native-progress.md).
