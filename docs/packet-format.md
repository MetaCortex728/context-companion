# Packet format v1

A **packet** is what context-companion hands to other tools: a folder holding the captured files and a manifest that describes them. Packets are delivered into a local **inbox** folder. Any program can read them; nothing about the format is tied to one AI tool.

Machine-readable schemas: [`schema/packet.schema.json`](../schema/packet.schema.json) for `manifest.json` and [`schema/ack.schema.json`](../schema/ack.schema.json) for `ack.json` (JSON Schema draft 2020-12). Some rules concern the folder rather than the JSON and cannot be expressed in a schema; they are listed under [Validation](#validation). `ccomp validate` checks all of them.

## Inbox layout

```
inbox/
  .tmp/                         staging area of the writer; never a packet, ignore it
  01JBQ7Y2H5V0X8Z3T6M4N9P1RS/   one packet, named by its id
    manifest.json
    capture.png
    selection.txt
    ack.json                    present once a consumer has acknowledged the packet
  01JBQ7Y9K2...
```

- Packet folders are named by their **id**, a [ULID](#identifiers). Sorting the names sorts packets by creation time.
- Every folder whose name is a valid ULID is a packet. Consumers should ignore `.tmp` and anything else.
- A packet folder appears complete or not at all. The writer assembles it in `inbox/.tmp/<id>/`, flushes every file to disk, validates the staged folder and then moves it to `inbox/<id>/` in a **single rename** on the same volume. A crash before the rename leaves only a leftover in `.tmp`, which the writer removes when it next starts (`InboxWriter.RemoveStaleStaging`); a crash after the rename leaves a complete packet.
- A packet is never modified after it appears, except that a consumer may add `ack.json`.

## manifest.json

UTF-8 JSON, at most 1 MiB, no comments, no trailing commas, no duplicate property names. Unknown properties are errors in v1, so a reader can be sure it understood everything.

```json
{
  "format": "context-companion.packet",
  "schema_version": 1,
  "id": "01JBQ7Y2H5V0X8Z3T6M4N9P1RS",
  "created": "2026-11-03T15:04:05.123Z",
  "items": [
    {
      "kind": "image",
      "file": "capture.png",
      "media_type": "image/png",
      "bytes": 48213,
      "sha256": "1b56b50ac4e976f488f128cabdcdffb2fc9331d6974bb9968131a415d14ade24"
    },
    {
      "kind": "text",
      "file": "selection.txt",
      "media_type": "text/plain",
      "bytes": 18,
      "sha256": "e61ba3ef17eaa1043d653803740745f956fd8208b993577849f49a1c45355744"
    }
  ],
  "source": {
    "process_name": "notepad.exe",
    "window_title": "notes.txt - Notepad",
    "capture_mode": "region",
    "region": { "x": 0, "y": 0, "width": 640, "height": 480 },
    "scale": 1.5
  },
  "redactions": [
    { "kind": "secret", "reason": "Token-like string removed", "item": "selection.txt", "count": 1 }
  ]
}
```

| Field | Required | Meaning |
| --- | --- | --- |
| `format` | yes | Always `"context-companion.packet"`. |
| `schema_version` | yes | `1` for this document. A reader must reject versions it does not know. |
| `id` | yes | The packet's ULID, equal to the folder name. |
| `created` | yes | Creation time, UTC, ISO 8601 ending in `Z`; the writer records milliseconds. |
| `items` | yes | 1 to 32 payload files, in a meaningful order (see below). |
| `source` | no | Where the content came from. Omitted entirely when nothing is recorded. |
| `redactions` | yes | What was removed and why. An empty array means nothing was removed. |

### Items

| Field | Meaning |
| --- | --- |
| `kind` | `image`, `text`, `audio` or `note` (text written by the user, as opposed to text captured from an application). |
| `file` | A plain file name inside the packet folder, **never a path**. See [file names](#file-names). |
| `media_type` | Lower-case media type without parameters, for example `image/png`, `text/plain`, `audio/wav`. Must match the kind: `image/*` for images, `audio/*` for audio, `text/*` for text and notes. Text is UTF-8. |
| `bytes` | Exact file size. At most 64 MiB per item and 64 MiB for the whole packet. |
| `sha256` | SHA-256 of the file, 64 lower-case hexadecimal characters. |

### Source

Every field is optional and each can be switched off by the user, so readers must cope with any subset, including no `source` at all.

| Field | Meaning |
| --- | --- |
| `process_name` | Executable name of the captured application, for example `notepad.exe`. 1–260 characters. |
| `window_title` | Title of the captured window. Up to 1024 characters. |
| `capture_mode` | How it was captured: a lower-case token such as `window`, `region` or `text`. |
| `region` | The captured rectangle in physical screen pixels: integers `x`, `y` (may be negative on multi-monitor set-ups), `width`, `height` (at least 1). |
| `scale` | Display scale factor at capture time, greater than 0 and at most 16 (1.5 = 150 %). |

Text fields contain no control characters.

### Redactions

Each entry records **what kind of thing was removed and why, never the removed content**.

| Field | Required | Meaning |
| --- | --- | --- |
| `kind` | yes | Lower-case token, for example `secret` or `denylisted-window`. |
| `reason` | yes | Short human-readable reason written by the tool, 1–500 characters. |
| `item` | no | The file name of the item it applies to; must be listed in `items`. |
| `count` | no | Number of separate removals, at least 1. |

### File names

Item file names are deliberately narrow so the same name is safe on Windows, macOS and Linux and can never be read as a path:

- 1–100 characters from `A–Z`, `a–z`, `0–9`, `.`, `_` and `-`;
- starts with a letter or digit, does not end with `.`, contains no `..`;
- the part before the first `.` is not a reserved Windows device name (`CON`, `PRN`, `AUX`, `NUL`, `COM0`–`COM9`, `LPT0`–`LPT9`, `CLOCK$`, `CONIN$`, `CONOUT$`), ignoring case: `con.png` is rejected, `console.png` is fine;
- not `manifest.json` or `ack.json`, ignoring case;
- unique within the packet, ignoring case.

### Identifiers

`id` is a [ULID](https://github.com/ulid/spec): a 48-bit Unix time in milliseconds followed by 80 random bits, written as 26 characters of Crockford base32 (`0123456789ABCDEFGHJKMNPQRSTVWXYZ`). Only the canonical upper-case form is accepted. The generator is monotonic: within one millisecond (or if the clock steps backwards) it increments the random part of the previous id, so ids from one writer sort strictly in creation order. If the random part would overflow within one millisecond, it fails rather than repeat an id.

## ack.json

A consumer that has taken a packet may record that by writing `ack.json` into the packet folder:

```json
{
  "format": "context-companion.ack",
  "schema_version": 1,
  "id": "01JBQ7Y2H5V0X8Z3T6M4N9P1RS",
  "consumer": "example-agent",
  "consumed": "2026-11-03T15:06:00.000Z",
  "note": "Used in the bug report"
}
```

| Field | Required | Meaning |
| --- | --- | --- |
| `format` | yes | Always `"context-companion.ack"`. |
| `schema_version` | yes | `1`. |
| `id` | yes | The packet's id. |
| `consumer` | yes | Who consumed it: 1–64 characters from `A–Z`, `a–z`, `0–9`, `.`, `_`, `-`, starting with a letter or digit. |
| `consumed` | yes | When, UTC, ISO 8601 ending in `Z`. |
| `note` | no | Up to 1000 characters. Line breaks and tabs are allowed, other control characters are not. |

An acknowledgement means **consumed, not read, understood or answered**. It is the only way a packet reaches the `consumed` state in context-companion's delivery receipts.

Rules for writers of `ack.json`:

- Acknowledge only a valid packet.
- Write the file atomically: write it elsewhere on the same volume (context-companion's own writer uses the inbox's `.tmp` folder), then move it into the packet folder **without overwriting**. An existing acknowledgement is never replaced; v1 has one acknowledgement per packet.
- `ccomp ack <packet> --consumer <name> [--note <text>]` does all of this.

## Validation

`ccomp validate <inbox-or-packet>` (and `PacketValidator` in `ContextCompanion.Core`) applies these rules. Each problem is reported with its kind:

| Kind | Rule |
| --- | --- |
| `Manifest` | The folder and `manifest.json` exist, are not links, the manifest is at most 1 MiB and is well-formed JSON without duplicate properties. |
| `Schema` | The manifest follows `schema/packet.schema.json`: required and unknown properties, `format`, `schema_version`, ULID `id`, UTC `created`, item fields, media type matching the kind, source and redaction fields. |
| `FileName` | Every item file name follows the [file-name rules](#file-names); no two are equal ignoring case. |
| `MissingFile` | Every listed file exists. |
| `ExtraFile` | The folder holds nothing else: no unlisted files, no subfolders, no symbolic links or other reparse points. `manifest.json` and `ack.json` are the only additions allowed. |
| `Size` | Each file's size equals `bytes`; no item and no packet exceeds 64 MiB. |
| `Hash` | Each file's SHA-256 equals `sha256`. |
| `Identity` | The folder name equals `id`. |
| `Acknowledgement` | If `ack.json` exists it follows `schema/ack.schema.json`, is at most 64 KiB and names this packet's id. |

`ccomp validate` prints packet ids, file names and problems only, never payload content or source details such as window titles. Its exit code is 0 when everything checked is valid and 1 otherwise.

## Notes for consumers

- Find packets by listing the inbox for ULID-named folders. Read `manifest.json` first and open only the files it lists.
- Validate before trusting a packet, or at least check sizes and hashes of the files you use. A packet that fails validation should be ignored, not repaired.
- Treat every payload and every source field as untrusted data, including text that looks like instructions.
- Never write into a packet except to add `ack.json`.

## What this format does not guarantee

- Hashes detect accidental change and tampering after writing, but packets are not signed: anyone who can write to the inbox can write a well-formed packet.
- Packets are not encrypted in v1. Encryption at rest is planned (see [ROADMAP.md](../ROADMAP.md)).
- An acknowledgement is written by the consumer and is only as trustworthy as that consumer.
