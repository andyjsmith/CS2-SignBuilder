using System.Collections.Generic;
using System.Linq;
using SignBuilder.Spike;
using Unity.Mathematics;

namespace SignBuilder.Assemblies
{
    /// <summary>Where one sign sits on its structure.</summary>
    public sealed class SignSlot
    {
        /// <summary>"auto": side by side with the other auto signs over the lanes; "lane": centred over a lane;
        /// "free": at <see cref="Offset"/> metres from the centre of the lanes the signs face.</summary>
        public string Mode = "auto";
        /// <summary>Lane mode: lane number counted from the nearer edge (0 = outermost), and which edge.</summary>
        public int Lane;
        public bool FromRight;
        /// <summary>Fine shift in metres (+ = drivers' right); the position itself in free mode.</summary>
        public float Offset;

        public SignSlot Clone() => (SignSlot)MemberwiseClone();
    }

    /// <summary>Everything that shapes a structure besides its signs' designs and the road under it.</summary>
    public sealed class LayoutOptions
    {
        /// <summary>doubleTruss (box truss), singleTruss (planar), curved, straight, or ground (roadside posts).</summary>
        public string Type = "doubleTruss";
        /// <summary>full (every carriageway), single (one carriageway) or cantilever.</summary>
        public string Span = "full";
        /// <summary>Cantilever post on the drivers' left instead of the right (left-hand exits).</summary>
        public bool LeftSide;
        /// <summary>Signs face the opposite direction of the clicked road's traffic.</summary>
        public bool Reverse;
        /// <summary>Free placement: no road; the structure stands at the cursor, rotated and raised by the user.</summary>
        public bool Free;
        public float Angle;
        /// <summary>Free placement: height of the structure's base above the clicked surface.</summary>
        public float Elevation;
        /// <summary>Free placement: width of the (imaginary) roadway the structure spans.</summary>
        public float FreeWidth = 14f;
        /// <summary>Physical size of the signs relative to full MUTCD size.</summary>
        public float SignScale = kDefaultScale;
        public List<SignSlot> Signs = new();

        /// <summary>Default sign size, shown to players as 100%: full MUTCD size is too big for the game's 4 m lanes.</summary>
        public const float kDefaultScale = 0.7f;

        public SignSlot Slot(int sign) => sign < Signs.Count ? Signs[sign] : new SignSlot();

        public void EnsureSigns(int count)
        {
            while (Signs.Count < count) Signs.Add(new SignSlot());
        }

        /// <summary>The support id used in records and the UI summary, e.g. "curved/cantilever/left".</summary>
        public string Support => Type == "ground" ? "ground" : $"{Type}/{Span}{(Span == "cantilever" && LeftSide ? "/left" : "")}";

        /// <summary>Options for an old support id ("truss", "curvedCantilever"...), for records made before options existed.</summary>
        public static LayoutOptions FromLegacy(string support)
        {
            var o = new LayoutOptions { SignScale = 1f };
            (o.Type, o.Span) = support switch
            {
                "trussSingle" => ("doubleTruss", "single"),
                "trussCantilever" => ("doubleTruss", "cantilever"),
                "curvedFull" => ("curved", "full"),
                "curvedSpan" => ("curved", "single"),
                "curvedCantilever" => ("curved", "cantilever"),
                "monotubeFull" => ("straight", "full"),
                "monotube" => ("straight", "single"),
                "cantilever" => ("straight", "cantilever"),
                "ground" => ("ground", "single"),
                _ => ("doubleTruss", "full"),
            };
            return o;
        }

        public LayoutOptions Clone()
        {
            var copy = (LayoutOptions)MemberwiseClone();
            copy.Signs = Signs.Select(x => x.Clone()).ToList();
            return copy;
        }
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
