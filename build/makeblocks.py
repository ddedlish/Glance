#!/usr/bin/env python3
"""Writes <file>.blocks: per-64 KiB block rolling (rsync) + strong (SHA-256 prefix) checksums for delta updates.
Format (little endian): b"GLBM1" | u32 blockSize | u64 fileLength | per block: u32 weak, 8 bytes sha256 prefix."""
import hashlib, operator, struct, sys

BS = 64 * 1024

def weak(data: bytes) -> int:
    n = len(data)
    a = sum(data)
    b = sum(map(operator.mul, range(n, 0, -1), data))
    return (a & 0xFFFF) | ((b & 0xFFFF) << 16)

def main(path: str) -> None:
    with open(path, "rb") as f:
        data = f.read()
    out = bytearray(b"GLBM1")
    out += struct.pack("<IQ", BS, len(data))
    for off in range(0, len(data), BS):
        blk = data[off:off + BS]
        out += struct.pack("<I", weak(blk))
        out += hashlib.sha256(blk).digest()[:8]
    with open(path + ".blocks", "wb") as f:
        f.write(out)
    print(f"{path}.blocks: {(len(data) + BS - 1) // BS} blocks")

if __name__ == "__main__":
    for p in sys.argv[1:]:
        main(p)
