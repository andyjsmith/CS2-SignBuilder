using System.Collections.Generic;
using System.Linq;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json.Linq;
using SignBuilder.Assemblies;
using SignBuilder.Core;
using SignBuilder.Spike;
using SignBuilder.Systems;
using Unity.Entities;
using Unity.Jobs;

namespace SignBuilder.Tools
{
    /// <summary>
    /// Sign Builder's in-world tool.
    /// Pick mode (while the editor is open): hover a placed Sign Builder assembly and click to load
    /// it back into the editor. Place mode: hover a road and click to build the current design there,
    /// snapped across the road; when re-editing, the original assembly is replaced.
    /// </summary>
    public partial class SignPlacementToolSystem : ToolBaseSystem
    {
        public enum Mode { Pick, Place }

        private ToolSystem m_ToolSystem;
        private DefaultToolSystem m_DefaultTool;
        private PrefabSystem m_PrefabSystem;
        private SignBuilderUISystem m_UI;
        private Mode m_Mode;
        private List<JObject> m_Designs = new();
        private string m_Support;
        private Entity m_Replacing;
        private bool m_Busy;
        private bool m_Reverse;

        // Placement preview: the design is baked once when placement starts; the support + panel mesh
        // is rebuilt only when the hovered road position changes, and drawn every frame.
        private List<Rendering.BakedFace> m_PreviewFaces;
        private UnityEngine.Mesh m_PreviewMesh;
        private UnityEngine.Material m_PreviewSteel;
        private List<UnityEngine.Material> m_PreviewFaceMaterials;
        private Entity m_PreviewEdge;
        private float m_PreviewT = -1f;
        private Unity.Mathematics.float3 m_PreviewOrigin;
        private Unity.Mathematics.quaternion m_PreviewRotation;

        public override string toolID => "SignBuilderTool";
        public Mode CurrentMode => m_Mode;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_DefaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_UI = World.GetOrCreateSystemManaged<SignBuilderUISystem>();
        }

        public void BeginPick()
        {
            m_Mode = Mode.Pick;
            m_ToolSystem.activeTool = this;
            m_UI.SetStatus("Click a placed Sign Builder sign to edit it, or design one and press Place.");
        }

        public void BeginPlace(IReadOnlyList<JObject> designs, string support, bool reverse = false)
        {
            m_Designs = designs.ToList();
            m_Support = support;
            m_Reverse = reverse;
            m_PreviewFaces = null;
            m_PreviewT = -1f;
            SignBuilderRuntime.Run(AssemblyBuilder.BakeAll(m_Designs, faces =>
            {
                m_PreviewFaces = faces;
                m_PreviewFaceMaterials = null;
            }));
            m_Mode = Mode.Place;
            m_ToolSystem.activeTool = this;
            m_UI.SetStatus(m_Replacing != Entity.Null ? "Click a road to place the updated sign (replaces the original)." : "Click a road to place the sign. Right-click to stop.");
        }

        public void Deactivate()
        {
            m_Replacing = Entity.Null;
            if (m_ToolSystem.activeTool == this) m_ToolSystem.activeTool = m_DefaultTool;
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = m_Mode == Mode.Place ? TypeMask.Net : TypeMask.StaticObjects;
            m_ToolRaycastSystem.netLayerMask = Layer.Road;
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            applyAction.shouldBeEnabled = !m_Busy;
            cancelAction.shouldBeEnabled = true;

            if (cancelAction.WasPressedThisFrame())
            {
                if (m_Mode == Mode.Place) BeginPick();
                else Deactivate();
                return base.OnUpdate(inputDeps);
            }

            var hit = GetRaycastResult(out Entity entity, out RaycastHit raycast);
            if (m_Mode == Mode.Place)
            {
                if (hit && EntityManager.HasComponent<Edge>(entity) && EntityManager.HasComponent<Curve>(entity) && !m_Busy)
                {
                    DrawPreview(entity, raycast.m_CurvePosition);
                    if (applyAction.WasPressedThisFrame()) Place(entity, raycast.m_CurvePosition);
                }
            }
            else if (hit && EntityManager.TryGetComponent(entity, out PrefabRef prefabRef)
                     && m_PrefabSystem.TryGetPrefab(prefabRef, out PrefabBase prefab))
            {
                var record = AssemblyLibrary.FromPrefabName(prefab.name);
                if (record != null && applyAction.WasPressedThisFrame())
                {
                    m_Replacing = entity;
                    m_UI.OpenForEdit(record);
                    m_UI.SetStatus("Editing a placed sign. Change it, press Place, then click a road.");
                }
            }
            return base.OnUpdate(inputDeps);
        }

        private void DrawPreview(Entity edge, float t)
        {
            if (m_PreviewFaces == null || m_PreviewFaces.Count == 0) return;
            if (edge != m_PreviewEdge || UnityEngine.Mathf.Abs(t - m_PreviewT) > 0.01f || m_PreviewMesh == null)
            {
                var snap = new RoadProbe(EntityManager, m_PrefabSystem).SnapAt(edge, t);
                var placement = AssemblyBuilder.Layout(m_Support, snap, AssemblyBuilder.Sizes(m_PreviewFaces), m_Reverse);
                if (m_PreviewMesh != null) UnityEngine.Object.Destroy(m_PreviewMesh);
                m_PreviewMesh = AssemblyBuilder.Merge(placement.Support, m_PreviewFaces, placement.SignCentres);
                UnityEngine.Object.Destroy(placement.Support);
                m_PreviewOrigin = placement.Origin;
                m_PreviewRotation = placement.Rotation;
                m_PreviewEdge = edge;
                m_PreviewT = t;
            }
            m_PreviewSteel ??= Rendering.SignPanelRenderer.CreateMaterial("SignBuilder.PreviewSteel", UnityEngine.Texture2D.whiteTexture, new UnityEngine.Color(0.7f, 0.72f, 0.75f), 0.4f);
            m_PreviewFaceMaterials ??= m_PreviewFaces.Select(f => Rendering.SignPanelRenderer.CreateFaceMaterial(f.Texture, 0.3f)).ToList();
            var matrix = UnityEngine.Matrix4x4.TRS(m_PreviewOrigin, m_PreviewRotation, UnityEngine.Vector3.one);
            UnityEngine.Graphics.DrawMesh(m_PreviewMesh, matrix, m_PreviewSteel, 0, null, 0);
            for (var i = 0; i < m_PreviewFaceMaterials.Count; i++)
            {
                UnityEngine.Graphics.DrawMesh(m_PreviewMesh, matrix, m_PreviewFaceMaterials[i], 0, null, i + 1);
            }
        }

        /// <summary>Test hook: draw the preview for a road position (as hovering would).</summary>
        public void PreviewForTest(Entity edge, float t) => DrawPreview(edge, t);

        public bool PreviewReady => m_PreviewFaces != null;

        private void Place(Entity edge, float t)
        {
            var probe = new RoadProbe(EntityManager, m_PrefabSystem);
            var snap = probe.SnapAt(edge, t);
            m_Busy = true;
            m_UI.SetStatus($"Building sign on {snap.PrefabName}...");
            var replacing = m_Replacing;
            var reverse = m_Reverse;
            var support = m_Support;
            SignBuilderRuntime.Run(AssemblyBuilder.Build(m_Designs, support, reverse,
                sizes => AssemblyBuilder.Layout(support, snap, sizes, reverse),
                (record, placement) =>
                {
                    m_Busy = false;
                    if (replacing != Entity.Null)
                    {
                        new RuntimePrefabs().Delete(replacing);
                        m_Replacing = Entity.Null;
                    }
                    LastPlaced = record;
                    m_UI.SetStatus("Placed. Click another road to place again, or right-click to stop.");
                },
                error =>
                {
                    m_Busy = false;
                    m_UI.SetStatus(error);
                }));
        }

        /// <summary>The most recent assembly built by this tool (used by the test harness).</summary>
        public static AssemblyRecord LastPlaced { get; private set; }

        /// <summary>Test hook: place at a road edge without a mouse click.</summary>
        public void PlaceForTest(Entity edge, float t, IReadOnlyList<JObject> designs, string support, bool reverse = false)
        {
            m_Designs = designs.ToList();
            m_Support = support;
            m_Reverse = reverse;
            Place(edge, t);
        }

        /// <summary>Test hook: behave as if the given placed assembly was clicked in pick mode.</summary>
        public void PickForTest(Entity entity)
        {
            if (!EntityManager.TryGetComponent(entity, out PrefabRef prefabRef) || !m_PrefabSystem.TryGetPrefab(prefabRef, out PrefabBase prefab)) return;
            var record = AssemblyLibrary.FromPrefabName(prefab.name);
            if (record == null) return;
            m_Replacing = entity;
            m_UI.OpenForEdit(record);
        }

        public bool Busy => m_Busy;

        public override PrefabBase GetPrefab() => null;

        public override bool TrySetPrefab(PrefabBase prefab) => false;
    }
}
