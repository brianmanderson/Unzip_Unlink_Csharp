// Unzip & Unlink - browser client. Plain ES module, no dependencies.
// All text that comes from DICOM files or the file system is written with textContent, never innerHTML.

const $ = (id) => document.getElementById(id);
const MODALITY_LABEL = { CT: "CT", MR: "MR", PT: "PET" };
const POLL_MS = 350;

const state = {
  info: null,
  job: null,            // latest job snapshot from the server
  jobId: null,
  preview: null,
  upload: null,         // { cancelled, xhrs:Set }
  options: null,
  pollTimer: null,
  previewTimer: null,
  browsePath: "",
  browseSelection: null,
};

// ---- Server calls -----------------------------------------------------------------------------

async function api(method, url, body) {
  const init = { method, headers: { "X-UnzipUnlink": "1" } };
  if (body !== undefined) {
    init.headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(body);
  }
  let response;
  try {
    response = await fetch(url, init);
  } catch {
    throw new Error("Cannot reach the Unzip & Unlink server. Is it still running?");
  }
  const isJson = (response.headers.get("content-type") || "").includes("json");
  const data = isJson ? await response.json() : null;
  if (!response.ok) {
    throw new Error((data && data.error) || `The server answered ${response.status}.`);
  }
  return data;
}

// ---- Small helpers ----------------------------------------------------------------------------

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === "class") node.className = value;
    else if (key === "text") node.textContent = value;
    else if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else if (value !== false && value != null) node.setAttribute(key, value === true ? "" : value);
  }
  for (const child of children) {
    if (child != null) node.append(child);
  }
  return node;
}

function icon(path) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("aria-hidden", "true");
  const p = document.createElementNS("http://www.w3.org/2000/svg", "path");
  p.setAttribute("d", path);
  svg.append(p);
  return svg;
}

const ICONS = {
  folder: "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z",
  drive: "M4 14h16v5H4zM6 14l2-9h8l2 9M8 16.5h.01",
  zip: "M6 3h9l4 4v14H6zM14 3v5h5M10 11h2M10 14h2M10 17h2",
  check: "M5 12.5l4.5 4.5L19 7.5",
  warn: "M12 8v5M12 16.5v.5M10.3 3.9 2.4 17.5A2 2 0 0 0 4.1 20.5h15.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z",
  download: "M12 4v12M7 11l5 5 5-5M4 20h16",
};

const number = (n) => n.toLocaleString();
const plural = (n, one, many = one + "s") => `${number(n)} ${n === 1 ? one : many}`;

function bytes(n) {
  if (n < 1024) return `${n} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = n / 1024, unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value >= 100 ? value.toFixed(0) : value.toFixed(1)} ${units[unit]}`;
}

function dicomDate(value) {
  return /^\d{8}$/.test(value) ? `${value.slice(0, 4)}-${value.slice(4, 6)}-${value.slice(6)}` : value;
}

let toastTimer;
function toast(message) {
  const node = $("toast");
  node.textContent = message;
  node.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { node.hidden = true; }, 5000);
}

// ---- Views ------------------------------------------------------------------------------------

const VIEWS = ["add", "busy", "review", "done", "error"];
const STEP_OF = { add: "add", busy: "add", review: "review", done: "done", error: null };
const STEP_ORDER = ["add", "review", "run", "done"];

function show(view, step = STEP_OF[view]) {
  for (const name of VIEWS) {
    $(`view-${name}`).hidden = name !== view;
  }
  const index = STEP_ORDER.indexOf(step);
  for (const item of $("steps").children) {
    const i = STEP_ORDER.indexOf(item.dataset.step);
    item.classList.toggle("current", i === index);
    item.classList.toggle("complete", index >= 0 && i < index);
    if (i === index) item.setAttribute("aria-current", "step"); else item.removeAttribute("aria-current");
  }
  window.scrollTo({ top: 0 });
}

function showBusy(title, detail, done, total, countText, step) {
  $("busyTitle").textContent = title;
  $("busyDetail").textContent = detail || "";
  const meter = $("busyMeter");
  const known = total > 0;
  meter.classList.toggle("indeterminate", !known);
  const percent = known ? Math.min(100, Math.round((done / total) * 100)) : 0;
  $("busyFill").style.width = known ? `${percent}%` : "";
  if (known) meter.setAttribute("aria-valuenow", String(percent)); else meter.removeAttribute("aria-valuenow");
  $("busyCount").textContent = countText ?? (known ? `${number(done)} of ${number(total)}` : "");
  if ($("view-busy").hidden) show("busy", step);
  else setStep(step);
}

function setStep(step) {
  const index = STEP_ORDER.indexOf(step);
  for (const item of $("steps").children) {
    const i = STEP_ORDER.indexOf(item.dataset.step);
    item.classList.toggle("current", i === index);
    item.classList.toggle("complete", i < index);
  }
}

function showError(message) {
  stopPolling();
  $("errorText").textContent = message;
  show("error");
}

// ---- Start over / clean up --------------------------------------------------------------------

function discardJob() {
  stopPolling();
  if (state.upload) {
    state.upload.cancelled = true;
    for (const xhr of state.upload.xhrs) xhr.abort();
    state.upload = null;
  }
  if (state.jobId) {
    // keepalive lets this finish even while the page is closing.
    fetch(`/api/jobs/${state.jobId}`, { method: "DELETE", headers: { "X-UnzipUnlink": "1" }, keepalive: true }).catch(() => {});
  }
  state.jobId = null;
  state.job = null;
  state.preview = null;
  state.options = null;
}

function startOver() {
  discardJob();
  $("folderInput").value = "";
  $("filesInput").value = "";
  show("add");
}

// ---- Upload -----------------------------------------------------------------------------------

/** Walks a dropped folder tree (DataTransferItem entries) into [{ file, path }]. */
async function filesFromDrop(dataTransfer) {
  const entries = [...dataTransfer.items].map((item) => item.webkitGetAsEntry?.()).filter(Boolean);
  if (entries.length === 0) {
    return [...dataTransfer.files].map((file) => ({ file, path: file.name }));
  }
  const found = [];
  async function walk(entry, prefix) {
    if (entry.isFile) {
      const file = await new Promise((resolve, reject) => entry.file(resolve, reject));
      found.push({ file, path: prefix + entry.name });
    } else if (entry.isDirectory) {
      const reader = entry.createReader();
      for (;;) {
        // readEntries hands back batches (100 at a time in Chromium) until it returns an empty list.
        const batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject));
        if (batch.length === 0) break;
        for (const child of batch) await walk(child, `${prefix}${entry.name}/`);
      }
    }
  }
  for (const entry of entries) await walk(entry, "");
  return found;
}

/** A friendly job name: the dropped folder, or the .zip, otherwise just "study". */
function uploadName(items) {
  const first = items[0].path;
  if (first.includes("/")) {
    const top = first.split("/")[0];
    return items.every((i) => i.path.startsWith(top + "/")) ? top : "study";
  }
  return items.length === 1 && /\.zip$/i.test(first) ? first.replace(/\.zip$/i, "") : "study";
}

function putFile(jobId, item, onProgress, tracker) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    tracker.xhrs.add(xhr);
    xhr.open("PUT", `/api/jobs/${jobId}/files?path=${encodeURIComponent(item.path)}`);
    xhr.setRequestHeader("X-UnzipUnlink", "1");
    xhr.setRequestHeader("Content-Type", "application/octet-stream");
    xhr.upload.onprogress = (event) => onProgress(event.loaded);
    xhr.onload = () => {
      tracker.xhrs.delete(xhr);
      if (xhr.status >= 200 && xhr.status < 300) return resolve();
      let message = `Upload failed (${xhr.status}).`;
      try { message = JSON.parse(xhr.responseText).error || message; } catch { /* keep default */ }
      reject(new Error(message));
    };
    xhr.onerror = () => { tracker.xhrs.delete(xhr); reject(new Error("The upload was interrupted. Check the connection to the server.")); };
    xhr.onabort = () => { tracker.xhrs.delete(xhr); reject(new Error("cancelled")); };
    xhr.send(item.file);
  });
}

async function startUpload(items) {
  items = items.filter((i) => i.file.size > 0 || !i.path.endsWith("/"));
  if (items.length === 0) {
    toast("Nothing to upload - that selection is empty.");
    return;
  }
  const totalBytes = items.reduce((sum, i) => sum + i.file.size, 0);
  if (state.info && totalBytes > state.info.maxUploadBytes) {
    toast(`That is ${bytes(totalBytes)} - more than this server accepts in one upload (${bytes(state.info.maxUploadBytes)}).`);
    return;
  }
  discardJob();
  const tracker = { cancelled: false, xhrs: new Set() };
  state.upload = tracker;
  showBusy("Uploading", `${plural(items.length, "file")} · ${bytes(totalBytes)}`, 0, totalBytes, "", "add");

  try {
    const { id } = await api("POST", "/api/jobs/upload", { name: uploadName(items) });
    state.jobId = id;
    let finishedBytes = 0, finishedFiles = 0, next = 0;
    const inFlight = new Map();
    const report = () => {
      let sent = finishedBytes;
      for (const loaded of inFlight.values()) sent += loaded;
      showBusy("Uploading", `${plural(items.length, "file")} · ${bytes(totalBytes)}`, sent, totalBytes,
        `${bytes(sent)} of ${bytes(totalBytes)} · ${number(finishedFiles)} of ${number(items.length)} files`, "add");
    };
    const worker = async () => {
      while (next < items.length && !tracker.cancelled) {
        const item = items[next++];
        inFlight.set(item, 0);
        await putFile(id, item, (loaded) => { inFlight.set(item, loaded); report(); }, tracker);
        inFlight.delete(item);
        finishedBytes += item.file.size;
        finishedFiles++;
        report();
      }
    };
    // A handful of parallel requests keeps thousands of small slices moving without flooding the server.
    await Promise.all(Array.from({ length: Math.min(6, items.length) }, worker));
    if (tracker.cancelled) return;
    state.upload = null;
    await api("POST", `/api/jobs/${id}/prepare`);
    poll();
  } catch (error) {
    if (tracker.cancelled || error.message === "cancelled") return;
    discardJob();
    showError(error.message);
  }
}

// ---- Local folder -----------------------------------------------------------------------------

async function openLocal() {
  const path = $("localPath").value.trim();
  if (!path) {
    $("localPath").focus();
    return;
  }
  discardJob();
  try {
    const { id } = await api("POST", "/api/jobs/local", { path });
    state.jobId = id;
    showBusy("Opening", path, 0, 0, "", "add");
    poll();
  } catch (error) {
    toast(error.message);
  }
}

async function browse(path) {
  $("browseError").textContent = "";
  let listing;
  try {
    listing = await api("GET", `/api/fs?path=${encodeURIComponent(path || "")}`);
  } catch (error) {
    $("browseError").textContent = error.message;
    return;
  }
  state.browsePath = listing.path;
  state.browseSelection = null;

  const crumbs = $("browseCrumbs");
  crumbs.replaceChildren(el("button", { type: "button", text: "This machine", onclick: () => browse("") }));
  if (listing.path) {
    const parts = listing.path.split(/[\\/]/).filter(Boolean);
    let built = "";
    parts.forEach((part, i) => {
      built = i === 0 ? (listing.path.startsWith("\\\\") ? `\\\\${part}` : `${part}\\`) : `${built}${built.endsWith("\\") ? "" : "\\"}${part}`;
      const target = built;
      crumbs.append(el("span", { text: "›" }), el("button", { type: "button", text: part, onclick: () => browse(target) }));
    });
  }

  const list = $("browseList");
  list.replaceChildren();
  if (listing.entries.length === 0) {
    list.append(el("li", { class: "empty", text: "No sub-folders or .zip files here. Use this folder if it holds your DICOM files." }));
  }
  for (const entry of listing.entries) {
    const isZip = entry.kind === "zip";
    const button = el("button", { type: "button", class: isZip ? "zip" : "" },
      icon(isZip ? ICONS.zip : entry.kind === "drive" ? ICONS.drive : ICONS.folder), el("span", { text: entry.name }));
    if (isZip) {
      button.addEventListener("click", () => {
        for (const other of list.querySelectorAll(".selected")) other.classList.remove("selected");
        button.classList.add("selected");
        state.browseSelection = entry.path;
        updateBrowseSelect();
      });
      button.addEventListener("dblclick", () => chooseBrowsed(entry.path));
    } else {
      button.addEventListener("click", () => browse(entry.path));
    }
    list.append(el("li", {}, button));
  }
  updateBrowseSelect();
}

function updateBrowseSelect() {
  const button = $("browseSelect");
  button.textContent = state.browseSelection ? "Use this .zip" : "Use this folder";
  button.disabled = !state.browseSelection && !state.browsePath;
}

function chooseBrowsed(path) {
  $("localPath").value = path;
  $("browseDialog").close();
  openLocal();
}

// ---- Polling ----------------------------------------------------------------------------------

function stopPolling() {
  clearTimeout(state.pollTimer);
  state.pollTimer = null;
}

async function poll() {
  stopPolling();
  const id = state.jobId;
  if (!id) return;
  let job;
  try {
    job = await api("GET", `/api/jobs/${id}`);
  } catch (error) {
    if (state.jobId === id) {
      state.jobId = null;
      showError(error.message);
    }
    return;
  }
  if (state.jobId !== id) return;
  const previous = state.job;
  state.job = job;

  switch (job.state) {
    case "preparing":
      showBusy(job.kind === "upload" ? "Reading your study" : "Opening", job.phase, job.progress.done, job.progress.total, undefined, "add");
      break;
    case "running":
      showBusy("Unlinking", job.phase, job.progress.done, job.progress.total,
        job.progress.total ? `${number(job.progress.done)} of ${number(job.progress.total)} files` : "", "run");
      break;
    case "ready":
      if (!previous || previous.state !== "ready") renderReview();
      return;
    case "done":
      renderDone();
      return;
    case "failed":
      showError(job.error || "Something went wrong.");
      return;
    default:
      break;
  }
  state.pollTimer = setTimeout(poll, POLL_MS);
}

// ---- Review -----------------------------------------------------------------------------------

function supportedCounts(scan) {
  const counts = { CT: 0, MR: 0, PT: 0 };
  for (const s of scan.series) {
    if (s.supported && s.modality in counts) counts[s.modality]++;
  }
  return counts;
}

/** Number of series sharing each frame of reference, among the given modalities. */
function sharedFrameSeries(scan, modalities) {
  const byFrame = new Map();
  for (const s of scan.series) {
    if (s.frame == null || !modalities.includes(s.modality)) continue;
    if (!byFrame.has(s.frame)) byFrame.set(s.frame, []);
    byFrame.get(s.frame).push(s);
  }
  return [...byFrame.values()].filter((group) => group.length > 1);
}

function renderReview() {
  const job = state.job, scan = job.scan;
  const counts = supportedCounts(scan);
  if (!state.options) {
    const ctGroups = sharedFrameSeries(scan, ["CT"]);
    const petGroups = sharedFrameSeries(scan, ["CT", "PT"]).filter((g) => g.some((s) => s.modality === "PT") && g.some((s) => s.modality === "CT"));
    state.options = {
      frame: true, series: false, sop: true,
      modalities: new Set(Object.keys(counts).filter((m) => counts[m] > 0)),
      // Sensible starting point: if the data visibly contains a 4DCT or PET/CT, keep it together.
      keepCt: ctGroups.length > 0,
      keepPet: petGroups.length > 0,
      excluded: new Set(),
      workOnCopy: false,
    };
  }

  $("reviewTitle").textContent = job.name;
  const patients = new Set(scan.series.map((s) => `${s.patient}|${s.patientId}`)).size;
  const totalBytes = scan.series.reduce((sum, s) => sum + s.bytes, 0);
  $("reviewSummary").textContent = scan.series.length === 0
    ? "No DICOM images were found."
    : `${plural(scan.series.length, "series", "series")} · ${plural(scan.fileCount, "image")} · ${bytes(totalBytes)} · ${plural(patients, "patient")}`;

  // Notices
  const notices = $("notices");
  notices.replaceChildren();
  const notice = (kind, text) => notices.append(el("div", { class: `notice ${kind}` }, el("span", { text })));
  if (scan.series.length === 0) {
    notice("warn", scan.otherFileCount > 0
      ? `None of the ${number(scan.otherFileCount)} files here are DICOM. Check that you picked the right folder or archive.`
      : "This location is empty.");
  }
  for (const text of job.notes) notice("info", text);
  if (job.kind === "local") notice("info", `Working on ${job.location}`);
  if (scan.dicomDirCount > 0) {
    notice("warn", "This study contains a DICOMDIR index. It is not updated, so it will be out of date after unlinking - import from the folder rather than from the DICOMDIR.");
  }
  if (scan.unreadableCount > 0) {
    notice("warn", `${plural(scan.unreadableCount, "file")} looked like DICOM but could not be read and will be skipped (first: ${scan.unreadable[0].file}).`);
  }
  if (scan.series.some((s) => s.mixedFrames)) {
    notice("warn", "At least one series contains images with different frames of reference. After unlinking, all images of a series share one new frame of reference.");
  }
  const others = scan.series.filter((s) => !s.supported);
  if (others.length > 0 && others.length < scan.series.length) {
    const kinds = [...new Set(others.map((s) => s.modality || "unknown"))].join(", ");
    notice("info", `${plural(others.length, "other object")} (${kinds}) ${others.length === 1 ? "is" : "are"} left unchanged. Anything that refers to an image by its identifiers - a structure set, plan, dose or registration - will keep pointing at the original, not at the unlinked copy.`);
  }
  if (scan.series.length > 0 && scan.series.every((s) => !s.supported)) {
    notice("warn", "There are no CT, MR or PET series here, so there is nothing this tool can change.");
  }

  // Modality toggles
  const toggles = $("modalityToggles");
  toggles.replaceChildren();
  for (const modality of ["CT", "MR", "PT"]) {
    const button = el("button", {
      type: "button", class: `modality ${modality.toLowerCase()}`, "aria-pressed": String(state.options.modalities.has(modality)),
      disabled: counts[modality] === 0,
      onclick: () => {
        if (state.options.modalities.has(modality)) state.options.modalities.delete(modality);
        else state.options.modalities.add(modality);
        syncOptions();
      },
    }, el("b", { text: MODALITY_LABEL[modality] }), el("small", { text: counts[modality] === 0 ? "none found" : plural(counts[modality], "series", "series") }));
    button.dataset.modality = modality;
    toggles.append(button);
  }

  $("outputCard").hidden = job.kind !== "local";
  if (job.kind === "local") {
    $("outCopyHint").textContent = `The originals stay untouched; the result goes to a new folder next to them ("${job.location.split(/[\\/]/).filter(Boolean).pop()}_unlinked").`;
  }

  renderRows();
  syncOptions();
  show("review");
}

function renderRows() {
  const scan = state.job.scan;
  const body = $("seriesBody");
  body.replaceChildren();
  let lastStudy = null;
  for (const s of scan.series) {
    if (s.study !== lastStudy) {
      lastStudy = s.study;
      const title = [s.patient || "Unnamed patient", s.patientId && `(${s.patientId})`].filter(Boolean).join(" ");
      const detail = [dicomDate(s.studyDate), s.studyDescription].filter(Boolean).join(" · ");
      body.append(el("tr", { class: "study" }, el("td", { colspan: "8" }, el("span", { text: title }), el("small", { text: detail }))));
    }
    const checkbox = el("input", { type: "checkbox", "aria-label": `Include series ${s.number} ${s.description}` });
    checkbox.addEventListener("change", () => {
      if (checkbox.checked) state.options.excluded.delete(s.uid); else state.options.excluded.add(s.uid);
      syncOptions();
    });
    const cls = s.modality === "CT" ? "ct" : s.modality === "MR" ? "mr" : s.modality === "PT" ? "pt" : "";
    const row = el("tr", { class: s.supported ? "" : "unsupported off" },
      el("td", { class: "col-check" }, checkbox),
      el("td", {}, el("span", { class: `chip ${cls}`, text: MODALITY_LABEL[s.modality] || s.modality || "?" })),
      el("td", { class: "num", text: s.number }),
      el("td", {}, el("div", { class: "desc", text: s.description || "(no description)", title: s.description })),
      el("td", { class: "num", text: number(s.files) }),
      el("td", { class: "num", text: bytes(s.bytes) }),
      el("td", { class: "now" }),
      el("td", { class: "after" }));
    row.dataset.uid = s.uid;
    body.append(row);
  }
}

/**
 * frame id -> { index (letter), size } for frames shared by two or more series.
 * When `before` is given (the "now" frames and their groups), a group that is simply what is left of an
 * existing group keeps that group's letter, so "B" before is still "B" after.
 */
function linkGroups(frames, before) {
  const members = new Map();
  frames.forEach((frame, i) => {
    if (frame == null) return;
    if (!members.has(frame)) members.set(frame, []);
    members.get(frame).push(i);
  });
  const groups = new Map();
  const used = new Set();
  const shared = [...members].filter(([, list]) => list.length > 1);
  if (before) {
    for (const [frame, list] of shared) {
      const origins = new Set(list.map((i) => before.frames[i]));
      const origin = origins.size === 1 ? before.groups.get([...origins][0]) : null;
      if (origin && !used.has(origin.index)) {
        used.add(origin.index);
        groups.set(frame, { index: origin.index, size: list.length });
      }
    }
  }
  let next = 0;
  for (const [frame, list] of shared) {
    if (groups.has(frame)) continue;
    while (used.has(next) || (before && [...before.groups.values()].some((g) => g.index === next))) next++;
    used.add(next);
    groups.set(frame, { index: next, size: list.length });
  }
  return groups;
}

function linkBadge(frame, groups) {
  const group = frame == null ? null : groups.get(frame);
  if (!group) {
    return el("span", { class: "link-badge solo", text: "–", title: frame == null ? "No frame of reference" : "Not linked to any other series" });
  }
  const letter = String.fromCharCode(65 + (group.index % 26)) + (group.index >= 26 ? Math.floor(group.index / 26) : "");
  return el("span", { class: `link-badge g${group.index % 8}`, text: letter, title: `Shares a frame of reference with ${plural(group.size - 1, "other series", "other series")}` });
}

function isIncluded(series) {
  return series.supported && state.options.modalities.has(series.modality) && !state.options.excluded.has(series.uid);
}

function changeRequest() {
  const o = state.options, scan = state.job.scan;
  return {
    frameOfReference: o.frame,
    seriesUid: o.series,
    sopUid: o.sop,
    modalities: [...o.modalities],
    keepCtLinked: o.keepCt && o.frame && o.modalities.has("CT"),
    keepPetCtLinked: o.keepPet && o.frame && o.modalities.has("CT") && o.modalities.has("PT"),
    seriesUids: scan.series.filter(isIncluded).map((s) => s.uid),
    workOnCopy: state.job.kind === "local" && o.workOnCopy,
  };
}

/** Pushes option state into the controls, then asks the server what would happen. */
function syncOptions() {
  const o = state.options, scan = state.job.scan;
  $("optFrame").checked = o.frame;
  $("optSeries").checked = o.series;
  $("optSop").checked = o.sop;

  for (const button of $("modalityToggles").children) {
    button.setAttribute("aria-pressed", String(o.modalities.has(button.dataset.modality)));
  }

  const ctGroups = sharedFrameSeries(scan, ["CT"]);
  const petGroups = sharedFrameSeries(scan, ["CT", "PT"]).filter((g) => g.some((s) => s.modality === "PT") && g.some((s) => s.modality === "CT"));
  const canKeepCt = o.frame && o.modalities.has("CT");
  const canKeepPet = o.frame && o.modalities.has("CT") && o.modalities.has("PT");
  setSwitch("optKeepCt", o.keepCt && canKeepCt, canKeepCt);
  setSwitch("optKeepPet", o.keepPet && canKeepPet, canKeepPet);
  $("keepCtHint").textContent = ctGroups.length > 0
    ? `Found ${plural(ctGroups.reduce((n, g) => n + g.length, 0), "CT series", "CT series")} sharing a frame of reference. Keep them registered to each other.`
    : "CT series that share a frame of reference today stay registered to each other. None found in this study.";
  $("keepPetHint").textContent = petGroups.length > 0
    ? `Found ${plural(petGroups.length, "PET/CT set")} sharing a frame of reference. Keep each PET registered to its CT.`
    : "A PET and the CT it shares a frame of reference with stay registered to each other. None found in this study.";
  $("keepCard").hidden = !("CT" in supportedCounts(scan)) || supportedCounts(scan).CT === 0;

  $("outInPlace").checked = !o.workOnCopy;
  $("outCopy").checked = o.workOnCopy;

  let selectable = 0, included = 0;
  for (const row of $("seriesBody").querySelectorAll("tr[data-uid]")) {
    const series = scan.series.find((s) => s.uid === row.dataset.uid);
    const checkbox = row.querySelector("input");
    const modalityOn = series.supported && o.modalities.has(series.modality);
    checkbox.disabled = !modalityOn;
    checkbox.checked = isIncluded(series);
    checkbox.title = !series.supported ? "Only CT, MR and PET are modified" : modalityOn ? "" : `Turn on ${MODALITY_LABEL[series.modality]} under "Which images" to include this series`;
    row.classList.toggle("off", !checkbox.checked);
    if (modalityOn) { selectable++; if (checkbox.checked) included++; }
  }
  const all = $("checkAll");
  all.disabled = selectable === 0;
  all.checked = selectable > 0 && included === selectable;
  all.indeterminate = included > 0 && included < selectable;

  clearTimeout(state.previewTimer);
  state.previewTimer = setTimeout(refreshPreview, 60);
}

function setSwitch(id, checked, enabled) {
  const input = $(id);
  input.checked = checked;
  input.disabled = !enabled;
  input.closest(".switch-row").classList.toggle("disabled", !enabled);
}

async function refreshPreview() {
  const id = state.jobId;
  let preview;
  try {
    preview = await api("POST", `/api/jobs/${id}/preview`, changeRequest());
  } catch (error) {
    if (state.jobId === id) showError(error.message);
    return;
  }
  if (state.jobId !== id || !state.job || state.job.state !== "ready") return;
  state.preview = preview;

  const scan = state.job.scan;
  const nowFrames = scan.series.map((s) => s.frame);
  const nowGroups = linkGroups(nowFrames);
  const afterGroups = linkGroups(scan.series.map((s) => preview.series[s.uid].frame), { frames: nowFrames, groups: nowGroups });
  for (const row of $("seriesBody").querySelectorAll("tr[data-uid]")) {
    const series = scan.series.find((s) => s.uid === row.dataset.uid);
    const after = preview.series[series.uid];
    row.querySelector(".now").replaceChildren(linkBadge(series.frame, nowGroups));
    const cell = row.querySelector(".after");
    if (after.selected) {
      const parts = [el("span", { class: "pill new", text: "new IDs" })];
      if (state.options.frame) parts.unshift(linkBadge(after.frame, afterGroups), document.createTextNode(" "));
      cell.replaceChildren(...parts);
    } else {
      cell.replaceChildren(linkBadge(after.frame, afterGroups), document.createTextNode(" "), el("span", { class: "pill same", text: "unchanged" }));
    }
  }

  const o = state.options;
  const button = $("runButton"), summary = $("runSummary");
  const nothingToChange = !o.frame && !o.series && !o.sop;
  button.disabled = preview.seriesCount === 0;
  button.textContent = preview.seriesCount === 0 ? "Unlink" : `Unlink ${plural(preview.seriesCount, "series", "series")}`;
  if (preview.seriesCount === 0) {
    summary.textContent = nothingToChange ? "Turn on at least one identifier under \"What to change\"."
      : o.modalities.size === 0 ? "Choose at least one image type under \"Which images\"."
      : "No series are selected.";
  } else {
    const what = [o.frame && "Frame of Reference", o.series && "Series Instance", o.sop && "SOP Instance"].filter(Boolean).join(", ");
    const before = [...nowGroups.values()].reduce((n, g) => n + g.size, 0);
    const after = [...afterGroups.values()].reduce((n, g) => n + g.size, 0);
    const links = before === 0 && after === 0 ? "" : ` Linked series: ${before} now → ${after} after.`;
    summary.textContent = `New ${what} UIDs for ${plural(preview.fileCount, "file")} in ${plural(preview.seriesCount, "series", "series")}.${links}`;
  }
}

function confirmAndRun() {
  const job = state.job, preview = state.preview;
  if (!preview || preview.seriesCount === 0) return;
  if (job.kind === "local" && !state.options.workOnCopy) {
    $("confirmTitle").textContent = "Change these files in place?";
    $("confirmText").textContent = `${plural(preview.fileCount, "file")} in ${plural(preview.seriesCount, "series", "series")} will be rewritten where they are. This cannot be undone.`;
    $("confirmPath").textContent = job.location;
    $("confirmDialog").showModal();
  } else {
    run();
  }
}

async function run() {
  try {
    await api("POST", `/api/jobs/${state.jobId}/run`, changeRequest());
    showBusy("Unlinking", "Starting", 0, 0, "", "run");
    poll();
  } catch (error) {
    toast(error.message);
  }
}

// ---- Finish -----------------------------------------------------------------------------------

function renderDone() {
  const job = state.job, result = job.result;
  const failed = result.failureCount > 0;
  const iconBox = $("doneIcon");
  iconBox.className = `result-icon${result.cancelled || failed ? " warn" : ""}`;
  iconBox.replaceChildren(icon(result.cancelled || failed ? ICONS.warn : ICONS.check));
  $("doneTitle").textContent = result.cancelled ? "Stopped early" : failed ? "Finished with problems" : "Unlinked";
  $("doneDetail").textContent = result.cancelled
    ? "You cancelled part-way. The series already rewritten are complete and keep their new identifiers; the remaining series are unchanged."
    : failed ? "Most files were rewritten, but some could not be changed. They are listed below and were left as they were."
    : "Every selected series now carries new identifiers and will import as independent data.";

  $("doneStats").replaceChildren(
    el("div", { class: "stat" }, el("b", { text: number(result.seriesChanged) }), el("span", { text: "series" })),
    el("div", { class: "stat" }, el("b", { text: number(result.filesChanged) }), el("span", { text: "files rewritten" })),
    el("div", { class: "stat" }, el("b", { text: `${result.seconds}s` }), el("span", { text: "elapsed" })));

  const location = $("doneLocation");
  location.replaceChildren();
  if (result.outputFolder) {
    location.append(el("div", { class: "path", text: result.outputFolder }));
  }

  const actions = $("doneActions");
  actions.replaceChildren();
  if (result.canDownload) {
    const link = el("a", { class: "btn primary large", href: `/api/jobs/${job.id}/download`, download: "" }, icon(ICONS.download), document.createTextNode("Download unlinked .zip"));
    actions.append(link);
  } else if (result.outputFolder) {
    actions.append(el("button", {
      class: "btn primary", type: "button", text: "Copy folder path",
      onclick: async () => {
        try { await navigator.clipboard.writeText(result.outputFolder); toast("Folder path copied."); }
        catch { toast("Could not copy - select the path above instead."); }
      },
    }));
  }
  actions.append(
    el("a", { class: "btn", href: `/api/jobs/${job.id}/report.csv`, download: "", text: "UID report (.csv)" }),
    el("button", { class: "btn", type: "button", text: "Unlink another", onclick: startOver }));

  const failures = $("doneFailures");
  failures.replaceChildren();
  if (failed) {
    const list = el("div", { class: "failures" });
    for (const failure of result.failures) {
      list.append(el("div", {}, el("b", { text: failure.file }), document.createTextNode(` - ${failure.error}`)));
    }
    if (result.failureCount > result.failures.length) {
      list.append(el("div", { text: `...and ${number(result.failureCount - result.failures.length)} more.` }));
    }
    failures.append(list);
  }

  $("doneNote").textContent = result.canDownload
    ? `The upload is deleted from the server when you choose "Unlink another", close this page, or after ${state.info.retentionMinutes} idle minutes. The UID report lists old and new identifiers and contains no patient details.`
    : "The UID report lists old and new identifiers for each series and contains no patient details.";
  show("done");
}

// ---- Wiring -----------------------------------------------------------------------------------

function wire() {
  // Theme
  $("themeButton").addEventListener("click", () => {
    const root = document.documentElement;
    const current = root.getAttribute("data-theme") || (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
    const next = current === "dark" ? "light" : "dark";
    root.setAttribute("data-theme", next);
    try { localStorage.setItem("unzipunlink.theme", next); } catch { /* not persisted */ }
  });

  // Dialogs
  for (const dialog of document.querySelectorAll("dialog")) {
    for (const closer of dialog.querySelectorAll("[data-close]")) closer.addEventListener("click", () => dialog.close());
    dialog.addEventListener("click", (event) => { if (event.target === dialog) dialog.close(); });
  }
  $("aboutButton").addEventListener("click", () => $("aboutDialog").showModal());

  // Upload
  const dropzone = $("dropzone");
  $("chooseFolder").addEventListener("click", (event) => { event.stopPropagation(); $("folderInput").click(); });
  $("chooseFiles").addEventListener("click", (event) => { event.stopPropagation(); $("filesInput").click(); });
  dropzone.addEventListener("keydown", (event) => {
    if ((event.key === "Enter" || event.key === " ") && event.target === dropzone) { event.preventDefault(); $("folderInput").click(); }
  });
  $("folderInput").addEventListener("change", (event) =>
    startUpload([...event.target.files].map((file) => ({ file, path: file.webkitRelativePath || file.name }))));
  $("filesInput").addEventListener("change", (event) =>
    startUpload([...event.target.files].map((file) => ({ file, path: file.name }))));
  for (const type of ["dragenter", "dragover"]) {
    dropzone.addEventListener(type, (event) => { event.preventDefault(); dropzone.classList.add("over"); });
  }
  for (const type of ["dragleave", "drop"]) {
    dropzone.addEventListener(type, (event) => { event.preventDefault(); dropzone.classList.remove("over"); });
  }
  dropzone.addEventListener("drop", async (event) => {
    try {
      startUpload(await filesFromDrop(event.dataTransfer));
    } catch {
      toast("Could not read what was dropped. Try the Choose folder button instead.");
    }
  });
  // Dropping a file outside the zone must not navigate the page away.
  window.addEventListener("dragover", (event) => event.preventDefault());
  window.addEventListener("drop", (event) => event.preventDefault());

  // Local folder
  $("openLocal").addEventListener("click", openLocal);
  $("localPath").addEventListener("keydown", (event) => { if (event.key === "Enter") openLocal(); });
  $("browseButton").addEventListener("click", () => {
    $("browseDialog").showModal();
    const typed = $("localPath").value.trim();
    browse(typed && !/\.zip$/i.test(typed) ? typed : "").then(() => {
      if ($("browseError").textContent) browse("");
    });
  });
  $("browseSelect").addEventListener("click", () => chooseBrowsed(state.browseSelection || state.browsePath));

  // Busy
  $("busyCancel").addEventListener("click", async () => {
    if (state.upload || !state.job || state.job.state === "preparing") {
      startOver();
    } else {
      $("busyDetail").textContent = "Stopping once the current series is finished...";
      try { await api("POST", `/api/jobs/${state.jobId}/cancel`); } catch (error) { toast(error.message); }
    }
  });

  // Review
  $("startOver").addEventListener("click", startOver);
  $("errorBack").addEventListener("click", startOver);
  $("optFrame").addEventListener("change", (event) => { state.options.frame = event.target.checked; if (event.target.checked) state.options.sop = true; syncOptions(); });
  $("optSeries").addEventListener("change", (event) => { state.options.series = event.target.checked; if (event.target.checked) state.options.sop = true; syncOptions(); });
  $("optSop").addEventListener("change", (event) => { state.options.sop = event.target.checked; syncOptions(); });
  $("optKeepCt").addEventListener("change", (event) => { state.options.keepCt = event.target.checked; syncOptions(); });
  $("optKeepPet").addEventListener("change", (event) => { state.options.keepPet = event.target.checked; syncOptions(); });
  $("outInPlace").addEventListener("change", () => { state.options.workOnCopy = false; syncOptions(); });
  $("outCopy").addEventListener("change", () => { state.options.workOnCopy = true; syncOptions(); });
  $("checkAll").addEventListener("change", (event) => {
    for (const s of state.job.scan.series) {
      if (!s.supported || !state.options.modalities.has(s.modality)) continue;
      if (event.target.checked) state.options.excluded.delete(s.uid); else state.options.excluded.add(s.uid);
    }
    syncOptions();
  });
  $("runButton").addEventListener("click", confirmAndRun);
  $("confirmOk").addEventListener("click", () => { $("confirmDialog").close(); run(); });

  // Uploaded data should not linger once the page is gone.
  window.addEventListener("pagehide", () => {
    if (state.jobId && (!state.job || state.job.kind === "upload")) discardJob();
  });
}

async function init() {
  wire();
  try {
    state.info = await api("GET", "/api/info");
  } catch (error) {
    showError(error.message);
    return;
  }
  const info = state.info;
  $("localCard").hidden = !info.localFolders;
  $("uploadPrivacy").textContent = info.localFolders
    ? `Files are sent only to this app on ${info.machine} and are deleted when you finish or after ${info.retentionMinutes} idle minutes.`
    : `Files are sent only to the Unzip & Unlink server (${info.machine}), never to a third party, and are deleted when you finish or after ${info.retentionMinutes} idle minutes.`;
  $("footerInfo").textContent = `Unzip & Unlink ${info.version} · running on ${info.machine}`;
  $("aboutVersion").textContent = `Version ${info.version}`;
  show("add");
}

init();
