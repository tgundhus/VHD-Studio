---
type: design
title: Password-protected virtual disks
scope: VHD-Studio/encryption
specificity: specific
credibility: inferred
generated: {at: 2026-10-10, by: agent}
sources: [docs/PROPOSAL.md, Source/VhdAttach-Service/VirtualDiskImage.cs, CHANGELOG.md]
links: [../SAFETY.md]
---

# Password-protected virtual disks

## Summary
Proposal, not built. To password-protect a disk, turn on BitLocker for the volume inside the VHD or
VHDX, and let VHD Studio create, unlock and auto-mount such disks. A lock that doesn't encrypt can't
meet the requirement below, because the data stays readable in the file.

## Requirements (maintainer, 2026-10-10)
1. No software can open the disk's contents without the password.
2. Opening stays fast.
3. No slow whole-file encrypt/decrypt pass and no added risk of corrupting data.

## Options evaluated
| Option | Meets 1? | Why |
|---|---|---|
| Custom wrapper or scrambled header (own extension) | No | The data blocks stay plaintext. VHDX keeps two header copies and two region tables, so restoring a header or carving files from the raw bytes recovers the data. Rewriting headers in place is itself a corruption risk. |
| Password check in VHD Studio only | No | Stops only VHD Studio; Windows, 7-Zip, qemu-img or Linux open the file directly. |
| NTFS encryption (EFS) or compression of the file | n/a | Windows refuses to attach such files (`ERROR_VHD_INVALID_FILE_ATTRIBUTES`, 0xC03A001A). |
| Own encryption driver | Yes | Needs a signed kernel driver and years of hardening; disproportionate. |
| VeraCrypt container | Yes | Works on Windows Home, but it is a separate product and format, not a VHDX. |
| **BitLocker on the volume inside the VHDX** | **Yes** | Built into Windows, encrypts per sector on the fly, leaves the VHDX container standard. Recommended. |

## Behavior (proposed)
- **Create:** New disk or Maintenance → "Protect with password", on Windows Pro, Enterprise and
  Education. Through `Win32_EncryptableVolume`: add a passphrase protector, add a recovery password
  that the user must save before continuing, then encrypt used space only. On a new, empty disk that
  takes seconds.
- **Attach:** when an attached volume is BitLocker-locked, prompt for the password and unlock it. This
  part works on Windows Home too. It is the P1 item "BitLocker unlock after attach" in `PROPOSAL.md`.
- **Auto-mount:** attach at boot and leave the volume locked until the user unlocks it. Storing the
  password for unattended unlock is out of scope until designed.
- **Lock:** detaching locks the volume.

## Constraints
- Windows Home can unlock and use BitLocker volumes but can't turn BitLocker on.
- Any tool can still attach the VHDX, but the volume inside is unreadable without the password or the
  recovery key.
- A lost password and recovery key means the data is gone. The recovery-key step is mandatory.
- Don't use `.vhds` for anything: it is Hyper-V's VHD Set format.
- Compact probably recovers less space from an encrypted volume. Needs testing.
- Encryption overhead depends on CPU and SSD; measure on target hardware before quoting numbers.

## Related
- [SAFETY.md](../SAFETY.md): the data-safety model a new feature must keep.
- [PROPOSAL.md](../PROPOSAL.md): roadmap item "BitLocker unlock after attach" (P1).
