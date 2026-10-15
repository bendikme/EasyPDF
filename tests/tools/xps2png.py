"""Turns the screenshot tool's XPS pages into PNGs: python tests/tools/xps2png.py <folder>"""
import glob
import os
import sys

import pymupdf

for xps in sorted(glob.glob(os.path.join(sys.argv[1], "*.xps"))):
    page = pymupdf.open(xps)[0]
    zoom = float(sys.argv[2]) if len(sys.argv) > 2 else 96 / 72
    page.get_pixmap(matrix=pymupdf.Matrix(zoom, zoom)).save(xps[:-4] + ".png")
    print(xps[:-4] + ".png")
