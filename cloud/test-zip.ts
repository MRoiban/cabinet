// Test-only ZIP writer: stored or deflated entries, optional raw overrides for malformed cases.
import { deflateRawSync } from "node:zlib";

export interface TestEntry { name: string; data: string | Uint8Array; deflate?: boolean }
export const sha = (data: string | Uint8Array) => new Bun.CryptoHasher("sha256").update(data).digest("hex");
const bytesOf = (data: string | Uint8Array) => typeof data === "string" ? new TextEncoder().encode(data) : data;

export function zipOf(entries: TestEntry[], options: { eocdCount?: number } = {}): Uint8Array<ArrayBuffer> {
  const locals: Uint8Array[] = [], centrals: Uint8Array[] = [];
  let offset = 0;
  for (const entry of entries) {
    const name = new TextEncoder().encode(entry.name), data = bytesOf(entry.data);
    const packed = entry.deflate ? deflateRawSync(data) : data, crc = Bun.hash.crc32(data);
    const local = Buffer.alloc(30 + name.length);
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(0x800, 6);
    local.writeUInt16LE(entry.deflate ? 8 : 0, 8); local.writeUInt32LE(0x00210000, 10);
    local.writeUInt32LE(crc, 14); local.writeUInt32LE(packed.length, 18); local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(name.length, 26); local.set(name, 30);
    const central = Buffer.alloc(46 + name.length);
    central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x800, 8); central.writeUInt16LE(entry.deflate ? 8 : 0, 10); central.writeUInt32LE(0x00210000, 12);
    central.writeUInt32LE(crc, 16); central.writeUInt32LE(packed.length, 20); central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(name.length, 28); central.writeUInt32LE(offset, 42); central.set(name, 46);
    locals.push(local, packed); centrals.push(central);
    offset += local.length + packed.length;
  }
  const cd = Buffer.concat(centrals), eocd = Buffer.alloc(22), count = options.eocdCount ?? entries.length;
  eocd.writeUInt32LE(0x06054b50, 0); eocd.writeUInt16LE(count, 8); eocd.writeUInt16LE(count, 10);
  eocd.writeUInt32LE(cd.length, 12); eocd.writeUInt32LE(offset, 16);
  return new Uint8Array(Buffer.concat([...locals, cd, eocd]));
}

export interface ArchiveFile { root: number; path: string; data: string }
export interface Archive { zip: Uint8Array<ArrayBuffer>; sha256: string; contentHash: string; files: number; bytes: number }
/** A format-2 archive per docs/save-sync.md; `tamper` edits the manifest object before writing. */
export function archiveOf(appid: string, files: ArchiveFile[], options: {
  roots?: { id: string; source: string }[]; tamper?: (manifest: Record<string, any>) => void;
  entries?: (entries: TestEntry[]) => TestEntry[]; deflateManifest?: boolean; eocdCount?: number;
} = {}): Archive {
  const roots = options.roots ?? [{ id: "manifest:<winLocalAppDataLow>/Louqou/TravellersRest/GameSaves", source: "manifest" }];
  const list = files.map(f => ({ root: f.root, path: f.path, size: bytesOf(f.data).length, sha256: sha(f.data) }));
  const contentHash = sha(list.map(f => `${roots[f.root]?.id}\t${f.path}\t${f.size}\t${f.sha256}\n`).join(""));
  const manifest: Record<string, any> = { format: "cabinet-save", version: 2, gameId: `steam:${appid}`, contentHash, roots, files: list };
  options.tamper?.(manifest);
  let entries: TestEntry[] = [{ name: "cabinet-save.json", data: JSON.stringify(manifest), deflate: options.deflateManifest ?? false },
    ...files.map(f => ({ name: `roots/${f.root}/${f.path}`, data: f.data, deflate: true }))];
  if (options.entries) entries = options.entries(entries);
  const zip = zipOf(entries, options.eocdCount === undefined ? {} : { eocdCount: options.eocdCount });
  return { zip, sha256: sha(zip), contentHash: manifest.contentHash, files: list.length, bytes: list.reduce((a, f) => a + f.size, 0) };
}
