# VHD Studio data-safety model

VHD Studio changes disks and disk images, so its design goal is that **no action in the tool loses
user data unless the user explicitly chose a destructive action and confirmed it**. This document
lists the safeguards and how they are tested. It is intended for administrators, security reviewers
and change-approval boards.

## Principles

1. **Never replace or delete what we didn't create.** New disks, conversions and backups use
   create-new semantics and refuse existing files. A failed or cancelled operation removes only the
   file it created itself.
2. **Never write when asked to read.** A read-only attach never modifies the image, not even to
   replay a pending VHDX log.
3. **Back up before changing in place.** Every Maintenance operation that modifies an existing file
   offers a SHA-256-verified backup. It is enabled by default whenever there is enough free space.
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
| Compact | detached, no pending log, dynamic or differencing | yes (documented safe) | file never grows |
| Resize | detached, no pending log, ≥ smallest safe size, free space for fixed growth | no | size verified |
| Convert | source detached, chain complete, destination new, free space for the full copy, sector size unchanged | yes, and the partial destination is removed | result size, type and sector size verified, source never modified |
| Differencing | parent detached, no pending log, child new | no | parent protected (read-only) |
| Merge | child and parent detached, child has no children of its own, parent found, parent not protected, free space; sibling disks it would invalidate are listed | no | the child goes to the **Recycle Bin**, never a permanent delete; nothing is deleted on network shares |
| Fix parent path | child detached, candidate parent detached, backup **mandatory** | no | checked before writing (VHD: identifier; VHDX: the child's `parent_linkage` must equal the parent's DataWriteGuid, which also detects a parent modified after the child was created); Windows then re-validates the chain; on rejection the previous path is restored, or the user is told to restore the backup |
| Reset identifier | detached, typed confirmation | no | none |
| Replay log | detached | no | the log must be clear afterwards |

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

`%ProgramData%\VHD Studio\Logs\audit-YYYY-MM.log` holds one JSON object per line with the time,
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
