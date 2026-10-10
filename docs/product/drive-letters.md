---
type: concept
title: Drive letters of attached disks
scope: VHD-Studio/attach
specificity: exact
credibility: inferred
generated: {at: 2026-10-10, by: agent}
sources: [Source/VhdAttach/DriveLetters.cs, Source/VhdAttach/AttachForm.cs, Source/VhdAttach/ChangeDriveLetterForm.cs, Source/VhdAttach-Service/Volume.cs, Source/VhdAttach-Test/DriveLetterTest.cs]
links: [../SAFETY.md]
---

# Drive letters of attached disks

## Summary
Windows assigns drive letters when the service attaches a disk. Because the service runs as Local System,
Windows only sees global drives, not the mapped network drives and `subst` drives in the user's own
sign-in session. After an interactive attach, VHD Studio checks the letters from the user's session and
moves the disk off any letter the user already uses. Compiles; the elevated tests have not run on
Windows yet, hence `credibility: inferred`.

## Behavior
- **Who picks the letter:** Windows' mount manager, in the global namespace. VHD Studio never chooses a
  letter at attach time, except when moving off a conflict.
- **Conflict check** (`DriveLetters.GetConflict`, runs in the UI process after `AttachForm` attaches a
  disk, not for new blank disks): a volume's letter conflicts when
  1. the user has a network connection on it, connected or remembered but not connected
     (`WNetGetConnection`, or `HKCU\Network\<letter>`), or
  2. the user's session resolves the letter to something other than the volume
     (`QueryDosDevice` on the letter vs. on the volume's `Volume{GUID}` name), e.g. a `subst` drive.
- **Resolution:** the volume moves to the first letter from D: that is free in the user's session and
  not remembered for a network drive, through the service (`ChangeDriveLetter`). The user sees one
  message per moved volume. Windows remembers the new letter for that volume, so later attaches and
  auto-mount at startup reuse it.
- **Waiting:** letters appear shortly after the disk arrives; the check polls for up to 3 seconds until
  any volume has a letter. Disks with no lettered volume add that wait to the attach.
- **Change drive letter:** the list excludes letters in use in the session and remembered network
  drives. `Volume.ChangeLetter` refuses a letter that is already in use before removing the current one,
  and restores the old letter if Windows rejects the new one.
- **Failures never fail the attach:** an exception in the check is ignored; a failed move is reported.

## Constraints
- Auto-mount at startup runs before anyone signs in, so it can't see the user's drives. A disk that has
  only ever been auto-mounted keeps a conflicting letter until it is attached once from the UI or its
  letter is changed; after that Windows remembers the new letter.
- An elevated VHD Studio window can't see non-remembered network drives mapped in the non-elevated
  session (Windows keeps separate drive maps). Remembered ones are still detected through the registry.
- ISO images are not checked: their volumes are found by letter in the user's session, which a
  conflicting drive hides.

## Related
- [SAFETY.md](../SAFETY.md): letter changes must never leave a volume unreachable.
