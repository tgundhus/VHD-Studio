# Changelog

## 5.0.0: VHD Studio

First release under the VHD Studio name. It continues VHD Attach 4.22 by Josip Medved
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
  replayed automatically (upstream #11).
- `.avhd`/`.avhdx` differencing disks, and files with the wrong extension, could not be opened.
- Auto-mount entries with more than one option (`/readonly,nodriveletter/`) applied none of them.
- Files on mapped network drives could not be attached by the service.
- Uninstall left the default context-menu verb behind, which broke other tools (upstream #5).
- Removed a code path that started a `VhdAttachExecutor.exe` that no longer exists.

### Security
- The service, which runs as Local System, now checks that the calling user can access a file
  before attaching, detaching or auto-mounting it, or before changing a drive letter.

### Changed
- Runtime moved from .NET Framework 4.0 to **.NET 10**. Minimum OS is Windows 10 1809 (x64).
- New name, icon and look. Update checks and bug reports go to this project's GitHub page.
- Setup removes VHD Attach 4.x and keeps its auto-mount list.
- The build uses `dotnet` and GitHub Actions instead of Visual Studio 2019 batch scripts.
