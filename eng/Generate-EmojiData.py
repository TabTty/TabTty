#!/usr/bin/env python3
"""Generate or verify the terminal's pinned Emoji 17 sequence table."""
import argparse
import hashlib
from pathlib import Path
import re
import struct

EXPECTED = {
    "emoji-sequences.txt": "12cc8267dc33cbd11ed32bcf6fc5dc2ad9c7a77bae1bdfba2f41b1b9b3ead8dd",
    "emoji-zwj-sequences.txt": "5b25441daed2322b068c5e70cda522946a4f0274df864445a1965a92e5fc5cad",
    "emoji-test.txt": "1d8a944f88d7952f7ef7c5167fef3c67995bcae24543949710231b03a201acda",
    "emoji-variation-sequences.txt": "bb3d09ef03f206012c7532dd52dc0a21c9efddba0135ea4cf0d9201b8b9bba7e",
    "PropList.txt": "130dcddcaadaf071008bdfce1e7743e04fdfbc910886f017d9f9ac931d8c64dd",
}
RGI = {
    "Basic_Emoji", "Emoji_Keycap_Sequence", "RGI_Emoji_Flag_Sequence",
    "RGI_Emoji_Tag_Sequence", "RGI_Emoji_Modifier_Sequence", "RGI_Emoji_ZWJ_Sequence",
}

def records(directory, filename):
    data = (directory / filename).read_bytes()
    if hashlib.sha256(data).hexdigest() != EXPECTED[filename]:
        raise ValueError(f"{filename}: pinned source hash mismatch")
    for line in data.decode("utf-8").splitlines():
        line = line.partition("#")[0].strip()
        if line:
            fields = [field.strip() for field in line.split(";")]
            if len(fields) < 2:
                raise ValueError(f"{filename}: invalid record")
            yield fields[0], fields[1]

def scalar(value):
    if not re.fullmatch(r"[0-9A-F]{4,6}", value):
        raise ValueError(f"Invalid scalar: {value}")
    number = int(value, 16)
    if number > 0x10FFFF or 0xD800 <= number <= 0xDFFF:
        raise ValueError(f"Not a scalar: {value}")
    return number

def bounds(value):
    first, separator, last = value.partition("..")
    low, high = scalar(first), scalar(last if separator else first)
    if low > high:
        raise ValueError(f"Inverted range: {value}")
    return low, high

def generate(directory):
    sequences = set()
    for name in EXPECTED:
        for spelling, kind in records(directory, name):
            if name == "PropList.txt":
                continue
            if kind in RGI:
                if ".." in spelling:
                    low, high = bounds(spelling)
                    sequences.update((i,) for i in range(low, high + 1))
                else:
                    sequences.add(tuple(map(scalar, spelling.split())))
            elif kind == "emoji style" or (
                    kind in {"minimally-qualified", "unqualified"} and " " in spelling):
                sequences.add(tuple(map(scalar, spelling.split())))
    variation = sorted(bounds(spelling) for spelling, kind in records(directory, "PropList.txt")
                       if kind == "Variation_Selector")
    ordered = sorted(sequences)
    offsets = [0]
    scalars = []
    for seq in ordered:
        scalars.extend(seq)
        offsets.append(len(scalars))
    words = [len(ordered), len(variation), *[v for pair in variation for v in pair], *offsets, *scalars]
    return b"TTEM170\0" + struct.pack(f"<{len(words)}I", *words)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Directory containing the pinned Unicode files")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    output = Path(__file__).resolve().parent.parent / "src/TabTty.Terminal/src/Emoji17.bin"
    generated = generate(args.source)
    if args.check:
        if output.read_bytes() != generated:
            parser.error("checked-in Emoji17.bin differs from the generated data")
    else:
        output.write_bytes(generated)
    print(hashlib.sha256(generated).hexdigest())

if __name__ == "__main__":
    main()
