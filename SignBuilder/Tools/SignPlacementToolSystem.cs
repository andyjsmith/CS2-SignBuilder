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
    /// Sign Builder's in-world tool, with three modes:
    /// <list type="bullet">
    /// <item>Posts: place one sign on roadside posts, snapped beside a road or freely placed.</item>
    /// <item>PickRoad: hover and click a road (or a free spot) to pin a gantry there.</item>
    /// <item>Arrange: the gantry is pinned and previewed live while the arrangement panel adds, drags and
    /// snaps its signs and picks the structure; Build makes it.</item>
    /// </list>
    /// Free placement rotates with a right-drag and rises with Page Up / Page Down, like vanilla props.
    /// Placed structures can be edited (Save rebuilds in place), moved or duplicated.
    /// </summary>
    public partial class SignPlacementToolSystem : ToolBaseSystem
    {
        public enum Mode { Posts, PickRoad, Arrange }

        private ToolSystem m_ToolSystem;
        private DefaultToolSystem m_DefaultTool;
        private PrefabSystem m_PrefabSystem;
        private SignBuilderUISystem m_UI;
        private Mode m_Mode;
        private readonly List<JObject> m_Designs = new();
        private readonly List<Rendering.BakedFace> m_Faces = new();
        private LayoutOptions m_Options = new();
        private RoadProbe.Snap m_Pinned;
        private Entity m_Replacing;
        private bool m_Busy;
        private int m_LaneCount;
        private AssemblyPlacement? m_LastPlacement;
        private int m_FacesVersion;

        // Right mouse button in free placement: a drag rotates, a click without dragging cancels.
        private bool m_RightDown;
        private bool m_RightDragged;
        private float m_RightStartX;
        private float m_RightLastX;

        // Preview: rebuilt when the position, options or faces change; drawn every frame.
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

        // ------------------------------------------------------------------ starting

        /// <summary>Place one sign on roadside posts.</summary>
        public void BeginPosts(JObject design, LayoutOptions start = null)
        {
            SetSigns(new[] { design });
            m_Options = start?.Clone() ?? new LayoutOptions { FreeWidth = m_Options.FreeWidth };
            m_Options.Type = "ground";
            m_Options.EnsureSigns(1);
            Activate(Mode.Posts);
        }

        /// <summary>Pick a road for a gantry carrying <paramref name="designs"/> (may be empty: signs are added while arranging).</summary>
        public void BeginGantry(IEnumerable<JObject> designs, LayoutOptions start = null)
        {
            SetSigns(designs);
            m_Options = start?.Clone() ?? new LayoutOptions { FreeWidth = m_Options.FreeWidth, Type = m_Options.Type == "ground" ? "doubleTruss" : m_Options.Type, Span = m_Options.Span };
            if (m_Options.Type == "ground") m_Options.Type = "doubleTruss";
            m_Options.EnsureSigns(m_Designs.Count);
            Activate(Mode.PickRoad);
        }

        private void Activate(Mode mode)
        {
            m_Mode = mode;
            m_PreviewKey = null;
            m_ToolSystem.activeTool = this;
            Status();
        }

        /// <summary>Stops the tool (back to the game's default tool) and forgets any edit in progress.</summary>
        public void Deactivate()
        {
            m_Replacing = Entity.Null;
            m_Pinned = null;
            if (m_ToolSystem.activeTool == this) m_ToolSystem.activeTool = m_DefaultTool;
        }

        public bool Active => m_ToolSystem.activeTool == this;

        private void Status()
        {
            var replacing = m_Replacing != Entity.Null ? " (replaces the original)" : "";
            m_UI.SetStatus(m_Mode switch
            {
                Mode.Posts => (m_Options.Free ? "Click to place the sign" : "Click beside a road to place the sign") + replacing + ". Right-click to stop.",
                Mode.PickRoad => (m_Options.Free ? "Click where the gantry should stand" : "Click a road for the gantry") + replacing + ". Right-click to stop.",
                _ => m_Replacing != Entity.Null ? "Arrange the signs, then press Save." : "Arrange the signs, then press Build. Click another road to move the gantry.",
            });
        }

        private void SetSigns(IEnumerable<JObject> designs)
        {
            m_Designs.Clear();
            m_Faces.Clear();
            foreach (var d in designs) AddSign(d, bake: false);
            for (var i = 0; i < m_Designs.Count; i++) Bake(i);
        }

        private void AddSign(JObject design, bool bake = true)
        {
            m_Designs.Add(design);
            m_Faces.Add(null);
            m_Options.EnsureSigns(m_Designs.Count);
            if (bake) Bake(m_Designs.Count - 1);
        }

        private void Bake(int index)
        {
            var design = m_Designs[index];
            SignBuilderRuntime.Run(AssemblyBuilder.BakeAll(new[] { design }, faces =>
            {
                // The sign may have been removed or replaced meanwhile.
                if (index >= m_Designs.Count || !ReferenceEquals(m_Designs[index], design)) return;
                m_Faces[index] = faces?.FirstOrDefault();
                m_FacesVersion++;
                m_PreviewFaceMaterials = null;
            }));
        }

        private bool FacesReady => m_Faces.All(f => f != null);

        // ------------------------------------------------------------------ adjustments

        /// <summary>Placement bar actions while placing posts or picking a road.</summary>
        public void Adjust(string action)
        {
            switch (action)
            {
                case "free": m_Options.Free = true; break;
                case "road": m_Options.Free = false; break;
                case "rotateLeft": m_Options.Angle = (m_Options.Angle + 345f) % 360f; break;
                case "rotateRight": m_Options.Angle = (m_Options.Angle + 15f) % 360f; break;
                case "raise": m_Options.Elevation = math.min(m_Options.Elevation + 0.5f, 40f); break;
                case "lower": m_Options.Elevation = math.max(m_Options.Elevation - 0.5f, 0f); break;
                case "wider": m_Options.FreeWidth = math.min(m_Options.FreeWidth + 1f, 60f); break;
                case "narrower": m_Options.FreeWidth = math.max(m_Options.FreeWidth - 1f, 4f); break;
                case "smaller": m_Options.SignScale = Step(m_Options.SignScale, -1); break;
                case "larger": m_Options.SignScale = Step(m_Options.SignScale, 1); break;
            }
            if (m_Mode == Mode.Arrange && m_Pinned != null && m_Options.Free) RepinFree();
            Status();
        }

        /// <summary>Sign size steps of 5% of the default size (shown to players as 100%).</summary>
        private static float Step(float scale, int direction)
        {
            var shown = math.round(scale / LayoutOptions.kDefaultScale * 20f) + direction;
            return math.clamp(shown, 10f, 34f) / 20f * LayoutOptions.kDefaultScale;
        }

        private float3 m_FreePoint;

        private void RepinFree() => m_Pinned = PlacementContext.FreeSnap(m_FreePoint, m_Options.Angle, m_Options);

        /// <summary>
        /// Arrangement panel operations (JSON { op, ... }): type, span, side, reverse, scale, addSign, removeSign,
        /// replaceSign, moveSign (drop at x, snapping to lanes), slot, nudge, auto, build, cancel, repick.
        /// </summary>
        public void Arrange(string json)
        {
            if (m_Mode != Mode.Arrange) return;
            JObject a;
            try { a = JObject.Parse(json); } catch { return; }
            var i = (int?)a["index"] ?? -1;
            switch ((string)a["op"])
            {
                case "type": m_Options.Type = (string)a["value"]; break;
                case "span": m_Options.Span = (string)a["value"]; break;
                case "side": m_Options.LeftSide = (string)a["value"] == "left"; break;
                case "reverse": m_Options.Reverse = (bool?)a["value"] ?? false; break;
                case "scale": m_Options.SignScale = Step(m_Options.SignScale, (int?)a["value"] ?? 0); break;
                case "addSign":
                    if (a["design"] is JObject added) AddSign(added);
                    break;
                case "removeSign":
                    if (i >= 0 && i < m_Designs.Count)
                    {
                        m_Designs.RemoveAt(i);
                        m_Faces.RemoveAt(i);
                        if (i < m_Options.Signs.Count) m_Options.Signs.RemoveAt(i);
                        m_PreviewFaceMaterials = null;
                        m_FacesVersion++;
                    }
                    break;
                case "replaceSign":
                    if (i >= 0 && i < m_Designs.Count && a["design"] is JObject replaced)
                    {
                        m_Designs[i] = replaced;
                        m_Faces[i] = null;
                        Bake(i);
                    }
                    break;
                case "moveSign": if (i >= 0) MoveSign(i, (float?)a["x"] ?? 0f, (bool?)a["snap"] ?? true); break;
                case "slot":
                    if (i >= 0)
                    {
                        m_Options.EnsureSigns(i + 1);
                        var slot = m_Options.Signs[i];
                        slot.Mode = (string)a["mode"] ?? slot.Mode;
                        if (a["lane"] != null) slot.Lane = (int)a["lane"];
                        if (a["fromRight"] != null) slot.FromRight = (bool)a["fromRight"];
                        if (a["offset"] != null) slot.Offset = (float)a["offset"];
                    }
                    break;
                case "nudge":
                    if (i >= 0)
                    {
                        m_Options.EnsureSigns(i + 1);
                        m_Options.Signs[i].Offset += (float?)a["value"] ?? 0f;
                    }
                    break;
                case "auto":
                    foreach (var slot in m_Options.Signs) { slot.Mode = "auto"; slot.Offset = 0; }
                    break;
                case "build": BuildPinned(); break;
                case "cancel": CancelArrange(); break;
                case "repick":
                    m_Mode = Mode.PickRoad;
                    m_Pinned = null;
                    break;
                default:
                    Adjust((string)a["op"]);
                    break;
            }
            Status();
        }

        /// <summary>
        /// Drops sign <paramref name="index"/> at structure x: within 0.75 m of a lane centre it snaps to that lane,
        /// remembered from the nearer edge ("2nd from right"); otherwise it stays where dropped, relative to the lanes.
        /// </summary>
        private void MoveSign(int index, float x, bool snap)
        {
            if (m_LastPlacement == null) return;
            var p = m_LastPlacement.Value;
            m_Options.EnsureSigns(index + 1);
            var slot = m_Options.Signs[index];
            var lanes = p.LaneXs ?? System.Array.Empty<float>();
            if (snap && lanes.Length > 0)
            {
                var nearest = Enumerable.Range(0, lanes.Length).OrderBy(k => math.abs(lanes[k] - x)).First();
                if (math.abs(lanes[nearest] - x) < 0.75f)
                {
                    slot.Mode = "lane";
                    slot.FromRight = nearest > (lanes.Length - 1) * 0.5f;
                    slot.Lane = slot.FromRight ? lanes.Length - 1 - nearest : nearest;
                    slot.Offset = 0f;
                    return;
                }
            }
            var centre = lanes.Length > 0 ? lanes.Average() : 0f;
            slot.Mode = "free";
            slot.Offset = x - centre;
        }

        private void CancelArrange()
        {
            m_Pinned = null;
            m_Replacing = Entity.Null;
            Deactivate();
            m_UI.SetStatus("");
        }

        /// <summary>The arrangement panel's data: lanes, deck, structure, posts and signs in structure space.</summary>
        public string ArrangeInfo()
        {
            if (m_Mode != Mode.Arrange || m_LastPlacement == null) return "";
            var p = m_LastPlacement.Value;
            var sizes = Sizes();
            var signs = new JArray();
            for (var i = 0; i < m_Designs.Count; i++)
            {
                var slot = m_Options.Slot(i);
                signs.Add(new JObject
                {
                    ["x"] = p.SignXs != null && i < p.SignXs.Length ? p.SignXs[i] : 0f,
                    ["w"] = sizes[i].x, ["h"] = sizes[i].y, ["ready"] = m_Faces[i] != null,
                    ["mode"] = slot.Mode, ["lane"] = slot.Lane, ["fromRight"] = slot.FromRight, ["offset"] = slot.Offset,
                    ["design"] = m_Designs[i],
                });
            }
            return new JObject
            {
                ["lanes"] = new JArray(p.LaneXs ?? System.Array.Empty<float>()),
                ["deck"] = new JArray(p.DeckX.x, p.DeckX.y),
                ["structure"] = new JArray(p.StructureX.x, p.StructureX.y),
                ["posts"] = new JArray(p.PostXs ?? System.Array.Empty<float>()),
                ["signs"] = signs,
                ["type"] = m_Options.Type, ["span"] = m_Options.Span, ["side"] = m_Options.LeftSide ? "left" : "right",
                ["reverse"] = m_Options.Reverse, ["free"] = m_Options.Free, ["scale"] = m_Options.SignScale / LayoutOptions.kDefaultScale,
                ["angle"] = m_Options.Angle, ["elevation"] = m_Options.Elevation, ["width"] = m_Options.FreeWidth,
                ["replacing"] = m_Replacing != Entity.Null, ["busy"] = m_Busy,
            }.ToString(Formatting.None);
        }

        /// <summary>Placement bar data while placing posts or picking a road.</summary>
        public string PlacementInfo => new JObject
        {
            ["free"] = m_Options.Free, ["angle"] = m_Options.Angle, ["elevation"] = m_Options.Elevation, ["width"] = m_Options.FreeWidth,
            ["scale"] = m_Options.SignScale / LayoutOptions.kDefaultScale, ["posts"] = m_Mode == Mode.Posts,
        }.ToString(Formatting.None);

        /// <summary>Sign sizes in metres at the current scale; signs still baking use a placeholder size.</summary>
        private List<UnityEngine.Vector2> Sizes() =>
            m_Faces.Select(f => f == null ? new UnityEngine.Vector2(3f, 2f) * m_Options.SignScale / LayoutOptions.kDefaultScale * 0.7f
                : new UnityEngine.Vector2(f.WidthMeters, f.HeightMeters) * m_Options.SignScale).ToList();

        // ------------------------------------------------------------------ update

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            var free = m_Options.Free;
            m_ToolRaycastSystem.typeMask = free ? TypeMask.Terrain | TypeMask.Net : TypeMask.Net;
            m_ToolRaycastSystem.netLayerMask = free ? Layer.All : Layer.Road;
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            applyAction.shouldBeEnabled = !m_Busy;
            cancelAction.shouldBeEnabled = true;

            // Free placement: right-drag rotates like vanilla prop placement; a right click without a drag
            // still cancels (on release). Other cancel inputs (Escape) work as usual.
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var rotating = m_Options.Free && mouse != null;
            if (rotating && TrackRightDrag(mouse))
            {
                Cancel();
                return base.OnUpdate(inputDeps);
            }
            if (cancelAction.WasPressedThisFrame() && !(rotating && (mouse.rightButton.isPressed || mouse.rightButton.wasReleasedThisFrame)))
            {
                Cancel();
                return base.OnUpdate(inputDeps);
            }
            HandleKeys();

            var hit = GetRaycastResult(out Entity entity, out RaycastHit raycast);
            Hovered = hit ? entity : Entity.Null;
            RoadProbe.Snap snap = null;
            string key = null;
            if (hit && m_Options.Free)
            {
                snap = PlacementContext.FreeSnap(raycast.m_HitPosition, m_Options.Angle, m_Options);
                key = $"free {math.round(raycast.m_HitPosition * 10f)}";
            }
            else if (hit && EntityManager.HasComponent<Edge>(entity) && EntityManager.HasComponent<Curve>(entity))
            {
                snap = new RoadProbe(EntityManager, m_PrefabSystem).SnapAt(entity, raycast.m_CurvePosition);
                key = $"road {entity} {math.round(raycast.m_CurvePosition * 100f)}";
            }

            if (m_Mode == Mode.Arrange)
            {
                // The pinned gantry is previewed; clicking another road (or spot) moves it there.
                if (snap != null && !m_Busy && applyAction.WasPressedThisFrame())
                {
                    m_Pinned = snap;
                    if (m_Options.Free) m_FreePoint = raycast.m_HitPosition;
                }
                if (m_Pinned != null) DrawPreview(m_Pinned, "pinned " + m_Pinned.Point);
            }
            else if (snap != null && !m_Busy)
            {
                DrawPreview(snap, key);
                if (applyAction.WasPressedThisFrame())
                {
                    if (m_Mode == Mode.Posts) Build(snap, "Placed. Click again to place another, or right-click to stop.");
                    else
                    {
                        m_Pinned = snap;
                        if (m_Options.Free) m_FreePoint = raycast.m_HitPosition;
                        m_Mode = Mode.Arrange;
                        Status();
                    }
                }
            }
            return base.OnUpdate(inputDeps);
        }

        private void Cancel()
        {
            if (m_Mode == Mode.Arrange) CancelArrange();
            else Deactivate();
        }

        private void HandleKeys()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null || !m_Options.Free) return;
            if (keyboard.pageUpKey.wasPressedThisFrame) Adjust("raise");
            if (keyboard.pageDownKey.wasPressedThisFrame) Adjust("lower");
        }

        /// <summary>Right-button handling in free placement; returns true when a plain right click asks to cancel.</summary>
        private bool TrackRightDrag(UnityEngine.InputSystem.Mouse mouse)
        {
            var x = mouse.position.ReadValue().x;
            if (mouse.rightButton.wasPressedThisFrame)
            {
                m_RightDown = true;
                m_RightDragged = false;
                m_RightStartX = m_RightLastX = x;
                return false;
            }
            if (!m_RightDown) return false;
            if (mouse.rightButton.isPressed)
            {
                if (math.abs(x - m_RightStartX) > 4f) m_RightDragged = true;
                if (m_RightDragged)
                {
                    m_Options.Angle = ((m_Options.Angle + (x - m_RightLastX) * 0.5f) % 360f + 360f) % 360f;
                    if (m_Mode == Mode.Arrange && m_Pinned != null) RepinFree();
                }
                m_RightLastX = x;
                return false;
            }
            m_RightDown = false;
            return !m_RightDragged;
        }

        private string OptionsKey => JsonConvert.SerializeObject(m_Options) + "|" + m_FacesVersion;

        private void DrawPreview(RoadProbe.Snap snap, string key)
        {
            key += OptionsKey;
            if (key != m_PreviewKey)
            {
                var placement = AssemblyBuilder.Layout(snap, Sizes(), m_Options);
                m_LastPlacement = placement;
                m_LaneCount = placement.LaneCount;
                if (FacesReady)
                {
                    if (m_PreviewMesh != null) UnityEngine.Object.Destroy(m_PreviewMesh);
                    m_PreviewMesh = AssemblyBuilder.Merge(placement.Support, m_Faces, placement.SignCentres, m_Options.SignScale);
                    m_PreviewOrigin = placement.Origin;
                    m_PreviewRotation = placement.Rotation;
                }
                UnityEngine.Object.Destroy(placement.Support);
                m_PreviewKey = FacesReady ? key : null;
            }
            if (m_PreviewMesh == null) return;
            m_PreviewSteel ??= Rendering.SignPanelRenderer.CreateMaterial("SignBuilder.PreviewSteel", UnityEngine.Texture2D.whiteTexture, new UnityEngine.Color(0.7f, 0.72f, 0.75f), 0.4f);
            m_PreviewFaceMaterials ??= m_Faces.Where(f => f != null).Select(f => Rendering.SignPanelRenderer.CreateFaceMaterial(f.Texture, 0.3f)).ToList();
            var matrix = UnityEngine.Matrix4x4.TRS(m_PreviewOrigin, m_PreviewRotation, UnityEngine.Vector3.one);
            UnityEngine.Graphics.DrawMesh(m_PreviewMesh, matrix, m_PreviewSteel, 0, null, 0);
            for (var i = 0; i < m_PreviewFaceMaterials.Count && i + 1 < m_PreviewMesh.subMeshCount; i++)
            {
                UnityEngine.Graphics.DrawMesh(m_PreviewMesh, matrix, m_PreviewFaceMaterials[i], 0, null, i + 1);
            }
        }

        /// <summary>Test hook: draw the preview for a road position (as hovering would).</summary>
        public void PreviewForTest(Entity edge, float t) => DrawPreview(new RoadProbe(EntityManager, m_PrefabSystem).SnapAt(edge, t), $"road {edge} {t}");

        public bool PreviewReady => FacesReady && m_Designs.Count > 0;

        // ------------------------------------------------------------------ building

        private void BuildPinned()
        {
            if (m_Pinned == null || m_Designs.Count == 0 || !FacesReady) return;
            Build(m_Pinned, m_Replacing != Entity.Null ? "Saved." : "Built.", afterwards: () =>
            {
                m_Pinned = null;
                Deactivate();
            });
        }

        private void Build(RoadProbe.Snap snap, string doneStatus, System.Action afterwards = null)
        {
            if (m_Designs.Count == 0) return;
            m_Busy = true;
            m_UI.SetStatus("Building...");
            var replacing = m_Replacing;
            var options = m_Options.Clone();
            var designs = m_Designs.ToList();
            SignBuilderRuntime.Run(AssemblyBuilder.Build(designs,
                sizes => AssemblyBuilder.Layout(snap, sizes, options),
                (record, placement) =>
                {
                    m_Busy = false;
                    if (replacing != Entity.Null)
                    {
                        new RuntimePrefabs().Delete(replacing);
                        m_Replacing = Entity.Null;
                    }
                    LastPlaced = record;
                    m_UI.SetStatus(doneStatus);
                    afterwards?.Invoke();
                },
                error =>
                {
                    m_Busy = false;
                    m_UI.SetStatus(error);
                },
                PlacementContext.From(snap, options)));
        }

        /// <summary>The most recent assembly built by this tool (used by the test harness).</summary>
        public static AssemblyRecord LastPlaced { get; private set; }

        // ------------------------------------------------------------------ placed structures

        private AssemblyRecord RecordOf(Entity entity) =>
            EntityManager.TryGetComponent(entity, out PrefabRef prefabRef) && m_PrefabSystem.TryGetPrefab(prefabRef, out PrefabBase prefab)
                ? AssemblyLibrary.FromPrefabName(prefab.name) : null;

        private static LayoutOptions OptionsOf(AssemblyRecord record) =>
            record.Context?.Options?.Type != null && record.Context.Options.Signs != null ? record.Context.Options.Clone() : LayoutOptions.FromLegacy(record.Support);

        /// <summary>
        /// Edit a placed structure: a gantry reopens in the arrangement panel pinned where it stands (Save
        /// rebuilds it there); a roadside sign opens in the designer (its Save rebuilds it in place).
        /// </summary>
        public bool EditPlaced(Entity entity)
        {
            var record = RecordOf(entity);
            if (record == null) return false;
            var options = OptionsOf(record);
            if (options.Type == "ground" || record.Context == null)
            {
                Deactivate();
                m_Replacing = entity;
                m_Options = options;
                m_UI.OpenForEdit(record, "posts");
                m_UI.SetStatus(record.Context == null ? "This sign is from an older version: Save asks for a road click." : "Editing a placed sign: change it and press Save.");
                return true;
            }
            SetSigns(record.Designs);
            m_Options = options;
            m_Options.EnsureSigns(m_Designs.Count);
            m_Replacing = entity;
            m_Pinned = record.Context.ToSnap();
            if (m_Options.Free) m_FreePoint = m_Pinned.Point - new float3(0, m_Options.Elevation, 0);
            m_UI.OpenForEdit(record, "gantry");
            Activate(Mode.Arrange);
            return true;
        }

        /// <summary>Saves a roadside sign edited in the designer, rebuilt where it stands.</summary>
        public bool SavePosts(JObject design)
        {
            if (m_Replacing == Entity.Null || m_Busy) return false;
            var record = RecordOf(m_Replacing);
            var options = record == null ? m_Options : OptionsOf(record);
            if (record?.Context == null)
            {
                BeginPosts(design, options);
                m_UI.SetStatus("This sign is from an older version: click beside its road to place the updated sign.");
                return false;
            }
            SetSigns(new[] { design });
            m_Options = options;
            SignBuilderRuntime.Run(WaitThenBuild(record.Context.ToSnap()));
            return true;
        }

        private IEnumerator WaitThenBuild(RoadProbe.Snap snap)
        {
            var end = UnityEngine.Time.realtimeSinceStartup + 15f;
            while (!FacesReady && UnityEngine.Time.realtimeSinceStartup < end) yield return null;
            Build(snap, "Saved.", afterwards: () => m_Replacing = Entity.Null);
        }

        /// <summary>
        /// Move (the original is removed once placed again) or duplicate a placed structure, keeping its signs
        /// and arrangement: gantries pick a new road, roadside signs a new spot.
        /// </summary>
        public bool PlaceAgain(Entity entity, bool move)
        {
            var record = RecordOf(entity);
            if (record == null) return false;
            var options = OptionsOf(record);
            m_Replacing = move ? entity : Entity.Null;
            if (options.Type == "ground") BeginPosts(record.Designs[0], options);
            else BeginGantry(record.Designs, options);
            m_UI.OpenForEdit(record, options.Type == "ground" ? "posts" : "gantry");
            return true;
        }

        public bool IsReplacing => m_Replacing != Entity.Null;

        /// <summary>Whether the tool is active, and in which mode: "off", "posts", "pickRoad" or "arrange".</summary>
        public string ModeName => !Active ? "off" : m_Mode switch { Mode.Posts => "posts", Mode.PickRoad => "pickRoad", _ => "arrange" };

        /// <summary>The entity under the cursor this frame (test harness).</summary>
        public Entity Hovered { get; private set; }

        public bool Busy => m_Busy;
        public int SignCount => m_Designs.Count;

        // ------------------------------------------------------------------ test hooks

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

        public override PrefabBase GetPrefab() => null;

        public override bool TrySetPrefab(PrefabBase prefab) => false;
    }
}
