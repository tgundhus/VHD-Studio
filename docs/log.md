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

## 2026-10-09
- Merged to `master` (08e38e6). Website run #1 built and deployed successfully; GitHub now detects the
  license as MIT.
- Corrected [operations/website.md](operations/website.md) and decision 001: with the Pages source
  still on "Deploy from a branch", the `deploy` job does not fail. GitHub's Jekyll build deploys as
  well, and the later deploy wins.

## 2026-10-10
- Added [product/password-protection-design.md](product/password-protection-design.md) and
  `product/index.md`: proposal to password-protect disks with BitLocker inside the VHDX, with the
  options that were ruled out. Not built.
- Added [product/drive-letters.md](product/drive-letters.md): fix for attached disks getting a drive
  letter the user already uses for a network or subst drive, and for a refused letter change leaving a
  volume without a letter.
- Rewrote [product/drive-letters.md](product/drive-letters.md): the service now remembers each volume's
  last drive letter and restores it on every attach, or gives a free one; the user's network and subst
  letters are sent by the UI with each attach. Replaces the UI-side letter move from earlier today.

## 2026-10-11
- Merged the drive-letter changes to `master` (4ae993a). Build #14 on Windows passed, including the three
  new elevated `DriveLetterTest` tests (22 of 22 elevated tests); marked
  [product/drive-letters.md](product/drive-letters.md) as verified.

## 2026-10-22
- Version 5.0.3: the changelog's "Unreleased" section became 5.0.3.

## 2026-10-23
- Added [VSUX.md](VSUX.md) and its documentation/product index entries for the shipped experimental
  mount lock: password prompts, safe detach, companion portability, eight-byte writes, standalone
  recovery and disposable-image verification. Updated [SAFETY.md](SAFETY.md) with its safeguards and
  limits, including read-only signature writes and interrupted sessions; corrected a joined list line.
- Updated [product/password-protection-design.md](product/password-protection-design.md) to distinguish
  the revised unencrypted mount-deterrence requirement from the original confidentiality goal.
  The BitLocker integration remains a proposal; VSUX is shipped in 5.1.0.
- Added the 5.1.0 section to [CHANGELOG.md](../CHANGELOG.md), retaining the 5.0.3 drive-letter changes.
  Updated [README.md](../README.md) and [the website](../site/index.html) for VSUX, recovery, signed
  releases and the retained drive-letter behavior; aligned the visible FAQ with its JSON-LD.
- Updated [BUILD.md](../BUILD.md) for production staging, standalone recovery, `-SignFile` signing
  and manual GitHub releases. Corrected its earlier claim that pushing a tag creates a release.
  Updated [operations/website.md](operations/website.md) to avoid stale installer-size estimates.
- Updated the [prototype guide](../Research/VsuPrototype/README.md) to describe its shared production
  core and independent recovery checks rather than the earlier pre-integration state.
- Updated [VSUX.md](VSUX.md) with successful 5.1.0 installer compilation using the official portable
  Inno Setup compiler, valid timestamped xGND signatures through `sign-file`, signed uninstallers,
  the recovery executable's help smoke check and matching uploaded installer hashes. Kept the original
  80 product tests and 19 VSUX checks identified as the initial integration verification.
- Added the 5.1.1 changelog for service-stop hangs during upgrades and incorrect runtime lookup after
  switching installer types. Updated [README.md](../README.md) and [the website](../site/index.html)
  to explain switching packages and the shared-runtime lookup policy.
- Updated [BUILD.md](../BUILD.md) with the disposable installer regression suite and its guarded
  `installerOnly` CI mode, covering upgrades from 5.1.0, stuck services, runtime switching and uninstall.
- Updated the build requirements to Inno Setup 7.1 or newer and documented the pinned, verified CI
  compiler, preventing an older compiler from retaining 5.1.0's previous uninstall callback.
