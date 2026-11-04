# Privacy

context-companion hands context you choose to AI tools. Privacy here means that what you capture, and the people who appear in it, go nowhere you did not intend. It never means hiding the tool: context-companion does not hide its windows or itself from screen capture or from other people (see [ROADMAP.md](ROADMAP.md), *No concealment*).

This document describes what is implemented in `ContextCompanion.Core`, `ContextCompanion.Platform.Windows` and `ccomp` today. Window, region and text capture (run with `ccomp capture` until the tray app gets its triggers) hand their results to the privacy pipeline described here, so nothing reaches a packet before these rules have run. How capture itself works, including the clipboard rules for text, is in [docs/capture.md](docs/capture.md).

## What is captured, and when

Only when you trigger a capture. There are no timers, no background recording and no audio. Each trigger takes one frame of one window or region, or one piece of text. While Windows.Graphics.Capture takes the frame, Windows shows its capture border; context-companion never turns it off.

Before anything is taken, the target window is checked against the denylist: a denylisted window is refused before any pixels are grabbed and before a copy keystroke could reach it. Minimised windows and windows an application protects from capture are refused too.

One trigger produces one **capture request**: optionally the pixels of a window or screen region, optionally text (for example the selection), optionally a note you wrote, and source details reported by the operating system (process name, window class, window title, executable path, capture mode, region, display scale), plus the rectangles, process names and classes of the windows visible inside the region. The request exists only in memory.

The **privacy pipeline** (`PrivacyPipeline`) turns it into a packet, always in this order:

1. **Denylist.** If the captured window matches a denylist rule, the capture is refused before any pixels or text are kept. Otherwise every denylisted window inside the region is filled with opaque black. For a region, the captured window is the topmost window at its centre.
2. **Metadata filter.** Source fields that are switched off are dropped. From here on they do not exist: they never appear in a manifest, the audit log or a message.
3. **Text redaction.** Secret-shaped text in the captured text and the note is replaced with `[REDACTED:<pattern>]`.
4. **Packet.** The packet is built: PNG image, text files, manifest with hashes, the enabled source fields and a record of every redaction.
5. **Encryption** (optional, off by default). Every payload file is encrypted.
6. **Outbox.** The packet is queued for delivery to the local inbox.

The pipeline works on a copy of the pixels; it never changes the caller's buffer.

## Where everything is stored

| Place | Contents |
| --- | --- |
| Outbox folder: `outbox.json` | State of each packet (id, destination, state, sizes, hashes, times). No content, no source details. |
| Outbox folder: `packets/<id>/` | A copy of each packet until it is delivered or discarded. Contains the payload. |
| Outbox folder: `audit.jsonl` | The audit log, unless the outbox is given another log. |
| Inbox folder: `<id>/` | Delivered packets: `manifest.json`, payload files, and `ack.json` once a consumer acknowledged it. |
| Inbox folder: `.audit.jsonl` | The inbox's audit log (removals by retention and `ccomp purge`). The app can share one log between outbox and inbox. |
| Inbox folder: `.tmp/` | Staging for atomic writes and removals. Never a packet. |
| Settings file (JSON) | The privacy settings below and the capture settings ([docs/capture.md](docs/capture.md#settings)). Read with `PrivacySettings.LoadOrDefault`; without a file the defaults apply. |
| `ccomp capture` outbox: `%LOCALAPPDATA%\context-companion\outbox` | The outbox `ccomp capture` uses unless given `--outbox`. Its audit entries go to the inbox's `.audit.jsonl`. |

Everything stays on the local disk. There is no network code and no telemetry.

## Settings

The settings are a JSON file with snake_case names. Unknown properties are rejected, `schema_version` is required, and a property left out keeps its default. A complete file with every default:

```json
{
  "schema_version": 1,
  "metadata": {
    "process_name": true,
    "window_class": false,
    "window_title": false,
    "executable_path": false,
    "capture_mode": true,
    "region": true,
    "scale": true
  },
  "denylist": [ "… the default list below …" ],
  "redaction": { "secrets": true, "disabled_patterns": [], "extra_patterns": [] },
  "retention": { "max_packets": 200, "max_bytes": 536870912, "max_age": "7d" },
  "encryption": { "enabled": false, "protection": "dpapi-current-user" },
  "capture": { "max_long_edge": 1568, "include_cursor": false, "explicit_copy": true, "copy_timeout_ms": 500, "extra_terminal_window_classes": [] }
}
```

| Setting | Default | What it does |
| --- | --- | --- |
| `metadata.process_name` | on | Writes the executable name, for example `notepad.exe`. |
| `metadata.window_class` | off | Writes the window class name. |
| `metadata.window_title` | off | Writes the window title. Titles often name documents, people and conversations. |
| `metadata.executable_path` | off | Writes the full executable path. Paths often contain your user name. |
| `metadata.capture_mode` | on | Writes how it was captured: `window-wgc`, `window-gdi`, `region-wgc`, `region-gdi`, `text-selection`, `text-clipboard` or `text-copy`. |
| `metadata.region` | on | Writes the captured rectangle in screen pixels. |
| `metadata.scale` | on | Writes the display scale factor. |
| `denylist` | default list | Rules for windows that are never captured. Leaving it out keeps the default list; `[]` switches it off. |
| `redaction.secrets` | on | Redacts secret-shaped text in captured text and notes. |
| `redaction.disabled_patterns` | none | Names of built-in patterns to skip. |
| `redaction.extra_patterns` | none | Your own patterns, run after the built-in ones. |
| `retention.max_packets` | 200 | Most packets an inbox keeps. `null` for no limit. |
| `retention.max_bytes` | 536870912 (512 MiB) | Most bytes an inbox keeps. `null` for no limit. |
| `retention.max_age` | `7d` | Oldest packet an inbox keeps: a number followed by `s`, `m`, `h`, `d` or `w`. `null` for no limit. |
| `encryption.enabled` | off | Stores payload files encrypted. See [Encryption at rest](#encryption-at-rest). |
| `encryption.protection` | `dpapi-current-user` | Which protector to use. |
| `capture.*` | see [docs/capture.md](docs/capture.md#settings) | Image size (default long edge 1568 px), the mouse pointer (off), the explicit copy for text (on) and its timeout (500 ms), extra terminal window classes. |

A field that is switched off is removed before anything else reads the metadata, which the tests check against the manifest, the outbox, the audit log and error messages. The denylist still sees the window title for matching even when titles are not recorded; it never writes it anywhere.

## Denylist and blackout

A rule matches a window when **every** condition it sets matches; it must set at least one:

- `process_name`: the executable name, ignoring case, folders and a trailing `.exe` (`KeePass.exe`, `keepass` and `C:\…\KeePass.exe` are the same).
- `window_class`: the window class, ignoring case.
- `title_pattern`: a .NET regular expression searched for in the title, ignoring case, with a 100 ms timeout. A pattern that times out counts as a match, so the window is treated as denylisted.

`name` (1–64 characters from `A–Z a–z 0–9 . _ -`) is what the audit log and the manifest record; `description` is for you and is never written anywhere.

- **The captured window matches:** the capture is refused. No pixels or text are kept, no packet is made, the audit log records `blocked` with the rule's name and nothing else, and the caller gets `CaptureStatus.Blocked` with the rule name.
- **A matching window lies inside the captured region:** its rectangle, clipped to the region, is filled with opaque black in the pixels before they are encoded. Edges are rounded outwards when the image is scaled. The manifest records `{"kind": "blackout", "rule": "<name>", "count": <windows>, "item": "capture.png"}` for each rule.

Default list (`src/ContextCompanion.Core/Privacy/Defaults/denylist.json`):

| Rule | Process | Application |
| --- | --- | --- |
| `keepass` | `KeePass.exe` | KeePass Password Safe |
| `keepassxc` | `KeePassXC.exe` | KeePassXC |
| `1password` | `1Password.exe` | 1Password desktop app |
| `bitwarden` | `Bitwarden.exe` | Bitwarden desktop app |
| `enpass` | `Enpass.exe` | Enpass |
| `nordpass` | `NordPass.exe` | NordPass |
| `windows-credential-prompt` | `CredentialUIBroker.exe` | Windows credential prompt (Windows Security sign-in dialog) |
| `windows-uac-prompt` | `consent.exe` | User Account Control prompt |

To change it, write the whole list into your settings file. For example, to keep the defaults you want and add a browser tab of a web vault:

```json
"denylist": [
  { "name": "keepassxc", "process_name": "KeePassXC.exe" },
  { "name": "windows-credential-prompt", "process_name": "CredentialUIBroker.exe" },
  { "name": "vault-tab", "process_name": "msedge.exe", "title_pattern": "\\bvault\\b", "description": "Web vault in Edge" }
]
```

Executable names differ between versions and editions; check yours in Task Manager (Details tab).

## Secret redaction

Each built-in pattern replaces what it matches with `[REDACTED:<pattern>]`. Where a pattern has a group named `secret`, only that group is replaced, so `DB_PASSWORD=…` becomes `DB_PASSWORD=[REDACTED:env-assignment]`. The manifest records `{"kind": "secret", "pattern": "<name>", "count": <n>, "item": "<file>"}` per pattern and file, never the removed text.

Matches of all patterns are found in the original text. Overlapping matches become one replacement, named after the match that starts first (for equal starts the longer one, then the earlier pattern in the list), so nothing of either survives. Text already inside a `[REDACTED:…]` marker is left alone, so redacting twice changes nothing more.

Built-in patterns (`src/ContextCompanion.Core/Privacy/Defaults/secret-patterns.json`, a versioned JSON list compiled with a 250 ms timeout per pattern):

| Pattern | Matches |
| --- | --- |
| `private-key` | PEM, OpenSSH and PGP private key blocks; an unterminated block is redacted to the end of the text. |
| `jwt` | JSON Web Tokens (three base64url parts, the first two starting with `eyJ`). |
| `aws-access-key` | AWS access key ids. |
| `github-token` | GitHub personal access, OAuth, app and refresh tokens, classic and fine-grained. |
| `gitlab-token` | GitLab personal access tokens. |
| `slack-token` | Slack bot, user, app and refresh tokens. |
| `stripe-key` | Stripe secret and restricted keys. |
| `google-api-key` | Google API keys, including Gemini API keys. |
| `anthropic-api-key` | Anthropic API keys. |
| `openai-api-key` | OpenAI API keys (`sk-`, `sk-proj-`, `sk-svcacct-`, `sk-admin-`) containing at least one digit. |
| `huggingface-token` | Hugging Face access tokens. |
| `connection-string` | Passwords and account keys in connection strings (`Password=`, `Pwd=`, `AccountKey=`, `SharedAccessKey=` after a `;`) and in URLs (`scheme://user:password@host`). |
| `env-assignment` | Upper-case environment-style assignments at the start of a line, such as `DB_PASSWORD=…`, `API_KEY=…` or `export GITHUB_TOKEN=…`. |
| `secret-assignment` | Assignments with `=` or `:` to names ending in `password`, `passwd`, `passphrase`, `secret`, `token`, `api_key`, `access_key`, `private_key` or `secret_key`. Quoted values of 4 or more characters are redacted; unquoted values only when they have 6 or more characters including a digit or symbol, so code such as `password = getPassword()` or `self.password = password` is left alone. |

To add patterns, list them in the settings; to replace a built-in one, disable it and add your own under the same name:

```json
"redaction": {
  "disabled_patterns": ["openai-api-key"],
  "extra_patterns": [
    { "name": "ticket-id", "regex": "\\bSEC-[0-9]{4,}\\b", "description": "Internal security tickets" }
  ]
}
```

A pattern name is a lower-case token (`a–z`, `0–9`, `-`, up to 32 characters); the regular expression must compile and must not match empty text. If a pattern runs past its timeout, the text cannot be shown to be clean, so that text item is **left out** of the packet and the manifest records `{"kind": "withheld", "pattern": "<name>"}`; if nothing else remains, nothing is queued (`CaptureStatus.Withheld`).

## Text and the clipboard

Text comes from the focused element's selection (UI Automation) first. The clipboard's current text is read **only** when you ask for it, and not at all when the application that put it there marked it as sensitive, as password managers do. As a last resort the tool sends Ctrl+C, reads the copy and restores your clipboard; it never does this in consoles and terminals, where Ctrl+C would interrupt the running program. Copied content marked sensitive is discarded.

Whatever the tool writes to the clipboard (today only the restore) is marked `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory` = 0 and `CanUploadToCloudClipboard` = 0, so it stays out of Windows clipboard history, the cloud clipboard and clipboard managers that honour these marks. The restore keeps text, images, files and registered formats; other formats can be lost. Details: [docs/capture.md](docs/capture.md#text).

## Audit log

The audit log is JSON Lines, appended and flushed per entry, never rewritten. Each entry may hold:

| Field | Meaning |
| --- | --- |
| `time` | When, UTC. |
| `packet`, `destination` | Packet id and destination name; absent for a refused capture. |
| `from`, `to` | State change: `queued`, `delivering`, `delivered`, `consumed`, `uncertain`, `failed`, or `discarded`, `blocked`, `removed`. |
| `content_sha256`, `item_sha256`, `bytes` | Hashes and size of a packet's content, for delivery state changes. |
| `consumer` | The restricted consumer name from `ack.json`, for `consumed`. |
| `rule` | The denylist rule's name, for `blocked`. |
| `reason` | A fixed token for `removed`: `retention-max-age`, `retention-max-packets`, `retention-max-bytes`, `purge-all`, `purge-older-than`, `purge-id`. |

It **never** records payload content, text, notes, window titles, process names, executable paths, window classes, regions, redaction reasons, removed secrets or acknowledgement notes. `ccomp audit` shows exactly what the log holds:

```
$ ccomp audit inbox --packet 01M6HXXNGZJXM5SEVYEM9KBPK5
TIME                      PACKET                      DESTINATION  CHANGE                   REASON
2026-11-04T00:04:12.512Z  01M6HXXNGZJXM5SEVYEM9KBPK5  inbox        - -> queued
2026-11-04T00:04:12.532Z  01M6HXXNGZJXM5SEVYEM9KBPK5  inbox        queued -> delivering
2026-11-04T00:04:12.545Z  01M6HXXNGZJXM5SEVYEM9KBPK5  inbox        delivering -> delivered
2026-11-04T00:04:25.125Z  01M6HXXNGZJXM5SEVYEM9KBPK5  -            - -> removed             purge-id
4 entries.
```

The audit log itself is kept until you delete it; retention does not shorten it.

## Encryption at rest

When `encryption.enabled` is on, every payload file of a new packet is encrypted before it enters the outbox, and the packet is delivered to the inbox in that form. With the default protector, `dpapi-current-user`, each file is stored as `<file>.dpapi` using Windows DPAPI for the current user (`System.Security.Cryptography.ProtectedData`, no additional entropy). The manifest keeps each item's plaintext `bytes` and `sha256` and adds a `stored` object (`file`, `bytes`, `sha256` of the encrypted file, `protection`); see [docs/packet-format.md](docs/packet-format.md#encrypted-items). `ccomp validate` checks the encrypted files everywhere, and the plaintext too where it can decrypt (the Windows build, on Windows, as the same user).

If encryption is enabled but the protector is not available (for example outside Windows), the pipeline refuses to start rather than store plaintext.

**Default: off.** The inbox is the hand-off point that coding agents and other tools read by path. Encrypted packets cannot be read that way; a consumer must decrypt first. Turn it on if you would rather consumers take that extra step than leave plaintext captures on disk.

How consumers decrypt:

- **`ccomp open <packet> --to <dir>`** writes a plaintext copy to `<dir>/<id>/` (a normal, valid packet) after verifying every plaintext size and SHA-256. It needs the Windows build of `ccomp` (`net10.0-windows`), on Windows, run by the user account that captured the packet. Elsewhere it refuses with a message and writes nothing. Plaintext packets can be opened anywhere.
- **Programs** running as that user can call `ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser)` on each `.dpapi` file and compare the result with the item's `sha256`.

## Retention and purge

Each inbox keeps at most `max_packets` packets and `max_bytes` bytes, none older than `max_age` (defaults: 200, 512 MiB, 7 days). The limits are applied after each delivery. Packets that a consumer acknowledged (`consumed`) go first, then the oldest. A packet the outbox still has as `queued` or `delivering` is never removed.

`ccomp purge` removes packets on request. It is a dry run unless you add `--yes`:

```
$ ccomp purge inbox --older-than 1s
Would remove 01M6HXXNGZJXM5SEVYEM9KBPK5: purge-older-than, 1457 bytes
Would remove 01M6HXXNK7PAJ68K5J0V7TDA3J: purge-older-than, 584 bytes
2 packets, 2041 bytes would be removed. Run again with --yes to delete.

$ ccomp purge inbox --id 01M6HXXNGZJXM5SEVYEM9KBPK5 --yes
Removed 01M6HXXNGZJXM5SEVYEM9KBPK5: purge-id, 1457 bytes
1 packet removed, 1457 bytes. Deleted files may still be recoverable from the disk or from backups; see PRIVACY.md.
```

`--all` removes every packet, `--older-than <duration>` those older than the duration, `--id <id>` one packet; without a selector the retention limits apply (from `--settings <file>` or the defaults). `--outbox <dir>` protects packets the outbox still has as queued or delivering. Every removal is written to the audit log as the packet id and a reason (by default the inbox's `.audit.jsonl`, or `--audit <log>`). A packet folder is first moved into `.tmp/` in one rename, so a consumer never sees half a packet, then deleted.

**Deletion removes files; it cannot guarantee that the bytes are unrecoverable.** SSDs remap and keep old blocks, file systems keep journals and snapshots, and backups, sync clients and shadow copies keep their own copies. Encryption at rest reduces what such leftovers reveal.

## Honest limits

- **DPAPI current-user** protects against other user accounts and against someone reading the disk offline. It does **not** protect against software running as the same user, which can decrypt just as `ccomp open` does.
- **The denylist sees only windows the operating system reports**, with the names it reports. A password shown inside another application (a browser tab, a terminal, a remote desktop window, a video) is not recognised unless a rule matches that window, and a renamed executable escapes a process rule.
- **Redaction is pattern-based and can miss secrets**: unusual formats, secrets split across lines, passwords in prose, secrets in images. It can also redact harmless text that looks like a secret. Read what you send.
- **Deletion is not secure erasure** (see above).
- **Screen sharing is not handled.** context-companion does not detect or change what screen-sharing, recording or meeting software captures, and it never hides its own windows from them.
- Some applications block capture, or come out black; the capture step reports a refusal or delivers what Windows gives it, and never works around the protection.
- **The explicit copy changes the clipboard for a moment** and sends a real Ctrl+C to the focused application. Formats it cannot restore are lost (see [docs/capture.md](docs/capture.md#what-the-explicit-copy-does-to-the-clipboard)), and an application that uses Ctrl+C for something other than copy does that instead. Switch it off with `capture.explicit_copy`.
- The GDI fallback (used when Windows.Graphics.Capture is unavailable, and for regions across monitors) shows no capture border, because Windows offers none for it.
- Packets are not signed; anyone who can write to the inbox can write a packet.
