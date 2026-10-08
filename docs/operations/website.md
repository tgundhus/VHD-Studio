---
type: runbook
title: Website deployment and search indexing
scope: VHD-Studio/site
specificity: exact
credibility: inferred
generated: {at: 2026-10-08, by: agent}
sources: [site/index.html, site/sitemap.xml, .github/workflows/pages.yml, Source/Directory.Build.props, docs/images]
links: [../decisions/001-landing-page-via-github-actions.md]
---

# Website deployment and search indexing

## Summary
The landing page at https://tgundhus.github.io/VHD-Studio/ is the static page in `site/`, published to
GitHub Pages by `.github/workflows/pages.yml`. It is the URL we register with Google Search Console,
because `github.com` itself can't be verified by us. The assembly step was run and checked locally;
the deploy steps and the Search Console flow have not yet been run against GitHub, hence
`credibility: inferred`.

## Behavior / structure
- **Triggers:** a push to `master` that touches `site/**`, `docs/images/**`,
  `Source/Directory.Build.props` or the workflow itself, and manual runs (Actions → Website → Run workflow).
- **Assembly** (job `build`, Ubuntu):
  1. Reads the version from `<Version>` in `Source/Directory.Build.props`; fails if it is empty.
  2. Copies `site/` to `_site/` and `docs/images/*.png` to `_site/images/`. Screenshots are not duplicated
     in the repository.
  3. Replaces `__VERSION__` in `index.html` (title area, download section, JSON-LD `softwareVersion`) and
     `__LASTMOD__` in `sitemap.xml` (date of the head commit).
  4. Fails if a placeholder is left.
- **Deploy** (job `deploy`): `actions/upload-pages-artifact@v3` then `actions/deploy-pages@v4`, into the
  `github-pages` environment.
- **Page content:** title (60 characters) and meta description (159 characters) are written for search
  results; Open Graph uses `images/social-preview.png`; JSON-LD declares a `SoftwareApplication` and a
  `FAQPage`. No JavaScript, no external requests.
- **Not included on purpose:** `robots.txt`. Crawlers read it only at the host root
  (`https://tgundhus.github.io/robots.txt`), which belongs to a separate `tgundhus.github.io` repository.
  The sitemap is submitted in Search Console instead.

## Procedure

### One-time: switch Pages to the workflow
1. Repository → Settings → Pages → Build and deployment → Source: **GitHub Actions**.
   Until then Pages keeps rendering the repository root with Jekyll, and the `deploy` job fails.
2. Merge the change to `master`, or run the Website workflow manually if it is already merged.
3. Open https://tgundhus.github.io/VHD-Studio/ and check that the version and images show.

### One-time: Google Search Console
1. https://search.google.com/search-console → Add property → **URL prefix** →
   `https://tgundhus.github.io/VHD-Studio/`.
2. Verification method **HTML tag**. Paste the `<meta name="google-site-verification" ...>` tag into
   `site/index.html` on the line after the marked comment in `<head>`, commit to `master`, wait for the
   Website workflow, then press Verify. Keep the tag afterwards; removing it un-verifies the property.
3. Sitemaps → submit `sitemap.xml`.
4. URL Inspection → `https://tgundhus.github.io/VHD-Studio/` → Request indexing.
5. Optional: Bing Webmaster Tools → Import from Google Search Console, which also covers DuckDuckGo
   and other Bing-based search.

### Repository metadata (Settings and the About gear on the repository page)
These are not in the repository, so they are listed here. GitHub uses the description in the page
title Google shows for the repository (`GitHub - tgundhus/VHD-Studio: <description>`).
- **Description:** Free, open-source VHD/VHDX manager for Windows: attach, auto-mount at startup,
  compact, resize, convert and repair virtual disks. No Hyper-V needed, works on Windows Home.
  Successor to VHD Attach.
- **Website:** tick "Use your GitHub Pages website".
- **Topics:** vhd, vhdx, virtual-disk, virtual-hard-disk, disk-management, disk-utility, diskpart,
  hyper-v, windows, windows-10, windows-11, wsl, wsl2, docker-desktop, dev-drive, vhd-attach, iso,
  dotnet, csharp.
- **Social preview:** Settings → General → Social preview → `docs/images/social-preview.png`.

### Preview locally
Run the assembly commands from the workflow with `_site` pointing to a temporary folder (they need
`bash`, `sed` and `git`), then open `index.html` from that folder in a browser.

## Constraints
- Every claim on the page must be traceable to `README.md`, `CHANGELOG.md` or `docs/SAFETY.md`. When
  those change, update `site/index.html` in the same change.
- The FAQ answers in the `FAQPage` JSON-LD must match the visible FAQ text (arrows and curly quotes
  aside). Google requires structured data to describe content visible on the page.
- Installer sizes ("about 36 MB", "about 2.4 MB") are hard-coded from `README.md` and must be updated
  by hand.
- No ratings or reviews in the structured data unless they are real.
- Download links go to `/releases/latest`, not to versioned asset URLs, so they never point at a file
  that doesn't exist.

## Related
- [Decision 001](../decisions/001-landing-page-via-github-actions.md): why the site is built this way.
- Discoverability strategy (backlinks, communities, package managers) is business knowledge and belongs
  in Company Brain, which this repository does not have yet.
