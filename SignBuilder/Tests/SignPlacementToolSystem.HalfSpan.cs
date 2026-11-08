using Unity.Entities;

namespace SignBuilder.Tools
{
    /// <summary>Test hooks for half-span gantries (test builds only).</summary>
    public partial class SignPlacementToolSystem
    {
        /// <summary>Adds a road alongside to the pinned gantry's span, as clicking it would; returns its index, or -1.</summary>
        public int AddRoadForTest(Entity edge)
        {
            var road = RoadAlongside(edge);
            if (road > 0 && !m_Options.ExtraRoads.Contains(road))
            {
                m_Options.ExtraRoads.Add(road);
                m_Changed = true;
            }
            return road;
        }
    }
}
