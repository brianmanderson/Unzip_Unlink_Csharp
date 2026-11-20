// Static test server for the browser edition. Serves a published site under a sub-path, the way GitHub Pages does
// (https://<owner>.github.io/Unzip_Unlink_Csharp/), gzip-compressed when the client accepts it, and nothing at "/".
//   node server.mjs <siteDir> <port> [mount=dir ...]      e.g. demo=C:\data\SyntheticDemo\DemoStudy
// GET /__log returns every request served so far (used to check that nothing leaves the sub-path).
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import zlib from "node:zlib";

export const SUB_PATH = "/Unzip_Unlink_Csharp/";

const TYPES = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript", ".mjs": "text/javascript", ".css": "text/css",
  ".json": "application/json", ".wasm": "application/wasm", ".svg": "image/svg+xml", ".png": "image/png",
  ".dat": "application/octet-stream", ".zip": "application/zip", ".dcm": "application/dicom",
};

export function startServer(siteDir, port, extraMounts = {}) {
  const mounts = { [SUB_PATH]: path.resolve(siteDir) };
  for (const [name, dir] of Object.entries(extraMounts)) mounts[`/${name}/`] = path.resolve(dir);
  const log = [];
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, "http://localhost");
    if (url.pathname === "/__log") {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify(log));
      return;
    }
    const mount = Object.keys(mounts).find((m) => url.pathname.startsWith(m));
    const fail = (status) => { log.push({ url: url.pathname, status }); res.writeHead(status); res.end(); };
    if (!mount) return fail(404);
    const root = mounts[mount];
    let file = path.resolve(root, decodeURIComponent(url.pathname.slice(mount.length)));
    if (file !== root && !file.startsWith(root + path.sep)) return fail(403);
    if (url.pathname.endsWith("/")) file = path.join(file, "index.html");
    fs.stat(file, (error, stat) => {
      if (error || !stat.isFile()) return fail(404);
      const headers = { "Content-Type": TYPES[path.extname(file).toLowerCase()] || "application/octet-stream", "Cache-Control": "no-cache" };
      let body = fs.readFileSync(file);
      if (/\bgzip\b/.test(req.headers["accept-encoding"] || "") && !/\.(zip|png|dcm)$/i.test(file)) {
        body = zlib.gzipSync(body, { level: 6 });
        headers["Content-Encoding"] = "gzip";
      }
      headers["Content-Length"] = body.length;
      log.push({ url: url.pathname, status: 200, bytes: body.length, raw: stat.size });
      res.writeHead(200, headers);
      res.end(body);
    });
  });
  return new Promise((resolve) => server.listen(port, "127.0.0.1", () => resolve({ server, log, url: `http://127.0.0.1:${port}` })));
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1"))) {
  const [siteDir, port = "5190", ...extra] = process.argv.slice(2);
  const mounts = Object.fromEntries(extra.map((m) => m.split("=")));
  const { url } = await startServer(siteDir, Number(port), mounts);
  console.log(`serving ${siteDir} at ${url}${SUB_PATH}`);
}
