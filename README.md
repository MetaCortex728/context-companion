# context-companion

A lightweight Windows utility for handing your own context to AI tools. When you press a key or make a gesture, it captures exactly what you choose (a window, a screen region, selected text), packages it as a documented packet with hashes and source details, and delivers it to a local inbox folder that coding agents and other tools can read. It records nothing in the background, does no reasoning of its own and favours no AI vendor.

Status: Stage 1 of the [roadmap](ROADMAP.md) is complete. The tray app captures a window, a region or selected text on a hotkey (or, if you switch it on, a tilt of the mouse wheel), shows a preview of exactly what will be sent, and delivers the packet to a local inbox with receipts. Every capture passes the privacy pipeline first. Measured on one desktop PC: under 0.2 % of one CPU core and 17 MB of private memory while idle, and about 0.2 s from hotkey to packet in the inbox. Stage 2 is next: an MCP server, audio clips, transcription, assistant destinations, window text and OCR.

## Layout

- `src/ContextCompanion.Core`: platform-neutral library (`net10.0`, no Windows APIs).
- `src/ContextCompanion.App`: the WPF tray app (`net10.0-windows10.0.19041.0`, Per-Monitor V2 DPI aware): tray icon and menu, hotkeys, the wheel gesture, the region selector, the preview and the deliveries window.
- `src/ContextCompanion.Platform.Windows`: Windows implementations of Core interfaces (`net10.0-windows10.0.19041.0`, Windows 10 version 2004 or later): DPAPI encryption at rest, window and region capture (Windows.Graphics.Capture with a GDI fallback), window enumeration, text through UI Automation, the clipboard and an explicit copy, global hotkeys and the wheel-gesture hook.
- `src/ContextCompanion.Tool`: the `ccomp` command-line tool (`net10.0`, plus a `net10.0-windows10.0.19041.0` build that can decrypt DPAPI-encrypted packets and capture).
- `tests/ContextCompanion.Core.Tests`: xUnit tests for Core and `ccomp`, run on Linux and Windows.
- `examples/consumer/read_packets.py`: a reference consumer in plain Python that checks and acknowledges packets.
- `tools/measure.ps1`: the measurements below.
- `tests/ContextCompanion.Platform.Windows.Tests`: DPAPI, capture, window enumeration, UI Automation, clipboard, hotkey and hook tests; they run on Windows with an interactive desktop and are reported as skipped, with the reason, elsewhere. The text tests take the foreground and use the clipboard, so leave the desktop alone while they run.

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

Continuous integration (`.github/workflows/ci.yml`) does both: `ubuntu-latest` builds everything with Windows targeting enabled and runs the Core tests, and `windows-latest` does the same natively and also runs the Windows tests. .NET CLI telemetry is switched off in CI.

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

## Using the tray app

Build it (`dotnet build src/ContextCompanion.App -c Release`) and run `ContextCompanion.exe` from `src/ContextCompanion.App/bin/Release/net10.0-windows10.0.19041.0/`. It needs Windows 10 version 2004 or later. Its icon stays in the notification area while it runs; a second copy refuses to start.

| Trigger | Captures |
| --- | --- |
| `Ctrl+Alt+W` | The foreground window. |
| `Ctrl+Alt+R` | A region: the screen dims, you drag a rectangle on one monitor, Esc or a right click cancels. |
| `Ctrl+Alt+T` | The selected text of the focused application (UI Automation first, then a copy that restores your clipboard; never Ctrl+C in a terminal). |
| Wheel tilt left / right (off by default) | The foreground window / a region around the pointer, as in the prototype. Switch it on in the tray menu or with `app.wheel_gesture`. |
| Tray menu | The same three captures (window and text after 3 s, so you can click back into the window first), the preview and gesture switches, the deliveries window, and the inbox and settings folders. |

With preview on (the default), each capture opens a window that shows what the packet will hold after the privacy rules: blacked-out windows, redacted text, the source details you left on. Add a note if you like and choose Send to inbox (Ctrl+Enter) or Discard (Esc). A notification then names the packet, its capture mode and how many redactions it has, never its content.

"Deliveries..." (or a double click on the icon) lists every packet with its state, when it reached the inbox and which consumer acknowledged it.

The app reads `%APPDATA%\context-companion\settings.json`, or the file given with `--settings <file>`. Without one the defaults apply: inbox `%USERPROFILE%\ContextCompanion\inbox`, preview on, gesture off, the hotkeys above. The `app` section changes them:

```json
{ "schema_version": 1,
  "app": { "inbox": "D:\\agents\\inbox", "preview": true, "wheel_gesture": false,
           "hotkeys": { "window": "Ctrl+Alt+W", "region": "Ctrl+Alt+R", "text": "Ctrl+Alt+T" } } }
```

At start, a notification reports any hotkey that another application already holds. Every setting is listed in [PRIVACY.md](PRIVACY.md#settings).

## Reading packets

Agents and scripts read the inbox folder directly. [docs/agents.md](docs/agents.md) describes the layout and gives instructions to paste into `CLAUDE.md`, `AGENTS.md` or similar files. [`examples/consumer/read_packets.py`](examples/consumer/read_packets.py) is plain Python. It lists the packets nobody has taken yet, checks every hash and prints each packet's files and text. With `--ack <name>` it also acknowledges them:

```
> python examples\consumer\read_packets.py %USERPROFILE%\ContextCompanion\inbox --ack claude-code
OK 01M6JWVCC2J331XV24C22K4HYJ  2026-11-04T09:04:43.394Z  window-wgc  redactions: 1
  image: C:\Users\me\ContextCompanion\inbox\01M6JWVCC2J331XV24C22K4HYJ\capture.png
  note: C:\Users\me\ContextCompanion\inbox\01M6JWVCC2J331XV24C22K4HYJ\note.txt
    | layout check; password=[REDACTED:connection-string]
  acknowledged as claude-code
```

`ReferenceConsumerTests` runs it against packets written by Core and checks its `ack.json` with the C# validator.

## Measurements

Measured on 4 November 2026 with [`tools/measure.ps1`](tools/measure.ps1) on one PC: Release build, Windows 11, Intel Core i5-14500, one monitor at 100 %.

| What | Result |
| --- | --- |
| Idle, hotkeys only, 60 s after a 15 s settle | 46.9 ms of CPU time (0.08 % of one core), working set 67 MB, private memory 17 MB, 15 threads |
| Idle, hotkeys and the wheel-gesture hook | 109.4 ms of CPU time (0.18 % of one core), working set 66 MB, private memory 17 MB, 16 threads |
| Window hotkey to packet in the inbox (1280×720 window, preview off, 10 runs) | median 228 ms, range 77 to 590 ms |
| Text hotkey to packet in the inbox (explicit copy, preview off, 10 runs) | median 172 ms, range 158 to 202 ms |

Latency is measured from the hotkey press to the moment the packet folder and its manifest exist in the inbox. It includes the capture, the privacy rules, PNG encoding, the outbox and delivery. The idle CPU with the hook includes ordinary mouse movement during that minute. These numbers come from one machine and are not a benchmark.

## Capture from the command line

The Windows build of `ccomp` runs the same captures from the command line, through the same privacy pipeline, outbox and inbox destination as the app:

```
ccomp capture window --foreground --inbox <dir> [--delay <seconds>]
ccomp capture window --hwnd 0x1A2B3C --inbox <dir>
ccomp capture region --rect <x,y,width,height> --inbox <dir>
ccomp capture text [--clipboard] --inbox <dir> --delay 3
```

Each command also takes `--settings <file>` (privacy and capture settings) and `--outbox <dir>` (default `%LOCALAPPDATA%\context-companion\outbox`). `--delay` waits up to 60 seconds first, so you can focus another window. Rectangles are physical screen pixels. The command prints the packet id, the capture mode (for example `window-wgc` or `region-gdi`, the method that produced the pixels, or `text-selection`, `text-clipboard` or `text-copy`, the source the text came from), whether it was delivered, and the redaction counts, never content or titles. A refused capture (denylisted, minimised or protected window, no text) exits with 1 and says why; the audit log in the inbox records refusals by the denylist.

Text comes from the UI Automation selection, then the clipboard (only with `--clipboard`), then a Ctrl+C that restores your clipboard afterwards. Ctrl+C is never sent to consoles and terminals. Methods, fallbacks, DPI, the visible capture border, minimised windows, multi-monitor regions, resizing and the clipboard rules are described in [docs/capture.md](docs/capture.md).

`CaptureCliTests` runs these commands with stand-ins for the Windows parts, and a Windows test runs `ccomp capture window` against a real window on `windows-latest`. The `net10.0` build refuses; this is from Linux:

```
$ ccomp capture region --rect 0,0,10,10 --inbox /tmp/ccomp-x
ccomp: capture works only on Windows, with the Windows build of ccomp. Nothing was captured.
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
