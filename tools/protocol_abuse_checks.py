"""Exercise the compiled protocol peers with invalid/reordered inputs over pipes."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import struct
import subprocess
import threading
import uuid

ROOT = Path(__file__).resolve().parents[1]


def frame(kind: int, identifier: uuid.UUID, payload: bytes = b"") -> bytes:
    return b"BBRG" + bytes([1, kind]) + b"\0\0" + identifier.bytes + struct.pack(">I", len(payload)) + payload


def control(value: dict) -> bytes:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


class Peer:
    def __init__(self, prefix: list[str], folder: Path, expected: int):
        folder.mkdir(parents=True)
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        self.process = subprocess.Popen(prefix + ["--receive-dir", str(folder), "--expect", str(expected)],
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                        bufsize=0, creationflags=flags)
        self.folder = folder
        self.frames: queue.Queue = queue.Queue()
        self.errors = bytearray()
        threading.Thread(target=self.read_frames, daemon=True).start()
        threading.Thread(target=self.read_errors, daemon=True).start()

    def exact(self, length: int) -> bytes:
        data = bytearray()
        while len(data) < length:
            chunk = os.read(self.process.stdout.fileno(), length - len(data))
            if not chunk:
                raise EOFError("peer stdout closed")
            data.extend(chunk)
        return bytes(data)

    def read_frames(self):
        try:
            while True:
                header = self.exact(28)
                assert header[:5] == b"BBRG\x01", "Peer sent a malformed header"
                size = struct.unpack(">I", header[24:28])[0]
                assert size <= 1048576, "Peer sent an oversized payload"
                self.frames.put((header[5], uuid.UUID(bytes=header[8:24]), self.exact(size)))
        except Exception as error:
            self.frames.put(error)

    def read_errors(self):
        while True:
            data = os.read(self.process.stderr.fileno(), 4096)
            if not data:
                return
            if len(self.errors) < 65536:
                self.errors.extend(data)

    def send(self, kind: int, identifier: uuid.UUID, payload: bytes = b""):
        data = memoryview(frame(kind, identifier, payload))
        while data:
            count = self.process.stdin.write(data)
            if not count:
                raise OSError("pipe write failed")
            data = data[count:]

    def expect(self, kind: int, identifier: uuid.UUID | None = None, stage: str | None = None):
        incoming = self.frames.get(timeout=15)
        if isinstance(incoming, Exception):
            raise incoming
        actual_kind, actual_id, payload = incoming
        assert actual_kind == kind, f"Expected kind {kind}, got {actual_kind}"
        if identifier:
            assert actual_id == identifier, "Response routed to the wrong transfer"
        if stage:
            assert json.loads(payload)["stage"] == stage, "Incorrect ACK stage"
        return payload

    def handshake(self, approve=True):
        self.expect(1)
        self.send(1, uuid.uuid4(), control(dict(deviceId=str(uuid.uuid4()), deviceName="Reference fixture peer")))
        self.expect(2)
        if approve:
            self.send(2, uuid.uuid4(), control(dict(accepted=True)))

    def begin(self, name: str, length=0, identifier=None):
        identifier = identifier or uuid.uuid4()
        self.send(4, identifier, control(dict(name=name, length=length, kind="file", mime="application/octet-stream")))
        return identifier

    def finish_empty(self, name: str):
        identifier = self.begin(name)
        self.expect(7, identifier, "begin")
        self.send(6, identifier, control(dict(sha256=hashlib.sha256(b"").hexdigest())))
        self.expect(7, identifier, "complete")
        return identifier

    def exit(self, successful=True):
        try:
            self.process.stdin.close()
        except OSError:
            if successful:
                raise
        code = self.process.wait(timeout=20)
        assert (code == 0) == successful, f"Unexpected exit {code}: {self.errors.decode(errors='replace')}"

    def close(self):
        if self.process.poll() is None:
            self.process.kill()
            self.process.wait(timeout=5)


def run_case(prefix: list[str], endpoint: str, case: str) -> dict:
    folder = ROOT / "artifacts/adversarial" / f"{endpoint}-{case}-{uuid.uuid4().hex[:8]}"
    peer = Peer(prefix, folder, 2 if case == "duplicate-completed-id" else 1)
    try:
        peer.handshake(approve=case != "untrusted-text")
        if case == "untrusted-text":
            peer.send(3, uuid.uuid4(), b"must-not-be-delivered-before-both-trust")
            peer.exit(False)
            assert not list(folder.rglob("*.txt")), "Untrusted clipboard written"
        elif case == "oversized-chunk":
            identifier = peer.begin("oversized.bin", 65537)
            peer.expect(7, identifier, "begin")
            try:
                peer.send(5, identifier, b"x" * 65537)
            except BrokenPipeError:
                pass  # A codec may reject the header before reading the oversized body.
            response = peer.frames.get(timeout=15)
            if isinstance(response, Exception):
                peer.exit(False)
            else:
                assert response[0] == 9 and response[1] == identifier
                peer.finish_empty("valid-after-oversized-chunk.bin")
                peer.exit()
            assert not (folder / "oversized.bin").exists()
        elif case == "invalid-path":
            identifier = peer.begin("..\\escape.txt")
            peer.expect(9, identifier)
            peer.finish_empty("valid-after-rejection.bin")
            peer.exit()
            assert not (folder.parent / "escape.txt").exists()
        elif case == "hash-mismatch":
            identifier = peer.begin("must-not-commit.bin", 3)
            peer.expect(7, identifier, "begin")
            peer.send(5, identifier, b"abc")
            peer.send(6, identifier, control(dict(sha256="0" * 64)))
            peer.expect(9, identifier)
            peer.finish_empty("valid-after-hash-error.bin")
            peer.exit()
            assert not (folder / "must-not-commit.bin").exists()
        elif case == "cancel-late-chunk":
            identifier = peer.begin("cancelled.bin", 100)
            peer.expect(7, identifier, "begin")
            peer.send(8, identifier, b"cancel fixture")
            peer.send(5, identifier, b"already-in-flight")
            # Implementations may reject or silently ignore a known cancelled chunk.
            valid = peer.begin("valid-after-cancel.bin")
            response = peer.frames.get(timeout=15)
            if isinstance(response, Exception):
                raise response
            if response[0] == 9:
                assert response[1] == identifier
                peer.expect(7, valid, "begin")
            else:
                assert response[0] == 7 and response[1] == valid
                assert json.loads(response[2])["stage"] == "begin"
            peer.send(6, valid, control(dict(sha256=hashlib.sha256(b"").hexdigest())))
            peer.expect(7, valid, "complete")
            peer.exit()
            assert not (folder / "cancelled.bin").exists()
        elif case == "duplicate-completed-id":
            identifier = peer.finish_empty("once.bin")
            peer.begin("once.bin", identifier=identifier)
            peer.expect(9, identifier)
            peer.finish_empty("another.bin")
            peer.exit()
            assert not (folder / "once (1).bin").exists(), "Duplicate transfer committed twice"
        elif case == "duplicate-active-begin":
            identifier = peer.begin("active.bin", 3)
            peer.expect(7, identifier, "begin")
            peer.begin("duplicate.bin", 3, identifier)
            peer.expect(9, identifier)
            peer.send(5, identifier, b"abc")
            peer.send(6, identifier, control(dict(sha256=hashlib.sha256(b"abc").hexdigest())))
            peer.expect(7, identifier, "complete")
            peer.exit()
            assert (folder / "active.bin").read_bytes() == b"abc"
            assert not (folder / "duplicate.bin").exists()
        assert not any(p.suffix in (".part", ".tmp") for p in folder.rglob("*") if p.is_file()), "Partial was not cleaned"
        return dict(endpoint=endpoint, case=case, status="PASS", artifactDirectory=str(folder))
    finally:
        peer.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--endpoint", choices=("windows", "android", "both"), default="both")
    parser.add_argument("--case", choices=("untrusted-text", "oversized-chunk", "invalid-path", "hash-mismatch",
                                           "cancel-late-chunk", "duplicate-completed-id", "duplicate-active-begin"))
    args = parser.parse_args()
    config = json.loads(args.config.read_text(encoding="utf-8-sig"))
    report = dict(transport="OS pipes; no network or Bluetooth radio", cases=[])
    try:
        for endpoint in (("windows", "android") if args.endpoint == "both" else (args.endpoint,)):
            for case in ((args.case,) if args.case else ("untrusted-text", "oversized-chunk", "invalid-path", "hash-mismatch",
                         "cancel-late-chunk", "duplicate-completed-id", "duplicate-active-begin")):
                result = run_case(config[endpoint], endpoint, case)
                report["cases"].append(result)
                print(f"PASS {endpoint}/{case}", flush=True)
        report["status"] = "PASS"
    except Exception as error:
        report.update(status="FAIL", error=str(error))
        print(f"FAIL: {error}", flush=True)
    path = ROOT / "artifacts/adversarial/report.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
