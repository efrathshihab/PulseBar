"""Builds the app icon files from src/PulseBar/Assets/logo.png.

Writes:
  - src/PulseBar/Assets/PulseBar.ico  (16, 20, 24, 32, 40, 48, 64, 256): exe, installer, shortcuts, tray at startup
  - src/PulseBar/Assets/logo-96.png   (96x96): the popup header badge (22 DIP, sharp up to ~400% DPI)

The logo is trimmed to its visible square first, so the icon fills the whole frame. Every size is
resized from the full-resolution logo (not from another size); 16-32 px also get a light sharpen
so they don't look blurry.

Usage: python Scripts/make-icon.py   (needs Pillow)
"""
from pathlib import Path

from PIL import Image, ImageFilter

ASSETS = Path(__file__).resolve().parent.parent / "src" / "PulseBar" / "Assets"
LOGO = ASSETS / "logo.png"
ICO = ASSETS / "PulseBar.ico"
HEADER = ASSETS / "logo-96.png"

SIZES = [16, 20, 24, 32, 40, 48, 64, 256]

# Sharpen settings for small sizes: (radius, percent, threshold)
SHARPEN = {
    16: (0.6, 140, 1),
    20: (0.6, 120, 1),
    24: (0.6, 110, 1),
    32: (0.7, 90, 1),
}


def load_square() -> Image.Image:
    image = Image.open(LOGO).convert("RGBA")

    # Trim the transparent margin (ignore near-invisible shadow pixels), then pad to a centered square
    mask = image.getchannel("A").point(lambda a: 255 if a > 128 else 0)
    box = mask.getbbox()
    if box is None:
        raise SystemExit("logo.png is fully transparent")
    image = image.crop(box)
    side = max(image.size)
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.paste(image, ((side - image.width) // 2, (side - image.height) // 2))
    return square


def render(source: Image.Image, size: int) -> Image.Image:
    # Resize in premultiplied alpha so the edges don't get a dark fringe
    small = source.convert("RGBa").resize((size, size), Image.LANCZOS).convert("RGBA")
    if size in SHARPEN:
        radius, percent, threshold = SHARPEN[size]
        alpha = small.getchannel("A")
        rgb = small.convert("RGB").filter(ImageFilter.UnsharpMask(radius=radius, percent=percent, threshold=threshold))
        small = rgb.convert("RGBA")
        small.putalpha(alpha)
    return small


def main() -> None:
    source = load_square()
    frames = {size: render(source, size) for size in SIZES}

    largest = frames[SIZES[-1]]
    others = [frames[s] for s in SIZES[:-1]]
    largest.save(ICO, format="ICO", sizes=[(s, s) for s in SIZES], append_images=others)

    render(source, 96).save(HEADER, format="PNG", optimize=True)
    print(f"Wrote {ICO} ({', '.join(map(str, SIZES))}) and {HEADER}")


if __name__ == "__main__":
    main()
