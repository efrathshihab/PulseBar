"""Generates src/PulseBar/Assets/PulseBar.ico: a white bold "P" on purple #7C5CFF.

Each size is drawn separately (not downscaled from 256) so small sizes stay crisp.
Usage: python Scripts/make-icon.py   (needs Pillow)
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

PURPLE = (0x7C, 0x5C, 0xFF, 255)
SIZES = [16, 24, 32, 48, 64, 128, 256]
FONT = Path("C:/Windows/Fonts/segoeuib.ttf")
OUT = Path(__file__).resolve().parent.parent / "src" / "PulseBar" / "Assets" / "PulseBar.ico"


def draw(size: int) -> Image.Image:
    # Draw at 4x and downsample for smooth edges
    scale = 4
    big = size * scale
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    radius = int(big * 0.22)
    d.rounded_rectangle((0, 0, big - 1, big - 1), radius=radius, fill=PURPLE)

    font = ImageFont.truetype(str(FONT), int(big * 0.72))
    left, top, right, bottom = d.textbbox((0, 0), "P", font=font)
    x = (big - (right - left)) / 2 - left
    y = (big - (bottom - top)) / 2 - top
    d.text((x, y), "P", font=font, fill=(255, 255, 255, 255))
    return image.resize((size, size), Image.LANCZOS)


def main() -> None:
    OUT.parent.mkdir(parents=True, exist_ok=True)
    images = [draw(s) for s in SIZES]
    images[-1].save(OUT, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    print(f"Wrote {OUT}")


if __name__ == "__main__":
    main()
