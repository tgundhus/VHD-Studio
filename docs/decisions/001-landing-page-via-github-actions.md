---
type: decision
title: Landing page in site/, deployed by GitHub Actions
scope: VHD-Studio/site
specificity: specific
credibility: inferred
generated: {at: 2026-10-08, by: agent}
sources: [site/index.html, .github/workflows/pages.yml]
links: [../operations/website.md]
supersedes:
---

# Landing page in site/, deployed by GitHub Actions

## Context
VHD Studio needs a page on a domain we can verify in Google Search Console, because a repository on
`github.com` can't be submitted there. On 2026-10-08 GitHub Pages was enabled with "Deploy from a branch",
`master`, `/ (root)`. That makes Jekyll render `README.md` as the home page and publish the whole
repository as a site: `Source/`, `Setup/`, the PDF in `Research/`, `CHANGELOG.md`, `BUILD.md` and
everything under `docs/`.

## Decision
The website is a single hand-written HTML page in `site/`, with screenshots taken from `docs/images/`
at build time. `.github/workflows/pages.yml` assembles and deploys it, and the Pages source is
"GitHub Actions". Proposed by the agent on 2026-10-08; pending the maintainer's review of the change.

## Rationale
- Full control over what search results show: title, meta description, Open Graph image and structured
  data (`SoftwareApplication`, `FAQPage`). The default Jekyll rendering of the README gives none of this.
- Only the page is published. The source tree, the installer scripts and the internal documentation in
  `docs/` (including the market analysis in `PROPOSAL.md`) stay off the website.
- The version shown on the page comes from `Source/Directory.Build.props`, so it can't go stale.
- No Jekyll, so no Liquid processing of repository Markdown and no theme dependency.

## Alternatives considered
- **Keep "Deploy from a branch", `master` root:** no setting change, but no control over metadata and the
  whole repository is published.
- **"Deploy from a branch", `master` `/docs`:** would publish `docs/`, which now holds the internal
  documentation bundle and the market analysis.
- **Add `index.html` and `_config.yml` to the repository root:** keeps the branch source, but clutters the
  root of a C# repository and still needs an exclude list maintained by hand.

## Consequences
- The maintainer must switch Settings → Pages → Source to "GitHub Actions" once; until then the
  `deploy` job fails.
- `site/index.html` duplicates claims from `README.md` and must be kept in sync by hand.
- A custom domain can be added later through the Pages settings without changing the workflow.

## Affected concepts
- [operations/website.md](../operations/website.md)
