// Times "Choose folder" in the browser edition: how long the scan and the run take for a folder, optionally with an
// artificial delay on every file read to stand in for a network drive (where each read is a round trip).
//
//   node time-folder.mjs --site ../publish/pages/wwwroot --folder ../data/SyntheticDemo/DemoStudy [--latency 20] [--channel msedge]
//
// Prints one line: scan seconds, run seconds. Synthetic data only.
import { chromium } from "playwright-core";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { startServer, SUB_PATH } from "./server.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : fallback;
};
const site = path.resolve(arg("site", path.join(here, "../publish/pages/wwwroot")));
const folder = path.resolve(arg("folder", path.join(here, "../data/SyntheticDemo/DemoStudy")));
const latency = Number(arg("latency", "0"));
const port = Number(arg("port", "5198"));

const { server, url } = await startServer(site, port, {});
const browser = await chromium.launch({ channel: arg("channel", "msedge"), headless: true });
try {
  const context = await browser.newContext({ acceptDownloads: true });
  if (latency > 0) {
    // Init scripts do not reach workers, so the delay is added to the worker script itself as it is served.
    const shim = `{ const read = Blob.prototype.arrayBuffer; Blob.prototype.arrayBuffer = async function () { await new Promise((r) => setTimeout(r, ${latency})); return read.call(this); }; }\n`;
    await context.route("**/engine-worker.js", async (route) => {
      const response = await route.fetch();
      await route.fulfill({ response, body: shim + (await response.text()) });
    });
  }
  const page = await context.newPage();
  await page.goto(url + SUB_PATH);
  await page.locator("#footerInfo", { hasText: "nothing leaves" }).waitFor({ timeout: 60000 });

  let started = Date.now();
  await page.setInputFiles("#folderInput", folder);
  await page.locator("#view-review").waitFor({ state: "visible", timeout: 600000 });
  await page.locator("#runButton", { hasText: /Unlink \d+ series/ }).waitFor();
  const scan = (Date.now() - started) / 1000;
  const summary = (await page.textContent("#reviewSummary")).trim();

  started = Date.now();
  await page.click("#runButton");
  await page.locator("#view-done").waitFor({ state: "visible", timeout: 600000 });
  const run = (Date.now() - started) / 1000;
  const title = (await page.textContent("#doneTitle")).trim();
  const stats = (await page.locator("#doneStats").innerText()).replace(/\s+/g, " ");
  console.log(`latency ${latency} ms | scan ${scan.toFixed(1)} s | run ${run.toFixed(1)} s | ${title}: ${stats} | ${summary}`);
} finally {
  await browser.close();
  server.close();
}
