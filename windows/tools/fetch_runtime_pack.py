"""Fetch an official NuGet runtime pack in bounded ranges for slow connections.

Build helper only; never distributed with the desktop application.
"""
import concurrent.futures
import pathlib
import urllib.request
import zipfile

URL = "https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.win-x64/10.0.10/microsoft.netcore.app.runtime.win-x64.10.0.10.nupkg"
TOTAL = 39_889_960
BLOCK = 1_048_576
root = pathlib.Path(__file__).resolve().parents[1] / "validation" / "runtime-parts"
root.mkdir(parents=True, exist_ok=True)

def download(index):
    first = index * BLOCK
    last = min(TOTAL - 1, first + BLOCK - 1)
    path = root / f"{index:03d}.bin"
    if path.exists() and path.stat().st_size == last - first + 1:
        return path
    for attempt in range(3):
        request = urllib.request.Request(URL + f"?part={index}&attempt={attempt}", headers={"Range": f"bytes={first}-{last}"})
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                if response.status != 206 or response.headers.get("Content-Range") != f"bytes {first}-{last}/{TOTAL}":
                    raise IOError("Server returned a different range")
                content = response.read()
            if len(content) != last - first + 1:
                raise IOError("Truncated range")
            path.write_bytes(content)
            print(f"Downloaded range {index}", flush=True)
            return path
        except OSError:
            if attempt == 2:
                raise

with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
    parts = list(pool.map(download, range((TOTAL + BLOCK - 1) // BLOCK)))
destination = root.parent / "microsoft.netcore.app.runtime.win-x64.10.0.10.nupkg"
with destination.open("wb") as output:
    for part in parts:
        output.write(part.read_bytes())
with zipfile.ZipFile(destination) as archive:
    bad = archive.testzip()
    if bad is not None:
        raise IOError("Runtime archive CRC failed: " + bad)
print(f"Complete archive: {destination}", flush=True)
