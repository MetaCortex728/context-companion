# context-companion

A lightweight Windows utility for handing your own context to AI tools. When you press a key or make a gesture, it captures exactly what you choose (a window, a screen region, selected text), packages it as a documented packet with hashes and source details, and delivers it to a local inbox folder that coding agents and other tools can read. It records nothing in the background, does no reasoning of its own and favours no AI vendor.

Status: planning. See [ROADMAP.md](ROADMAP.md).

## Layout

- `src/ContextCompanion.Core`: platform-neutral library (`net10.0`, no Windows APIs).
- `src/ContextCompanion.App`: the WPF tray app (`net10.0-windows`). For now a skeleton that shows a tray icon and exits from the tray menu.
- `src/ContextCompanion.Tool`: the `ccomp` command-line tool (`net10.0`).
- `tests/ContextCompanion.Core.Tests`: xUnit tests for Core and `ccomp`, run on Linux and Windows.

## Build and test

Requires the .NET 10 SDK (see `global.json`).

On Windows:

```
dotnet build ContextCompanion.sln
dotnet test tests/ContextCompanion.Core.Tests
```

On Linux or macOS the WPF app can be compiled but not run; enable Windows targeting to build the whole solution:

```
dotnet build ContextCompanion.sln -p:EnableWindowsTargeting=true
dotnet test tests/ContextCompanion.Core.Tests
```

Continuous integration (`.github/workflows/ci.yml`) does both: `ubuntu-latest` builds everything with Windows targeting enabled and runs the Core tests, and `windows-latest` does the same natively. .NET CLI telemetry is switched off in CI.

## Packets and `ccomp`

Each capture becomes a **packet**: a folder `inbox/<id>/` with a `manifest.json` (ULID id, creation time, items with sizes and SHA-256 hashes, optional source details, a record of redactions) and the captured files. Packets appear in the inbox atomically, and a consumer may acknowledge one by adding `ack.json`. The format is specified in [docs/packet-format.md](docs/packet-format.md), with JSON Schemas in [`schema/`](schema/).

The `ccomp` tool checks packets and writes acknowledgements. Build it with `dotnet build src/ContextCompanion.Tool` (or `dotnet publish src/ContextCompanion.Tool -o <dir>`) and run `ccomp` from the output, or `dotnet ccomp.dll` on any platform. The transcript below is from a packet written with `PacketBuilder` and `InboxWriter`, the same calls the tests use; `CliTests` runs these commands against packets created in tests.

```
$ ccomp validate inbox
OK      01M6H1SAXM46JXKMSM1R4EM449: 2 items, 30 bytes
1 packet checked, 0 invalid.

$ ccomp ack inbox/01M6H1SAXM46JXKMSM1R4EM449 --consumer example-agent
Acknowledged 01M6H1SAXM46JXKMSM1R4EM449 as example-agent at 2026-11-03T15:52:30.640Z.

$ ccomp validate inbox/01M6H1SAXM46JXKMSM1R4EM449
OK      01M6H1SAXM46JXKMSM1R4EM449: 2 items, 30 bytes, acknowledged by example-agent

$ printf 'x' >> inbox/01M6H1SAXM46JXKMSM1R4EM449/selection.txt
$ ccomp validate inbox
INVALID 01M6H1SAXM46JXKMSM1R4EM449
  - Size: 'selection.txt' has 19 bytes, manifest says 18
1 packet checked, 1 invalid.
```

`ccomp validate` exits with 0 when everything is valid and 1 otherwise; it never prints payload content or window titles. `ccomp ack` refuses invalid packets and never replaces an existing acknowledgement. An acknowledgement means the packet was taken, not that it was read or answered.

Before delivery, packets wait in a bounded outbox that suppresses consecutive duplicates, tracks each delivery as queued, delivering, delivered, consumed, uncertain or failed, and records every state change, with hashes but no content, in an append-only audit log. See [docs/outbox-and-receipts.md](docs/outbox-and-receipts.md).

## Origin

Han built the first version from 23 October 2026 for his own use, with OpenAI Codex as a coding assistant and without version control: a .NET WPF tray app that captured the foreground window or a cursor region on horizontal mouse-wheel gestures, kept a bounded outbox on disk and delivered captures to one Codex chat with receipt tracking.

`prototype/` keeps only the parts this project reuses, unchanged except as noted:

- `Capture.cs`: window and region capture in a helper process, and the low-level mouse trigger. In window mode it refuses windows that other applications protect from capture.
- `CaptureOutbox.cs`: the persistent, bounded outbox and attempt states. The Codex delivery class (`CaptureSender`) at the end of the original file is removed; one line still reads the Codex thread identifier to tag attempts.
- `CaptureDeliveryQueue.cs`: the single-consumer delivery queue with pause, resume and retry rules.
- `CaptureChecks.cs`: the prototype's self-checks for capture and outbox behaviour.
- `TrayControls.cs`, `CompanionIcon.cs`, `Assets/companion.ico`: tray recovery controls and the icon.

Left out on purpose: the Codex reply reader and delivery scripts, the paired-browser relay, the reply overlay (which hid itself from screen capture) and settings tied to a chat destination. The prototype files refer to classes that are not included, so they document the original design rather than build here.

## Licence

Apache License 2.0. See [LICENSE](LICENSE).
