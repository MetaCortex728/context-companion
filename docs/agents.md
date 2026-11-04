# Reading packets from a coding agent

context-companion delivers each capture to a folder on disk: the inbox. Any tool that can read files can use it,
including coding agents such as Claude Code, Codex, Gemini CLI or Cursor. Nothing is sent over the network, and the
tool does not call the agent. The agent reads the packet when you ask it to.

## Where the packets are

The inbox is `%USERPROFILE%\ContextCompanion\inbox` unless the settings file says otherwise (`app.inbox`; the
settings file is `%APPDATA%\context-companion\settings.json`, and the tray menu's "Open settings folder" opens it).
`ccomp capture ... --inbox <dir>` writes to the folder you give it.

Each packet is a folder named by a [ULID](https://github.com/ulid/spec). ULIDs sort by creation time, so the
newest packet is the last folder in name order. Folders whose names start with a dot (`.tmp`, `.audit.jsonl`) are
the inbox's own and are not packets.

```
inbox/
  01M6JWVCC2J331XV24C22K4HYJ/
    manifest.json    what the packet holds: items with sizes and SHA-256, the capture mode, the source details you
                     left switched on, and every redaction
    capture.png      a window or region image, if the packet has one
    selection.txt    captured text, if any
    note.txt         your note from the preview, if any
    ack.json         written by a consumer that took the packet
```

Text such as `[REDACTED:aws-access-key]` was replaced on purpose by the privacy pipeline; `manifest.json` lists each
redaction and blackout. The full format is in [packet-format.md](packet-format.md).

## Instructions to give an agent

Add something like this to the agent's instructions file (`CLAUDE.md`, `AGENTS.md`, `GEMINI.md` or the tool's
equivalent), with your own inbox path:

```markdown
## Context packets

When I say "check the inbox" or "look at my capture", read the newest packet in C:\Users\me\ContextCompanion\inbox.
Packet folders are named by ULIDs, which sort by time; ignore folders starting with a dot. Read manifest.json first,
then the files it lists: capture.png (an image of a window or region), selection.txt (text I selected) and note.txt
(my note about it). [REDACTED:...] marks text that was removed on purpose; do not try to recover it.
When you have used a packet, acknowledge it:
  python <path to context-companion>/examples/consumer/read_packets.py C:\Users\me\ContextCompanion\inbox --ack claude-code
```

Agents that can view images read `capture.png` directly. Packets are small: images are downscaled so the long edge is
at most 1568 px by default (`capture.max_long_edge`).

## Checking and acknowledging packets

Two consumers come with the repository. Both check every item's size and SHA-256 against the manifest before they
acknowledge anything, and neither ever replaces an existing acknowledgement.

- [`examples/consumer/read_packets.py`](../examples/consumer/read_packets.py) needs only Python 3. It lists the
  packets nobody has acknowledged yet, prints each file's path and the text of text items, and with `--ack <name>`
  writes `ack.json` into each packet that checked out. `--json` prints one JSON object per packet, for scripts.
  Use it as a starting point for a consumer of your own.
- `ccomp validate <inbox>` and `ccomp ack <packet> --consumer <name>` do the same from the command line.

An acknowledgement means that a consumer took the packet. It does not mean anyone read it, understood it or answered
it. The tray app's "Deliveries" window shows which packets have been acknowledged and by whom.

## Encrypted packets

When encryption at rest is switched on (`encryption.enabled`, off by default for the inbox), each item is stored as
`<file>.dpapi` and only the Windows account that captured it can decrypt it. Agents cannot read those files directly;
`ccomp open <packet> --to <dir>` writes a decrypted copy and checks every hash. See [PRIVACY.md](../PRIVACY.md).

## Coming later

An MCP server that exposes inbox packets as resources, with list, get and acknowledge tools, is planned
(roadmap item 10). Until then the inbox folder is the interface.
