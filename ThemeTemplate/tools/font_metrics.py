"""
Prints the "scale" and "offset" Sign Builder needs for a font, so letters sit exactly in their box.

    pip install fonttools
    python font_metrics.py MyFont-Bold.ttf [more fonts...]

scale: font size per letter height (units per em / cap height), so a 16-unit "text height" gives 16-unit capitals.
offset: how far to move the text so the capitals' top sits at the top of the box (fraction of the letter height).
"""
import sys

from fontTools.pens.boundsPen import BoundsPen
from fontTools.ttLib import TTFont


def metrics(path):
    font = TTFont(path, lazy=True)
    upm = font["head"].unitsPerEm
    # Browsers (and Gameface) place text with the hhea ascent and descent.
    ascent, descent = font["hhea"].ascent, -font["hhea"].descent
    cap = getattr(font["OS/2"], "sCapHeight", 0) or 0
    if cap <= 0:
        # No usable cap height in the font: measure the H.
        glyphs = font.getGlyphSet()
        pen = BoundsPen(glyphs)
        glyphs["H"].draw(pen)
        cap = pen.bounds[3]
    scale = upm / cap
    half_leading = (1 - (ascent + descent) / upm * scale) / 2
    baseline = half_leading + ascent / upm * scale
    offset = 1 - baseline
    return round(scale, 4), round(offset, 4)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    for path in sys.argv[1:]:
        scale, offset = metrics(path)
        print(f'{path}: "scale": {scale}, "offset": {offset}')
