# Changelog

## 5.0.2

### Fixed
- A backup made in a compressed or encrypted folder inherited that setting, and Windows refuses such
  files as virtual disks ("must be uncompressed and unencrypted and must not be sparse"). Backups are
  now always stored plainly. After the checksum matches, Windows must also open the backup as a virtual
  disk, or the backup counts as failed and the original is not changed.
- Files that Windows refuses for this reason (for example earlier backups) can be fixed with
  **Repair → Make usable as a virtual disk**. When opening such a file fails, the main window offers
  the same fix. It works like `compact /u`; the content does not change.
- The error message now explains the cause and the fix instead of showing the raw Windows text.

## 5.0.1

### Fixed
- Installing over, or uninstalling, 5.0.0 could fail with "DeleteFile failed; code 5. Access is denied"
  (for example on `clrjit.dll`). The 5.0.0 service process did not exit after being stopped, because its
  request loop could wait forever and .NET 10 no longer supports the old `Thread.Abort` fallback.
  The loop now ends on stop or after 10 seconds without data (which also stops a silent client from
  hanging the service). The service thread no longer keeps the process alive, and setup and uninstall
  make sure the service process has exited before files are replaced.
- Restarting the service (for example during an upgrade) re-ran auto-mount. That re-attached disks
  you had detached on purpose and logged false failures for disks still attached. Auto-mount now runs
  once per boot and skips disks that are already attached. Attached disks are never touched by
  install, upgrade or uninstall.
- Maintenance and Disk Manager could hide in the toolbar's overflow menu at the default window width.
  The window is now always wide enough for every toolbar button.

### Improved
- Backups are about 3.4× faster (3 GB: 7.2 s → 2.1 s on NVMe). Reading, checksumming and writing are
  pipelined, large files are written unbuffered like Explorer does (so a big backup no longer flushes
  everything else out of memory), and data is flushed to disk once.
- Backup verification now re-reads the copy from disk, bypassing the cache, so it checks what was
  really written. It uses a 128-bit XXH128 checksum instead of SHA-256, which was the bottleneck at
  about 2 GB/s.

## 5.0.0: VHD Studio

First release under the VHD Studio name, by Tobias Gundhus (xGND Software). It continues
VHD Attach 1.0–4.22, created and maintained 2009–2020 by Josip Medved
([@medo64](https://github.com/medo64/VhdAttach)).

### New
- **Maintenance window**: compact (file-system aware or zeroed blocks only, with a WSL/Docker
  helper), resize and shrink, convert (VHD↔VHDX, dynamic↔fixed, 512/4K sectors), create
  differencing disks, merge into parent, replay the VHDX log, fix the parent path, reset the
  identifier, and detailed information. No Hyper-V module needed.
- **Disk Manager** (DiskPart GUI): initialize, create, format, extend, shrink and delete partitions;
  manage drive letters and folder mounts; online/offline, read-only, active flag, retrim,
  scan/spot-fix, and clean. Every change first shows the equivalent PowerShell command. System
  and boot disks are always protected.
- Auto-mount and attach can **mount into an empty folder** instead of a drive letter.
- An Explorer context-menu entry **Maintenance…** and a Start-menu shortcut for Disk Manager.
- Command line: `/maintain "file" [/task=...]` and `/diskmanager [/disk=N]`.

### Fixed
- VHDX files that weren't closed cleanly failed with "Access denied". The pending log is now
  replayed for read/write attach (upstream #11). Read-only attach never writes and explains the problem.
- **New disk** permanently deleted an existing file when you picked its name. Existing files are now
  never replaced.
- The service's "initialize disk" step could overwrite the partition table of an existing disk. It now
  only initializes verifiably blank disks.
- Detaching cut off open files. Volumes are now flushed and locked first, and the user can retry
  instead of losing unsaved data.
- New-disk partition entries were written with the wrong layout (one-byte BOOLEAN fields marshalled as
  four bytes), and the attach parameter structure was too small.
- Cancelling a fixed-size VHDX creation left a partial file. The old code also passed a movable managed
  OVERLAPPED to the kernel; creation now uses the hardened engine.
- Uninstall no longer deletes the auto-mount list.
- `.avhd`/`.avhdx` differencing disks, and files with the wrong extension, could not be opened.
- Auto-mount entries with more than one option (`/readonly,nodriveletter/`) applied none of them.
- Files on mapped network drives could not be attached by the service.
- Uninstall left the default context-menu verb behind, which broke other tools (upstream #5).
- Removed a code path that started a `VhdAttachExecutor.exe` that no longer exists.

### Data safety
- Verified backups (128-bit checksum, re-read from disk) before every in-place change, enabled by default.
- Operations refuse attached or in-use disks, disks changed since selection, and protected disks
  (system, boot, cluster, page file, partitions holding attached images). Format never forces a dismount.
- Resize and merge cannot be interrupted midway. A cancelled or failed convert removes only the file
  it created.
- Fixing a parent path verifies the parent and restores the previous path on a mismatch.
- Differencing disks mark their parent read-only. A merged child goes to the Recycle Bin.
- Audit log of every change in `%ProgramData%\xGND Software\VHD Studio\Logs`.
- End-to-end data-safety test suite on real virtual disks (`Setup\Test-DataSafety.ps1`, runs in CI).

### Security
- The service, which runs as Local System, now checks that the calling user can access a file
  before attaching, detaching or auto-mounting it, or before changing a drive letter.
- Disks are opened under the caller's own token, so access is checked by Windows against the exact
  file being opened (no path races). Paths through junctions or links, and hard-linked files, are refused.
- The command pipe denies network access, rejects remote clients, cannot be squatted, and is verified
  by the client. Identification-only clients are rejected.
- "Detach anyway" and mount folders require an administrator. The audit log folder is created with a
  restricted ACL.

### Changed
- Runtime moved from .NET Framework 4.0 to **.NET 10**. Minimum OS is Windows 10 1809 (x64).
- New name, icon and look; published by xGND Software. Settings live under `Software\xGND Software\VHD Studio`.
  Update checks and bug reports go to this project's GitHub page.
- Setup removes VHD Attach 4.x and keeps its auto-mount list.
- The build uses `dotnet` and GitHub Actions instead of Visual Studio 2019 batch scripts.
