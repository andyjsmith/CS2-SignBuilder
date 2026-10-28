using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SignBuilder.Assemblies
{
    /// <summary>
    /// The outline of a sign face as rectangles (UV space, 0..1, origin bottom-left), read from the baked
    /// texture's alpha. A sign with an exit tab becomes the tab plus the main panel, so the panel's edges and
    /// back follow the sign instead of framing its whole bounding box. Rounded corners stay to the alpha cutout.
    /// </summary>
    public static class PanelShape
    {
        private const int kColumns = 128;
        /// <summary>Bands thinner than this (fraction of the height) are corner rounding, not a shape change.</summary>
        private const float kMinBand = 0.05f;

        public static List<Rect> Rects(Texture2D face)
        {
            var full = new List<Rect> { new Rect(0, 0, 1, 1) };
            if (face == null || !face.isReadable) return full;
            Color32[] pixels;
            try { pixels = face.GetPixels32(0); }
            catch { return full; }
            int w = face.width, h = face.height;
            if (w < 8 || h < 8) return full;

            // Per row: which column cells are mostly opaque.
            var bands = new List<(int y0, int y1, bool[] mask)>();
            for (var y = 0; y < h; y++)
            {
                var mask = new bool[kColumns];
                for (var c = 0; c < kColumns; c++)
                {
                    int x0 = c * w / kColumns, x1 = Mathf.Max(x0 + 1, (c + 1) * w / kColumns), opaque = 0;
                    for (var x = x0; x < x1; x++) if (pixels[y * w + x].a > 127) opaque++;
                    mask[c] = opaque * 2 > x1 - x0;
                }
                if (bands.Count > 0 && bands[^1].mask.SequenceEqual(mask)) bands[^1] = (bands[^1].y0, y + 1, mask);
                else bands.Add((y, y + 1, mask));
            }

            // Fold thin bands (anti-aliased or rounded edges) into a neighbour, keeping the union of both.
            var minRows = Mathf.Max(2, Mathf.RoundToInt(h * kMinBand));
            bool merged;
            do
            {
                merged = false;
                for (var i = 0; i < bands.Count && bands.Count > 1; i++)
                {
                    if (bands[i].y1 - bands[i].y0 >= minRows) continue;
                    var j = i == 0 ? 1 : i == bands.Count - 1 ? i - 1
                        : (bands[i - 1].y1 - bands[i - 1].y0 >= bands[i + 1].y1 - bands[i + 1].y0 ? i - 1 : i + 1);
                    var union = bands[i].mask.Zip(bands[j].mask, (a, b) => a || b).ToArray();
                    var lo = Mathf.Min(i, j);
                    bands[lo] = (Mathf.Min(bands[i].y0, bands[j].y0), Mathf.Max(bands[i].y1, bands[j].y1), union);
                    bands.RemoveAt(lo + 1);
                    merged = true;
                    break;
                }
                // Neighbouring bands that now match become one.
                for (var i = bands.Count - 1; i > 0; i--)
                {
                    if (!bands[i].mask.SequenceEqual(bands[i - 1].mask)) continue;
                    bands[i - 1] = (bands[i - 1].y0, bands[i].y1, bands[i].mask);
                    bands.RemoveAt(i);
                    merged = true;
                }
            } while (merged);

            var rects = new List<Rect>();
            foreach (var (y0, y1, mask) in bands)
            {
                for (var c = 0; c < kColumns; c++)
                {
                    if (!mask[c]) continue;
                    var start = c;
                    while (c < kColumns && mask[c]) c++;
                    rects.Add(Rect.MinMaxRect(start / (float)kColumns, y0 / (float)h, c / (float)kColumns, y1 / (float)h));
                }
            }
            return rects.Count == 0 ? full : rects;
        }
    }
}
