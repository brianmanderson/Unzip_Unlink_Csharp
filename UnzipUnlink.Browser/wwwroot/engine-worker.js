// Unzip & Unlink - in-browser engine host (module Web Worker).
// Loads the .NET WebAssembly runtime with the UnzipUnlink.Core engine and does all file I/O off the UI thread.
// Data never leaves this browser: files are read from what the user picked or dropped, rewritten in memory one at
// a time, and written to a .zip (kept by the browser, which pages large blobs to disk) or back to a folder the
// user chose through the File System Access API.

import { readZipDirectory, readZipEntry, ZipWriter, ZIP_LIMIT_ENTRIES } from "./zip.js";

const HEADER_SLICE = 1 << 20;         // first 1 MB is enough for the header of practically every image
const MAX_FILE_BYTES = 512 << 20;     // one file is copied in, parsed and copied out: keep well inside the heap
// Ceiling for studies that come back as a .zip download. The result is held by the browser until it is saved
// (Chromium keeps big blobs on disk, other browsers may keep them in memory), and the .zip format without ZIP64
// stops at 4 GB. 2 GB of images leaves room for both. Folders opened with "Open a folder" have no ceiling: they
// are rewritten file by file on disk.
const ZIP_PATH_LIMIT = 2 * 1024 ** 3;

let engine = null;
let runtime = null;
let cancelRequested = false;
/** The study being worked on: { name, kind, entries: [{ key, size, read(), readStart(n), ... }], ... } */
let study = null;
let lastPlan = null;

// ---- Messages ---------------------------------------------------------------------------------------

const handlers = {
  init, scan, preview, run, report, tags, tagValues,
  diagnose: (args) => diagnose(args),
};

// Note: addEventListener, not self.onmessage. dotnet.js decides at import time whether it runs as the main runtime
// of a worker (a "sidecar") or as a helper thread of another runtime, by checking whether globalThis.onmessage is
// set. With onmessage set it waits forever for a parent runtime. dotnetSidecar makes the intent explicit.
globalThis.dotnetSidecar = true;
// Operations run strictly one after another (the engine holds one study), while "cancel" acts at once on the one
// that is running. A scan or run that starts later clears the flag, so a late cancel never reaches the next study.
let queue = Promise.resolve();
self.addEventListener("message", (event) => {
  const { id, op, args } = event.data;
  if (op === "cancel") {
    cancelRequested = true;
    return;
  }
  queue = queue.then(async () => {
    try {
      const result = await handlers[op](args ?? {});
      self.postMessage({ id, ok: true, result });
    } catch (error) {
      self.postMessage({ id, ok: false, error: describeError(error) });
    }
  });
});

function progress(phase, done, total, extra = {}) {
  self.postMessage({ type: "progress", phase, done, total, ...extra });
}

function describeError(error) {
  const text = String(error?.message ?? error);
  if (/out of memory|OutOfMemory|Maximum call stack|memory access out of bounds/i.test(text)) {
    return "The browser ran out of memory for this study. Use \"Open a folder\" (Chrome or Edge), which works file by file without a size limit, or split the study.";
  }
  return text;
}

// ---- Runtime ----------------------------------------------------------------------------------------

async function init() {
  if (engine) return { ready: true };
  const started = performance.now();
  const { dotnet } = await import("./_framework/dotnet.js");
  runtime = await dotnet.withDiagnosticTracing(false).create();
  const config = runtime.getConfig();
  const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
  engine = exports.UnzipUnlink.Browser.Engine;
  return { ready: true, ms: Math.round(performance.now() - started) };
}

/** Size of the WebAssembly linear memory. It never shrinks, so this is also the peak. */
function wasmMemory() {
  return runtime?.Module?.HEAPU8?.length ?? 0;
}

function need() {
  if (!engine) throw new Error("The engine is still loading.");
  return engine;
}

// ---- Sources: what the user gave us -----------------------------------------------------------------

/** Loose files from a file picker or a drop. Each .zip among them is opened as an archive. */
async function entriesFromFiles(items, notes) {
  const entries = [];
  for (const { file, path } of items) {
    if (/\.zip$/i.test(path)) {
      const prefix = path.replace(/\.zip$/i, "") + "/";
      const zipEntries = await entriesFromZip(file, items.length === 1 ? "" : prefix);
      entries.push(...zipEntries);
      if (items.length > 1) notes.push(`Extracted ${path}.`);
      continue;
    }
    entries.push({
      key: path,
      size: file.size,
      readStart: async (n) => new Uint8Array(await file.slice(0, n).arrayBuffer()),
      read: async () => new Uint8Array(await file.arrayBuffer()),
      raw: null,
    });
  }
  return entries;
}

async function entriesFromZip(file, prefix) {
  const directory = await readZipDirectory(file);
  return directory.filter((e) => !e.isDirectory).map((e) => {
    const read = () => readZipEntry(file, e);
    return {
      key: prefix + e.name,
      size: e.uncompressedSize,
      readStart: read,           // compressed entries have to be inflated whole
      read,
      raw: { file, entry: e },   // lets an unchanged entry be copied into the result without recompressing
    };
  });
}

/** A folder opened through the File System Access API (Chromium). Files are read and written in place. */
async function entriesFromDirectory(root, notes) {
  const entries = [];
  let zips = 0;
  async function walk(dir, prefix) {
    for await (const [name, handle] of dir.entries()) {
      if (handle.kind === "directory") {
        await walk(handle, `${prefix}${name}/`);
      } else {
        if (/\.zip$/i.test(name)) zips++;
        const file = await handle.getFile();
        entries.push({
          key: prefix + name,
          size: file.size,
          handle,
          parent: dir,
          readStart: async (n) => new Uint8Array(await (await handle.getFile()).slice(0, n).arrayBuffer()),
          read: async () => new Uint8Array(await (await handle.getFile()).arrayBuffer()),
          raw: null,
        });
      }
    }
  }
  await walk(root, "");
  if (zips > 0) {
    notes.push(`${zips === 1 ? "One .zip archive" : `${zips} .zip archives`} in this folder ${zips === 1 ? "was" : "were"} not opened. To unlink what is inside, use "Choose .zip" instead.`);
  }
  return entries;
}

// ---- Scan -------------------------------------------------------------------------------------------

async function scan({ source }) {
  const e = need();
  cancelRequested = false;
  const notes = [];
  progress("Listing files", 0, 0);
  let entries;
  if (source.kind === "directory") entries = await entriesFromDirectory(source.handle, notes);
  else if (source.kind === "files") entries = await entriesFromFiles(source.items, notes);
  else throw new Error(`Unknown source ${source.kind}`);

  const totalSize = entries.reduce((sum, x) => sum + x.size, 0);
  if (source.kind !== "directory" && totalSize > ZIP_PATH_LIMIT) {
    throw new Error(`This study holds ${(totalSize / 1024 ** 3).toFixed(1)} GB of files. Studies that come back as a .zip download can be up to ${ZIP_PATH_LIMIT / 1024 ** 3} GB in this browser edition. `
      + "In Edge or Chrome, use \"Open a folder\" instead, which works on the files where they are and has no size limit; otherwise unlink the study in parts, or use the desktop app.");
  }
  if (source.kind !== "directory" && entries.length > ZIP_LIMIT_ENTRIES) {
    throw new Error(`This study holds ${entries.length.toLocaleString()} files; a .zip download can hold up to ${ZIP_LIMIT_ENTRIES.toLocaleString()}. In Edge or Chrome, use "Open a folder" instead.`);
  }

  const started = performance.now();
  e.Reset();
  const byKey = new Map();
  const totalBytes = totalSize;
  let done = 0, lastReport = 0, tooLarge = 0;
  for (const entry of entries) {
    if (cancelRequested) throw new Error("cancelled");
    byKey.set(entry.key, entry);
    if (entry.size > MAX_FILE_BYTES) {
      tooLarge++;
    } else if (entry.size >= 132) {
      const partial = entry.size > HEADER_SLICE && entry.readStart !== entry.read;
      let bytes = partial ? await entry.readStart(HEADER_SLICE) : await entry.read();
      let kind = e.AddFile(entry.key, bytes, entry.size, partial);
      if (kind === 4) {
        bytes = await entry.read();
        kind = e.AddFile(entry.key, bytes, entry.size, false);
      }
    } else {
      e.AddFile(entry.key, new Uint8Array(0), entry.size, false);
    }
    done++;
    const now = performance.now();
    if (now - lastReport > 120 || done === entries.length) {
      lastReport = now;
      progress("Reading DICOM headers", done, entries.length);
      await yieldToEvents();
    }
  }
  if (tooLarge > 0) {
    notes.push(`${tooLarge === 1 ? "One file is" : `${tooLarge} files are`} larger than ${MAX_FILE_BYTES >> 20} MB and ${tooLarge === 1 ? "was" : "were"} skipped: the in-browser edition handles one file at a time in memory.`);
  }
  const scanView = JSON.parse(e.FinishScan());
  study = { name: source.name, kind: source.kind, entries, byKey, totalBytes, root: source.handle ?? null };
  lastPlan = null;
  return {
    name: source.name,
    kind: source.kind === "directory" ? "folder" : "upload",
    notes,
    totalBytes,
    entryCount: entries.length,
    scan: scanView,
    scanMs: Math.round(performance.now() - started),
    heap: e.ManagedMemory(),
    wasmMemory: wasmMemory(),
  };
}

/** Gives the worker's event loop a turn so a "cancel" message can arrive during long loops. */
function yieldToEvents() {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

// ---- Preview / plan / run ---------------------------------------------------------------------------

function preview({ request }) {
  return JSON.parse(need().Preview(JSON.stringify(request)));
}

async function run({ request, output, destination }) {
  const e = need();
  if (!study) throw new Error("Nothing has been opened.");
  cancelRequested = false;
  const plan = JSON.parse(e.CreatePlan(JSON.stringify(request)));
  lastPlan = plan;
  const started = performance.now();
  const failures = [];
  const handled = new Set();
  let filesDone = 0, filesChanged = 0, seriesDone = 0, cancelled = false, lastReport = 0, unit = null;

  let zip = null, target = null, outputFolder = null;
  if (output === "zip") {
    zip = new ZipWriter();
  } else if (output === "copy") {
    target = await uniqueFolder(destination, `${study.name}_unlinked`);
    outputFolder = target.name;
  } else if (output !== "inplace") {
    throw new Error(`Unknown output ${output}`);
  }

  const write = async (entry, bytes) => {
    if (zip) await zip.add(entry.key, bytes);
    else if (target) await writeFileAt(target, entry.key, bytes);
    else await writeInPlace(entry, bytes);
  };

  for (let index = 0; index < plan.series.length; index++) {
    // Like the native engine, cancelling stops between units: a series is rewritten completely or not at all, and
    // a structure set is never separated from the images it follows (they share a unit and sit next to each other).
    const series = plan.series[index];
    if (cancelRequested && series.unit !== unit) { cancelled = true; break; }
    unit = series.unit;
    for (const key of series.files) {
      const entry = study.byKey.get(key);
      handled.add(key);
      try {
        const input = await entry.read();
        const rewritten = e.Rewrite(index, input);
        await write(entry, rewritten);
        filesChanged++;
      } catch (error) {
        failures.push({ file: key.split("/").pop(), error: describeError(error) });
        // A file that could not be rewritten still belongs in a copy, unchanged.
        if (output !== "inplace") await copyUnchanged(entry, zip, target).catch(() => {});
      }
      filesDone++;
      const now = performance.now();
      if (now - lastReport > 120) {
        lastReport = now;
        progress("Unlinking", filesDone, plan.fileCount, { detail: series.label });
        await yieldToEvents();
      }
    }
    seriesDone++;
  }
  progress("Unlinking", filesDone, plan.fileCount, { detail: "" });

  // Everything that was not rewritten goes into a copy unchanged (in place, it simply stays where it is).
  if (output !== "inplace") {
    const rest = study.entries.filter((x) => !handled.has(x.key));
    let copied = 0;
    for (const entry of rest) {
      await copyUnchanged(entry, zip, target);
      copied++;
      const now = performance.now();
      if (now - lastReport > 120 || copied === rest.length) {
        lastReport = now;
        progress(zip ? "Packing the .zip" : "Copying the other files", copied, rest.length);
        await yieldToEvents();
      }
    }
  }

  let blob = null;
  if (zip) {
    progress("Packing the .zip", 1, 1);
    blob = zip.finish();
  }
  const seconds = Math.round((performance.now() - started) / 100) / 10;
  return {
    seriesChanged: seriesDone,
    filesChanged,
    cancelled,
    seconds,
    failures: failures.slice(0, 200),
    failureCount: failures.length,
    outputFolder,
    zip: blob,
    heap: e.ManagedMemory(),
    wasmMemory: wasmMemory(),
  };
}

async function copyUnchanged(entry, zip, target) {
  if (zip) {
    if (entry.raw) await zip.addRaw(entry.key, entry.raw.file, entry.raw.entry);
    else await zip.add(entry.key, await entry.read());
  } else if (target) {
    await writeFileAt(target, entry.key, await entry.read());
  }
}

function tags() {
  return JSON.parse(need().Tags());
}

/**
 * What the given tags hold today in every series, from its first and last file (enough to say "this differs between
 * images" without reading a whole study). Returns { seriesUid: [{ value, varies }] } in the order of keywords.
 */
async function tagValues({ keywords }) {
  const e = need();
  if (!study) throw new Error("Nothing has been opened.");
  const tagsJson = JSON.stringify(keywords);
  const read = async (key) => {
    const entry = study.byKey.get(key);
    if (!entry || entry.size > MAX_FILE_BYTES) return null;
    try {
      const partial = entry.size > HEADER_SLICE && entry.readStart !== entry.read;
      let json = e.TagValues(partial ? await entry.readStart(HEADER_SLICE) : await entry.read(), tagsJson);
      if (json === "" && partial) json = e.TagValues(await entry.read(), tagsJson);
      return json === "" ? null : JSON.parse(json);
    } catch {
      return null;
    }
  };
  const result = {};
  for (const [uid, [first, last]] of Object.entries(JSON.parse(e.SampleFiles()))) {
    const a = await read(first);
    const b = last === first ? a : await read(last);
    if (!a || !b) continue;   // unreadable: leave the series out rather than claim the tag is not set
    result[uid] = a.map((value, i) => ({ value, varies: value !== b[i] }));
  }
  return result;
}

function report() {
  if (!lastPlan) throw new Error("Nothing has been unlinked yet.");
  return need().Report();
}

// ---- File System Access helpers ---------------------------------------------------------------------

async function writeInPlace(entry, bytes) {
  // createWritable writes to a temporary file that replaces the original only on close(), so an interrupted run
  // never leaves a half-written DICOM file behind - the same guarantee as the desktop engine.
  const writable = await entry.handle.createWritable({ keepExistingData: false });
  try {
    await writable.write(bytes);
    await writable.close();
  } catch (error) {
    await writable.abort().catch(() => {});
    throw error;
  }
}

async function writeFileAt(root, key, bytes) {
  const parts = key.split("/");
  let dir = root;
  for (const part of parts.slice(0, -1)) dir = await dir.getDirectoryHandle(part, { create: true });
  const handle = await dir.getFileHandle(parts[parts.length - 1], { create: true });
  await writeInPlace({ handle }, bytes);
}

async function uniqueFolder(parent, name) {
  for (let n = 1; ; n++) {
    const candidate = n === 1 ? name : `${name} (${n})`;
    try {
      await parent.getDirectoryHandle(candidate);
    } catch {
      return parent.getDirectoryHandle(candidate, { create: true });
    }
  }
}

// ---- Self-check hooks (used by the automated browser test, not by the page) -------------------------

async function diagnose({ bytes, frame, series, sop }) {
  const e = need();
  const started = performance.now();
  const output = e.RewriteWithUids(bytes, frame ?? "", series ?? "", sop ?? "");
  return { output, ms: performance.now() - started, heap: e.ManagedMemory(), wasmMemory: wasmMemory() };
}
