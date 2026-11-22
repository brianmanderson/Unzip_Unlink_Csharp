// The page's view of the engine: a small promise API over the Web Worker in engine-worker.js.
// app.js talks only to this object, so the four-step flow does not care where the engine runs.

export function createEngine() {
  const worker = new Worker(new URL("engine-worker.js", document.baseURI), { type: "module" });
  const pending = new Map();
  let seq = 0;
  let onProgress = null;
  let broken = null;

  worker.onmessage = (event) => {
    const message = event.data;
    if (message.type === "progress") {
      onProgress?.(message);
      return;
    }
    const call = pending.get(message.id);
    if (!call) return;
    pending.delete(message.id);
    if (message.ok) call.resolve(message.result); else call.reject(new Error(message.error));
  };
  worker.onerror = (event) => {
    event.preventDefault?.();
    broken = new Error("The engine could not start in this browser. Try a current version of Edge, Chrome, Firefox or Safari.");
    for (const call of pending.values()) call.reject(broken);
    pending.clear();
  };

  function call(op, args, progress) {
    if (broken) return Promise.reject(broken);
    onProgress = progress ?? null;
    return new Promise((resolve, reject) => {
      const id = ++seq;
      pending.set(id, { resolve, reject });
      worker.postMessage({ id, op, args });
    });
  }

  const ready = call("init");

  return {
    ready,
    /** source: { kind: "files", items: [{ file, path }], name } | { kind: "directory", handle, name } */
    scan: async (source, progress) => { await ready; return call("scan", { source }, progress); },
    preview: (request) => call("preview", { request }),
    /** output: "zip" | "inplace" | "copy" (copy needs a destination directory handle). */
    run: (request, output, destination, progress) => call("run", { request, output, destination }, progress),
    report: () => call("report"),
    /** Every DICOM tag that can be overwritten: [{ keyword, name, tag, vr }]. */
    tags: async () => { await ready; return call("tags"); },
    /** What the given tags hold today, per series: { seriesUid: [{ value, varies }] } in the order of keywords. */
    tagValues: (keywords) => call("tagValues", { keywords }),
    cancel: () => worker.postMessage({ op: "cancel" }),
  };
}

export const capabilities = {
  /** File System Access API with directory pickers (Chromium-based browsers: Edge, Chrome). */
  folders: typeof window.showDirectoryPicker === "function",
};
