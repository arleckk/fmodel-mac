"""Build the macOS app icon (1024px master + .iconset) from FModel's Windows .ico.

Follows Apple's Big Sur icon grid: 1024 canvas, 824px rounded body at (100,100), soft drop shadow.
Usage: python3 -I make_icon.py <FModel.ico> <out_dir> [dark|light]
"""
import io
import struct
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

CANVAS = 1024
BODY = 824
BODY_OFFSET = (CANVAS - BODY) // 2
RADIUS = 185
SS = 4  # supersampling for smooth edges

THEMES = {
    "dark": ((44, 52, 74), (17, 21, 33)),      # navy, matches FModel's blue/charcoal palette
    "light": ((246, 248, 252), (211, 218, 232)),
}

ICONSET = [(16, 1), (16, 2), (32, 1), (32, 2), (128, 1), (128, 2), (256, 1), (256, 2), (512, 1), (512, 2)]


def largest_ico_frame(path: Path) -> Image.Image:
    data = path.read_bytes()
    count = struct.unpack("<H", data[4:6])[0]
    best = None
    for i in range(count):
        w, h, _, _, _, _, size, offset = struct.unpack("<BBBBHHII", data[6 + 16 * i:22 + 16 * i])
        w = w or 256
        if best is None or w > best[0]:
            best = (w, data[offset:offset + size])
    return Image.open(io.BytesIO(best[1])).convert("RGBA")


def vertical_gradient(size: int, top, bottom) -> Image.Image:
    grad = Image.new("RGBA", (1, size))
    for y in range(size):
        t = y / (size - 1)
        grad.putpixel((0, y), tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    return grad.resize((size, size))


def rounded_mask(size: int, radius: int) -> Image.Image:
    big = Image.new("L", (size * SS, size * SS), 0)
    ImageDraw.Draw(big).rounded_rectangle((0, 0, size * SS - 1, size * SS - 1), radius * SS, fill=255)
    return big.resize((size, size), Image.LANCZOS)


def build(logo: Image.Image, theme: str) -> Image.Image:
    top, bottom = THEMES[theme]
    canvas = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))

    mask = rounded_mask(BODY, RADIUS)

    # Drop shadow under the body
    shadow = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    shadow_alpha = Image.new("L", (CANVAS, CANVAS), 0)
    shadow_alpha.paste(mask.point(lambda a: a * 0.35), (BODY_OFFSET, BODY_OFFSET + 12))
    shadow.putalpha(shadow_alpha.filter(ImageFilter.GaussianBlur(24)))
    canvas = Image.alpha_composite(canvas, shadow)

    body = vertical_gradient(BODY, top, bottom)
    body.putalpha(mask)
    canvas.paste(body, (BODY_OFFSET, BODY_OFFSET), body)

    # Logo: trim transparent margin, scale to ~62% of the body, center optically
    logo = logo.crop(logo.getbbox())
    target = round(BODY * 0.62)
    scale = target / max(logo.size)
    logo = logo.resize((round(logo.width * scale), round(logo.height * scale)), Image.LANCZOS)
    logo = logo.filter(ImageFilter.UnsharpMask(radius=2, percent=60, threshold=2))

    # Soft shadow behind the logo so it lifts off the body
    logo_shadow = Image.new("RGBA", logo.size, (0, 0, 0, 0))
    logo_shadow.putalpha(logo.getchannel("A").point(lambda a: a * 0.4).filter(ImageFilter.GaussianBlur(10)))

    x = (CANVAS - logo.width) // 2
    y = (CANVAS - logo.height) // 2
    layer = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    layer.paste(logo_shadow, (x, y + 10), logo_shadow)
    layer.paste(logo, (x, y), logo)
    # Keep everything inside the rounded body
    clip = Image.new("L", (CANVAS, CANVAS), 0)
    clip.paste(mask, (BODY_OFFSET, BODY_OFFSET))
    layer.putalpha(ImageChops.multiply(layer.getchannel("A"), clip))
    return Image.alpha_composite(canvas, layer)


def main() -> None:
    ico, out_dir = Path(sys.argv[1]), Path(sys.argv[2])
    theme = sys.argv[3] if len(sys.argv) > 3 else "dark"
    out_dir.mkdir(parents=True, exist_ok=True)

    master = build(largest_ico_frame(ico), theme)
    master.save(out_dir / f"icon-{theme}-1024.png")

    iconset = out_dir / "FModelMac.iconset"
    iconset.mkdir(exist_ok=True)
    for size, scale in ICONSET:
        px = size * scale
        name = f"icon_{size}x{size}{'@2x' if scale == 2 else ''}.png"
        master.resize((px, px), Image.LANCZOS).save(iconset / name)
    print(f"wrote {out_dir / f'icon-{theme}-1024.png'} and {iconset}")


if __name__ == "__main__":
    main()
