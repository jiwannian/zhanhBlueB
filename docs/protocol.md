# BlueBridge protocol v1

Bluetooth RFCOMM service UUID: `dc0b3714-a8e6-4c25-a219-775346d2ef91`.
Windows is the server; Android is the client. Use secure, bonded Bluetooth.
No TCP, HTTP, cloud or runtime Internet transport is part of the application.

## Frames

All integers are unsigned big-endian, unless a JSON number is used. A header
has 28 bytes: ASCII `BBRG` (4), version `1` (1), type (1), reserved zero (2),
message UUID (16 RFC 4122 network-order bytes), payload length (4), then payload.
Maximum payload is 1,048,576 bytes. Read exactly; clean EOF before a header
means disconnected, EOF inside a header/payload is a protocol error. Unknown
type, wrong magic/version/reserved, and oversized lengths are errors. Serialize
all frame writes. Never allocate memory from an unvalidated length.

| Type | Name | Payload |
| --- | --- | --- |
| 1 | HELLO | UTF-8 JSON `{ "deviceId": "persistent UUID", "deviceName": "name" }` |
| 2 | TRUST | UTF-8 JSON `{ "accepted": true }` or false |
| 3 | TEXT | Exact UTF-8 text; maximum 1 MiB |
| 4 | BEGIN | UTF-8 JSON `{ "name": "basename", "length": 123, "kind": "file" or "image", "mime": "application/octet-stream" or "image/png" }` |
| 5 | CHUNK | Raw bytes; maximum 65,536 bytes |
| 6 | END | UTF-8 JSON `{ "sha256": "64 lowercase hexadecimal characters" }` |
| 7 | ACK | UTF-8 JSON `{ "stage": "begin" or "complete" }` |
| 8 | CANCEL | UTF-8 reason, may be empty |
| 9 | ERROR | UTF-8 user-readable reason |
| 10 | PING | Empty |
| 11 | PONG | Empty, echoes PING message UUID |

Control JSON limit is 16 KiB. Name must be a non-empty basename, at most 240
characters: reject `.`/`..`, separators, absolute paths, control characters and
Windows invalid filename characters/reserved device names. Reject invalid
lengths (non-integer, negative, or above 1 TiB). Image transfers are PNG only,
at most 32 MiB, at most 32 megapixels; reject invalid PNG dimensions before
decoding. Each image also has a user-facing save action.

## Trust and lifecycle

Both peers send HELLO immediately. Each displays the peer name and bonded
Bluetooth address for first-time confirmation. Persist trust bound to both the
peer device UUID and Bluetooth address. Each sends TRUST only once locally
approved (automatically for a previously trusted pair). Allow TEXT/BEGIN/CHUNK/
END only after **both** local and remote trust are true. Rejection disconnects.
Do not accept a second active session. Before trust allow only HELLO, TRUST,
PING/PONG, CANCEL/ERROR. HELLO deviceId must be a UUID; refuse repeated HELLO.
Reject unsolicited TRUST before HELLO. Do not trust a displayed name alone.

Store immutable frame data. Keep a bounded set of received TEXT message UUIDs
and locally-applied clipboard fingerprints so received clipboard writes are
not re-sent by Windows. Fingerprint suppression applies only to the next
matching local clipboard event; subsequent deliberate copies remain sendable.
No clipboard content is written to logs or persistent history.

## Transfers

BEGIN/CHUNK/END/ACK/CANCEL/ERROR for a transfer all use the same message UUID.
Each direction sends only one file at a time; both directions can operate at
once. Clipboard text and controls can be interleaved between file chunks.
Sender waits for ACK begin before chunks and ACK complete after END, at most
30 seconds each. ACK for an unexpected UUID/stage must not complete a send.

Receiver validates BEGIN, opens a random app-owned temporary file, ACKs begin,
streams chunks while checking declared length and SHA-256, and validates
END length and hash. It then atomically commits without overwriting any file,
or applies a validated PNG image to the clipboard, and ACKs complete. Empty
files are supported. Preserve file bytes exactly; do not auto-open received
files. On failure send ERROR, remove the partial and leave existing files
intact. CANCEL from either side terminates that transfer and removes partials.
Duplicate IDs, out-of-order chunks/end, multiple BEGIN or oversized chunks
are rejected without corrupting an active transfer.

Reconnect a requested Android session with bounded exponential backoff.
On disconnect fail outstanding sends and remove partials; never auto-replay
old clipboard content. Failed files can be explicitly retried from the start.
User Stop, force stop and desktop Exit must stop reconnect/listening.

Golden codec fixtures live in `protocol/golden.json` and must be checked by
both language implementations. Android package: `com.zhanh.bluebridge`.
