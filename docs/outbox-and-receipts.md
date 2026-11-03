# Outbox, receipts and audit log

`ContextCompanion.Core.Delivery` holds packets until they are delivered, tracks what is known about each delivery and records every change in an audit log. It is platform-neutral and has no network code.

## Outbox

The outbox is a folder:

```
outbox/
  outbox.json          state of every packet, replaced atomically on each change
  audit.jsonl          append-only audit log
  packets/<id>/        stored copy of each packet not yet delivered (same layout as an inbox packet)
  packets/.tmp/        staging
```

- **Bounded.** At most 4 pending packets (queued, delivering or uncertain) and 64 MiB of stored packets (every packet not yet delivered or discarded, including failed ones). Both are set with `OutboxOptions`. When a limit would be exceeded, `Enqueue` throws `OutboxFullException` and changes nothing.
- **Exact consecutive duplicate suppression, per destination.** A packet whose content hash equals that of the previous packet for the same destination is dropped, unless that previous packet failed. The content hash covers each item's kind, media type and SHA-256 in order; it ignores the id, time, file names and source details, so recapturing the same pixels or text counts as the same content. Only consecutive duplicates are dropped: A, B, A delivers all three.
- **Atomic and crash-safe.** The stored copy is written first with the atomic packet writer; `outbox.json` is then replaced through a temporary file and a rename, and that replacement is the commit point. If the state cannot be written, nothing changes in memory or on disk. On start the outbox removes stored copies that were never committed and staged leftovers. A corrupt `outbox.json` stops the outbox with an error and leaves every stored packet in place.

## States

| State | Meaning |
| --- | --- |
| `queued` | Stored, waiting for delivery. |
| `delivering` | An attempt is in progress. |
| `delivered` | The destination reported it delivered. For the inbox: the packet is visible and validates. **Not** proof that anyone read it. |
| `consumed` | A consumer wrote a valid `ack.json` for it. **Not** proof that it was read or answered. |
| `uncertain` | It may or may not have arrived. Never re-sent automatically. |
| `failed` | Definitely not delivered. |

Allowed transitions; everything else is refused:

```
queued      → delivering | failed (destination not configured)
delivering  → delivered | failed | uncertain | queued (destination unavailable, definitely unsent)
delivered   → consumed   (only from ack.json)
uncertain   → consumed   (only from ack.json) | queued (explicit retry by the user)
failed      → queued     (explicit retry by the user)
```

- **Restart.** Anything left `delivering` becomes `uncertain`, because the attempt may have completed. It is never retried automatically; the user may retry it explicitly, accepting that it might then arrive twice (the inbox destination recognises a packet that is already there and reports it delivered).
- **Consumed** is reached only by reading a valid `ack.json` through the destination, for the same packet id, with a plausible time.
- **Discard** removes a failed or uncertain packet and its stored copy; the audit log keeps its history.

A destination implements `IDestination`: it delivers a stored packet and reports `Delivered`, `NotDelivered`, `Unavailable` or `Uncertain`, and reads acknowledgements. Any exception or cancellation during an attempt is recorded as `uncertain`. `InboxDestination` writes the packet into a local inbox with the atomic writer and reports `Delivered` only once the packet is visible and valid. `DeliveryQueue` delivers queued packets one at a time, in order, with pause and resume, and retries an unavailable destination after a delay (3 seconds by default).

## Audit log

`audit.jsonl` has one JSON object per line, one line per state change, flushed to disk as it is written and never rewritten:

```json
{"time":"2026-11-03T15:04:05.133Z","packet":"01JBQ7Y2H5V0X8Z3T6M4N9P1RS","destination":"inbox","from":"delivered","to":"consumed","content_sha256":"…","item_sha256":["…","…"],"bytes":48231,"consumer":"example-agent"}
```

| Field | Meaning |
| --- | --- |
| `time` | When the change happened, UTC. |
| `packet` | Packet id. |
| `destination` | Destination name. |
| `from` | Previous state; absent when the packet entered the outbox. |
| `to` | New state, or `discarded`. |
| `content_sha256`, `item_sha256`, `bytes` | The packet's content hash, item hashes and total size. |
| `consumer` | For `consumed` only: the consumer name from `ack.json`, a restricted identifier. |

The audit log never contains payload content, window titles, process names, regions, redaction reasons, acknowledgement notes or any other text from a packet. Neither does `outbox.json`. The stored copies in `packets/` do contain the payload until delivery.
