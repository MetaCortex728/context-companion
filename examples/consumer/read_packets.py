#!/usr/bin/env python3
"""Reference consumer for context-companion inboxes (Python standard library only).

Lists the packets in an inbox that no consumer has acknowledged yet, checks every item's size and SHA-256 against
the manifest, prints where each file is (and the text of text items), and with --ack writes ack.json into each packet
that checked out, recording that this consumer took it. An acknowledgement means taken, not read or answered.

    python read_packets.py <inbox> [--all] [--ack NAME] [--json]

Exit code 0 when every listed packet checked out, 1 when any did not, 2 for a usage error.
The packet format is described in docs/packet-format.md; this script is the smallest complete consumer of it.
"""
import argparse
import datetime
import hashlib
import json
import os
import re
import sys
import uuid

ULID = re.compile(r"^[0-7][0-9A-HJKMNP-TV-Z]{25}$")
CONSUMER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
TEXT_KINDS = {"text", "note"}
MAX_TEXT_CHARS = 20000


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def check(packet_dir):
    """Returns (manifest, problems). Problems name files and fields, never content."""
    problems = []
    try:
        with open(os.path.join(packet_dir, "manifest.json"), "rb") as stream:
            manifest = json.load(stream)
    except (OSError, ValueError) as error:
        return None, [f"manifest.json cannot be read: {error.__class__.__name__}"]
    if manifest.get("format") != "context-companion.packet" or manifest.get("schema_version") != 1:
        problems.append("not a context-companion packet, format v1")
    if manifest.get("id") != os.path.basename(packet_dir):
        problems.append("the manifest id does not match the folder name")
    for item in manifest.get("items", []):
        stored = item.get("stored")
        name = (stored or item).get("file", "")
        path = os.path.join(packet_dir, name)
        if os.path.basename(name) != name or not os.path.isfile(path):
            problems.append(f"{name}: missing")
            continue
        expected = stored or item
        if os.path.getsize(path) != expected.get("bytes"):
            problems.append(f"{name}: size differs from the manifest")
        elif sha256(path) != expected.get("sha256"):
            problems.append(f"{name}: SHA-256 differs from the manifest")
    return manifest, problems


def acknowledge(inbox, packet_dir, packet_id, consumer):
    """Writes ack.json atomically and never replaces an existing one."""
    now = datetime.datetime.now(datetime.timezone.utc)
    ack = {
        "format": "context-companion.ack",
        "schema_version": 1,
        "id": packet_id,
        "consumer": consumer,
        "consumed": now.strftime("%Y-%m-%dT%H:%M:%S.") + f"{now.microsecond // 1000:03d}Z",
    }
    staging = os.path.join(inbox, ".tmp")
    os.makedirs(staging, exist_ok=True)
    temp = os.path.join(staging, f"ack-{packet_id}-{uuid.uuid4().hex}.json")
    target = os.path.join(packet_dir, "ack.json")
    with open(temp, "x", encoding="utf-8") as stream:
        json.dump(ack, stream, indent=2)
        stream.flush()
        os.fsync(stream.fileno())
    try:
        os.link(temp, target)  # fails if ack.json already exists, unlike a rename on POSIX
    except FileExistsError:
        return None
    finally:
        os.remove(temp)
    return ack


def main(argv):
    parser = argparse.ArgumentParser(description="Read and acknowledge context-companion packets.")
    parser.add_argument("inbox")
    parser.add_argument("--all", action="store_true", help="also list packets that are already acknowledged")
    parser.add_argument("--ack", metavar="NAME", help="acknowledge each packet that checks out, as consumer NAME")
    parser.add_argument("--json", action="store_true", help="print one JSON object per packet instead of text")
    args = parser.parse_args(argv)
    if args.ack is not None and not CONSUMER.match(args.ack):
        parser.error("--ack takes 1 to 64 characters from A-Z, a-z, 0-9, '.', '_' and '-'")
    inbox = os.path.abspath(args.inbox)
    if not os.path.isdir(inbox):
        parser.error(f"{inbox} is not a folder")

    failed = False
    for name in sorted(os.listdir(inbox)):  # ULIDs sort by creation time
        packet_dir = os.path.join(inbox, name)
        if name.startswith(".") or not ULID.match(name) or not os.path.isdir(packet_dir):
            continue
        acknowledged = os.path.exists(os.path.join(packet_dir, "ack.json"))
        if acknowledged and not args.all:
            continue
        manifest, problems = check(packet_dir)
        failed |= bool(problems)
        items = []
        for item in (manifest or {}).get("items", []):
            entry = {"kind": item.get("kind"), "path": os.path.join(packet_dir, (item.get("stored") or item).get("file", ""))}
            if item.get("stored"):
                entry["encrypted"] = True  # open with: ccomp open <packet> --to <dir>
            elif item.get("kind") in TEXT_KINDS and not problems:
                with open(entry["path"], encoding="utf-8") as stream:
                    entry["text"] = stream.read(MAX_TEXT_CHARS)
            items.append(entry)
        ack = acknowledge(inbox, packet_dir, name, args.ack) if args.ack and not problems and not acknowledged else None
        result = {
            "id": name,
            "created": (manifest or {}).get("created"),
            "mode": ((manifest or {}).get("source") or {}).get("capture_mode"),
            "items": items,
            "redactions": len((manifest or {}).get("redactions", [])),
            "problems": problems,
            "acknowledged": acknowledged or ack is not None,
        }
        if args.json:
            print(json.dumps(result, ensure_ascii=False))
            continue
        print(f"{'INVALID' if problems else 'OK'} {name}  {result['created']}  {result['mode']}  redactions: {result['redactions']}")
        for problem in problems:
            print(f"  - {problem}")
        for item in items:
            print(f"  {item['kind']}: {item['path']}" + ("  (encrypted)" if item.get("encrypted") else ""))
            if "text" in item:
                for line in item["text"].splitlines():
                    print(f"    | {line}")
        if ack:
            print(f"  acknowledged as {args.ack}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
