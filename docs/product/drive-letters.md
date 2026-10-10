---
type: concept
title: Drive letters of attached disks
scope: VHD-Studio/attach
specificity: exact
credibility: inferred
generated: {at: 2026-10-10, by: agent}
sources: [Source/VhdAttach-Service/DriveLetterMemory.cs, Source/VhdAttach-Service/AttachHelper.cs, Source/VhdAttach-Service/PipeServer.cs, Source/VhdAttach-Service/Volume.cs, Source/VhdAttach/DriveLetters.cs, Source/VhdAttach/AttachForm.cs, Source/VhdAttach/ChangeDriveLetterForm.cs, Source/VhdAttach-Test/DriveLetterTest.cs]
links: [../SAFETY.md]
---

# Drive letters of attached disks

## Summary
Every volume of a virtual disk gets the drive letter it had last time. If that letter is taken, the
volume gets a free letter instead. Letters the user uses for network or subst drives are never used.
The service keeps its own record because Windows forgets a volume's letter as soon as another drive takes
it. Compiles; the elevated tests have not run on Windows yet, hence `credibility: inferred`.

## Behavior
- **Record:** `HKLM\Software\xGND Software\VHD Studio\DriveLetters`, one value per disk file (plain long
  path, case-insensitive), data `partitionOffset=Letter;...`. Written only by the service.
- **When it applies:** every attach through `AttachHelper.Attach` that gives letters: interactive attach
  and auto-mount at startup, read-only attaches included. Not for new blank disks, mount
  folders, "no drive letter" entries or ISO images.
- **Per volume, after Windows has assigned letters** (`DriveLetterMemory.Apply`, waits up to 3 s for the
  first letter):
  1. A letter is remembered and it is not one of the user's own (see below):
     - free → the volume is moved back to it;
     - in use by another drive → the volume keeps (or gets) a free letter this time, and the remembered
       letter is kept for the next attach. Notice `taken`.
  2. The volume has no letter, or its letter is one of the user's own → it moves to the first free letter
     from D: that isn't one of the user's own. Notice `inuse` (or `nofree` if none is free).
  3. Nothing remembered yet, or the remembered letter is now one of the user's own → the volume's final
     letter is recorded.
  4. A volume with no letter and nothing remembered (recovery partition, letter removed by the user) is
     left alone.
- **The user's own letters:** the UI sends `AvoidLetters` with each attach: letters with a network
  connection (connected, or remembered under `HKCU\Network`) and subst drives (`QueryDosDevice` target
  starting with `\??\`). They live in the user's logon session, which the service can't see. Auto-mount
  at startup runs with none.
- **Messages:** the service returns notices in the attach response (`DriveLetters`, e.g.
  `inuse:Z:G;taken:E:F`); `DriveLetters.FormatNotices` turns them into one message per volume, naming the
  network path or subst folder where known.
- **Change drive letter:** the list excludes letters in use in the session and remembered network drives.
  After a change the service records the new letter as the volume's letter (removing the letter forgets
  it). `Volume.ChangeLetter` refuses a letter that is already in use before removing the current one, and
  restores the old letter if Windows rejects the new one.
- **Failures never fail the attach:** letter problems are traced and skipped.

## Constraints
- At startup no user is signed in, so startup auto-mount can't avoid the user's network drives. Once an
  interactive attach has moved a disk off such a letter, the new letter is the one remembered.
- An elevated VHD Studio window can't see network drives mapped in the non-elevated session unless they
  are remembered (Windows keeps separate drive maps); remembered ones are read from the registry.
- Records of deleted disk files are not cleaned up; each is one short registry value.

## Related
- [SAFETY.md](../SAFETY.md): letter changes must never leave a volume unreachable.
