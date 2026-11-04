# context-companion

A lightweight Windows utility for handing your own context to AI tools. When you press a key or make a gesture, it captures exactly what you choose (a window, a screen region, selected text), packages it as a documented packet with hashes and source details, and delivers it to a local inbox folder that coding agents and other tools can read. It records nothing in the background, does no reasoning of its own and favours no AI vendor.

Status: the first half of Stage 1 of the [roadmap](ROADMAP.md) works and is tested on Linux and Windows: the packet format and `ccomp`, the outbox with delivery receipts and the audit log, and the privacy pipeline. Capture itself (window, region and selected text), hotkeys and the tray app's delivery to the inbox are next.

## Layout

- `src/ContextCompanion.Core`: platform-neutral library (`net10.0`, no Windows APIs).
- `src/ContextCompanion.App`: the WPF tray app (`net10.0-windows`). For now a skeleton that shows a tray icon and exits from the tray menu.
- `src/ContextCompanion.Platform.Windows`: Windows implementations of Core interfaces (`net10.0-windows`); for now DPAPI encryption at rest.
- `src/ContextCompanion.Tool`: the `ccomp` command-line tool (`net10.0`, plus a `net10.0-windows` build that can decrypt DPAPI-encrypted packets).
- `tests/ContextCompanion.Core.Tests`: xUnit tests for Core and `ccomp`, run on Linux and Windows.
- `tests/ContextCompanion.Platform.Windows.Tests`: DPAPI tests; they run on Windows and are reported as skipped elsewhere.

## Build and test

Requires the .NET 10 SDK (see `global.json`).

On Windows:

```
dotnet build ContextCompanion.sln
dotnet test tests/ContextCompanion.Core.Tests
dotnet test tests/ContextCompanion.Platform.Windows.Tests
```

On Linux or macOS the WPF app can be compiled but not run; enable Windows targeting to build the whole solution:

```
dotnet build ContextCompanion.sln -p:EnableWindowsTargeting=true
dotnet test tests/ContextCompanion.Core.Tests
```

Continuous integration (`.github/workflows/ci.yml`) does both: `ubuntu-latest` builds everything with Windows targeting enabled and runs the Core tests, and `windows-latest` does the same natively and also runs the DPAPI tests. .NET CLI telemetry is switched off in CI.

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

## Privacy

Every capture passes through a fixed privacy pipeline before it becomes a packet: windows on a denylist (password managers, the Windows credential prompt) are refused or blacked out, source details you have switched off are dropped (window titles and executable paths are off by default), secret-shaped text is replaced with `[REDACTED:<pattern>]`, and the packet can optionally be encrypted with DPAPI. Inboxes keep packets within retention limits, and every delivery, refusal and removal is written to an audit log that holds ids, states and hashes, never content. [PRIVACY.md](PRIVACY.md) describes each rule, setting and default, how to extend the denylist and the redaction patterns, how consumers decrypt, and what none of this can guarantee.

Three `ccomp` commands go with it. The transcripts below come from an inbox filled by `PrivacyPipeline`, `Outbox`, `DeliveryQueue` and `InboxDestination`: one capture refused because the captured window was KeePassXC, then a region capture with a Bitwarden window in it and a connection string in the text, then a text capture.

`ccomp audit` shows the audit log; it can show only what the log holds:

```
$ ccomp audit inbox
TIME                      PACKET                      DESTINATION  CHANGE                   REASON
2026-11-04T00:04:12.375Z  -                           -            - -> blocked             denylist rule keepassxc
2026-11-04T00:04:12.512Z  01M6HXXNGZJXM5SEVYEM9KBPK5  inbox        - -> queued
2026-11-04T00:04:12.525Z  01M6HXXNK7PAJ68K5J0V7TDA3J  inbox        - -> queued
2026-11-04T00:04:12.532Z  01M6HXXNGZJXM5SEVYEM9KBPK5  inbox        queued -> delivering
2026-11-04T00:04:12.545Z  01M6HXXNGZJXM5SEVYEM9KBPK5  inbox        delivering -> delivered
2026-11-04T00:04:12.550Z  01M6HXXNK7PAJ68K5J0V7TDA3J  inbox        queued -> delivering
2026-11-04T00:04:12.557Z  01M6HXXNK7PAJ68K5J0V7TDA3J  inbox        delivering -> delivered
7 entries.
```

`--since <time>` and `--packet <id>` filter it and `--json` prints the entries as JSON lines.

`ccomp open` writes a plaintext copy of a packet after checking every plaintext size and SHA-256. For DPAPI-encrypted packets it needs the Windows build of `ccomp`, on Windows, run by the user who captured them; elsewhere it refuses and writes nothing. This run was on Linux, with a plaintext packet:

```
$ ccomp open inbox/01M6HXXNGZJXM5SEVYEM9KBPK5 --to opened
Opened 01M6HXXNGZJXM5SEVYEM9KBPK5 into /tmp/ccomp-demo/run/opened/01M6HXXNGZJXM5SEVYEM9KBPK5: 2 items, 310 bytes, 0 decrypted, plaintext sizes and SHA-256 verified.
```

`ccomp purge` removes packets: `--all`, `--older-than <duration>`, `--id <id>`, or without a selector the retention limits. It is a dry run unless you add `--yes`, and each removal is audited as the packet id and a reason:

```
$ ccomp purge inbox --older-than 1s
Would remove 01M6HXXNGZJXM5SEVYEM9KBPK5: purge-older-than, 1457 bytes
Would remove 01M6HXXNK7PAJ68K5J0V7TDA3J: purge-older-than, 584 bytes
2 packets, 2041 bytes would be removed. Run again with --yes to delete.

$ ccomp purge inbox --id 01M6HXXNGZJXM5SEVYEM9KBPK5 --yes
Removed 01M6HXXNGZJXM5SEVYEM9KBPK5: purge-id, 1457 bytes
1 packet removed, 1457 bytes. Deleted files may still be recoverable from the disk or from backups; see PRIVACY.md.
```

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
