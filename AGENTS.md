# VHD Studio

VHD Studio is a free, open-source Windows utility (C#, .NET 10) to attach, auto-mount, maintain and
partition VHD, VHDX and ISO images without Hyper-V. It is the successor to VHD Attach by Josip Medved.
`Source/` holds the app, service and tests, `Setup/` the installer and publish scripts, and `site/` the
landing page at https://tgundhus.github.io/VHD-Studio/. There is no Company Brain bundle yet; product
positioning and roadmap currently live in `docs/PROPOSAL.md`.

## How to use docs/
Read `docs/index.md` first, then only the relevant folder index. Current truth lives in concepts and
runbooks; history and rationale live in `docs/decisions/`. Append every documentation change to
`docs/log.md`.

## Conventions the agent must respect
- The version number lives only in `Source/Directory.Build.props` (`<Version>`); the installer and the
  website read it from there.
- Every release gets a section in `CHANGELOG.md`.
- Data-safety guarantees are specified in `docs/SAFETY.md`; changes that touch disks must keep them true.
- `site/index.html` repeats claims from `README.md`. When a feature, requirement or installer changes,
  update both, and keep the FAQ JSON-LD identical to the visible FAQ text.
- `README.md`, `site/index.html` and the installer link to `docs/SAFETY.md`, `docs/PROPOSAL.md` and
  `docs/images/`; don't move them.
