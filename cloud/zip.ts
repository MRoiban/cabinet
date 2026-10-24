import { inflateRawSync } from "node:zlib";
import { invalid } from "./validation";

// Minimal read-only ZIP inspection. The server never extracts saves; it only reads the
// central directory and the small format-2 manifest entry.
export interface CentralEntry { name: string; method: number; flags: number; crc: number; compressedSize: number; size: number; localOffset: number }

const u16 = (b: Uint8Array, i: number) => b[i]! | (b[i + 1]! << 8);
const u32 = (b: Uint8Array, i: number) => (b[i]! | (b[i + 1]! << 8) | (b[i + 2]! << 16) | (b[i + 3]! << 24)) >>> 0;

/** Parses the end-of-central-directory record and every central directory entry. ZIP64 is refused. */
export function centralDirectory(bytes: Uint8Array): CentralEntry[] {
  if (bytes.length < 22) invalid("Not a ZIP archive.");
  let eocd = -1;
  for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 22 - 0xffff); i--) {
    if (u32(bytes, i) === 0x06054b50 && i + 22 + u16(bytes, i + 20) === bytes.length) { eocd = i; break; }
  }
  if (eocd < 0) invalid("Not a ZIP archive.");
  if (eocd >= 20 && u32(bytes, eocd - 20) === 0x07064b50) invalid("ZIP64 archives are not supported.");
  const disk = u16(bytes, eocd + 4), cdDisk = u16(bytes, eocd + 6);
  const count = u16(bytes, eocd + 10), total = u16(bytes, eocd + 8);
  const cdSize = u32(bytes, eocd + 12), cdOffset = u32(bytes, eocd + 16);
  if (count === 0xffff || cdSize === 0xffffffff || cdOffset === 0xffffffff) invalid("ZIP64 archives are not supported.");
  if (disk !== 0 || cdDisk !== 0 || total !== count) invalid("Multi-disk archives are not supported.");
  if (cdOffset + cdSize > eocd) invalid("Corrupt ZIP central directory.");
  const entries: CentralEntry[] = [];
  let p = cdOffset;
  for (let n = 0; n < count; n++) {
    if (p + 46 > cdOffset + cdSize || u32(bytes, p) !== 0x02014b50) invalid("Corrupt ZIP central directory.");
    const nameLength = u16(bytes, p + 28), extra = u16(bytes, p + 30), comment = u16(bytes, p + 32);
    if (p + 46 + nameLength > cdOffset + cdSize) invalid("Corrupt ZIP central directory.");
    const entry = {
      flags: u16(bytes, p + 8), method: u16(bytes, p + 10), crc: u32(bytes, p + 16),
      compressedSize: u32(bytes, p + 20), size: u32(bytes, p + 24), localOffset: u32(bytes, p + 42),
      name: new TextDecoder("utf-8", { fatal: false }).decode(bytes.subarray(p + 46, p + 46 + nameLength)),
    };
    if (entry.compressedSize === 0xffffffff || entry.size === 0xffffffff || entry.localOffset === 0xffffffff) invalid("ZIP64 archives are not supported.");
    entries.push(entry);
    p += 46 + nameLength + extra + comment;
  }
  if (p !== cdOffset + cdSize) invalid("Corrupt ZIP central directory.");
  return entries;
}

/** Number of non-directory entries; a legacy save with none holds no backup content. */
export function fileEntryCount(bytes: Uint8Array): number {
  return centralDirectory(bytes).filter(entry => !entry.name.endsWith("/") && !entry.name.endsWith("\\")).length;
}

/** Reads an entry's bytes (stored or deflated), bounded by `limit`, and checks its CRC-32. */
export function readEntry(bytes: Uint8Array, entry: CentralEntry, limit: number): Uint8Array {
  if (entry.flags & 0x1) invalid("Encrypted ZIP entries are not supported.");
  if (entry.size > limit || entry.compressedSize > limit) invalid("ZIP entry is too large.");
  const p = entry.localOffset;
  if (p + 30 > bytes.length || u32(bytes, p) !== 0x04034b50) invalid("Corrupt ZIP local header.");
  const start = p + 30 + u16(bytes, p + 26) + u16(bytes, p + 28);
  if (start + entry.compressedSize > bytes.length) invalid("Corrupt ZIP entry.");
  const raw = bytes.subarray(start, start + entry.compressedSize);
  let data: Uint8Array;
  if (entry.method === 0) data = raw;
  else if (entry.method === 8) {
    try { data = inflateRawSync(raw, { maxOutputLength: limit }); } catch { invalid("Corrupt ZIP entry."); }
  } else invalid("Unsupported ZIP compression method.");
  if (data.length !== entry.size || Bun.hash.crc32(data) !== entry.crc) invalid("ZIP entry CRC or size mismatch.");
  return data;
}
