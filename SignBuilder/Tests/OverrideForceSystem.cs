using Game;
using Game.Common;
using Game.SceneFlow;
using Unity.Entities;

namespace SignBuilder.Tests
{
    /// <summary>
    /// Spike mode "overrideforce": every road is updated early in each of the first frames of a city load, as a mod
    /// that updates roads when a city loads would (Road Builder does), so the game's override check runs on the
    /// frames where <see cref="OverrideProbeSystem"/> looks for sign prefabs carrying the Overridable flag.
    /// </summary>
    public partial class OverrideForceSystem : GameSystemBase
    {
        public static int ForcedFrames;
        private EntityQuery m_Roads;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Roads = GetEntityQuery(ComponentType.ReadOnly<Game.Net.Edge>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Game.Tools.Temp>());
        }

        protected override void OnUpdate()
        {
            if (SpikeBootstrapSystem.Mode != "overrideforce" || !GameManager.instance.gameMode.IsGame() || ForcedFrames >= 120) return;
            ForcedFrames++;
            EntityManager.AddComponent<Updated>(m_Roads);
        }
    }
}
