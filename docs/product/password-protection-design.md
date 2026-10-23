---
type: design
title: Password-protected virtual disks
scope: VHD-Studio/encryption
specificity: specific
credibility: inferred
generated: {at: 2026-10-23, by: agent}
sources: [docs/PROPOSAL.md, docs/VSUX.md, Source/VhdStudio.Vsux/VsuxOperations.cs, CHANGELOG.md]
links: [../SAFETY.md, ../VSUX.md]
---

# Password-protected virtual disks

## Summary
VHD Studio ships the experimental [VSUX mount lock](../VSUX.md) for a revised requirement:
leave the data unencrypted while preventing ordinary users and generic tools from mounting a locked
image, including on another PC. The maintainer explicitly accepted that specialists can bypass it.
VSUX's raw lock changes only the eight VHDX signature bytes. Current images store their password
information inside the VHDX; initial protection, explicit upgrades and conversion back also update
VHDX metadata. Earlier images need their original recovery file until explicitly upgraded.
Attach prompts for the password; Detach & lock safely detaches first. Independent recovery restores
the public signature without a password. This is a convenience barrier, not confidentiality.

The earlier requirement below was stronger. The BitLocker integration described here remains an
unbuilt proposal for that confidentiality goal. An unencrypted lock cannot meet it because the data
stays readable in the file. VSUX does not promise zero corruption risk or automatic relocking after
an interrupted session; see its usage guide and the safety model.

## Requirements (maintainer, 2026-10-10)
1. No software can open the disk's contents without the password.
2. Opening stays fast.
3. No slow whole-file encrypt/decrypt pass and no added risk of corrupting data.

## Options evaluated for the original requirement
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
