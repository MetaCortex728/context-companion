# Provenance

context-companion continues a capture tray app that Han built for his own use from 23 October 2026. He built it with OpenAI Codex as a coding assistant and without version control, so that work has no commit history. This page lists the evidence that the work existed before this repository. For each item it says what kind of evidence it is, and what it does and does not show. All times are UTC.

The repository's first commit (`4fde841`, 3 November 2026 15:22) imports the six prototype files this project reuses into [`prototype/`](../prototype/). The README's [Origin](../README.md#origin) section says what was kept and what was left out.

## Timeline

| Time (UTC) | Event | Source |
|---|---|---|
| 2026-10-23 04:53 | The Codex session in which the app is built is under way (earliest entry in its stored segment) | session record |
| 2026-10-23 08:38 | The app's self-checks report "30 checks passed", before the capture files below are added | session record |
| 2026-10-23 10:34 | `TrayControls.cs` and `CompanionIcon.cs` written | session record, file creation time |
| 2026-10-23 11:03 | `Capture.cs` written: window and region capture and the horizontal-wheel trigger | session record, file creation time |
| 2026-10-23 11:04 | `CaptureOutbox.cs` written: the bounded outbox and attempt states | session record, file creation time |
| 2026-10-23 11:07 | `CaptureChecks.cs` written: the self-checks for capture and the outbox | session record, file creation time |
| 2026-10-23 11:52 | `CaptureDeliveryQueue.cs` written: the delivery queue with pause, resume and retry | session record, file creation time |
| 2026-10-23 13:21 | Last recorded changes to the outbox and the checks | session record, file write time |
| 2026-10-29 19:30 | Programme invitation received | email |
| 2026-11-03 15:22 | This repository's first commit imports the six files | git |

## The imported files are the October files

Each file in `prototype/` was compared with the original in Han's tools folder (`tools/domo-companion/`) on 4 November 2026.

| File in `prototype/` | Created | Last written | SHA-256 of the original | Same bytes |
|---|---|---|---|---|
| `Capture.cs` | 2026-10-23 11:03:40 | 2026-10-23 11:04:57 | `404f3eeee2c199b0dd51873d09939da75091c7f92d729e32067e5e80b45b8ed9` | yes |
| `CaptureChecks.cs` | 2026-10-23 11:07:25 | 2026-10-23 13:21:28 | `1aff5e027920d43e1b2c77831b5c9f1b247e3a7b61a2b5df0bc036a582f71e7d` | yes |
| `CaptureDeliveryQueue.cs` | 2026-10-23 11:52:07 | 2026-10-23 13:09:14 | `995ad60ef6f84b73b308b43564f69536c23faf5780c7cdfbce5fb7709af8a2fd` | yes |
| `CompanionIcon.cs` | 2026-10-23 10:35:40 | 2026-10-23 10:35:40 | `7ed19247ea48797a186bed2f0804118cb1547fcbd85d2621d3da03c4a6710485` | yes |
| `TrayControls.cs` | 2026-10-23 10:35:40 | 2026-10-23 10:35:40 | `0ef42d5a3dda270ce4bb56b8f6a68879cd780901fd8197d34d4058df277cbc08` | yes |
| `CaptureOutbox.cs` | 2026-10-23 11:04:57 | 2026-10-23 13:21:28 | `1781d4933730fdb0a919a46e9626043492a8c083a540abc5beeaf3f5dd2bfa86` | no: the Codex delivery class at the end of the file was removed on import |

`sha256sum prototype/*.cs` in this repository gives the same values for the first five files.

## The assistant's session records

OpenAI Codex keeps a local record of each session. Every file edit and command it ran is stored there with a timestamp. On 4 November 2026 the assistant read the session in which the app was built (`01a1077b-d0e3-7311-8391-32aff5b94dd9`). Only entries before the invitation (29 October 2026, 19:30 UTC) were counted. It replayed every recorded edit to the six files and hashed the result after each one. [`provenance/session-writes.json`](provenance/session-writes.json) lists all 20 edits and the recorded check run, with session line numbers and call ids. It holds no source code or conversation text.

| File | First written | Last written before the invitation | Edits | Replayed content vs the original |
|---|---|---|---|---|
| `TrayControls.cs` | 2026-10-23 10:34:40.372 | 2026-10-23 10:35:38.128 | 2 | same SHA-256 |
| `CompanionIcon.cs` | 2026-10-23 10:34:40.372 | 2026-10-23 10:35:38.128 | 2 | same SHA-256 |
| `Capture.cs` | 2026-10-23 11:03:37.276 | 2026-10-23 11:04:54.130 | 2 | same SHA-256 |
| `CaptureOutbox.cs` | 2026-10-23 11:04:54.130 | 2026-10-23 13:21:25.912 | 7 | same SHA-256 as the original (the copy here drops one class) |
| `CaptureChecks.cs` | 2026-10-23 11:07:23.033 | 2026-10-23 13:21:25.912 | 4 | same SHA-256 |
| `CaptureDeliveryQueue.cs` | 2026-10-23 11:52:04.522 | 2026-10-23 13:09:11.548 | 3 | the replay after the second edit (13:08:30) matches the original; the last recorded edit could not be replayed |

Each first write in the session records is about three seconds before the file's creation time on disk (for example `Capture.cs`: 11:03:37 in the record, 11:03:40 on disk). The session records and the filesystem are separate records, and they agree.

## What the prototype gave this project

| In the prototype (23 October) | In context-companion |
|---|---|
| Wheel tilt left captures the foreground window, right a region of 40 % of the monitor around the pointer, with a 700 ms debounce | The same gesture and region (`WheelGesture`, `PointerRegion`), now off by default and with a watchdog that reinstalls the hook |
| A bounded outbox (4 pending captures, a 256 MiB archive), duplicate suppression and attempt states | The outbox with receipts and an audit log ([docs/outbox-and-receipts.md](outbox-and-receipts.md)) |
| A delivery queue with pause, resume and retry | `DeliveryQueue` |
| Refusing windows that other applications protect from capture | The same refusal, before any pixels are taken |
| Delivery to one Codex chat | A vendor-neutral packet in a local inbox, read by any tool ([docs/agents.md](agents.md)) |
| A reply overlay that hid itself from screen capture | Removed: this tool never hides itself |

## What this shows, and what it does not

- **The hashes show** that the files in `prototype/` are byte for byte the files in Han's tools folder. They do not show when those files were written.
- **The session records show** each file being written and edited on 23 October. For five of the six files, replaying the recorded edits reproduces the original byte for byte; for the sixth, the replay matches up to its second-to-last edit. They are local records on Han's PC: harder to fake consistently than a file time, but not independent of the PC.
- **The filesystem times** agree with the session records to within a few seconds. They are local too.
- **No server-side timestamp** was found for this app in the searches made. That is a negative result of a limited search, not a statement that none exists.

The originals, the full session records and a live comparison can be shown to anyone who wants to check beyond this page.
