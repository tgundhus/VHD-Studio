# VHD Studio: market analysis and feature proposal

*October 2026. Applies to VHD Studio 5.0, forked from VHD Attach 4.22 by Josip Medved (@medo64).*

## 1. Positioning

> **VHD Studio is the free, open-source GUI for everything `Hyper-V\*-VHD` does, and it works on
> every Windows edition. Attach, auto-mount, compact, resize, convert, repair and partition virtual
> disks without Hyper-V and without scripts.**

No other free tool combines persistent attach, compaction, shrink, conversion and chain repair in a
GUI that runs on Windows Home:

* **The Hyper-V PowerShell module** has all of these features but no GUI, and it isn't available
  on Windows Home.
* **Built-in Windows tools** (Disk Management, Settings, Explorer "Mount") can attach and create
  disks, but they don't persist mounts across reboots and can't do maintenance.
* **Third-party tools** each cover one slice: Sordum Simple VHD Manager does attach at boot,
  StarWind V2V does conversion, `wslcompact` does WSL compaction, and FSLogix tools handle
  profile disks.

VHD Studio already owns the "auto-mount at boot" niche through its service. 5.0 adds the maintenance
and DiskPart-style layers on top.

## 2. Competitive landscape

| Feature | Disk Mgmt | diskpart | Hyper-V PS | Win11 Settings | Simple VHD Mgr | StarWind V2V | qemu-img | Arsenal | **VHD Studio 5.0** |
|---|---|---|---|---|---|---|---|---|---|
| Attach / detach | ✔ | ✔ | ✔ | – | ✔ | – | – | ✔ | ✔ |
| Read-only attach | – | ✔ | ✔ | – | ◐ | – | – | ✔ | ✔ |
| Mount to folder / no letter | ✔ | ✔ | ◐ | – | – | – | – | ◐ | ✔ |
| Persist across reboot | – | – | – | – | ✔ | – | – | – | ✔ |
| Create fixed / dynamic | ✔ | ✔ | ✔ | ✔ | ✔ | – | ✔ | – | ✔ |
| Differencing disks | – | ✔ | ✔ | – | – | – | ✔ | ◐ | ✔ |
| Compact | – | ✔ | ✔ | – | – | – | ◐ | – | ✔ |
| Expand / shrink VHDX | – / – | ✔ / – | ✔ / ✔ | – | – | – | ✔ / ◐ | – | ✔ / ✔ |
| Convert VHD↔VHDX, fixed↔dynamic | – | – | ✔ | – | – | ✔ | ✔ | – | ✔ |
| Merge / fix parent chain | – | ◐ | ✔ | – | – | – | ◐ | – | ✔ |
| Replay VHDX log after crash | ◐ | ◐ | ◐ | – | – | – | ✔ | – | ✔ (automatic) |
| Partition operations | ✔ | ✔ | – | ◐ | – | – | – | – | ✔ |
| Works on Home without Hyper-V | ✔ | ✔ | – | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| GUI | ✔ | – | – | ✔ | ✔ | ✔ | – | ✔ | ✔ |
| Open source | – | – | – | – | – | – | ✔ | ◐ | ✔ |

✔ supported, ◐ partial, – no.

## 3. What users ask for

These are taken from Reddit (r/sysadmin, r/wsl, r/Windows11), SuperUser, Microsoft Q&A, GitHub
issues and the upstream tracker.

1. **Compacting without Hyper-V.** This is the most common pain point. WSL2 and Docker Desktop
   `ext4.vhdx` files only ever grow, and `Optimize-VHD` isn't available on Home. People copy a
   four-step diskpart routine from blog posts. FSLogix profile bloat is the same problem at
   enterprise scale. → **Shipped in 5.0** (full and zero-block modes, WSL shutdown helper).
2. **Auto-mount at boot without Task Scheduler hacks.** This is especially relevant for Dev Drive
   VHDX files, which Windows doesn't re-attach. → Existing strength. Market it as the
   "Dev Drive auto-mount fix".
3. **"Access denied" on a VHDX after a crash.** The real cause is an unreplayed log (upstream #11).
   → **Shipped**: detected and replayed automatically on attach, with a manual Repair action.
4. **Broken differencing chains** after the parent moves. → **Shipped**: Fix parent path.
5. **Conversion and shrinking** (VHD→VHDX, fixed→dynamic, `-ToMinimumSize`). → **Shipped**.
6. **Mount to a folder instead of a drive letter** (upstream #8; RDS/multi-user letter clashes).
   → **Shipped** for attach and auto-mount.
7. **Context menu is lost or left broken after other tools or an uninstall** (upstream #4, #5).
   → Uninstall now removes the default verb it set. A "Repair context menu" button is P1.
8. **4K versus 512e sector mismatches** (SQL Server, QEMU). → Partly covered (Convert with
   sector size). Setting the physical sector size is P1.
9. **Mounting without admin rights.** → Existing strength through the service. Hardened in 5.0,
   see §5.
10. **BitLocker-protected VHDX unlock after attach, adding a VHD to the boot menu, NAS/symlink
    paths re-mounting after reboot.** → P1/P2.

## 4. Delivered in 5.0

| Area | Change |
|---|---|
| Platform | .NET Framework 4.0 → **.NET 10 (LTS)**, SDK-style projects, `dotnet build`, GitHub Actions CI, Inno Setup 6 installer with runtime check, 1.6 MB payload |
| Attach fixes | VHDX log replay. `.avhd`/`.avhdx` and misnamed files open (content-based detection). Auto-mount option parsing bug fixed (`/readonly,nodriveletter/` applied neither option). Mapped drives converted to UNC paths. Clearer error messages. Dead executor path removed. |
| Maintenance | Compact, resize/shrink, convert, differencing, merge, replay log, fix parent, reset ID, details, all through VirtDisk.dll |
| Disk Manager | DiskPart GUI on the Storage Management API (WMI): initialize, create/format/delete, extend/shrink, letters and folder mounts, online/offline, read-only, active, retrim, scan/spot-fix, clean. Shows the PowerShell equivalent for every change. Protects system and boot disks. Physical disks require an explicit unlock. |
| Security | The service now checks the caller's own file permissions before attach, detach, auto-mount or relettering (see §5) |
| Branding | VHD Studio name, icon and banner. GitHub-based update check and bug reports (previously pointed at the upstream author's server). Upgrades in place from VHD Attach 4.x and keeps the auto-mount list. |

## 5. Security review of the service (fixed in 5.0)

The service runs as **Local System**, and its command pipe is writable by every local user. In 4.x
any user could ask it to:

* attach a disk file they themselves could not read, such as another user's profile container,
  and then browse its contents;
* add any file to the boot-time auto-mount list;
* detach anyone's disk, or change the drive letter of any volume.

5.0 impersonates the pipe client and opens the target file with the **caller's own token** before
acting. Read access is required for read-only attach and detach, and read/write access for a
writable attach. Non-administrators can only change drive letters of volumes on virtual disks
they can access. Remaining P1 hardening: restrict the HKCR context-menu commands to elevated
callers and add an admin-managed allow-list mode for shared or RDS machines.

## 6. Roadmap

### P0: next release (5.1)
* **Command-line maintenance**, e.g. `VhdStudio.exe /compact "disk.vhdx" [/zeroonly]`, `/resize`,
  `/convert`. This makes it usable for scheduled FSLogix/VDI/WSL clean-up and in scripts.
* **Compact wizard for WSL/Docker**: find `ext4.vhdx` files automatically, run `fstrim` through
  `wsl -e`, compact them in one batch, and show the space recovered.
* **Repair context menu** button in Options (upstream #4).
* **Per-disk auto-mount editor**: options, retry delay for NAS or BitLocker-dependent volumes,
  boot order, and a log of the last attach result.
* **Attach dialog options**: read-only, no drive letter, mount folder, initialize new disk.

### P1
* **BitLocker unlock after attach** through Win32_EncryptableVolume. The password is entered in a
  Windows prompt and never stored in plain text.
* **Set physical sector size** (512 ↔ 4096) and **attach with temporary writes** (a throw-away
  differencing child, as in Arsenal Image Mounter).
* **"What's attached" view** listing every attached virtual disk on the system, with its owning
  process, using GetStorageDependencyInformation.
* **winget / Scoop packages** and a portable ZIP.
* **Admin-managed allow-list** for non-admin attach on shared machines.
* Localization (strings are already centralized), plus dark mode for the new windows.

### P2
* VHD Set (`.vhds`) snapshots and resilient change tracking (RCT) viewer.
* Add a VHD to the boot menu (BCD), as Simple VHD Manager does.
* Live copy (MirrorVirtualDisk) and physical-to-virtual capture.
* Read-only browsing of VHDX contents without attaching, and QCOW2/VMDK import through `qemu-img`.

## 7. Technical notes

* Everything is built on **VirtDisk.dll** (available on every Windows 10/11 edition) and the
  **Storage Management API** (`root\Microsoft\Windows\Storage`). There's no dependency on the
  Hyper-V module and no parsing of diskpart output.
* A full compact attaches the disk read-only without a drive letter, runs CompactVirtualDisk, then
  detaches it and runs a zero-block pass. Linux file systems need `fstrim` first so freed blocks are
  zeroed.
* Shrinking a VHDX is done in two steps: shrink the partition (Disk Manager), then
  ResizeVirtualDisk with `RESIZE_TO_SMALLEST_SAFE_VIRTUAL_SIZE`.
* Log replay happens when the file is opened read/write once with administrator rights. VHD Studio
  detects a pending log by parsing the VHDX header (CRC-32C validated, highest sequence number
  wins).
* Minimum OS: Windows 10 1809. Windows 7 and 8.1 are out of support, and features such as
  `IS_LOADED` and the Storage API need 8.1 or later anyway.
