// Minimal .zip reader and writer for the in-browser edition. No dependencies: inflating and deflating use the
// browser's own DecompressionStream / CompressionStream ("deflate-raw").
//  - Reading never loads the whole archive: the central directory is read from the end of the file and each
//    entry is sliced out of the File on demand.
//  - Writing collects the output as Blob parts, which the browser may keep on disk rather than in memory.
//    Archives are capped at 4 GB / 65,535 entries (no ZIP64 output); the page checks that before it starts.

const SIG_EOCD = 0x06054b50;
const SIG_ZIP64_LOCATOR = 0x07064b50;
const SIG_ZIP64_EOCD = 0x06064b50;
const SIG_CENTRAL = 0x02014b50;
const SIG_LOCAL = 0x04034b50;

export const ZIP_LIMIT_BYTES = 0xFFFFFFFF - (64 << 20);   // keep clear of the 32-bit offset limit
export const ZIP_LIMIT_ENTRIES = 0xFFFF;

async function bytesOf(blob, start, end) {
  return new Uint8Array(await blob.slice(start, end).arrayBuffer());
}

const u16 = (b, o) => b[o] | (b[o + 1] << 8);
const u32 = (b, o) => (b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)) >>> 0;
const u64 = (b, o) => u32(b, o) + u32(b, o + 4) * 0x100000000;

/** Lists the entries of a .zip File/Blob: [{ name, isDirectory, method, crc, compressedSize, uncompressedSize, localOffset }]. */
export async function readZipDirectory(file) {
  const tailLength = Math.min(file.size, 22 + 0xFFFF);
  const tail = await bytesOf(file, file.size - tailLength, file.size);
  let eocd = -1;
  for (let i = tail.length - 22; i >= 0; i--) {
    if (u32(tail, i) === SIG_EOCD) { eocd = i; break; }
  }
  if (eocd < 0) throw new Error(`${file.name || "This file"} is not a .zip archive (or it is damaged).`);
  let count = u16(tail, eocd + 10);
  let cdSize = u32(tail, eocd + 12);
  let cdOffset = u32(tail, eocd + 16);
  if (count === 0xFFFF || cdSize === 0xFFFFFFFF || cdOffset === 0xFFFFFFFF) {
    const locatorAt = eocd - 20;
    if (locatorAt < 0 || u32(tail, locatorAt) !== SIG_ZIP64_LOCATOR) throw new Error("This .zip archive is damaged (ZIP64 locator missing).");
    const zip64At = u64(tail, locatorAt + 8);
    const z = await bytesOf(file, zip64At, zip64At + 56);
    if (u32(z, 0) !== SIG_ZIP64_EOCD) throw new Error("This .zip archive is damaged (ZIP64 record missing).");
    count = u64(z, 32);
    cdSize = u64(z, 40);
    cdOffset = u64(z, 48);
  }
  const cd = await bytesOf(file, cdOffset, cdOffset + cdSize);
  const utf8 = new TextDecoder("utf-8");
  const entries = [];
  let p = 0;
  for (let i = 0; i < count; i++) {
    if (u32(cd, p) !== SIG_CENTRAL) throw new Error("This .zip archive is damaged (bad central directory).");
    const flags = u16(cd, p + 8);
    const method = u16(cd, p + 10);
    const crc = u32(cd, p + 16);
    let compressedSize = u32(cd, p + 20);
    let uncompressedSize = u32(cd, p + 24);
    const nameLength = u16(cd, p + 28), extraLength = u16(cd, p + 30), commentLength = u16(cd, p + 32);
    let localOffset = u32(cd, p + 42);
    const name = utf8.decode(cd.subarray(p + 46, p + 46 + nameLength)).replace(/\\/g, "/");
    // ZIP64 extra field: the 64-bit values appear only for the fields that are saturated, in this order.
    let x = p + 46 + nameLength;
    const extraEnd = x + extraLength;
    while (x + 4 <= extraEnd) {
      const id = u16(cd, x), size = u16(cd, x + 2);
      if (id === 0x0001) {
        let q = x + 4;
        if (uncompressedSize === 0xFFFFFFFF) { uncompressedSize = u64(cd, q); q += 8; }
        if (compressedSize === 0xFFFFFFFF) { compressedSize = u64(cd, q); q += 8; }
        if (localOffset === 0xFFFFFFFF) { localOffset = u64(cd, q); q += 8; }
      }
      x += 4 + size;
    }
    if (flags & 1) throw new Error("This .zip archive is password-protected. Extract it first, then choose the folder.");
    if (!isSafeName(name)) throw new Error(`Archive entry '${name}' has an unsafe path.`);
    entries.push({ name, isDirectory: name.endsWith("/"), method, crc, compressedSize, uncompressedSize, localOffset, flags });
    p = extraEnd + commentLength;
  }
  return entries;
}

/** Same rule as the server: no absolute paths and no ".." segments. */
function isSafeName(name) {
  return !name.startsWith("/") && !/^[A-Za-z]:/.test(name) && !name.split("/").includes("..");
}

async function dataRange(file, entry) {
  const header = await bytesOf(file, entry.localOffset, entry.localOffset + 30);
  if (u32(header, 0) !== SIG_LOCAL) throw new Error(`Archive entry '${entry.name}' is damaged.`);
  const start = entry.localOffset + 30 + u16(header, 26) + u16(header, 28);
  return [start, start + entry.compressedSize];
}

/** Returns the uncompressed bytes of one entry. */
export async function readZipEntry(file, entry) {
  const [start, end] = await dataRange(file, entry);
  const compressed = file.slice(start, end);
  if (entry.method === 0) return new Uint8Array(await compressed.arrayBuffer());
  if (entry.method !== 8) throw new Error(`Archive entry '${entry.name}' uses an unsupported compression method (${entry.method}).`);
  const stream = compressed.stream().pipeThrough(new DecompressionStream("deflate-raw"));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

// ---- Writer -------------------------------------------------------------------------------------------

const CRC_TABLE = (() => {
  const table = new Uint32Array(256 * 8);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  for (let n = 0; n < 256; n++) {
    for (let t = 1; t < 8; t++) table[t * 256 + n] = (table[(t - 1) * 256 + n] >>> 8) ^ table[table[(t - 1) * 256 + n] & 0xFF];
  }
  return table;
})();

/** CRC-32 (slicing-by-8). */
export function crc32(bytes) {
  const t = CRC_TABLE;
  let crc = 0xFFFFFFFF, i = 0;
  const n8 = bytes.length & ~7;
  for (; i < n8; i += 8) {
    const a = (bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16) | (bytes[i + 3] << 24)) ^ crc;
    const b = bytes[i + 4] | (bytes[i + 5] << 8) | (bytes[i + 6] << 16) | (bytes[i + 7] << 24);
    crc = t[1792 + (a & 0xFF)] ^ t[1536 + ((a >>> 8) & 0xFF)] ^ t[1280 + ((a >>> 16) & 0xFF)] ^ t[1024 + (a >>> 24)]
        ^ t[768 + (b & 0xFF)] ^ t[512 + ((b >>> 8) & 0xFF)] ^ t[256 + ((b >>> 16) & 0xFF)] ^ t[b >>> 24];
  }
  for (; i < bytes.length; i++) crc = t[(crc ^ bytes[i]) & 0xFF] ^ (crc >>> 8);
  return (crc ^ 0xFFFFFFFF) >>> 0;
}

function dosDateTime(date) {
  const time = (date.getHours() << 11) | (date.getMinutes() << 5) | (date.getSeconds() >> 1);
  const day = ((date.getFullYear() - 1980) << 9) | ((date.getMonth() + 1) << 5) | date.getDate();
  return { time, day };
}

export class ZipWriter {
  constructor() {
    this.parts = [];
    this.central = [];
    this.offset = 0;
    this.stamp = dosDateTime(new Date());
    this.encoder = new TextEncoder();
  }

  /** Adds a file from memory, deflated (or stored when deflating does not help). */
  async add(name, bytes) {
    const crc = crc32(bytes);
    const deflated = await new Response(new Blob([bytes]).stream().pipeThrough(new CompressionStream("deflate-raw"))).blob();
    const useDeflate = deflated.size < bytes.length;
    this.#append(name, useDeflate ? 8 : 0, crc, useDeflate ? deflated.size : bytes.length, bytes.length,
      useDeflate ? deflated : new Blob([bytes]));
  }

  /** Copies an entry of another archive as it is, without inflating and deflating it again. */
  async addRaw(name, file, entry) {
    const [start, end] = await dataRange(file, entry);
    this.#append(name, entry.method, entry.crc, entry.compressedSize, entry.uncompressedSize, file.slice(start, end));
  }

  #append(name, method, crc, compressedSize, size, data) {
    const nameBytes = this.encoder.encode(name);
    if (this.central.length >= ZIP_LIMIT_ENTRIES || this.offset + 30 + nameBytes.length + compressedSize > ZIP_LIMIT_BYTES) {
      throw new Error("The result is larger than a .zip download can hold here (4 GB / 65,535 files). Use \"Open a folder\" instead.");
    }
    const local = new Uint8Array(30 + nameBytes.length);
    const v = new DataView(local.buffer);
    v.setUint32(0, SIG_LOCAL, true);
    v.setUint16(4, 20, true);
    v.setUint16(6, 0x0800, true);          // UTF-8 names
    v.setUint16(8, method, true);
    v.setUint16(10, this.stamp.time, true);
    v.setUint16(12, this.stamp.day, true);
    v.setUint32(14, crc, true);
    v.setUint32(18, compressedSize, true);
    v.setUint32(22, size, true);
    v.setUint16(26, nameBytes.length, true);
    local.set(nameBytes, 30);
    this.parts.push(local, data);
    this.central.push({ nameBytes, method, crc, compressedSize, size, offset: this.offset });
    this.offset += local.length + compressedSize;
  }

  /** Returns the finished archive. */
  finish() {
    const cdStart = this.offset;
    let cdSize = 0;
    for (const e of this.central) {
      const rec = new Uint8Array(46 + e.nameBytes.length);
      const v = new DataView(rec.buffer);
      v.setUint32(0, SIG_CENTRAL, true);
      v.setUint16(4, 20, true);
      v.setUint16(6, 20, true);
      v.setUint16(8, 0x0800, true);
      v.setUint16(10, e.method, true);
      v.setUint16(12, this.stamp.time, true);
      v.setUint16(14, this.stamp.day, true);
      v.setUint32(16, e.crc, true);
      v.setUint32(20, e.compressedSize, true);
      v.setUint32(24, e.size, true);
      v.setUint16(28, e.nameBytes.length, true);
      v.setUint32(42, e.offset, true);
      rec.set(e.nameBytes, 46);
      this.parts.push(rec);
      cdSize += rec.length;
    }
    const end = new Uint8Array(22);
    const v = new DataView(end.buffer);
    v.setUint32(0, SIG_EOCD, true);
    v.setUint16(8, this.central.length, true);
    v.setUint16(10, this.central.length, true);
    v.setUint32(12, cdSize, true);
    v.setUint32(16, cdStart, true);
    this.parts.push(end);
    const blob = new Blob(this.parts, { type: "application/zip" });
    this.parts = [];
    return blob;
  }
}
