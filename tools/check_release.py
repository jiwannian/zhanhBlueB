"""Validate packaged delivery, then start/stop the extracted secure listener."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def check_names(names: list[str]) -> None:
    for name in names:
        path = PurePosixPath(name)
        if path.is_absolute() or ".." in path.parts:
            raise ValueError(f"Unsafe archive path: {name}")
        if ".secrets" in path.parts or path.suffix.lower() in {".jks", ".keystore", ".pfx"}:
            raise ValueError(f"Signing material entered delivery: {name}")
        if path.name in {"local.properties", "android-signing.json"}:
            raise ValueError(f"Private machine configuration entered delivery: {name}")


def check_digests(release: Path) -> None:
    sums = (release / "SHA256SUMS.txt").read_text(encoding="utf-8")
    for line in sums.splitlines():
        expected, name = line.split("  ", 1)
        if Path(name).name != name:
            raise ValueError("Unexpected digest filename")
        actual = hashlib.sha256((release / name).read_bytes()).hexdigest()
        if actual != expected:
            raise ValueError(f"SHA-256 mismatch: {name}")

def check_apk(path: Path) -> None:
    with zipfile.ZipFile(path) as apk:
        check_names(apk.namelist())
        if apk.testzip() is not None:
            raise ValueError("APK archive CRC failed")
        for name in apk.namelist():
            if name.endswith(".dex") and b"HeadlessPeer" in apk.read(name):
                raise ValueError("Test peer entered production APK")
def check_source(path: Path) -> None:
    with zipfile.ZipFile(path) as source:
        check_names(source.namelist())
        if source.testzip() is not None:
            raise ValueError("Source archive CRC failed")
        for required in ("LICENSE", "README.md", "tools/verify.ps1", "android/gradle/wrapper/gradle-wrapper.jar", "protocol/golden.json"):
            if "bluebridge/" + required not in source.namelist():
                raise ValueError(f"Source delivery incomplete: {required}")
        excluded = {".git", ".gradle", "bin", "obj", "build", "artifacts", "publish", ".idea", "__pycache__", "TestResults", "coverage"}
        if any(excluded.intersection(PurePosixPath(name).parts) for name in source.namelist()):
            raise ValueError("Build caches entered source delivery")
        if any(name.startswith("bluebridge/windows/validation/") and name != "bluebridge/windows/validation/README.md" for name in source.namelist()):
            raise ValueError("Local validation logs entered source delivery")


def check_desktop(path: Path, skip_listener: bool, report: dict) -> None:
    with zipfile.ZipFile(path) as desktop:
        names = desktop.namelist()
        check_names(names)
        if desktop.testzip() is not None:
            raise ValueError("Windows archive CRC failed")
        for required in ("LICENSE", "zhanhBlueB.exe", "zhanhBlueB.runtimeconfig.json", "coreclr.dll", "hostfxr.dll", "PresentationFramework.dll", "InTheHand.Net.Personal.dll", "使用说明.md"):
            if "zhanhBlueB/" + required not in names:
                raise ValueError(f"Self-contained delivery missing {required}")
        if any("HeadlessPeer" in name or "xunit" in name.lower() or name.endswith("/BlueBridge.exe") for name in names):
            raise ValueError("Test artifacts or old executable entered Windows package")
        config = json.loads(desktop.read("zhanhBlueB/zhanhBlueB.runtimeconfig.json"))
        if "includedFrameworks" not in config["runtimeOptions"]:
            raise ValueError("Windows runtimeconfig is not self-contained")
        report["checks"].append("Windows CRC, bundled CLR/WPF/native Bluetooth library, no test executables")
        if not skip_listener:
            with tempfile.TemporaryDirectory(prefix="zhanhBlueB-release-") as temporary:
                desktop.extractall(temporary)
                executable = Path(temporary) / "zhanhBlueB/zhanhBlueB.exe"
                result = subprocess.run([str(executable), "--bluetooth-smoke"], cwd=executable.parent,
                                        capture_output=True, text=True, errors="replace", timeout=30)
                if result.returncode != 0:
                    raise ValueError(f"Extracted self-contained listener failed: {result.returncode}: {result.stderr}")
                report["checks"].append("Extracted Windows EXE secure RFCOMM listener start/stop exit code 0")
                report["listenerOutput"] = result.stdout.strip()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, default=ROOT / "artifacts/release")
    parser.add_argument("--version", default="1.0.0")
    parser.add_argument("--android-version", help="APK version when Android is unchanged")
    parser.add_argument("--skip-listener", action="store_true")
    args = parser.parse_args()
    release = args.directory.resolve()
    stem = f"zhanhBlueB-{{}}-{args.version}"
    android_version = args.android_version or args.version
    report = {"status": "PASS", "version": args.version, "androidVersion": android_version, "checks": [], "realBluetoothPeerVerified": False}
    check_digests(release)
    check_apk(release / f"zhanhBlueB-Android-{android_version}.apk")
    report["checks"].append("APK CRC, signing-material exclusions, no HeadlessPeer in DEX")
    check_source(release / (stem.format("Source") + ".zip"))
    report["checks"].append("Source CRC, build entrypoints, private-key/cache exclusions")
    check_desktop(release / (stem.format("Windows") + "-win-x64.zip"), args.skip_listener, report)
    destination = release / "package-verification.json"
    destination.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
