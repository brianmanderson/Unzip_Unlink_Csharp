// End-to-end check of the browser edition, in an installed Edge or Chrome driven by playwright-core.
//
//   dotnet publish UnzipUnlink.Browser/UnzipUnlink.Browser.csproj -c Release -o publish/pages
//   cd UnzipUnlink.BrowserCheck && npm install && node browser-check.mjs [--channel msedge|chrome] [--screenshots ../docs/screenshots]
//
// Serves publish/pages/wwwroot from a sub-path (/Unzip_Unlink_Csharp/, like GitHub Pages) and, in light and dark:
//  - loads the page and checks that every request stays under the sub-path, with no console errors or CSP violations;
//  - feeds it data/SyntheticDemo/DemoStudy_zipped.zip, runs with the defaults, downloads the .zip and the UID report,
//    and verifies the result with the native engine (Verifier.cs), including the structure set that follows its CT;
//  - in the second theme, also picks a tag from the searchable list, checks that a bad value blocks the run, and
//    verifies the new value in every rewritten file;
// and once:
//  - compares in-browser rewrites byte for byte with the native path-based UnlinkRunner.RewriteFile;
//  - "Open a folder" in place and to a new folder (Chromium), with the folder picker answered by an origin-private
//    folder so no dialog is needed, and verifies both results and that the source of the copy is untouched;
//  - checks that a study above the .zip ceiling is refused with a clear message.
// Synthetic data only. Exit code 0 when every check passes.
import { chromium } from "playwright-core";
import { execFileSync } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { startServer, SUB_PATH } from "./server.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, "..");
const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : fallback;
};
const site = path.resolve(arg("site", path.join(repo, "publish/pages/wwwroot")));
const demo = path.resolve(arg("demo", path.join(repo, "data/SyntheticDemo/DemoStudy")));
const demoZip = path.resolve(arg("zip", path.join(repo, "data/SyntheticDemo/DemoStudy_zipped.zip")));
const screenshots = arg("screenshots", null);
const out = path.resolve(arg("out", path.join(os.tmpdir(), "uu-browser-check")));
const channel = arg("channel", "msedge");
const port = Number(arg("port", "5197"));
const themes = arg("themes", "light,dark").split(",");
fs.mkdirSync(out, { recursive: true });

// ---- Native side ------------------------------------------------------------------------------------
const tool = path.join(here, "bin/Release/net10.0/UnzipUnlink.BrowserCheck.dll");
execFileSync("dotnet", ["build", here, "-c", "Release", "-nologo", "-v", "q"], { stdio: "inherit" });
const native = (...args) => execFileSync("dotnet", [tool, ...args], { encoding: "utf8" });

// ---- Reporting --------------------------------------------------------------------------------------
const results = [];
function check(name, ok, detail = "") {
  results.push({ name, ok });
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? `  - ${detail}` : ""}`);
}
function verify(name, output, ...tagEdits) {
  let text, ok = true;
  try { text = native("verify", demo, output, ...tagEdits); } catch (error) { text = String(error.stdout ?? error.message); ok = false; }
  check(name, ok && text.includes("VERIFY OK"), text.trim().split("\n").filter((l) => !l.startsWith("VERIFY")).join(" | "));
}

const walk = (dir) => fs.readdirSync(dir, { withFileTypes: true })
  .flatMap((e) => e.isDirectory() ? walk(path.join(dir, e.name)) : [path.join(dir, e.name)]);
const demoFiles = walk(demo).map((f) => path.relative(demo, f).split(path.sep).join("/")).sort();

// ---- Run --------------------------------------------------------------------------------------------
const { server, log, url } = await startServer(site, port, { demo });
const appUrl = url + SUB_PATH;
const browser = await chromium.launch({ channel, headless: true });
console.log(`${channel} ${browser.version()} - ${appUrl}`);

async function openApp(theme, init) {
  const context = await browser.newContext({ colorScheme: theme, acceptDownloads: true, viewport: { width: 1360, height: 900 }, deviceScaleFactor: 1 });
  await context.addInitScript(() => {
    window.__csp = [];
    document.addEventListener("securitypolicyviolation", (e) => window.__csp.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  if (init) await context.addInitScript(init);
  const page = await context.newPage();
  const problems = [];
  page.on("console", (m) => { if (m.type() === "error") problems.push(m.text()); });
  page.on("pageerror", (e) => problems.push(e.message));
  const requests = [];
  page.on("request", (r) => requests.push(r.url()));
  const started = Date.now();
  await page.goto(appUrl);
  await page.locator("#footerInfo", { hasText: "nothing leaves" }).waitFor({ timeout: 60000 });
  return { context, page, problems, requests, loadMs: Date.now() - started };
}

async function download(page, selector, file) {
  const [dl] = await Promise.all([page.waitForEvent("download"), page.click(selector)]);
  await dl.saveAs(file);
  return { file, name: dl.suggestedFilename() };
}

async function waitDone(page) {
  await page.locator("#view-done").waitFor({ state: "visible", timeout: 300000 });
  return (await page.textContent("#doneTitle")).trim();
}

try {
  // 1. Zip in, zip out - light and dark.
  for (const theme of themes) {
    const before = log.length;
    const { context, page, problems, requests, loadMs } = await openApp(theme);
    check(`[${theme}] page and engine load from ${SUB_PATH}`, true, `${loadMs} ms to ready`);
    const isDark = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
    await page.setInputFiles("#filesInput", demoZip);
    await page.locator("#view-review").waitFor({ state: "visible", timeout: 120000 });
    await page.locator("#runButton", { hasText: /Unlink \d+ series/ }).waitFor();
    const rows = await page.locator("#seriesBody tr[data-uid]").count();
    const summary = (await page.textContent("#reviewSummary")).trim();
    check(`[${theme}] review lists the study`, rows === 18, `${rows} series; ${summary}; body ${isDark}`);
    const after = await page.locator("#seriesBody td.after .link-badge").allTextContents();
    check(`[${theme}] before/after link preview`, after.length === 18, `after: ${after.join(" ")}`);
    const structureSet = page.locator("#seriesBody tr[data-uid]", { hasText: "RTSTRUCT" });
    check(`[${theme}] the structure set is offered to follow its images`,
      await page.isChecked("#optKeepStruct") && (await structureSet.locator("td.after").innerText()).includes("follows its images"),
      (await structureSet.locator("td.after").innerText()).replace(/\s+/g, " "));

    // Second theme only, so the first run stays the plain defaults: change a tag picked from the searchable list.
    const tagEdits = [];
    if (theme !== themes[0]) {
      await page.fill("#tagSearch", "0010,0020");
      await page.locator("#tagResults button").first().waitFor();   // the tag list is loaded on first use
      const offered = await page.locator("#tagResults button b").allTextContents();
      await page.locator("#tagResults button").first().click();
      await page.fill("#tagSearch", "study date");
      await page.locator("#tagResults button", { hasText: "StudyDate" }).first().click();
      const inputs = page.locator("#tagEdits input");
      await inputs.nth(0).fill("BROWSER-CHECK");
      await inputs.nth(1).fill("tomorrow");
      await page.locator("#tagEdits .tag-error", { hasText: "YYYYMMDD" }).waitFor();
      const blocked = await page.isDisabled("#runButton");
      await inputs.nth(1).fill("20300101");
      await page.locator("#tagEdits .tag-now", { hasText: "DEMO-0001" }).waitFor();
      await page.locator("#seriesBody .edits", { hasText: "PatientID: DEMO-0001 → BROWSER-CHECK" }).first().waitFor();
      await page.locator("#runButton:not([disabled])").waitFor();
      tagEdits.push("PatientID=BROWSER-CHECK", "StudyDate=20300101");
      check(`[${theme}] a tag is found by number, a bad value blocks the run, old → new is previewed`,
        offered.length === 1 && offered[0] === "PatientID" && blocked, `offered ${offered.join(",")}; run blocked while invalid: ${blocked}`);
    }
    // Viewport, not full page: the sticky action bar belongs at the bottom of the window.
    await page.screenshot({ path: path.join(out, `browser-review-${theme}.png`) });

    await page.click("#runButton");
    const title = await waitDone(page);
    const stats = (await page.locator("#doneStats").innerText()).replace(/\s+/g, " ");
    check(`[${theme}] run finishes`, title === "Unlinked", `${title}: ${stats}`);
    await page.screenshot({ path: path.join(out, `browser-done-${theme}.png`), fullPage: true });

    const zip = await download(page, "#downloadZip", path.join(out, `result-${theme}.zip`));
    check(`[${theme}] .zip download`, zip.name === "DemoStudy_zipped_unlinked.zip", `${zip.name}, ${fs.statSync(zip.file).size} bytes`);
    const csv = await download(page, "#downloadReport", path.join(out, `report-${theme}.csv`));
    const lines = fs.readFileSync(csv.file, "utf8").trim().split(/\r\n/);
    check(`[${theme}] UID report`, csv.name === "DemoStudy_zipped_uid_report.csv" && lines.length === 19 && lines[0].startsWith("Modality,")
      && lines.some((l) => l.startsWith("RTSTRUCT,")) && tagEdits.every((e) => lines[1].includes(e)),
      `${csv.name}, ${lines.length - 1} series rows`);
    verify(`[${theme}] downloaded .zip verified against the native engine`, zip.file, ...tagEdits);

    const foreign = requests.filter((u) => !u.startsWith(appUrl) && !u.startsWith("blob:") && !u.startsWith("data:"));
    const served = log.slice(before).filter((r) => !r.url.startsWith(SUB_PATH));
    check(`[${theme}] every request stays under ${SUB_PATH}`, foreign.length === 0 && served.length === 0, [...foreign, ...served.map((r) => r.url)].join(", "));
    const csp = await page.evaluate(() => window.__csp);
    check(`[${theme}] no console errors or CSP violations`, problems.length === 0 && csp.length === 0, [...problems, ...csp].join(" | "));
    if (screenshots && theme === "light") {
      fs.mkdirSync(screenshots, { recursive: true });
      fs.copyFileSync(path.join(out, "browser-review-light.png"), path.join(screenshots, "browser-review.png"));
      fs.copyFileSync(path.join(out, "browser-done-light.png"), path.join(screenshots, "browser-done.png"));
    }
    await context.close();
  }

  // 2. Byte identity with the native path-based rewrite.
  {
    const { context, page } = await openApp("light");
    const sample = ["4DCT/00", "4DCT/AVG", "CT_PLAN", "MR/T1", "MR/DWI", "PETCT/CT", "PETCT/PET"]
      .flatMap((dir) => { const all = demoFiles.filter((f) => f.startsWith(dir + "/")); return [all[0], all[all.length - 1]]; });
    const uid = () => "2.25." + BigInt("0x" + crypto.randomBytes(15).toString("hex")).toString();
    let same = 0, total = 0;
    const mismatches = [];
    for (const rel of sample) {
      for (const [frame, series, sop] of [[uid(), "", uid()], [uid(), uid(), uid()], ["", "", uid()]]) {
        const b64 = await page.evaluate(async ({ rel, frame, series, sop }) => {
          // A second engine worker, outside the app, called directly through its message protocol.
          const worker = window.__checkWorker ??= new Worker("engine-worker.js", { type: "module" });
          const rpc = (op, args) => new Promise((resolve, reject) => {
            const id = `${op}-${Math.random()}`;
            const listener = (e) => {
              if (e.data.id !== id) return;
              worker.removeEventListener("message", listener);
              e.data.ok ? resolve(e.data.result) : reject(new Error(e.data.error));
            };
            worker.addEventListener("message", listener);
            worker.postMessage({ id, op, args });
          });
          await rpc("init");
          const bytes = new Uint8Array(await (await fetch(`/demo/${rel}`)).arrayBuffer());
          const { output } = await rpc("diagnose", { bytes, frame, series, sop });
          let s = "";
          for (let i = 0; i < output.length; i += 0x8000) s += String.fromCharCode(...output.subarray(i, i + 0x8000));
          return btoa(s);
        }, { rel, frame, series, sop });
        const reference = path.join(out, "reference.dcm");
        native("reference", path.join(demo, rel), reference, frame, series, sop);
        total++;
        if (fs.readFileSync(reference).equals(Buffer.from(b64, "base64"))) same++; else mismatches.push(rel);
      }
    }
    check("in-browser rewrite is byte-identical to native UnlinkRunner.RewriteFile", same === total,
      `${same}/${total} (CT, 4DCT, MR, PET; frame+SOP, frame+series+SOP, SOP only) ${mismatches.join(", ")}`);
    await context.close();
  }

  // 3. "Open a folder" - in place, then to a new folder. Chromium only.
  const pickerShim = () => {
    // Answers the folder picker with an origin-private folder: the File System Access code path is the real one,
    // only the dialog is skipped. window.__pick holds the folder paths to hand out, in order.
    window.__pick = [];
    const resolveDir = async (p) => {
      let dir = await navigator.storage.getDirectory();
      for (const part of p.split("/")) dir = await dir.getDirectoryHandle(part, { create: true });
      return dir;
    };
    window.__resolveDir = resolveDir;
    window.showDirectoryPicker = async () => resolveDir(window.__pick.shift());
  };
  const seed = (page, target) => page.evaluate(async ({ target, files }) => {
    for (const rel of files) {
      const parts = `${target}/${rel}`.split("/");
      let dir = await window.__resolveDir(parts.slice(0, -1).join("/"));
      const handle = await dir.getFileHandle(parts[parts.length - 1], { create: true });
      const writable = await handle.createWritable();
      await writable.write(await (await fetch(`/demo/${rel}`)).arrayBuffer());
      await writable.close();
    }
  }, { target, files: demoFiles });
  // Packs an origin-private folder into a .zip with our own writer and hands it to Playwright as a download.
  const exportFolder = async (page, folder, file) => {
    await page.evaluate(async ({ folder }) => {
      const { ZipWriter } = await import("./zip.js");
      const zip = new ZipWriter();
      const walk = async (dir, prefix) => {
        for await (const [name, handle] of dir.entries()) {
          if (handle.kind === "directory") await walk(handle, `${prefix}${name}/`);
          else await zip.add(prefix + name, new Uint8Array(await (await handle.getFile()).arrayBuffer()));
        }
      };
      await walk(await window.__resolveDir(folder), "DemoStudy/");
      const link = document.createElement("a");
      link.href = URL.createObjectURL(zip.finish());
      link.download = "export.zip";
      link.id = "__export";
      document.body.append(link);
    }, { folder });
    const [dl] = await Promise.all([page.waitForEvent("download"), page.evaluate(() => document.getElementById("__export").click())]);
    await dl.saveAs(file);
    await page.evaluate(() => document.getElementById("__export").remove());
  };
  const hashes = (page, folder) => page.evaluate(async ({ folder }) => {
    const result = {};
    const walk = async (dir, prefix) => {
      for await (const [name, handle] of dir.entries()) {
        if (handle.kind === "directory") await walk(handle, `${prefix}${name}/`);
        else result[prefix + name] = [...new Uint8Array(await crypto.subtle.digest("SHA-256", await (await handle.getFile()).arrayBuffer()))].map((b) => b.toString(16).padStart(2, "0")).join("");
      }
    };
    await walk(await window.__resolveDir(folder), "");
    return result;
  }, { folder });

  {
    const { context, page, problems } = await openApp("light", pickerShim);
    const visible = await page.locator("#folderCard").isVisible();
    check("\"Open a folder\" is offered where the File System Access API exists", visible);

    // In place
    await seed(page, "inplace/DemoStudy");
    await page.evaluate(() => window.__pick.push("inplace/DemoStudy"));
    await page.click("#openFolder");
    await page.locator("#view-review").waitFor({ state: "visible", timeout: 120000 });
    await page.locator("#runButton", { hasText: /Unlink \d+ series/ }).waitFor();
    const outputCard = await page.locator("#outputCard").isVisible();
    await page.click("#runButton");
    await page.locator("#confirmDialog").waitFor({ state: "visible" });
    await page.click("#confirmOk");
    const title = await waitDone(page);
    const created = await page.evaluate(async () => {
      const names = [];
      for await (const [name] of (await window.__resolveDir("inplace")).entries()) names.push(name);
      return names;
    });
    check("folder in place: runs after confirmation", title === "Unlinked" && outputCard && created.join() === "DemoStudy", `${title}; folders now: ${created.join(", ")}`);
    await exportFolder(page, "inplace/DemoStudy", path.join(out, "folder-inplace.zip"));
    verify("folder in place: rewritten folder verified against the native engine", path.join(out, "folder-inplace.zip"));

    // To a new folder
    await page.click("text=Unlink another");
    await seed(page, "copysrc/DemoStudy");
    await page.evaluate(() => window.__pick.push("copysrc/DemoStudy", "copydest"));
    await page.click("#openFolder");
    await page.locator("#view-review").waitFor({ state: "visible", timeout: 120000 });
    await page.locator("#runButton", { hasText: /Unlink \d+ series/ }).waitFor();
    await page.check("#outCopy");
    await page.click("#runButton");
    const copyTitle = await waitDone(page);
    const location = (await page.textContent("#doneLocation")).trim();
    check("folder copy: writes DemoStudy_unlinked", copyTitle === "Unlinked" && location === "DemoStudy_unlinked", `${copyTitle}; ${location}`);
    await exportFolder(page, "copydest/DemoStudy_unlinked", path.join(out, "folder-copy.zip"));
    verify("folder copy: new folder verified against the native engine", path.join(out, "folder-copy.zip"));
    const source = await hashes(page, "copysrc/DemoStudy");
    const originalHashes = Object.fromEntries(demoFiles.map((rel) => [rel, crypto.createHash("sha256").update(fs.readFileSync(path.join(demo, rel))).digest("hex")]));
    const changed = demoFiles.filter((rel) => source[rel] !== originalHashes[rel]);
    check("folder copy: source folder untouched", changed.length === 0 && Object.keys(source).length === demoFiles.length, `${changed.length} changed`);
    check("folder runs: no console errors", problems.length === 0, problems.join(" | "));
    await context.close();
  }

  // 4. Above the .zip ceiling: refused up front with a clear message.
  {
    const fake = path.join(out, "too-big.zip");
    native("fakezip", fake, String(Math.round(2.2 * 1024 ** 3)));
    const { context, page } = await openApp("light");
    await page.setInputFiles("#filesInput", fake);
    await page.locator("#view-error").waitFor({ state: "visible", timeout: 60000 });
    const text = (await page.textContent("#errorText")).trim();
    check("study above the .zip ceiling is refused with a clear message", /2\.2 GB/.test(text) && /Open a folder/.test(text), text);
    await context.close();
  }
} catch (error) {
  check("check script ran to completion", false, error.stack);
} finally {
  await browser.close();
  server.close();
}

const failed = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} checks passed. Artifacts: ${out}`);
process.exit(failed === 0 ? 0 : 1);
