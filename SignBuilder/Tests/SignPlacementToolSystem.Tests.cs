using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SignBuilder.Assemblies;
using SignBuilder.Core;
using SignBuilder.Systems;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using SignBuilder.Geometry;

namespace SignBuilder.Tools
{
    /// <summary>Test hooks: what the in-game tests drive and inspect (test builds only).</summary>
    public partial class SignPlacementToolSystem
    {
        /// <summary>The most recent assembly built by this tool.</summary>
        public static AssemblyRecord LastPlaced { get; private set; }

        /// <summary>The entity under the cursor this frame.</summary>
        public Entity Hovered { get; private set; }

        public bool Busy => m_Busy;
        public int SignCount => m_Designs.Count;

        /// <summary>Whether the structure being edited is hidden.</summary>
        public bool ReplacingHidden => m_Replacing != Entity.Null && EntityManager.Exists(m_Replacing) && EntityManager.HasComponent<Hidden>(m_Replacing);

        public bool PreviewReady => FacesReady && m_Designs.Count > 0;

        /// <summary>The road a click on a node snaps to.</summary>
        public bool EdgeAtNodeForTest(Entity node, float3 point, out Entity edge, out float t) => EdgeAtNode(node, point, out edge, out t);

        /// <summary>Draws the preview for a road position (as hovering would).</summary>
        public void PreviewForTest(Entity edge, float t) => DrawPreview(new RoadProbe(EntityManager, m_PrefabSystem).SnapAt(edge, t), $"road {edge} {t}");

        /// <summary>Test hook: build at a road edge without a mouse click.</summary>
        public void PlaceForTest(Entity edge, float t, IReadOnlyList<JObject> designs, LayoutOptions options)
        {
            SetSigns(designs);
            m_Options = options.Clone();
            m_Options.EnsureSigns(m_Designs.Count);
            SignBuilderRuntime.Run(WaitThenBuild(new RoadProbe(EntityManager, m_PrefabSystem).SnapAt(edge, t)));
        }

        /// <summary>Test hook: free placement at a point.</summary>
        public void PlaceFreeForTest(float3 point, IReadOnlyList<JObject> designs, LayoutOptions options)
        {
            SetSigns(designs);
            m_Options = options.Clone();
            m_Options.Free = true;
            m_Options.EnsureSigns(m_Designs.Count);
            SignBuilderRuntime.Run(WaitThenBuild(PlacementContext.FreeSnap(point, m_Options.Angle, m_Options)));
        }

        /// <summary>Test hook: pin a gantry at a road position and open the arrangement, as a click would.</summary>
        public void ArrangeForTest(Entity edge, float t, IReadOnlyList<JObject> designs, LayoutOptions options)
        {
            BeginGantry(designs, options);
            m_Pinned = new RoadProbe(EntityManager, m_PrefabSystem).SnapAt(edge, t);
            m_Mode = Mode.Arrange;
            Status();
        }
    }
}
