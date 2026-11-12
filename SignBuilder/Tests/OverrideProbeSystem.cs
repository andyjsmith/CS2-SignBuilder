using Game;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using SignBuilder.Assemblies;
using Unity.Collections;
using Unity.Entities;

namespace SignBuilder.Tests
{
    /// <summary>
    /// Runs right before the game's object override check (Modification5) on every frame of an "override*" spike run:
    /// counts the frames on which a sign prefab carries the Overridable flag while sign objects exist, and whether the
    /// override check runs on those frames (objects or roads updated). Signs hidden by the game in such a window stay
    /// hidden: once the flag is cleared the check skips them for good.
    /// </summary>
    public partial class OverrideProbeSystem : GameSystemBase
    {
        public static int WindowFrames;
        public static int WindowFramesWithCheck;
        public static string FirstWindow = "";
        public static int Frames;

        private Game.Objects.UpdateCollectSystem m_ObjectUpdates;
        private Game.Net.UpdateCollectSystem m_NetUpdates;
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_Prefabs;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ObjectUpdates = World.GetOrCreateSystemManaged<Game.Objects.UpdateCollectSystem>();
            m_NetUpdates = World.GetOrCreateSystemManaged<Game.Net.UpdateCollectSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Prefabs = GetEntityQuery(ComponentType.ReadOnly<ObjectGeometryData>(), ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<StaticObjectData>());
        }

        protected override void OnUpdate()
        {
            if (SpikeBootstrapSystem.Mode == null || !SpikeBootstrapSystem.Mode.StartsWith("override") || !GameManager.instance.gameMode.IsGame()) return;
            Frames++;
            int overridable = 0, ours = 0;
            string example = null;
            using (var entities = m_Prefabs.ToEntityArray(Allocator.Temp))
            {
                foreach (var e in entities)
                {
                    if (!m_PrefabSystem.TryGetPrefab(EntityManager.GetComponentData<PrefabData>(e), out PrefabBase prefab) || !prefab.name.StartsWith("SignBuilder_A_")) continue;
                    ours++;
                    var flags = EntityManager.GetComponentData<ObjectGeometryData>(e).m_Flags;
                    if ((flags & Game.Objects.GeometryFlags.Overridable) == 0) continue;
                    overridable++;
                    example ??= $"{prefab.name} flags={flags} created={EntityManager.HasComponent<Created>(e)}";
                }
            }
            if (overridable == 0) return;
            WindowFrames++;
            var check = m_ObjectUpdates.isUpdated || m_NetUpdates.netsUpdated;
            if (check) WindowFramesWithCheck++;
            if (FirstWindow.Length == 0)
            {
                FirstWindow = $"frame {Frames}: {overridable}/{ours} sign prefabs Overridable, override check this frame={check}, loading={GameManager.instance.isGameLoading}, e.g. {example}";
            }
        }
    }
}
