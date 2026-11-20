# Hosting the browser edition on GitHub Pages

The browser edition (`UnzipUnlink.Browser/`) is the Unzip & Unlink engine compiled to WebAssembly.
It is a set of static files: the browser downloads them once and does all the DICOM work locally.
That is what lets it live on GitHub Pages, which can only serve static files.

**Address once live:** <https://brianmanderson.github.io/Unzip_Unlink_Csharp/>

**The one switch:** *Settings -> Pages -> Build and deployment -> Source = **GitHub Actions***.
Everything else is in `.github/workflows/pages.yml`, which deploys on every push to `main` that
touches the browser app, the engine or the tests.

- [One-time setup](#one-time-setup)
- [Deploying and rolling back](#deploying-and-rolling-back)
- [Before pointing clinical users at it](#before-pointing-clinical-users-at-it)
- [How the workflow works, and why](#how-the-workflow-works-and-why)
- [What GitHub Pages does and does not give a .NET WebAssembly app](#what-github-pages-does-and-does-not-give-a-net-webassembly-app)
- [Testing a Pages-shaped build locally](#testing-a-pages-shaped-build-locally)
- [Custom domain](#custom-domain)
- [Troubleshooting](#troubleshooting)

## Repository facts (checked 2026-11-20)

| | |
|---|---|
| Repository | `brianmanderson/Unzip_Unlink_Csharp`, **public** |
| Default branch | `main` (also `dev`) |
| GitHub Pages | **not enabled** (`GET /repos/.../pages` returns 404) |
| Actions | enabled, all actions allowed, no SHA pinning required |
| Default `GITHUB_TOKEN` permissions | write (the workflow narrows its own to `contents: read`) |
| Environments | none yet - GitHub creates `github-pages` when Pages is switched to GitHub Actions |

A public repository on any GitHub plan can use Pages. The site URL is
`https://<owner>.github.io/<repository>/`, so the app is served from the sub-path
`/Unzip_Unlink_Csharp/` - which is why its `<base href>` must be rewritten (see below).

## One-time setup

1. Merge the branch that contains `UnzipUnlink.Browser/` and `.github/workflows/pages.yml` into `main`
   (via `dev` as usual).
2. On GitHub: **Settings -> Pages**. Under **Build and deployment**, set **Source** to
   **GitHub Actions**. There is nothing to save beyond that choice; do not pick a branch.
   This creates the `github-pages` environment, which by default only accepts deployments from the
   default branch.
3. Start the first deployment: **Actions -> Pages -> Run workflow** (branch `main`), or push any
   change under the watched paths. The run's summary shows the site URL when the `deploy` job
   finishes (usually a few minutes; the first deploy can take up to ~10 minutes to appear).
4. Optional, recommended: **Settings -> Pages -> Enforce HTTPS** (on by default for `github.io`).
5. Optional: put the URL in the repository's **About -> Website** box so people find it.

Nothing else is needed: no secrets, no personal access token, no `gh-pages` branch, no change to
the Actions permission settings (the workflow requests `pages: write` and `id-token: write` for its
deploy job itself).

## Deploying and rolling back

- **Deploy:** push to `main` touching `UnzipUnlink.Browser/`, `UnzipUnlink.Core/`,
  `UnzipUnlink.Tests/`, the workflow, or root build files - or run the workflow by hand. The tests
  must pass first; a failing test means no deploy and the old site stays up.
- **Pull requests and forks** build, test and attach the prepared site as a `browser-site` artifact
  (kept 7 days) so a reviewer can download and try it, but they **never deploy**. A manual run on
  any branch other than `main` is also build-only.
- **Roll back:** either `git revert` the bad change on `main` (it redeploys), or open an earlier
  successful **Pages** run in the Actions tab and choose **Re-run all jobs** - that rebuilds and
  redeploys the site from that run's commit.
- **Take the site down:** *Settings -> Pages -> Unpublish site* (or set Source back to
  *Deploy from a branch* with no branch). Re-enable by choosing GitHub Actions again and re-running.

## Before pointing clinical users at it

These are facts and questions, not legal advice. Each institution's privacy / information-security
office should make its own call.

**What is true of this site**

- **No upload.** The app runs entirely inside the browser tab. DICOM files are read from the user's
  disk, rewritten in memory and handed back as a download. There is no server-side code on
  GitHub Pages, and the app makes no network request after it has loaded.
- **No third parties.** The page loads nothing from other sites: no analytics, fonts, CDNs or
  trackers. All its files come from `brianmanderson.github.io`.
- **But the page itself is public.** Anyone can open it. There is no login and no way to restrict
  it to one institution on GitHub Pages for a public repository.
- **GitHub sees ordinary web traffic.** Like any web host, GitHub (and its CDN) receives each
  visitor's IP address, browser user-agent and the URLs requested - i.e. that someone opened the
  app, not what they processed. GitHub's privacy statement covers those logs.
- **The code that runs is whatever is on `main`.** Anyone with write access to the repository can
  change what users' browsers execute on their next visit. Branch protection and required reviews
  on `main` are the control for that.
- **It still holds patient data in a web page.** While working, the DICOM data is in the memory of a
  browser tab. Browser extensions installed on that machine can in principle see page content.
  Closing the tab discards it; the output goes wherever the user saves the download.

**Questions each institution should ask**

1. Does our policy allow loading identifiable patient data into a web page served from the
   internet, even if it is processed locally and never transmitted?
2. Is `github.io` reachable from the clinical network, and is it allowed there?
3. Do we need to pin a reviewed version (download the release / run the desktop app or the
   self-hosted web app instead) rather than always getting the latest `main`?
4. Are browser extensions on clinical workstations managed?
5. Who at our site is the contact if the tool misbehaves?

**What the app's landing page should say** (suggested text for the browser edition's header or
"About" panel):

> Everything happens on this computer. Your files are read and rewritten inside this browser tab
> and are never uploaded - not to GitHub, not to us, not to anyone. This page is public and loads
> no trackers. Check with your institution before using it with patient data. Prefer an offline
> copy? Download the desktop app.

The privacy statement is only credible if it stays true, so a reviewer should confirm in the
browser's Network tab that processing a study makes no requests (the local check below does this).

## How the workflow works, and why

`.github/workflows/pages.yml`, two jobs:

**`build`** (`ubuntu-latest`, `contents: read` + `pages: read`)

1. Decides whether this run may deploy: only a non-PR run on the default branch of a repository that
   is not a fork.
2. Checks out, installs the .NET 10 SDK.
3. Installs the `wasm-tools` workload **only if** the project sets `RunAOTCompilation` or
   `WasmBuildNative` to `true`. A plain interpreted WebAssembly app does not need it, and skipping
   it saves a few minutes per run.
4. Runs `UnzipUnlink.Tests` (cross-platform; they generate synthetic DICOM). The WPF desktop app is
   not built here, which is what makes Linux possible - `ci.yml` still builds everything on Windows.
5. `dotnet publish UnzipUnlink.Browser/UnzipUnlink.Browser.csproj -c Release -o publish/pages`.
6. When deploying, `actions/configure-pages` reports the site's real base path (`/Unzip_Unlink_Csharp`,
   or empty once a custom domain is set). Otherwise the path is derived from the repository name,
   so forks and renames need no edits.
7. Prepares `publish/pages/wwwroot`: rewrites `<base href>`, copies `index.html` to `404.html`,
   adds `.nojekyll`, deletes `.br`/`.gz` duplicates, and fails if any file is over 100 MB or the
   site is over 1 GB.
8. Uploads it: as the Pages artifact when deploying, otherwise as a normal `browser-site` artifact.

**`deploy`** (only when `build` said so; `pages: write` + `id-token: write`, environment
`github-pages`) runs `actions/deploy-pages`.

A concurrency group (`pages-deploy`) makes deployments queue rather than overlap, and never cancels
one mid-way; pull-request builds have their own group and supersede each other.

### Actions used (latest majors as of 2026-11-20, checked against each repository's releases)

| Action | Pinned to | Latest release |
|---|---|---|
| `actions/checkout` | `@v7` | v7.0.1 |
| `actions/setup-dotnet` | `@v6` | v6.1.0 |
| `actions/configure-pages` | `@v6` | v6.0.0 |
| `actions/upload-pages-artifact` | `@v5` | v5.0.0 (dotfiles are excluded unless `include-hidden-files: true`, which the workflow sets so `.nojekyll` survives) |
| `actions/upload-artifact` | `@v7` | (used by upload-pages-artifact v5 itself) |
| `actions/deploy-pages` | `@v5` | v5.0.1 |

`ci.yml` and `build.yml` still use `checkout@v4` / `setup-dotnet@v4`; they work, and can be bumped
separately.

### Why the Actions flow and not a `gh-pages` branch

GitHub's current recommended way to publish a custom-built site is the Actions flow
(`configure-pages` -> `upload-pages-artifact` -> `deploy-pages`). Compared with pushing build
output to a `gh-pages` branch, it:

- keeps tens of MB of compiled WebAssembly out of git history on every release;
- needs no write access to repository contents - only the narrowly scoped `pages: write` and a
  short-lived OIDC token (`id-token: write`), and only in the deploy job;
- skips Jekyll entirely, so `_framework/` is served as-is;
- goes through the `github-pages` environment, which records every deployment, links it to its run,
  and by default refuses deployments from branches other than `main`;
- rolls back by re-running an old run rather than by force-pushing a branch.

A `gh-pages` branch is only worth it if you want the deployed bytes browsable in git, which this
project does not need.

## What GitHub Pages does and does not give a .NET WebAssembly app

| Need | GitHub Pages | What we do |
|---|---|---|
| Serve folders starting with `_` (`_framework/`) | Jekyll would hide them, but Actions deployments are not run through Jekyll | Add `.nojekyll` anyway, so a branch-based deploy would also work |
| `.wasm` served as `application/wasm` | Yes | Nothing |
| Pre-compressed `.br` / `.gz` | Never served in place of the original (no content negotiation for them); Pages compresses some responses itself | Delete them from the artifact - they would only roughly double the upload |
| Custom response headers | **None** - no COOP/COEP, no CSP header, no `Cache-Control` control | No WebAssembly multithreading (needs cross-origin isolation); any CSP must be a `<meta http-equiv>` tag in `index.html` |
| Sub-path hosting | Site lives at `/Unzip_Unlink_Csharp/` | Rewrite `<base href>` at build time; the app must use relative URLs (the publish contract) |
| Unknown URLs | Serves `404.html` from the site root, if present | `404.html` is a copy of `index.html`, so a stale deep link still opens the app |
| Size | Published site up to 1 GB; files over 100 MB are not accepted; soft bandwidth 100 GB/month | Workflow fails early on either size limit. The app is a few tens of MB, so bandwidth is not a concern at departmental scale |
| HTTPS | Yes, automatic on `github.io` | Nothing |

### Caching and how users get a new version

GitHub Pages sends every file with `Cache-Control: max-age=600` (10 minutes) and an `ETag`, and none
of that is configurable. After a deploy:

- Within about 10 minutes, every browser revalidates `index.html` and gets the new one. A normal
  reload after that picks up the new version; no user action beyond a reload is needed.
- The files under `_framework/` are **not fingerprinted**: their names stay the same between
  deploys, because the engine runs in a Web Worker and import maps (which fingerprinted names rely
  on) do not reach workers. For up to about 10 minutes after a deploy a browser that visited
  recently can therefore hold a mix of old and new files. The worst case is that the engine fails
  to start; reloading after the 10 minutes (or a hard reload, Ctrl+F5) fixes it. Deploy outside
  working hours if that matters.
- If the browser project ever adds a **service worker** (offline / PWA mode), that changes: the
  service worker then decides when updates apply, and the app should show an "update available"
  prompt. Also, a service-worker asset manifest that hashes `index.html` would be invalidated by the
  `<base href>` rewrite - rewrite the base *before* that manifest is generated (e.g. pass the base
  path into the build) in that case.

## Testing a Pages-shaped build locally

This reproduces what the workflow does and serves it the way Pages does: from a sub-path, no
`.br`/`.gz` negotiation, no special headers. Needs the .NET 10 SDK and Python 3.

```powershell
$repo = 'Unzip_Unlink_Csharp'
$out  = Join-Path $env:TEMP 'pages-test'
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
dotnet publish UnzipUnlink.Browser/UnzipUnlink.Browser.csproj -c Release -o "$out/publish"

# Lay the site out under /<repo>/ like github.io does
$site = New-Item -ItemType Directory -Force "$out/root/$repo"
Copy-Item -Recurse "$out/publish/wwwroot/*" $site
(Get-Content "$site/index.html" -Raw) -replace '<base href="[^"]*"', "<base href=`"/$repo/`"" |
  Set-Content "$site/index.html" -NoNewline -Encoding utf8
Copy-Item "$site/index.html" "$site/404.html"
New-Item -ItemType File -Force "$site/.nojekyll" | Out-Null
Get-ChildItem $site -Recurse -Include *.br,*.gz | Remove-Item

python -m http.server 8080 --directory "$out/root"
```

Then open <http://localhost:8080/Unzip_Unlink_Csharp/>, open the browser's developer tools
(Network tab), and process `data/SyntheticDemo/DemoStudy_zipped.zip` (synthetic, safe to share).
Check: no failed requests, no request at all while processing, and the download opens.
Python's simple server does not serve `404.html` for unknown paths; that one behaviour is
Pages-only.

To test a pull request's build without building it yourself, download its `browser-site`
artifact from the run page, unzip it into `root/Unzip_Unlink_Csharp/`, and serve as above.

## Custom domain

1. Choose a domain you control (e.g. `unlink.example.org`). At your DNS provider, add a `CNAME`
   record pointing it to `brianmanderson.github.io`. (For an apex domain use the A/AAAA records in
   GitHub's Pages documentation.)
2. Recommended first: verify the domain for your account (*GitHub Settings -> Pages -> Add a
   domain*) to prevent takeover.
3. **Repository Settings -> Pages -> Custom domain**: enter it, Save, wait for the DNS check, then
   tick **Enforce HTTPS**.
4. Re-run the **Pages** workflow. `configure-pages` now reports an empty base path, so the app is
   rebuilt with `<base href="/">` automatically. No `CNAME` file is needed with Actions deployments.

The `github.io` address then redirects to the custom domain.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Blank page; console shows 404s for `/_framework/...` without `/Unzip_Unlink_Csharp/` | Wrong `<base href>` | Check the "base href set to" line in the build log; view source of the live page. Re-run the workflow after changing the domain |
| 404 on every `_framework/...` file, base looks right | `_framework` filtered by Jekyll (branch-based deploy without `.nojekyll`) | Use Source = GitHub Actions, or make sure `.nojekyll` is in the deployed root |
| Old version still showing after a deploy | Browser cache (10-minute `max-age`) or a service worker | Wait 10 minutes and reload; hard-reload (Ctrl+F5); if a service worker exists, close all tabs of the site |
| `deploy` job fails "Branch is not allowed to deploy to github-pages" | Run was on a non-default branch | Deploy from `main` (by design) |
| `configure-pages` fails with "Get Pages site failed" / 404 | Pages not enabled, or Source is still a branch | Settings -> Pages -> Source = GitHub Actions |
| `build` fails "index.html has no `<base href>`" | Browser project's `index.html` lost its base tag | Restore `<base href="/" />` in `UnzipUnlink.Browser/wwwroot/index.html` |
| Integrity / hash mismatch errors in the console | Files from two deploys mixed in a cache, or a file altered after publish | Hard-reload; if it persists, re-run the deploy |
| Workflow did not run on push | Change was outside the watched paths | Run it by hand (Actions -> Pages -> Run workflow) |
