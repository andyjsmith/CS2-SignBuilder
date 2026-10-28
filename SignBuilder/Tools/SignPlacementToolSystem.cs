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
using SignBuilder.Spike;
using SignBuilder.Systems;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace SignBuilder.Tools
{
    /// <summary>
    /// Sign Builder's in-world tool.
    /// Pick mode (while the editor is open): click a placed Sign Builder assembly to load it back into
    /// the editor. Place mode: build the current design where the user clicks, either snapped across
    /// a road or freely placed (rotate with , and . , raise and lower with Page Up / Page Down). While a
    /// placed sign is being edited, Save rebuilds it in the same spot.
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
        private LayoutOptions m_Options = new();
        private float m_Angle;
        private int m_LaneCount;

        // Placement preview: the design is baked once when placement starts; the support + panel mesh
        // is rebuilt only when the hovered position or the options change, and drawn every frame.
        private List<Rendering.BakedFace> m_PreviewFaces;
        private UnityEngine.Mesh m_PreviewMesh;
        private UnityEngine.Material m_PreviewSteel;
        private List<UnityEngine.Material> m_PreviewFaceMaterials;
        private string m_PreviewKey;
        private float3 m_PreviewOrigin;
        private quaternion m_PreviewRotation;

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
            m_UI.SetStatus(m_Replacing != Entity.Null
                ? "Editing a placed sign: change it and press Save."
                : "Click a placed Sign Builder sign to edit it, or design one and press Place.");
        }

        public void BeginPlace(IReadOnlyList<JObject> designs, string support, bool reverse = false, bool free = false)
        {
            m_Designs = designs.ToList();
            m_Support = support;
            m_Reverse = reverse;
            m_Options = new LayoutOptions { Free = free, FreeWidth = m_Options.FreeWidth };
            m_PreviewFaces = null;
            m_PreviewKey = null;
            SignBuilderRuntime.Run(AssemblyBuilder.BakeAll(m_Designs, faces =>
            {
                m_PreviewFaces = faces;
                m_PreviewFaceMaterials = null;
            }));
            m_Mode = Mode.Place;
            m_ToolSystem.activeTool = this;
            PlacingStatus();
        }

        private void PlacingStatus()
        {
            var what = m_Options.Free ? "Click to place the sign" : "Click a road to place the sign";
            m_UI.SetStatus(m_Replacing != Entity.Null ? what + " (replaces the original)." : what + ". Right-click to stop.");
        }

        public void Deactivate()
        {
            m_Replacing = Entity.Null;
            if (m_ToolSystem.activeTool == this) m_ToolSystem.activeTool = m_DefaultTool;
        }

        /// <summary>Stops editing a placed sign: the next placement is a new sign.</summary>
        public void ForgetReplacing() => m_Replacing = Entity.Null;

        /// <summary>Placement adjustments from the placement bar and keyboard.</summary>
        public void Adjust(string action)
        {
            switch (action)
            {
                case "free": m_Options.Free = true; break;
                case "road": m_Options.Free = false; break;
                case "laneLeft": m_Options.Lane = m_Options.Lane < 0 ? m_LaneCount - 1 : m_Options.Lane - 1; m_Options.Shift = 0; break;
                case "laneRight": m_Options.Lane = m_Options.Lane >= m_LaneCount - 1 ? -1 : m_Options.Lane + 1; m_Options.Shift = 0; break;
                case "shiftLeft": m_Options.Shift -= 0.5f; break;
                case "shiftRight": m_Options.Shift += 0.5f; break;
                case "rotateLeft": m_Angle = (m_Angle + 345f) % 360f; break;
                case "rotateRight": m_Angle = (m_Angle + 15f) % 360f; break;
                case "raise": m_Options.Elevation = math.min(m_Options.Elevation + 0.5f, 40f); break;
                case "lower": m_Options.Elevation = math.max(m_Options.Elevation - 0.5f, 0f); break;
                case "wider": m_Options.FreeWidth = math.min(m_Options.FreeWidth + 1f, 60f); break;
                case "narrower": m_Options.FreeWidth = math.max(m_Options.FreeWidth - 1f, 4f); break;
                case "reset": m_Options = new LayoutOptions { Free = m_Options.Free, FreeWidth = m_Options.FreeWidth }; m_Angle = 0; break;
            }
            if (m_Options.Lane >= m_LaneCount) m_Options.Lane = -1;
            PlacingStatus();
        }

        /// <summary>Current placement settings for the placement bar.</summary>
        public string PlacementInfo => new JObject
        {
            ["free"] = m_Options.Free, ["lane"] = m_Options.Lane, ["laneCount"] = m_LaneCount, ["shift"] = m_Options.Shift,
            ["angle"] = m_Angle, ["elevation"] = m_Options.Elevation, ["width"] = m_Options.FreeWidth, ["support"] = m_Support,
        }.ToString(Formatting.None);

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            var free = m_Mode == Mode.Place && m_Options.Free;
            m_ToolRaycastSystem.typeMask = m_Mode == Mode.Place ? (free ? TypeMask.Terrain | TypeMask.Net : TypeMask.Net) : TypeMask.StaticObjects;
            m_ToolRaycastSystem.netLayerMask = free ? Layer.All : Layer.Road;
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
            Hovered = hit ? entity : Entity.Null;
            if (m_Mode == Mode.Place)
            {
                HandleKeys();
                RoadProbe.Snap snap = null;
                string key = null;
                if (hit && m_Options.Free)
                {
                    snap = PlacementContext.FreeSnap(raycast.m_HitPosition, m_Angle, m_Options);
                    key = $"free {math.round(raycast.m_HitPosition * 10f)}";
                }
                else if (hit && EntityManager.HasComponent<Edge>(entity) && EntityManager.HasComponent<Curve>(entity))
                {
                    snap = new RoadProbe(EntityManager, m_PrefabSystem).SnapAt(entity, raycast.m_CurvePosition);
                    key = $"road {entity} {math.round(raycast.m_CurvePosition * 100f)}";
                }
                if (snap != null && !m_Busy)
                {
                    DrawPreview(snap, key);
                    if (applyAction.WasPressedThisFrame()) Place(snap);
                }
            }
            else if (hit && EntityManager.TryGetComponent(entity, out PrefabRef prefabRef)
                     && m_PrefabSystem.TryGetPrefab(prefabRef, out PrefabBase prefab))
            {
                if (AssemblyLibrary.FromPrefabName(prefab.name) != null && applyAction.WasPressedThisFrame()) EditPlaced(entity);
            }
            return base.OnUpdate(inputDeps);
        }

        private void HandleKeys()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return;
            if (keyboard.commaKey.wasPressedThisFrame) Adjust(m_Options.Free ? "rotateLeft" : "laneLeft");
            if (keyboard.periodKey.wasPressedThisFrame) Adjust(m_Options.Free ? "rotateRight" : "laneRight");
            if (keyboard.pageUpKey.wasPressedThisFrame) Adjust("raise");
            if (keyboard.pageDownKey.wasPressedThisFrame) Adjust("lower");
        }

        private string OptionsKey => $"{m_Support}|{m_Reverse}|{m_Angle}|{m_Options.Free}|{m_Options.Lane}|{m_Options.Shift}|{m_Options.Elevation}|{m_Options.FreeWidth}";

        private void DrawPreview(RoadProbe.Snap snap, string key)
        {
            if (m_PreviewFaces == null || m_PreviewFaces.Count == 0) return;
            key += OptionsKey;
            if (key != m_PreviewKey || m_PreviewMesh == null)
            {
                var placement = AssemblyBuilder.Layout(m_Support, snap, AssemblyBuilder.Sizes(m_PreviewFaces), m_Reverse, m_Options);
                m_LaneCount = placement.LaneCount;
                if (m_PreviewMesh != null) UnityEngine.Object.Destroy(m_PreviewMesh);
                m_PreviewMesh = AssemblyBuilder.Merge(placement.Support, m_PreviewFaces, placement.SignCentres);
                UnityEngine.Object.Destroy(placement.Support);
                m_PreviewOrigin = placement.Origin;
                m_PreviewRotation = placement.Rotation;
                m_PreviewKey = key;
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
        public void PreviewForTest(Entity edge, float t) => DrawPreview(new RoadProbe(EntityManager, m_PrefabSystem).SnapAt(edge, t), $"road {edge} {t}");

        public bool PreviewReady => m_PreviewFaces != null;

        private void Place(RoadProbe.Snap snap) => Build(snap, m_Options.Clone(), "Placed. Click again to place another, or right-click to stop.");

        private void Build(RoadProbe.Snap snap, LayoutOptions options, string doneStatus)
        {
            m_Busy = true;
            m_UI.SetStatus(options.Free ? "Building sign..." : $"Building sign on {snap.PrefabName}...");
            var replacing = m_Replacing;
            var reverse = m_Reverse;
            var support = m_Support;
            SignBuilderRuntime.Run(AssemblyBuilder.Build(m_Designs, support, reverse,
                sizes => AssemblyBuilder.Layout(support, snap, sizes, reverse, options),
                (record, placement) =>
                {
                    m_Busy = false;
                    if (replacing != Entity.Null)
                    {
                        new RuntimePrefabs().Delete(replacing);
                        m_Replacing = Entity.Null;
                        // Keep editing the new sign, so Save can be pressed again.
                        SignBuilderRuntime.Run(TrackReplacement(record.PrefabName, placement.Origin));
                    }
                    LastPlaced = record;
                    m_UI.SetStatus(doneStatus);
                },
                error =>
                {
                    m_Busy = false;
                    m_UI.SetStatus(error);
                },
                PlacementContext.From(snap, options)));
        }

        private IEnumerator TrackReplacement(string prefabName, float3 origin)
        {
            for (var i = 0; i < 30; i++)
            {
                yield return null;
                var found = FindInstance(prefabName, origin);
                if (found == Entity.Null) continue;
                m_Replacing = found;
                yield break;
            }
        }

        private Entity FindInstance(string prefabName, float3 near)
        {
            if (!m_PrefabSystem.TryGetPrefab(new PrefabID(nameof(StaticObjectPrefab), prefabName), out var prefab)) return Entity.Null;
            var prefabEntity = m_PrefabSystem.GetEntity(prefab);
            using var query = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.Object>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Objects.Transform>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            return entities.Where(e => EntityManager.GetComponentData<PrefabRef>(e).m_Prefab == prefabEntity)
                .OrderBy(e => math.distance(EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position, near))
                .FirstOrDefault();
        }

        /// <summary>
        /// Saves the edited design over the placed sign being edited: rebuilt from the placement stored with it
        /// (same road position, lane and shift, or same free position), and the original removed. Signs placed
        /// before placements were stored fall back to clicking a road.
        /// </summary>
        public bool SaveEdit(IReadOnlyList<JObject> designs, string support, bool reverse)
        {
            if (m_Replacing == Entity.Null || m_Busy) return false;
            var record = EntityManager.TryGetComponent(m_Replacing, out PrefabRef prefabRef) && m_PrefabSystem.TryGetPrefab(prefabRef, out PrefabBase prefab)
                ? AssemblyLibrary.FromPrefabName(prefab.name) : null;
            if (record?.Context == null)
            {
                BeginPlace(designs, support, reverse);
                m_UI.SetStatus("This sign was placed with an older version: click its road once to place the updated sign.");
                return false;
            }
            m_Designs = designs.ToList();
            m_Support = support;
            m_Reverse = reverse;
            var options = record.Context.Options.Clone();
            m_Options = options.Clone();
            if (m_ToolSystem.activeTool != this) BeginPick();
            Build(record.Context.ToSnap(), options, "Saved.");
            return true;
        }

        /// <summary>The most recent assembly built by this tool (used by the test harness).</summary>
        public static AssemblyRecord LastPlaced { get; private set; }

        /// <summary>Test hook: place at a road edge without a mouse click.</summary>
        public void PlaceForTest(Entity edge, float t, IReadOnlyList<JObject> designs, string support, bool reverse = false, LayoutOptions options = null)
        {
            m_Designs = designs.ToList();
            m_Support = support;
            m_Reverse = reverse;
            m_Options = options ?? new LayoutOptions();
            Place(new RoadProbe(EntityManager, m_PrefabSystem).SnapAt(edge, t));
        }

        /// <summary>Test hook: free placement at a point.</summary>
        public void PlaceFreeForTest(float3 point, float angle, IReadOnlyList<JObject> designs, string support, LayoutOptions options)
        {
            m_Designs = designs.ToList();
            m_Support = support;
            m_Reverse = false;
            m_Options = options;
            m_Options.Free = true;
            Place(PlacementContext.FreeSnap(point, angle, m_Options));
        }

        /// <summary>
        /// Loads a placed assembly into the editor; Save then rebuilds it in place. Used by pick mode, the
        /// "Edit in Sign Builder" button of the selected-object panel and the test harness.
        /// </summary>
        public bool EditPlaced(Entity entity)
        {
            if (!EntityManager.TryGetComponent(entity, out PrefabRef prefabRef) || !m_PrefabSystem.TryGetPrefab(prefabRef, out PrefabBase prefab)) return false;
            var record = AssemblyLibrary.FromPrefabName(prefab.name);
            if (record == null) return false;
            m_Replacing = entity;
            if (m_ToolSystem.activeTool != this || m_Mode != Mode.Pick) BeginPick();
            m_UI.OpenForEdit(record);
            m_UI.SetStatus("Editing a placed sign: change it and press Save.");
            return true;
        }

        /// <summary>Test hook: behave as if the given placed assembly was clicked in pick mode.</summary>
        public void PickForTest(Entity entity) => EditPlaced(entity);

        /// <summary>Whether the tool is active, and in which mode: "off", "pick" or "place".</summary>
        public string ModeName => m_ToolSystem.activeTool != this ? "off" : m_Mode == Mode.Place ? "place" : "pick";

        public bool IsReplacing => m_Replacing != Entity.Null;

        /// <summary>The entity under the cursor this frame (test harness: checks real raycasts hit placed signs).</summary>
        public Entity Hovered { get; private set; }

        public bool Busy => m_Busy;

        public override PrefabBase GetPrefab() => null;

        public override bool TrySetPrefab(PrefabBase prefab) => false;
    }
}
