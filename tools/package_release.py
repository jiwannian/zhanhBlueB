"""Package built binaries/source without signing keys, caches or test input data."""
from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import shutil
import zipfile

ROOT = Path(__file__).resolve().parents[1]
EXCLUDED = {".git", ".gradle", "build", "bin", "obj", "publish", "artifacts", ".secrets", ".idea", "__pycache__", "TestResults", "coverage"}


def digest(path: Path) -> str:
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(data)
    return result.hexdigest()


def add_docs(package: zipfile.ZipFile, prefix: str = ""):
    package.write(ROOT / "LICENSE", prefix + "LICENSE")
    for source, name in [(ROOT / "README.md", "使用说明.md"),
                         (ROOT / "docs/verification.md", "验证结果.md"),
                         (ROOT / "docs/third-party.md", "第三方说明.md")]:
        package.write(source, prefix + name)
    for document in sorted((ROOT / "docs").glob("*.md")):
        package.write(document, prefix + "docs/" + document.name)
    for license in sorted((ROOT / "docs/licenses").glob("*")):
        if license.is_file():
            package.write(license, prefix + "licenses/" + license.name)
    for source in sorted((ROOT / "docs/third-party-source").glob("*")):
        if source.is_file():
            if source.suffix == ".zip":
                with zipfile.ZipFile(source) as upstream:
                    if upstream.testzip() is not None:
                        raise ValueError(f"Invalid third-party source archive: {source}")
            package.write(source, prefix + "third-party-source/" + source.name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk", type=Path, required=True)
    parser.add_argument("--windows-publish", type=Path, required=True)
    parser.add_argument("--version", default="1.0.0")
    parser.add_argument("--android-version", help="APK version when Android is unchanged by a desktop hotfix")
    parser.add_argument("--output-dir", type=Path, default=ROOT / "artifacts/release")
    args = parser.parse_args()
    if not args.apk.is_file() or not (args.windows_publish / "zhanhBlueB.exe").is_file():
        parser.error("Built APK and self-contained Windows zhanhBlueB.exe are required")
    if (args.windows_publish / "BlueBridge.exe").exists():
        parser.error("Publish directory includes the old executable; use a fresh publish directory")
    for document in (ROOT / "README.md", ROOT / "docs/verification.md", ROOT / "docs/third-party.md"):
        if not document.is_file():
            parser.error(f"Missing delivery document: {document}")
    destination = args.output_dir.resolve()
    destination.mkdir(parents=True, exist_ok=True)
    apk = destination / f"zhanhBlueB-Android-{args.android_version or args.version}.apk"
    shutil.copy2(args.apk, apk)
    desktop = destination / f"zhanhBlueB-Windows-{args.version}-win-x64.zip"
    with zipfile.ZipFile(desktop, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(args.windows_publish.rglob("*")):
            if path.is_file() and path.suffix != ".pdb":
                archive.write(path, "zhanhBlueB/" + path.relative_to(args.windows_publish).as_posix())
        add_docs(archive, "zhanhBlueB/")
    source = destination / f"zhanhBlueB-Source-{args.version}.zip"
    with zipfile.ZipFile(source, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(ROOT.rglob("*")):
            relative = path.relative_to(ROOT)
            if not path.is_file() or any(part in EXCLUDED for part in relative.parts):
                continue
            if relative.parts[:2] == ("windows", "validation") and relative.as_posix() != "windows/validation/README.md":
                continue
            if path.name == "local.properties" or path.suffix.lower() in (".jks", ".keystore", ".pfx", ".user", ".pyc", ".nupkg"):
                continue
            archive.write(path, "bluebridge/" + relative.as_posix())
    with zipfile.ZipFile(source) as archive:
        assert not any("/.secrets/" in name or name.endswith((".jks", ".keystore", ".pfx", "local.properties")) for name in archive.namelist())
    shutil.copy2(ROOT / "README.md", destination / "使用说明.md")
    shutil.copy2(ROOT / "docs/verification.md", destination / "验证结果.md")
    sums = destination / "SHA256SUMS.txt"
    sums.write_text("".join(f"{digest(path)}  {path.name}\n" for path in (apk, desktop, source)), encoding="utf-8")
    for path in (apk, desktop, source, sums):
        print(f"{path} ({path.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
