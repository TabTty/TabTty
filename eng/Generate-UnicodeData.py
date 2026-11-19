#!/usr/bin/env python3
"""Reproduce or verify the embedded Unicode 17.0.0 property tables."""

import argparse
import hashlib
from pathlib import Path
import re
import struct

INPUTS = {
    "EastAsianWidth.txt": "ea7ce50f3444a050333448dffef1cadd9325af55cbb764b4a2280faf52170a33",
    "GraphemeBreakProperty.txt": "d6b51d1d2ae5c33b451b7ed994b48f1f4dc62b2272a5831e7fd418514a6bae89",
    "DerivedCoreProperties.txt": "24c7fed1195c482faaefd5c1e7eb821c5ee1fb6de07ecdbaa64b56a99da22c08",
    "DerivedGeneralCategory.txt": "d62e5bab70ca74f099343f71224fa051cb1fdd61a1ab45c0488c44cfc0b6102e",
    "emoji-data.txt": "2cb2bb9455cda83e8481541ecf5b6dfda66a3bb89efa3fa7c5297eccf607b72b",
}
GCB = {name: index for index, name in enumerate(
    ("Other", "CR", "LF", "Control", "Extend", "ZWJ", "Regional_Indicator",
     "Prepend", "SpacingMark", "L", "V", "T", "LV", "LVT")
)}


def records(directory, name, default=None):
    data = (directory / name).read_bytes()
    if hashlib.sha256(data).hexdigest() != INPUTS[name]:
        raise ValueError(f"{name}: pinned source hash mismatch")
    source = data.decode("utf-8")
    if default is not None and f"# @missing: 0000..10FFFF; {default}" not in source:
        raise ValueError(f"{name}: unexpected default")
    for line in source.splitlines():
        value = line.partition("#")[0].strip()
        if not value:
            continue
        span, *fields = (part.strip() for part in value.split(";"))
        parts = span.split("..")
        if not fields or len(parts) not in (1, 2) or any(not re.fullmatch(r"[0-9A-F]{4,6}", part) for part in parts):
            raise ValueError(f"{name}: malformed property record")
        first, last = int(parts[0], 16), int(parts[-1], 16)
        if not 0 <= first <= last <= 0x10FFFF:
            raise ValueError(f"{name}: out-of-range property")
        yield first, last, tuple(fields)


def pack(ranges):
    ordered = sorted(ranges)
    result = bytearray(struct.pack("<I", len(ordered)))
    last_end = -1
    for first, last, value in ordered:
        if first <= last_end or value == 0:
            raise ValueError("overlapping property ranges or explicit default")
        result.extend(struct.pack("<IIB", first, last, value))
        last_end = last
    return result


def generate(directory):
    def table(source, values, default=None):
        return pack((first, last, values[record]) for first, last, record in records(directory, source, default)
                    if record in values and values[record] != 0)

    result = bytearray()
    result.extend(table("EastAsianWidth.txt", {("A",): 1, ("W",): 2, ("F",): 2}, "N"))
    result.extend(table("GraphemeBreakProperty.txt", {(key,): value for key, value in GCB.items()}, "Other"))
    result.extend(table("DerivedCoreProperties.txt",
                        {("InCB", "Consonant"): 1, ("InCB", "Extend"): 2, ("InCB", "Linker"): 3},
                        "InCB; None"))
    result.extend(table("DerivedGeneralCategory.txt", {("Mn",): 1, ("Mc",): 1, ("Me",): 1}))
    for property_name in ("Emoji", "Emoji_Presentation", "Extended_Pictographic", "Emoji_Modifier_Base", "Emoji_Modifier"):
        result.extend(table("emoji-data.txt", {(property_name,): 1}))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Directory containing the pinned Unicode files")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    output = Path(__file__).resolve().parent.parent / "src/TabTty.Terminal/src/Unicode17.bin"
    generated = generate(args.source)
    if args.check:
        if output.read_bytes() != generated:
            parser.error("checked-in Unicode17.bin differs from the generated data")
    else:
        output.write_bytes(generated)
    print(hashlib.sha256(generated).hexdigest())


if __name__ == "__main__":
    main()
