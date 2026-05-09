using System;
using HarmonyLib;

namespace VanillaProfiler.Diagnostics
{
    internal static class GraphicsSettingsReflectionReader
    {
        public static void ReadInto(GraphicsSettingsState state)
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
            TryReadTerrainShadows(state, graphics, graphicsType);
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
                // CS2 display-mode names have drifted across builds. Match by string
                // so we don't have to import the enum type at compile time.
                string name = value.ToString() ?? string.Empty;
                state.IsFullscreenWindowed =
                    string.Equals(name, "FullscreenWindow", StringComparison.Ordinal)
                    || string.Equals(name, "FullScreenWindow", StringComparison.Ordinal)
                    || string.Equals(name, "BorderlessWindow", StringComparison.Ordinal);
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
                string name = value.ToString() ?? string.Empty;
                // DepthOfFieldMode.Off/Disabled means it's disabled — anything else is on.
                state.DepthOfFieldEnabled =
                    !string.Equals(name, "Disabled", StringComparison.Ordinal)
                    && !string.Equals(name, "Off", StringComparison.Ordinal);
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

                if (TryReadBoolMember(qs, "enabled", out bool enabled))
                    assign(enabled);
            }
            catch (Exception ex) { ModLog.Warn($"{qualitySettingTypeName} probe failed: {ex.Message}"); }
        }

        private static void TryReadTerrainShadows(GraphicsSettingsState state, object graphics, Type graphicsType)
        {
            try
            {
                var qsType = AccessTools.TypeByName("Game.Settings.ShadowsQualitySettings")
                    ?? AccessTools.TypeByName("Game.Settings.TerrainQualitySettings");
                if (qsType == null) return;

                var qs = InvokeGetQualitySetting(graphics, graphicsType, qsType);
                if (qs == null) return;

                if (TryReadBoolMember(qs, "terrainCastShadows", out bool castsShadows)
                    || TryReadBoolMember(qs, "castsShadows", out castsShadows)
                    || TryReadBoolMember(qs, "castShadows", out castsShadows)
                    || TryReadBoolMember(qs, "terrainShadows", out castsShadows))
                    state.TerrainShadowsEnabled = castsShadows;
            }
            catch (Exception ex) { ModLog.Warn($"Terrain shadows probe failed: {ex.Message}"); }
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

        private static bool TryReadBoolMember(object target, string memberName, out bool value)
        {
            value = false;
            var type = target.GetType();
            var prop = AccessTools.Property(type, memberName);
            var raw = prop?.GetValue(target);
            if (raw == null)
            {
                var field = AccessTools.Field(type, memberName);
                raw = field?.GetValue(target);
            }

            if (raw is not bool b) return false;
            value = b;
            return true;
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
    }
}
