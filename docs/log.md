---
type: index
title: Documentation change log
scope: VHD-Studio
specificity: general
credibility: verified
generated: {at: 2026-10-08, by: agent}
---

# Documentation change log

Read this when you want to know what changed in the documentation and when. Append-only, newest last.

## 2026-10-08
- Created the OKF entry points: `AGENTS.md`, `docs/index.md`, this log, `docs/operations/` and
  `docs/decisions/`. Existing files (`SAFETY.md`, `PROPOSAL.md`, `images/`) keep their paths.
- Added [operations/website.md](operations/website.md): how `site/` is deployed to GitHub Pages and
  registered with Google Search Console.
- Added [decisions/001-landing-page-via-github-actions.md](decisions/001-landing-page-via-github-actions.md).
- `LICENSE.md` rewritten in the canonical MIT layout (title, copyright lines, unbulleted conditions, no
  BOM). The previous layout matched GitHub's MIT template at 71.5% (licensee 10.1.0, threshold 98%), so
  GitHub reported the license as `NOASSERTION`. The new file matches at 99.5%. Both copyright notices
  and the VHD Attach attribution are kept. The installer still ships it as `License.txt`.
