"""Connect production .NET/Kotlin protocol peers through OS pipes, without a network.

Usage: python tools/cross_language_e2e.py --config artifacts/peer-commands.json
Config: {"windows": ["dotnet", "...dll"], "android": ["java", "-cp", "...", "..."]}
Generated inputs/received files and a JSON report remain under artifacts/e2e.
"""
from __future__ import annotations

import argparse
import base64
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import threading
import time
import uuid
import zlib

ROOT = Path(__file__).resolve().parents[1]


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def png() -> bytes:
    def chunk(name: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + name + data + struct.pack(">I", zlib.crc32(name + data))
    # RGB, 16 x 12, using real PNG compression and CRCs, not a signature-only fixture.
    rows = b"".join(b"\0" + bytes([20 + y * 3, 110, 220]) * 16 for y in range(12))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 16, 12, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


def validate_golden() -> int:
    fixtures = json.loads((ROOT / "protocol/golden.json").read_text(encoding="utf-8"))
    for item in fixtures:
        payload = bytes.fromhex(item["payloadHex"])
        wire = (b"BBRG" + bytes([1, item["type"]]) + b"\0\0"
                + uuid.UUID(item["id"]).bytes + struct.pack(">I", len(payload)) + payload)
        assert wire.hex() == item["frameHex"], "Golden fixture has inconsistent bytes"
    return len(fixtures)


def command(prefix: list[str], receive: Path, files: list[Path], texts: list[str],
            image: Path | None, expected: int) -> list[str]:
    result = prefix + ["--receive-dir", str(receive), "--expect", str(expected)]
    for file in files:
        result += ["--send-file", str(file)]
    for text in texts:
        result += ["--send-text-base64", base64.b64encode(text.encode("utf-8")).decode("ascii")]
    if image:
        result += ["--send-image", str(image)]
    return result


def connect(left: list[str], right: list[str], fragmented: bool, timeout: int = 120) -> dict:
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    processes = [subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                 stderr=subprocess.PIPE, bufsize=0, creationflags=flags)
                 for cmd in (left, right)]
    errors: list[str] = []
    diagnostics: list[bytearray] = [bytearray(), bytearray()]
    byte_counts = [0, 0]

    def bridge(index: int) -> None:
        src, dest = processes[index], processes[1 - index]
        try:
            while True:
                block = os.read(src.stdout.fileno(), 65536)
                if not block:
                    break
                byte_counts[index] += len(block)
                step = 17 if fragmented else len(block)
                for offset in range(0, len(block), step):
                    view = memoryview(block)[offset:offset + step]
                    while view:
                        written = dest.stdin.write(view)
                        if not written:
                            raise OSError("pipe write made no progress")
                        view = view[written:]
        except (BrokenPipeError, OSError) as error:
            # A successful peer may exit after its final ACK; retain errors for diagnostics.
            errors.append(f"direction {index}: {error}")
        finally:
            try:
                dest.stdin.close()
            except OSError:
                pass

    def drain(index: int) -> None:
        while True:
            block = os.read(processes[index].stderr.fileno(), 8192)
            if not block:
                return
            if len(diagnostics[index]) < 128 * 1024:
                diagnostics[index].extend(block)

    threads = [threading.Thread(target=bridge, args=(i,), daemon=True) for i in range(2)]
    threads += [threading.Thread(target=drain, args=(i,), daemon=True) for i in range(2)]
    started = time.monotonic()
    for thread in threads:
        thread.start()
    try:
        while any(p.poll() is None for p in processes):
            if time.monotonic() - started > timeout:
                raise TimeoutError("Cross-language peer scenario timed out")
            time.sleep(0.05)
    finally:
        for process in processes:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
        for thread in threads:
            thread.join(timeout=3)
    detail = dict(exitCodes=[p.returncode for p in processes], bytes=byte_counts,
                  durationSeconds=round(time.monotonic() - started, 3), pipeDiagnostics=errors,
                  stderr=[bytes(x).decode("utf-8", errors="replace") for x in diagnostics])
    if detail["exitCodes"] != [0, 0]:
        raise AssertionError(json.dumps(detail, ensure_ascii=False, indent=2))
    return detail


def check_received(directory: Path, files: list[Path], texts: list[str],
                   image: Path | None, preexisting: tuple[Path, bytes] | None = None) -> dict:
    if preexisting:
        assert preexisting[0].read_bytes() == preexisting[1], "Pre-existing file overwritten"
    all_received = list(directory.rglob("*"))
    received = [p for p in all_received if p.is_file() and (not preexisting or p != preexisting[0])]
    expected = Counter(sha(file) for file in files)
    expected.update(hashlib.sha256(text.encode("utf-8")).hexdigest() for text in texts)
    if image:
        expected.update([sha(image)])
    actual = Counter(sha(file) for file in received)
    assert expected == actual, f"Received contents differ: expected={expected}, actual={actual}"
    assert not any(p.suffix in (".part", ".tmp") for p in received), "Uncommitted partial remains"
    return dict(fileCount=len(received), contentHashes=dict(actual), preservedExisting=bool(preexisting))


def scenario(name: str, config: dict, large: bool = False, fragmented: bool = False) -> dict:
    work = ROOT / "artifacts/e2e" / f"{name}-{uuid.uuid4().hex[:8]}"
    outputs = [work / "windows-received", work / "android-received"]
    sources = [work / "windows-input", work / "android-input"]
    for folder in outputs + sources:
        folder.mkdir(parents=True)
    files: list[list[Path]] = [[], []]
    images: list[Path | None] = [None, None]
    texts: list[list[str]] = [[], []]
    old_files = []
    for index in range(2):
        old = outputs[index] / "共享文件.txt"
        old.write_bytes(b"must-not-overwrite-existing-file")
        old_files.append((old, old.read_bytes()))
        if large:
            file = sources[index] / f"100MB-{index}.bin"
            block = bytes(range(256)) * 1024
            with file.open("wb") as stream:
                for _ in range(400):
                    stream.write(block)
            files[index].append(file)
        else:
            shared = sources[index] / "共享文件.txt"
            shared.write_bytes(f"文件原始内容 {index}\n不会覆盖接收端已有文件".encode("utf-8"))
            empty = sources[index] / "空文件.bin"
            empty.touch()
            binary = sources[index] / "二进制 数据.bin"
            binary.write_bytes(bytes(range(256)) * 31)
            files[index] = [shared, empty, binary, shared]
            image = sources[index] / f"image-{index}.png"
            image.write_bytes(png())
            images[index] = image
            texts[index] = [f"来自端 {index}：你好，蓝桥 🌉\n多行\t文字  ", "  保留空白\r\n", ""]
    commands = [command(config[key], outputs[i], files[i], texts[i], images[i],
                        len(files[1-i]) + len(texts[1-i]) + int(images[1-i] is not None))
                for i, key in enumerate(("windows", "android"))]
    run = connect(commands[0], commands[1], fragmented)
    result = dict(name=name, status="PASS", transport="OS pipes; no sockets or network", **run)
    result["received"] = [check_received(outputs[i], files[1-i], texts[1-i], images[1-i], old_files[i])
                          for i in range(2)]
    result["artifacts"] = str(work)
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    args = parser.parse_args()
    config = json.loads(args.config.read_text(encoding="utf-8-sig"))
    for key in ("windows", "android"):
        if not isinstance(config.get(key), list) or not all(isinstance(x, str) for x in config[key]):
            parser.error(f"{key} command must be a JSON array of strings")
    report = dict(goldenFixtures=validate_golden(), scenarios=[], realBluetoothVerified=False)
    destination = ROOT / "artifacts/e2e/report.json"
    destination.parent.mkdir(parents=True, exist_ok=True)
    try:
        for name, options in [("bidirectional-content", {}), ("fragmented-frames", {"fragmented": True}),
                              ("bidirectional-100MB", {"large": True})]:
            result = scenario(name, config, **options)
            report["scenarios"].append(result)
            print(f"PASS {name}: {result['durationSeconds']}s", flush=True)
        report["status"] = "PASS"
    except Exception as error:
        report["status"] = "FAIL"
        report["error"] = str(error)
        print(str(error), flush=True)
    destination.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Report: {destination}", flush=True)
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
