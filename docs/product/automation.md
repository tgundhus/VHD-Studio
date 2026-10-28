# Agent automation: VHDX workspaces

`VhdStudio.Automation.exe` provides a headless JSON CLI and local stdio MCP server. Both use the
same operations and policy checks. Privileged creation, attach and safe detach go through the
local Windows service; the automation process runs as the intended Windows user. Install the
application and service from the same build before using mutation tools.
Automation checks the service protocol before submitting mutations; an older installed service
returns `service_upgrade_required` without receiving a disk operation.

This first implementation supports standalone VHDX workspaces. VSUX retains its interactive
password flow. Remote image transfer, arbitrary partition editing, forced detach and program
execution are outside this interface.

## Authorize a directory

`--root` must be an existing full local directory. Without `--allow-write`, only inspection and
status are available. That flag explicitly delegates create, attach (including read-only attach)
and safe detach within the root; tool arguments cannot enable it. `--max-size-bytes` limits
creation and attachment capacity, defaulting to 100 GiB. The limit must be at least 64 MiB and
aligned to 4096 bytes. New images have the same minimum/alignment, including blank images.

Paths can be relative to the root or full paths inside it. Junctions, symbolic links, mounted
folders, hard-linked images, network/device paths and alternate data streams are refused.
`image_list` inspects at most 100 images directly in the root and reports truncation. Nested
images can be addressed explicitly. Scope and capacity settings are API guardrails for a trusted
local agent, not an OS sandbox. Windows permissions and the service's caller checks still apply.
The service opens existing images and creates new files under the caller's own Windows token.

## JSON CLI

Use `--help` for syntax or `capabilities` for machine-readable discovery. Each call reads one
JSON object from redirected stdin or `--input FILE`, limited to 64 KiB of UTF-8 JSON. With no
redirected input it uses `{}`. Standard output contains one JSON result. Exit codes: 0 success,
1 operation failure, 2 invalid startup/input.

PowerShell example, after creating and explicitly authorizing `D:\AgentDisks`:

```powershell
$automation = 'C:\Program Files\VHD Studio\VhdStudio.Automation.exe'
$imageRoot = 'D:\AgentDisks'
$createRequest = [guid]::NewGuid().ToString()

@{ path = 'project.vhdx'; sizeBytes = 1073741824; requestId = $createRequest } |
    ConvertTo-Json -Compress |
    & $automation --root $imageRoot --allow-write image_create

@{ path = 'project.vhdx'; readOnly = $false; requestId = [guid]::NewGuid().ToString() } |
    ConvertTo-Json -Compress |
    & $automation --root $imageRoot --allow-write image_attach

# Work with returned data.volumes paths, then close all file handles.
@{ path = 'project.vhdx'; requestId = [guid]::NewGuid().ToString() } |
    ConvertTo-Json -Compress |
    & $automation --root $imageRoot --allow-write image_detach
```

| Operation | Arguments | Behavior |
|---|---|---|
| `image_list` | `{}` | Lists top-level VHDX images and per-image errors. |
| `image_inspect` | `path` | Returns identity, type, capacity, attachment and volumes. |
| `image_create` | `path`, integer `sizeBytes`; optional `prepare` (true), `fixedSize` (false), `requestId` | Creates a new image. Preparation initializes GPT and one NTFS partition on that exact new disk, then safely detaches it. Clearing `prepare` leaves a blank image. |
| `image_attach` | `path`; optional `readOnly` (true), `expectedImageId`, `requestId` | Attaches without initializing or formatting. Returns actual volume paths. |
| `image_detach` | `path`; optional `expectedImageId`, `requestId` | Flushes, locks and dismounts volumes first. Open files cause `volume_busy`; no force option exists. |
| `operation_status` | `requestId` | Returns the stored outcome and fresh inspection separately. |

Existing destinations, including directories, are refused. Failed creation can retain a partial
image or attached disk; keep it for inspection. Attachment defaults to read-only. Pending VHDX
logs require interactive repair before automation attachment. Differencing images can be
inspected but are excluded from attachment.

Results contain `success`, `operation`, `requestId`, `data`, and `error` (`code`, `message`). An
image result includes `path`, `imageId`, `format`, `kind`, `virtualSizeBytes`, `physicalSizeBytes`,
`attached`, `physicalDrive`, `needsLogReplay`, and `volumes` (`volumePath`, `driveLetter`). Paths
and letters are host-specific. Windows can publish volumes shortly after an attach reply;
inspect again if paths are unavailable. For a detached image with a pending log, inspection
reports that condition without opening the provider; identity/capacity may be unavailable.

## MCP configuration

For a client using an `mcpServers` configuration object:

```json
{
  "mcpServers": {
    "vhdstudio": {
      "command": "C:\\Program Files\\VHD Studio\\VhdStudio.Automation.exe",
      "args": [
        "--mcp", "--root", "D:\\AgentDisks",
        "--allow-write", "--max-size-bytes", "21474836480"
      ]
    }
  }
}
```

Omit `--allow-write` for inspection-only access. The six tools match the CLI operations and return
structured content plus text, with MCP's error flag on failure. Protocol data uses stdout and
diagnostics use stderr. Client configuration syntax varies. There is no HTTP listener. A remote
agent needs a local worker or separately scoped file access to use Windows volume paths.

## Request tracking and recovery

Supply a new GUID in `requestId` for each intended mutation and store it before submission. If
omitted, the server generates and returns one, but an interrupted client might never receive it.
`expectedImageId` accepts the GUID returned by inspection to reject a changed image identifier.

The root's `.vhdstudio-operations` directory stores intent before sending the service request and
the final result afterward. Repeating the same ID and arguments returns the recorded result
without rerunning it; changed arguments return `request_conflict`. Successful records are
historical: inspect for present state. Calls for the same Windows user are serialized.
Journal readers allow atomic replacement, so polling status cannot block the final result save.
Saved paths are normalized like incoming paths before checking for unresolved requests, including
8.3 aliases in parent directories and custom short names without a tilde. Record paths must be
fully qualified.

A timeout or interruption can have an unknown outcome. Status shows the recorded state and fresh
inspection separately; an existing image is not proof that all preparation completed. Unknown
requests are never automatically repeated and block new mutations for that image. Inspect and
recover through the desktop UI. Once the original request has certainly ended and recovery is
verified, an operator can archive that request's journal file to permit new automation. Agents
must not delete unresolved records to bypass this guard. Keep these local records at the source
when carrying a detached image to another machine.

Disconnecting a client does not force detach or cancel noninterruptible work. Service audit logs
attribute mutations to the caller. `service_unavailable` means the request was not submitted to a
trusted service; `outcome_unknown` requires inspection before further action.

## Portable handoff and verification

Close programs and handles, confirm safe detach, then copy the standalone VHDX. It carries files,
including any project instructions or manifest inside it. The receiving Windows host needs its
own compatible installation, permissions and agent runtime. Conversations, host tools, process
state and credentials do not travel automatically. A VHDX does not isolate code from its host.
VSUX has its own single-file portability and experimental unencrypted mount lock; see
[VSUX usage](../VSUX.md).

`AutomationTest` covers scope, delegation, links, capacity, overwrite refusal, malformed input and
interrupted requests. `CreationBrokerTest` covers caller-bound creation and native preparation.
`AutomationJournalTest` verifies replacement while a status reader remains open, complete old/new
snapshots, equivalent-path replay and unresolved-request blocking for ordinary and custom short
directory names. Custom-name fixtures require elevation and change only their own temporary directory.
The opt-in `AutomationNative` test covers request replay/status, writable attach, busy-detach
refusal, safe detach and read-only reattach with SHA-256 payload verification. It needs elevation,
the matching running service and `VHDSTUDIO_AUTOMATION_NATIVE_TEST=1`. Tests touch only their own
unique temporary images and retain an image if safe cleanup cannot be confirmed.

After building, run `pwsh -File Source\VhdStudio.Automation\Smoke-Test.ps1` for CLI and legacy/current
MCP checks without touching disks. See [BUILD.md](../../BUILD.md).
