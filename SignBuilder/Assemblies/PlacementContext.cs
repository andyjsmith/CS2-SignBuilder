using System.Collections.Generic;
using System.Linq;
using SignBuilder.Spike;
using Unity.Mathematics;

namespace SignBuilder.Assemblies
{
    /// <summary>User choices that shape a layout besides the support type.</summary>
    public sealed class LayoutOptions
    {
        /// <summary>Free placement: no road; the structure stands at the cursor, rotated and raised by the user.</summary>
        public bool Free;
        /// <summary>-1 centres the signs over all lanes they face; otherwise the lane index, left to right as drivers see them.</summary>
        public int Lane = -1;
        /// <summary>Extra sideways shift of the signs along the structure, metres (+ = drivers' right).</summary>
        public float Shift;
        /// <summary>Free placement: height of the structure's base above the clicked surface.</summary>
        public float Elevation;
        /// <summary>Free placement: width of the (imaginary) roadway the structure spans.</summary>
        public float FreeWidth = 14f;

        public LayoutOptions Clone() => (LayoutOptions)MemberwiseClone();
    }

    /// <summary>
    /// A snapshot of where an assembly was placed: the road snap (lanes, widths, paired carriageway) or the
    /// free position, plus the layout options. Stored in its record so "Save" can rebuild an edited sign in
    /// the same spot without a road click, whether or not the road entities still exist.
    /// </summary>
    public sealed class PlacementContext
    {
        public float[] Point;
        public float[] Tangent;
        public float[] Right;
        public float EdgeWidth;
        public bool Paired;
        public float PairedOffset;
        public float PairedWidth;
        /// <summary>Per lane: offset, direction, width, on paired edge (0/1).</summary>
        public List<float[]> Lanes = new();
        public LayoutOptions Options = new();

        public static PlacementContext From(RoadProbe.Snap s, LayoutOptions options) => new PlacementContext
        {
            Point = new[] { s.Point.x, s.Point.y, s.Point.z },
            Tangent = new[] { s.Tangent.x, s.Tangent.y, s.Tangent.z },
            Right = new[] { s.Right.x, s.Right.y, s.Right.z },
            EdgeWidth = s.EdgeWidth,
            Paired = s.IsPaired,
            PairedOffset = s.PairedOffset,
            PairedWidth = s.PairedWidth,
            Lanes = s.Lanes.Select(l => new[] { l.Offset, l.Direction, l.Width, l.OnPairedEdge ? 1f : 0f }).ToList(),
            Options = (options ?? new LayoutOptions()).Clone(),
        };

        public RoadProbe.Snap ToSnap()
        {
            var s = new RoadProbe.Snap
            {
                PrefabName = Options.Free ? "free placement" : "stored road",
                Point = new float3(Point[0], Point[1], Point[2]),
                Tangent = new float3(Tangent[0], Tangent[1], Tangent[2]),
                Right = new float3(Right[0], Right[1], Right[2]),
                EdgeWidth = EdgeWidth,
                StoredPaired = Paired,
                PairedOffset = PairedOffset,
                PairedWidth = PairedWidth,
            };
            foreach (var l in Lanes)
            {
                s.Lanes.Add(new RoadProbe.LaneInfo { Offset = l[0], Direction = (int)l[1], Width = l[2], OnPairedEdge = l[3] > 0.5f });
            }
            return s;
        }

        /// <summary>A synthetic snap for free placement: a straight "road" of the free width through the point.</summary>
        public static RoadProbe.Snap FreeSnap(float3 point, float angleDegrees, LayoutOptions options)
        {
            var a = math.radians(angleDegrees);
            var tangent = new float3(math.sin(a), 0, math.cos(a));
            return new RoadProbe.Snap
            {
                PrefabName = "free placement",
                Point = point + new float3(0, options.Elevation, 0),
                Tangent = tangent,
                Right = new float3(tangent.z, 0, -tangent.x),
                EdgeWidth = options.FreeWidth,
            };
        }
    }
}
