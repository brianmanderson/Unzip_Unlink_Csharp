// Retakes the web app's documentation screenshots (docs/screenshots/web-add.png and web-review.png) against a
// running UnzipUnlink.Web, with the synthetic demo study. The machine name is replaced in the picture.
//
//   dotnet run --project ../UnzipUnlink.Web -- --urls http://localhost:5185 --UnzipUnlink:OpenBrowser=false
//   node web-shots.mjs [--url http://localhost:5185] [--out ../docs/screenshots] [--channel msedge]
import { chromium } from "playwright-core";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : fallback;
};
const url = arg("url", "http://localhost:5185");
const out = path.resolve(arg("out", path.join(here, "../docs/screenshots")));
const folder = path.resolve(arg("folder", path.join(here, "../data/SyntheticDemo/DemoStudy")));

const browser = await chromium.launch({ channel: arg("channel", "msedge"), headless: true });
try {
  const context = await browser.newContext({ viewport: { width: 1360, height: 900 }, colorScheme: "light" });
  const page = await context.newPage();
  const maskHost = () => page.evaluate((host) => {
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      if (node.nodeValue.toLowerCase().includes(host.toLowerCase())) {
        node.nodeValue = node.nodeValue.replace(new RegExp(host, "gi"), "IMAGING-PC");
      }
    }
  }, os.hostname());

  await page.goto(url);
  await page.locator("#view-add").waitFor({ state: "visible" });
  await page.locator("#footerInfo", { hasText: "running on" }).waitFor();
  await maskHost();
  await page.screenshot({ path: path.join(out, "web-add.png") });

  await page.setInputFiles("#folderInput", folder);
  await page.locator("#view-review").waitFor({ state: "visible", timeout: 120000 });
  await page.locator("#runButton", { hasText: /Unlink \d+ series/ }).waitFor();
  await maskHost();
  await page.screenshot({ path: path.join(out, "web-review.png") });
  console.log("saved web-add.png and web-review.png to " + out);
} finally {
  await browser.close();
}
