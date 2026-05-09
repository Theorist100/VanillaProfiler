using System;
using HarmonyLib;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Snapshot of graphics-related settings the player can change. Populated lazily
    /// on first access (see <see cref="GraphicsSettingsProbe.EnsureProbed"/>) so the
    /// hot path is never touched. Every field is tri-state — null means "we couldn't
    /// read it", which prevents the recommendation engine from acting on incomplete
    /// information.
    /// </summary>
    public sealed class GraphicsSettingsState
    {
        public bool? IsFullscreenWindowed;
        public bool? MotionBlurEnabled;
        public bool? DepthOfFieldEnabled;
        public bool? VolumetricsEnabled;
        public float? LevelOfDetail;     // 0.10 - 1.00; Paradox recommends 0.75
        public int? MaxFrameLatency;     // 1-3, CS2's "pre-rendered frames" setting
        public bool ProbeAttempted;
    }

    /// <summary>
    /// Reads the player's current CS2 graphics settings via Game.Settings.SharedSettings
    /// reflection. Cached after first call. Cost: ~5-15 ms one-time, never repeated.
    /// All access wrapped in try/catch — failures leave fields null, which the
    /// recommendation engine treats as "show advice, we don't know the state".
    /// </summary>
    public static class GraphicsSettingsProbe
    {
        private static GraphicsSettingsState? s_State;

        public static GraphicsSettingsState State
        {
            get
            {
                EnsureProbed();
                return s_State!;
            }
        }

        public static void EnsureProbed()
        {
            if (s_State != null) return;
            var state = new GraphicsSettingsState { ProbeAttempted = true };
            ProbeAll(state);
            s_State = state;
            ModLog.Info(
                "Graphics probe: " +
                $"FullscreenWindowed={Fmt(state.IsFullscreenWindowed)} " +
                $"MotionBlur={Fmt(state.MotionBlurEnabled)} " +
                $"DepthOfField={Fmt(state.DepthOfFieldEnabled)} " +
                $"Volumetrics={Fmt(state.VolumetricsEnabled)} " +
                $"LOD={(state.LevelOfDetail.HasValue ? state.LevelOfDetail.Value.ToString("F2") : "?")} " +
                $"MaxFrameLatency={(state.MaxFrameLatency.HasValue ? state.MaxFrameLatency.Value.ToString() : "?")}");
        }

        /// <summary>Force a re-read on next access. Call after the player likely changed settings.</summary>
        public static void Invalidate()
        {
            s_State = null;
        }

        private static void ProbeAll(GraphicsSettingsState state)
        {
            object? graphics = TryGetGraphicsSettings();
            if (graphics == null)
            {
                ModLog.Warn("Graphics probe: SharedSettings.instance.graphics not available");
                return;
            }

            var graphicsType = graphics.GetType();
            TryReadDisplayMode(state, graphics, graphicsType);
            TryReadDepthOfFieldMode(state, graphics, graphicsType);
            TryReadMaxFrameLatency(state, graphics, graphicsType);
            TryReadQualitySettingEnabled(state, graphics, graphicsType,
                "Game.Settings.MotionBlurQualitySettings", v => state.MotionBlurEnabled = v);
            TryReadQualitySettingEnabled(state, graphics, graphicsType,
                "Game.Settings.VolumetricsQualitySettings", v => state.VolumetricsEnabled = v);
            TryReadLevelOfDetail(state, graphics, graphicsType);
        }

        // SharedSettings.instance is a static getter that returns
        // GameManager.instance?.settings — null before the game is ready.
        private static object? TryGetGraphicsSettings()
        {
            try
            {
                var sharedType = AccessTools.TypeByName("Game.Settings.SharedSettings");
                if (sharedType == null) return null;

                var instanceProp = AccessTools.Property(sharedType, "instance");
                var shared = instanceProp?.GetValue(null);
                if (shared == null) return null;

                var graphicsProp = AccessTools.Property(shared.GetType(), "graphics");
                return graphicsProp?.GetValue(shared);
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Graphics probe init failed: {ex.Message}");
                return null;
            }
        }

        private static void TryReadDisplayMode(GraphicsSettingsState state, object graphics, Type graphicsType)
        {
            try
            {
                var prop = AccessTools.Property(graphicsType, "displayMode");
                var value = prop?.GetValue(graphics);
                if (value == null) return;
                // CS2's DisplayMode enum has Fullscreen / Windowed / FullScreenWindow.
                // Match by string so we don't have to import the enum type at compile time.
                state.IsFullscreenWindowed = string.Equals(value.ToString(), "FullScreenWindow", StringComparison.Ordinal);
            }
            catch (Exception ex) { ModLog.Warn($"DisplayMode probe failed: {ex.Message}"); }
        }

        private static void TryReadDepthOfFieldMode(GraphicsSettingsState state, object graphics, Type graphicsType)
        {
            try
            {
                var prop = AccessTools.Property(graphicsType, "depthOfFieldMode");
                var value = prop?.GetValue(graphics);
                if (value == null) return;
                // DepthOfFieldMode.Off means it's disabled — anything else is on.
                state.DepthOfFieldEnabled = !string.Equals(value.ToString(), "Off", StringComparison.Ordinal);
            }
            catch (Exception ex) { ModLog.Warn($"DepthOfField probe failed: {ex.Message}"); }
        }

        private static void TryReadMaxFrameLatency(GraphicsSettingsState state, object graphics, Type graphicsType)
        {
            try
            {
                var prop = AccessTools.Property(graphicsType, "maxFrameLatency");
                var value = prop?.GetValue(graphics);
                if (value is int i) state.MaxFrameLatency = i;
            }
            catch (Exception ex) { ModLog.Warn($"MaxFrameLatency probe failed: {ex.Message}"); }
        }

        // GraphicsSettings extends GlobalQualitySettings which has
        // T GetQualitySetting<T>() — we invoke it via reflection with the type of
        // each named QualitySetting (MotionBlur, Volumetrics, LOD).
        private static void TryReadQualitySettingEnabled(
            GraphicsSettingsState state, object graphics, Type graphicsType,
            string qualitySettingTypeName, Action<bool?> assign)
        {
            try
            {
                var qsType = AccessTools.TypeByName(qualitySettingTypeName);
                if (qsType == null) return;

                var qs = InvokeGetQualitySetting(graphics, graphicsType, qsType);
                if (qs == null) return;

                var enabledProp = AccessTools.Property(qs.GetType(), "enabled");
                if (enabledProp == null) return;
                var value = enabledProp.GetValue(qs);
                if (value is bool b) assign(b);
            }
            catch (Exception ex) { ModLog.Warn($"{qualitySettingTypeName} probe failed: {ex.Message}"); }
        }

        private static void TryReadLevelOfDetail(GraphicsSettingsState state, object graphics, Type graphicsType)
        {
            try
            {
                var qsType = AccessTools.TypeByName("Game.Settings.LevelOfDetailQualitySettings");
                if (qsType == null) return;
                var qs = InvokeGetQualitySetting(graphics, graphicsType, qsType);
                if (qs == null) return;
                var prop = AccessTools.Property(qs.GetType(), "levelOfDetail");
                var value = prop?.GetValue(qs);
                if (value is float f) state.LevelOfDetail = f;
            }
            catch (Exception ex) { ModLog.Warn($"LOD probe failed: {ex.Message}"); }
        }

        // Resolve the closed generic GetQualitySetting<T> method and invoke it.
        // GraphicsSettings inherits this from GlobalQualitySettings, so we walk the
        // hierarchy ourselves — AccessTools.FirstMethod uses BindingFlags.DeclaredOnly
        // and would miss inherited generic methods.
        private static object? InvokeGetQualitySetting(object graphics, Type graphicsType, Type qsType)
        {
            var generic = FindGenericGetQualitySetting(graphicsType);
            if (generic == null) return null;
            var closed = generic.MakeGenericMethod(qsType);
            return closed.Invoke(graphics, null);
        }

        private static System.Reflection.MethodInfo? FindGenericGetQualitySetting(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var methods = t.GetMethods(
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.DeclaredOnly);
                foreach (var m in methods)
                {
                    if (!string.Equals(m.Name, "GetQualitySetting", StringComparison.Ordinal)) continue;
                    if (!m.IsGenericMethodDefinition) continue;
                    if (m.GetParameters().Length != 0) continue;
                    return m;
                }
            }
            return null;
        }

        private static string Fmt(bool? v) => v.HasValue ? (v.Value ? "on" : "off") : "?";
    }
}
