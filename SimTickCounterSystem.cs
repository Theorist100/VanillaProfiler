using Game;
using Game.Simulation;
using Unity.Entities;

namespace VanillaProfiler
{
    /// <summary>
    /// Counts simulation ticks for sim throughput measurement.
    /// Runs in SimulationSystemGroup — one call per sim tick.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class SimTickCounterSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            try { ProfilerHost.TryGetHotPath()?.OnSimTick(); }
            catch { /* profiler — never crash game */ }
        }
    }
}
