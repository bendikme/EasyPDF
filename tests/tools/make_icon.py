"""
Packs the app icon. From the repository root:

    dotnet run --project tests/EasyPDF.Tests -- --write-icon icon
    python tests/tools/make_icon.py icon src/EasyPDF/Assets/easypdf.ico

Each icon.<size>.xps is one icon size, drawn by the app's own AppIconArt; MuPDF rasterises it at
exactly that many pixels and the PNGs become the .ico's frames.
"""
import glob
import struct
import sys

import pymupdf


def main(xps, ico):
    frames = []
    for path in sorted(glob.glob(xps + ".*.xps"), key=lambda p: int(p.rsplit(".", 2)[-2])):
        page = pymupdf.open(path)[0]
        # XPS units are 1/96 inch; MuPDF measures in points.
        px = round(page.rect.width * 96 / 72)
        scale = px / page.rect.width
        pix = page.get_pixmap(matrix=pymupdf.Matrix(scale, scale), alpha=True)
        assert pix.width == px and pix.height == px, (pix.width, pix.height, px)
        frames.append((px, pix.tobytes("png")))
        pix.save(ico + f".{px}.png")

    with open(ico, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(frames)))
        offset = 6 + 16 * len(frames)
        for px, data in frames:
            side = 0 if px >= 256 else px
            f.write(struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(data), offset))
            offset += len(data)
        for _, data in frames:
            f.write(data)
    print(f"Wrote {ico}: {', '.join(str(px) for px, _ in frames)}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
