"""Generate the native BlueBridge application icon from geometric strokes."""
from pathlib import Path
from PIL import Image, ImageDraw

image = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((4, 4, 252, 252), radius=52, fill="#2563EB")
draw.line((40, 170, 216, 170), fill="white", width=16)
draw.arc((50, 65, 206, 230), start=180, end=360, fill="white", width=15)
draw.line((52, 100, 52, 191), fill="white", width=15)
draw.line((204, 100, 204, 191), fill="white", width=15)
draw.line((94, 96, 94, 161), fill="white", width=7)
draw.line((162, 96, 162, 161), fill="white", width=7)
destination = Path(__file__).resolve().parents[1] / "BlueBridge.Desktop" / "BlueBridge.ico"
image.save(destination, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
