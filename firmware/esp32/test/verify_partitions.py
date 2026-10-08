"""Validate the generated default partition table and merged image, without flashing."""

from pathlib import Path
import struct

root = Path('.pio/build/esp32dev')
table = (root / 'partitions.bin').read_bytes()
partitions = {}
for position in range(0, len(table), 32):
    magic, kind, subtype, offset, size, name, flags = struct.unpack_from('<HBBII16sI', table, position)
    if magic != 0x50AA:
        break
    partitions[name.rstrip(b'\0').decode('ascii')] = (kind, subtype, offset, size)
assert partitions == {
    'nvs': (1, 2, 0x9000, 0x5000),
    'otadata': (1, 0, 0xE000, 0x2000),
    'factory': (0, 0, 0x10000, 0x300000),
    'journal': (1, 2, 0x310000, 0x20000),
}, partitions
regions = sorted((value[2], value[2] + value[3]) for value in partitions.values())
assert all(end <= regions[i + 1][0] for i, (_, end) in enumerate(regions[:-1]))
assert regions[-1][1] <= 4 * 1024 * 1024
merged = (root / 'firmware.factory.bin').read_bytes()
for name, offset in [('bootloader.bin', 0x1000), ('partitions.bin', 0x8000), ('firmware.bin', 0x10000)]:
    component = (root / name).read_bytes()
    assert merged[offset:offset + len(component)] == component, name
assert len(merged) <= partitions['journal'][2], 'Factory image overlaps the durable journal'
print('PASS partition layout, merged image offsets and separate journal region')
