"""Derive a correctness-only 512K/4K-stack/shutdown gate from MixedProbe.

Benchmarks continue using the unchanged frozen guest HUNKs. This separate guest
uses Public memory, a 4096-byte handler stack, startup markers, and an unflushed
append followed by ACTION_DIE. Source provenance: repository A86 MixedProbe.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
source = (root / 'tools/CopperCPX.Bench/MixedProbe.cs').read_text(encoding='utf-8-sig')
source = '// Correctness-only derivative of A86 MixedProbe; see prepare_nova_gate.py.\n' + source
source = source.replace('public static class Probe', 'public static class NovaGateProbe')
source = source.replace(' | Exec.MemoryFlags.Fast', '')
source = source.replace('DosLayout.DeviceNode.StackSize, 32768', 'DosLayout.DeviceNode.StackSize, 4096')
source = source.replace('        port = DOS.DeviceProc("CPX0:");', '        Report(224);\n        port = DOS.DeviceProc("CPX0:");', 1)
source = source.replace('        if (port.IsNull) return Fail(6);', '        if (port.IsNull) return Fail(6);\n        Report(225);', 1)
source = source.replace('''        Report(223);
        Report(210);''', '''        Report(223);
        file = DOS.Open("CPX0:Bench/Renamed.bin", DOS.FileMode.ReadWrite);
        if (!file.HasValue) return Fail(30);
        if (DOS.Seek(file.Value, 0, 1) != 0) return Fail(31);
        for (int i = 0; i < 1024; i++) APTR.WriteUInt8(data, i, 0x5B);
        if (DOS.Write(file.Value, data, 1024) != 1024 || DOS.Close(file.Value) == 0) return Fail(32);
        Report(232);
        if (DOS.DoPkt(port, 5, 0, 0, 0, 0, 0) == 0) return Fail(33);
        Report(233);
        Report(210);''')
(root / 'tools/CopperNova.Qualification/NovaGateProbe.cs').write_text(source, encoding='utf-8')
