# Roadmap

## Privacy principles

Privacy here means that what you capture, and the people who appear in it, go nowhere you did not intend. Integrity means the tool never misleads anyone about what it does. Each principle below becomes tested behaviour, not a promise.

- **User-initiated only.** Nothing is captured without a trigger: no timers, no background recording, no system or meeting audio.
- **Local first.** The default and, in Stage 1, only destination is a local inbox folder. Network destinations come later, ship disabled and show exactly what will leave the machine before the first send.
- **Minimisation.** Crop before delivery. Windows on a denylist (for example password managers and credential prompts) are blacked out inside a region. Secret-like text is redacted. Source details (process, window title, path) can be switched off one by one. No telemetry.
- **Control over what is kept.** Retention limits, a one-command purge, per-item encryption at rest with Windows DPAPI.
- **Accountability.** A local audit log records what was delivered, where and when, and receipts never claim more than is known: delivered is not read, read is not answered.
- **Honesty about limits.** The documentation says what the tool cannot guarantee, for example that some applications block capture.
- **No concealment.** The tool does not hide itself or its output from other people. The only window excluded from capture is the transient selection rectangle, so that it does not appear in the image being taken.

## Stage 1: capture to inbox

1. **Project layout:** `ContextCompanion.Core` (platform-neutral, tested on Linux and Windows), `ContextCompanion.App` (WPF tray app), tests, CI (about 2 hours).
2. **Packet format v1:** specification and JSON Schema (ULID identifier, creation time, items, per-item SHA-256, source details, redaction record, schema version); atomic inbox writer (write to a temporary folder, then rename); a validator command; consumer acknowledgements in `ack.json` (about 4 hours).
3. **Outbox and receipts:** the prototype's bounded outbox, duplicate suppression and attempt states (queued, delivered, consumed, uncertain, failed) moved onto packets, with crash recovery and an audit log (about 3 hours).
4. **Privacy core:** denylist and blackout, secret redaction, retention and purge, DPAPI encryption at rest with a documented decrypt path for consumers, `PRIVACY.md` (about 6 hours).
5. **Capture:** Windows.Graphics.Capture, one frame per trigger for a window or region, per-monitor DPI awareness, crop, resize to a configured size, PNG; fallback for windows the API cannot capture; minimised windows detected (about 4 hours).
6. **Text:** the selection through UI Automation, then the clipboard by format, then an explicit copy that restores the clipboard; anything the tool puts on the clipboard stays out of clipboard history (about 3 hours).
7. **Triggers and shell:** global hotkeys as the primary trigger, the horizontal-wheel gesture on its own thread with a watchdog, tray controls, a compose-and-preview window, a view of delivery receipts (about 4 hours).
8. **Inbox delivery:** destination interface, the inbox destination, a small reference consumer that reads a packet and acknowledges it, and instructions for coding agents to read packets by path (about 2 hours).
9. **Evidence:** measured idle CPU and memory and capture-to-inbox latency, published in the README (about 2 hours).

## Stage 2: more inputs and destinations

10. **MCP server:** a separate stdio server exposing inbox packets as resources, with list, get and acknowledge tools, and a one-click bundle; verified with real clients (about 5 hours).
11. **Audio clips:** push-to-hold recording, 16 kHz mono WAV, capped length, raw clip kept in the packet; the microphone indicator stays visible (about 2 hours).
12. **Transcription plug-ins:** a local Whisper-class engine and a bring-your-own-key cloud engine; the raw clip is always kept beside the transcript; generated Korean, English and mixed test clips (about 4 hours).
13. **AI destinations:** an assistant API destination using the user's own key, disabled by default, previewing what leaves the machine, with the provider's request ID as the receipt (about 2 hours).
14. **Window text and OCR:** the text an application exposes beyond the visible pixels, and OCR as a fallback, both passed through redaction (about 3 hours).
15. **Reliability:** sleep and wake, display and DPI changes, recovery after Windows removes the mouse hook, duplicate launches, a 24-hour run with bounded memory (about 3 hours).

## Later

- Signed releases and MSIX packaging.
- Further destinations on equal footing.
- Signed packets that can be attached to bug reports and shared between people.
- A browser extension that adds the page address and selection to a packet.
- Per-destination privacy profiles.
