# VHD Studio data-safety model

VHD Studio changes disks and disk images, so its design goal is that **no action in the tool loses
user data unless the user explicitly chose a destructive action and confirmed it**. This document
lists the safeguards and how they are tested. It is intended for administrators, security reviewers
and change-approval boards.

## Principles

1. **Never replace or delete what we didn't create.** New disks, conversions and backups use
   create-new semantics and refuse existing files. A failed or cancelled operation removes only the
   file it created itself.
2. **Never write to a mounted read-only disk.** A read-only VHD/VHDX/ISO attach never modifies
   the image, not even to replay a pending VHDX log. VSUX must restore its eight-byte signature
   before mounting and mask it after safe detach, including for read-only use; its payload stays read-only.
   Its separate protection, upgrade and conversion actions update VHDX container metadata.
3. **Back up before changing in place, and make sure the backup is usable.** Maintenance
   operations that modify an existing file offer a backup, enabled by default whenever there is
   enough free space. The backup is stored plainly (never compressed, encrypted or sparse, whatever the
   folder settings). Its XXH128 checksum must match the source when re-read from disk, bypassing the
   cache. Windows must also be able to open it as a virtual disk. VSUX soft protection is an
   exception: it does not make a full backup. An optional diagnostics export is not a backup.
   Make a full backup before protecting, upgrading or converting an existing VSUX image.
4. **Refuse rather than guess.** Operations stop when a disk is attached, in use, changed since it
   was selected, protected, or short of space. In every one of these cases nothing is changed.
5. **Only interrupt what is safe to interrupt.** Compact, convert and backup can be cancelled.
   Resize, merge and metadata changes cannot be cancelled once started.
6. **Explain, then confirm.** Every change shows a plain-language summary and, in Disk Manager, the
   equivalent PowerShell command. High-impact actions require typed confirmation.
7. **Record everything.** Every change is written to an append-only audit log.

## Safeguards by area

### Attach / detach (service, runs as Local System)
| Risk | Safeguard |
|---|---|
| Another user uses the service to read or modify disks they cannot access | The virtual disk is **opened with the caller's own token**, so Windows checks the caller's rights against the exact file being opened. Only the privileged attach or detach call on that handle runs as SYSTEM, which removes any check-then-use path race. Clients that refuse impersonation (identification-level tokens) are rejected, and any access that cannot be verified is denied. |
| Path tricks (junctions, symbolic links, hard links, 8.3 names) | Paths through junctions, links or mount points are refused, and the path must resolve to itself. Hard-linked files are refused for non-admins and for every auto-mount entry, which is re-checked at each boot. Network shares require an administrator, because a remote server cannot verify an impersonated user's access. |
| Rogue or remote pipe clients and servers | The pipe allows local authenticated users only to read and write data (no instance creation) and denies network logons. It rejects remote clients and must be the first instance, so it can't be squatted. Clients send commands only after verifying the pipe is served by the installed service process. |
| Forced detach of someone else's disk | Detaching a disk attached read/write requires write access to its file. "Detach anyway" (open files) requires an administrator, and is refused while another attached disk's image is stored on it. |
| Detaching while files are open loses unsaved data | Safe detach: volumes are flushed, locked and dismounted first. If files are open the user gets **Retry / Detach anyway / Cancel**, and Retry is the default. |
| "Initialize" wipes an existing partition table | The service initializes only disks that are blank: no MBR signature, no GPT header, and an all-zero first MiB. |
| Mounting into an arbitrary folder as SYSTEM | Mount folders require an administrator, because mounting runs as SYSTEM and cannot be made race-free against folder swaps. The folder must already exist, be empty, not be a link, and contain no links in its path. The service never creates folders. |
| Read-only attach of a VHDX with a pending log | Refused with guidance. The log is only replayed for read/write attach, or explicitly through Repair. |
| Writing to the parent of differencing disks | Creating a child marks the parent read-only by default. A writable attach of a read-only file is refused with an explanation. |

### Maintenance (image files)
| Operation | Preconditions | Interruptible | After |
|---|---|---|---|
| Soft protection (VSUX) | detached standalone dynamic or fixed VHDX, no pending log, new image destination in the same folder | no | password record stored in VHDX user metadata through Windows, image renamed to `.vsux`, signature masked and locked state verified; no full backup is made |
| Make VSUX self-contained | detached clean image in the earlier companion-based format, valid companion and password | no | password record stored in VHDX user metadata through Windows, original lock state preserved, old JSON retained as a backup |
| Convert VSUX back to VHDX | detached clean image, valid password, new `.vhdx` destination in the same folder | no | own embedded password record removed through Windows, ordinary signature restored, file renamed without copying the payload; legacy JSON retained |
| Compact | detached, no pending log, dynamic or differencing | yes (documented safe) | file never grows |
| Resize | detached, no pending log, ≥ smallest safe size, free space for fixed growth | no | size verified |
| Convert | source detached, chain complete, destination new, free space for the full copy, sector size unchanged | yes, and the partial destination is removed | result size, type and sector size verified, source never modified |
| Differencing | parent detached, no pending log, child new | no | parent protected (read-only) |
| Merge | child and parent detached, child has no children of its own, parent found, parent not protected, free space; sibling disks it would invalidate are listed | no | the child goes to the **Recycle Bin**, never a permanent delete; nothing is deleted on network shares |
| Fix parent path | child detached, candidate parent detached, backup **mandatory** | no | checked before writing (VHD: identifier; VHDX: the child's `parent_linkage` must equal the parent's DataWriteGuid, which also detects a parent modified after the child was created); Windows then re-validates the chain; on rejection the previous path is restored, or the user is told to restore the backup |
| Reset identifier | detached, typed confirmation | no | none |
| Replay log | detached | no | the log must be clear afterwards |

### VSUX mount lock (elevated helper)

VSUX is an experimental application password gate over an unencrypted VHDX, not encryption or
an access-control boundary against specialists. Only bytes 0–7 are changed by its raw writer:
`vsuxlock` when locked, `vhdxfile` when unlocked. Native read/write mounting can naturally change
the rest of the image. New VSUX images store a per-image password salt, verifier and derivation
parameters in VHDX user metadata through the Windows virtual-disk API. Initial protection,
explicit migration from the earlier format and conversion back change additional container
metadata, including Windows' normal header/log bookkeeping. The eight-byte boundary describes
the raw signature writer, not the total writes made by every action. No VSUX action encrypts
the disk payload or makes a full backup.

Earlier VSUX images still need their adjacent `.vsux.json` companion for password-based use
until explicitly upgraded through **Soft protection → Make self-contained…**. The upgrade
requires the password, a clean detached image and a valid companion; the old JSON is retained
as a backup. Normal Attach never silently migrates an image. New single-file images can move
between PCs without JSON. The optional **Export recovery information…** JSON contains
diagnostics, not password-verifier information, and is not required for normal use or recovery.

**New → Create one NTFS partition** is checked by default for VHD, VHDX and VSUX. Only the newly
created file is attached for setup: its exact backing-file identity and virtual-disk location are
checked, and the disk must be RAW, without partitions and with an all-zero first MiB. It is
initialized as GPT and one NTFS data partition is created, then safely detached. A VSUX image
is protected only after successful detach and finishes locked. Clearing the checkbox skips
initialization and formatting, leaving a blank disk for manual setup. The prepared option needs
at least 64 MiB and administrator approval. Ordinary Attach never invokes this creation step.
Existing image destinations, including directories with those names, are refused;
an unrelated adjacent JSON file is ignored and left unchanged. If creation fails after files have
been created, the error identifies the possible temporary VHDX and container paths. They are
retained for inspection and recovery; the user must not assume the partial result is locked.

| Risk | Safeguard or limit |
|---|---|
| Masking a disk while files remain open | Volumes must flush, lock and dismount before detach. Masking needs exclusive file access and validates the VHDX structures. VSUX has no force-detach path. |
| Wrong password, missing or invalid credential record | Password operations validate the embedded record, or the earlier companion against the image's stable identifier, before changing the signature. An earlier image with a missing companion requires recovery. |
| First protection or migration loses the password metadata | Windows writes the VHDX user-metadata item, which is read back and verified before the image is masked and reported locked. Existing image destinations are never overwritten. Keep a full backup before the operation. |
| Password exposure through service IPC, process arguments or logs | VSUX prompts in its elevated process and does not send passwords to the attach service or put them on the command line or in audit details. |
| An unattended path bypasses the prompt or changes locked-image metadata | Protected VSUX containers are excluded from service attach/auto-mount and standard Maintenance operations. Use their dedicated Attach, Detach and lock, and Soft protection actions. Initial protection of an ordinary VHDX is available on Maintenance's Soft protection page. |
| Conversion back damages or overwrites a different file | Conversion requires a detached clean source, password verification and a new `.vhdx` name in the same folder. An exclusive file-handle rename refuses existing destinations before any signature write. The signature is then restored and Windows removes only VHD Studio's own metadata item without copying the payload. Unrelated metadata is preserved; old JSON files are not deleted. |
| Lost password or companion, or an interrupted signature write | Independent `Recovery\VsuRecover.exe --restore <path>` validates the remaining structures, requires exclusive access and restores only the public eight-byte identifier. It requires no password, companion or saved header snapshot. |
| Application exit, crash or Windows shutdown while unlocked | Closing VHD Studio does not promise automatic relocking. An interrupted session can leave the normal signature available; check the state and use Detach and lock. |
| Power loss during a signature or metadata write | Eight-byte writes are not guaranteed atomic. Masking the VHDX file signature violates the normal VHDX writing rules; Windows metadata updates also make changes beyond those bytes. Structural checks and recovery reduce mistakes but cannot promise zero corruption risk. Keep a full backup. |

The first-MiB snapshot in an earlier companion or an exported diagnostics file is diagnostic
data. Neither the normal lock path nor standalone recovery writes it back over current VHDX
metadata. Recovery leaves any embedded password item intact and restores only bytes 0–7.
It ignores private password-record contents and format constraints, including empty or oversized
records, while still validating the generic VHDX metadata table, entry bounds and overlaps.
It refuses pending logs, differencing disks and invalid structures; it never repairs a filesystem
or rolls back disk data.
See [VSUX usage and recovery](VSUX.md).

### Disk Manager (partitions and volumes)
| Risk | Safeguard |
|---|---|
| Changing the running system | System, boot and cluster disks are always read-only. Partitions that hold the page file or the image file of an attached virtual disk are protected, and so are their disks. Dynamic-disk (LDM) and Storage Spaces members are protected too, because their volumes can span other disks. |
| Changing a physical disk by mistake | Physical disks are locked until "Allow changes to physical disks" is enabled with typed confirmation. Every change on a physical disk then needs the disk number typed, and shows the model and serial number. |
| Acting on an outdated list (disks renumbered, detached or replaced) | Before every change the disk and partition are re-read and compared (object ID, number, size, location, serial, offset). Any difference aborts the operation. |
| Formatting, deleting or cleaning a volume with open files | Volumes must be lockable (no open handles) first. Format never uses `Force`. |
| Concurrent or interrupted operations | One operation at a time, and the window cannot be closed while one runs. |

## Audit log

The log folder is created by the service at start-up with an Administrators/SYSTEM-only ACL. A folder
that is a link, or one pre-created by another user, is never written to.

`%ProgramData%\xGND Software\VHD Studio\Logs\audit-YYYY-MM.log` holds one JSON object per line with the time,
result (`started`/`succeeded`/`failed`), operation, target, details, user, machine and process.
The service records attach, detach and auto-mount changes on behalf of the calling user.
Maintenance and Disk Manager record their own actions, and these always run elevated.

## Verification

`Source/VhdAttach-Test/DataSafetyTest.cs` contains end-to-end tests on real virtual disks. Each test
writes random files, runs an operation and verifies every byte with SHA-256. Covered:
- Compact, cancelled compact, resize (grow, shrink, refused shrink), and operations on attached disks.
- Convert (VHD and fixed, cancelled, never overwriting an existing file) and differencing + merge with
  parent protection.
- Wrong-parent refusal with restore, safe detach with open files, the initialize guard, and Disk
  Manager in-use and stale-selection refusals.
- Verified backups, and read-only attach leaving the file bit-identical.

The tests need administrator rights and only touch scratch disks they create under
`%TEMP%\VhdStudioSafetyTests`; every storage call first checks that the target disk is backed by such
a file. Run them with:

```powershell
Setup\Test-DataSafety.ps1
```

GitHub Actions runs them on every build, because the hosted Windows runners are elevated.

## Residual risks

* An independent review of the data-loss surface (October 2026) found one critical issue, two high,
  eight medium and six low. All were fixed except the spanned-volume limitation above, and each fix
  has a regression test.
* Power loss during a non-interruptible operation (resize, merge) can damage the image. Keep the
  backup option enabled; it exists for exactly this case.
* Windows itself may need to replay a VHDX log after a crash. VHD Studio never does this silently for
  read-only use.
* "Detach anyway" and the physical-disk unlock are deliberate escape hatches. Both require explicit
  confirmation and administrator rights, and are audit-logged.
* Safe detach locks volumes by their first extent. A volume spanning several disks (dynamic disks)
  is not locked on its other disks. Such configurations are rare for virtual disks, and Disk Manager
  protects LDM members.
* Files inside OneDrive (cloud-file reparse points) and other linked folders cannot be attached
  through the service. Copy them to a normal folder first.
* VHD (not VHDX) parents are matched by identifier only. A modified copy of a VHD parent with the same
  identifier cannot be told apart, so keep VHD parents read-only (the default).
