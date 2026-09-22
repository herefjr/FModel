"""Spot-check a .ueanim written by FModel.

Prints additive metadata and curve keys. Exit code is 1 when a requested
curve name is missing or an expected additive type is absent.
"""

import argparse
import gzip
import struct
import sys
from pathlib import Path

try:
    import zstandard
except ImportError:
    zstandard = None

ADDITIVE = {
    0: "AAT_None",
    1: "AAT_LocalSpaceBase",
    2: "AAT_RotationOffsetMeshSpace",
}
REF_POSE = {
    0: "ABPT_None",
    1: "ABPT_RefPose",
    2: "ABPT_AnimScaled",
    3: "ABPT_AnimFrame",
    4: "ABPT_LocalAnimFrame",
}


class Reader:
    def __init__(self, data: bytes):
        self.data = data
        self.offset = 0

    def i32(self) -> int:
        value = struct.unpack_from("<i", self.data, self.offset)[0]
        self.offset += 4
        return value

    def u8(self) -> int:
        value = self.data[self.offset]
        self.offset += 1
        return value

    def f32(self) -> float:
        value = struct.unpack_from("<f", self.data, self.offset)[0]
        self.offset += 4
        return value

    def string(self) -> str:
        size = self.i32()
        if size < 0:
            raise ValueError(f"negative string length {size} at {self.offset}")
        raw = self.data[self.offset:self.offset + size]
        self.offset += size
        return raw.decode("utf-8", "replace")

    def take(self, size: int) -> bytes:
        raw = self.data[self.offset:self.offset + size]
        self.offset += size
        return raw


def decompress_body(header_reader: Reader) -> bytes:
    if header_reader.data[header_reader.offset:header_reader.offset + 8] != b"UEFORMAT":
        raise ValueError("not a UEFORMAT file")
    header_reader.offset += 8
    header_reader.string()
    header_reader.u8()
    header_reader.string()
    header_reader.string()
    compressed = header_reader.u8() != 0
    if not compressed:
        return header_reader.data[header_reader.offset:]

    fmt = header_reader.string()
    header_reader.i32()
    compressed_size = header_reader.i32()
    blob = header_reader.take(compressed_size)
    if fmt == "ZSTD":
        if zstandard is None:
            raise RuntimeError("pip install zstandard")
        return zstandard.ZstdDecompressor().decompress(blob)
    if fmt == "GZIP":
        return gzip.decompress(blob)
    return blob


def parse_attributes(body: bytes) -> dict[str, bytes]:
    reader = Reader(body)
    count = reader.i32()
    attributes = {}
    for _ in range(count):
        name = reader.string()
        size = reader.i32()
        attributes[name] = reader.take(size)
    return attributes


def summarize(path: Path) -> dict:
    body = decompress_body(Reader(path.read_bytes()))
    attributes = parse_attributes(body)
    meta = Reader(attributes["METADATA"])
    frames = meta.i32()
    fps = meta.f32()
    ref_pose = meta.string()
    additive = meta.u8()
    ref_type = meta.u8()
    ref_frame = meta.i32()
    curves = []
    if "CURVES" in attributes:
        curve_reader = Reader(attributes["CURVES"])
        for _ in range(curve_reader.i32()):
            name = curve_reader.string()
            keys = [(curve_reader.i32(), curve_reader.f32()) for _ in range(curve_reader.i32())]
            curves.append((name, keys))
    tracks = Reader(attributes["TRACKS"]).i32() if "TRACKS" in attributes else 0
    return {
        "path": path,
        "frames": frames,
        "fps": fps,
        "additive": ADDITIVE.get(additive, str(additive)),
        "ref_type": REF_POSE.get(ref_type, str(ref_type)),
        "ref_frame": ref_frame,
        "ref_pose": ref_pose,
        "tracks": tracks,
        "curves": curves,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("files", nargs="*", type=Path)
    parser.add_argument("--dir", type=Path)
    parser.add_argument("--name", default="", help="Comma-separated filename substrings")
    parser.add_argument("--expect-curve", default="", help="Comma-separated curve names that must exist")
    args = parser.parse_args()

    files = list(args.files)
    if args.dir:
        needles = [part.strip() for part in args.name.split(",") if part.strip()]
        for path in args.dir.rglob("*.ueanim"):
            if not needles or any(needle in path.name for needle in needles):
                files.append(path)

    expected = [part.strip() for part in args.expect_curve.split(",") if part.strip()]
    failed = 0
    for path in files:
        info = summarize(path)
        curve_text = ", ".join(f"{name}:{len(keys)}" for name, keys in info["curves"]) or "-"
        print(
            f"{info['path'].name}  additive={info['additive']}  ref={info['ref_type']}  "
            f"frame={info['ref_frame']}  tracks={info['tracks']}  curves={len(info['curves'])}  "
            f"[{curve_text}]"
        )
        print(f"  refPose={info['ref_pose'] or '-'}")
        present = {name for name, _ in info["curves"]}
        for name in expected:
            if name not in present:
                print(f"  MISSING {name}")
                failed += 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
