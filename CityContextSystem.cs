using System;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Simulation;
using Game.Vehicles;
using Unity.Entities;
using UnityEngine;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler
{
    /// <summary>
    /// Refreshes citizen/vehicle/building counts every 5 seconds for the overlay.
    /// Lives in GameSimulationSystemGroup so the queries run after a save is loaded.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class CityContextSystem : GameSystemBase
    {
        private const float REFRESH_INTERVAL = 5.0f;

        private EntityQuery m_CitizenQuery;
        private EntityQuery m_VehicleQuery;
        private EntityQuery m_BuildingQuery;
        private float m_NextRefresh;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitizenQuery = GetEntityQuery(
                ComponentType.ReadOnly<Citizen>(),
                ComponentType.Exclude<Deleted>());
            m_VehicleQuery = GetEntityQuery(
                ComponentType.ReadOnly<Vehicle>(),
                ComponentType.Exclude<Deleted>());
            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.Exclude<Deleted>());
        }

        protected override void OnUpdate()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < m_NextRefresh) return;
            m_NextRefresh = now + REFRESH_INTERVAL;

            if (!ProfilerHost.IsAvailable)
            {
                CityContext.Reset();
                return;
            }

            try
            {
                // CalculateEntityCount triggers a sync point because it Completes any
                // pending writer job on the queried components — the profiler should
                // not introduce its own sync. WithoutFiltering is fine here because the
                // queries have no chunk filters; result is identical, no sync.
                CityContext.Update(
                    m_CitizenQuery.CalculateEntityCountWithoutFiltering(),
                    m_VehicleQuery.CalculateEntityCountWithoutFiltering(),
                    m_BuildingQuery.CalculateEntityCountWithoutFiltering());
            }
            catch (Exception ex)
            {
                ModLog.Warn($"City context refresh failed: {ex}");
                CityContext.Reset();
            }
        }
    }
}
