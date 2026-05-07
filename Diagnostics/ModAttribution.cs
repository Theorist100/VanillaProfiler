using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.Modding;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Maps a Type to its owning mod (or "Vanilla").
    /// Foundation for player-facing reports — players need to see which mod owns a heavy system.
    /// </summary>
    /// <remarks>
    /// Resolution rules:
    ///   - Namespace starts with Game./Unity./Colossal. → "Vanilla"
    ///   - Namespace starts with VanillaProfiler        → "VanillaProfiler"
    ///   - Otherwise: assembly contains a type implementing IMod → use that mod's display name
    ///   - Fallback: assembly simple name
    /// Harmony Postfix on SystemBase.Update runs on the main thread, so caches are plain dictionaries.
    /// </remarks>
    public static class ModAttribution
    {
        public const string VANILLA = "Vanilla";
        public const string PROFILER = "VanillaProfiler";
        public const string UNKNOWN = "Unknown";

        private static readonly Dictionary<Type, string> s_TypeCache = new();
        private static readonly Dictionary<Assembly, string> s_AssemblyCache = new();

        /// <summary>Returns mod name for a given system type. Never null, never throws.</summary>
        public static string Resolve(Type type)
        {
            if (type == null) return UNKNOWN;
            if (s_TypeCache.TryGetValue(type, out var modName))
                return modName;

            modName = ResolveUncached(type);
            s_TypeCache[type] = modName;
            return modName;
        }

        /// <summary>Clears all caches. Call from Mod.OnDispose so reloads get fresh attribution.</summary>
        public static void Reset()
        {
            s_TypeCache.Clear();
            s_AssemblyCache.Clear();
        }

        /// <summary>
        /// Performs the expensive assembly scan during mod startup/export paths, not
        /// inside the SystemBase.Update Harmony Postfix hot path.
        /// </summary>
        public static void PrewarmLoadedAssemblies()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                ResolveAssembly(asm, allowReflection: true);
        }

        /// <summary>Returns true when the type belongs to the base game (Game/Unity/Colossal namespaces).</summary>
        public static bool IsVanilla(Type type)
        {
            if (type == null) return false;
            var ns = type.Namespace ?? string.Empty;
            return HasPrefixSegment(ns, "Game")
                || HasPrefixSegment(ns, "Unity")
                || HasPrefixSegment(ns, "UnityEngine")
                || HasPrefixSegment(ns, "Colossal");
        }

        /// <summary>Snapshot of all loaded mod names (excluding Vanilla and VanillaProfiler).</summary>
        public static List<string> GetLoadedMods()
        {
            var result = new List<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = TryGetModAssemblyName(asm);
                if (name == VANILLA || name == PROFILER || name == UNKNOWN) continue;
                if (!result.Contains(name)) result.Add(name);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static string ResolveUncached(Type type)
        {
            var ns = type.Namespace ?? string.Empty;

            // Use segment match for parity with IsVanilla / IsVanillaAssemblyName.
            // Plain StartsWith("VanillaProfiler") would steal credit for any third-party
            // mod whose namespace happens to start with the same string (e.g. a hypothetical
            // VanillaProfilerHelper).
            if (HasPrefixSegment(ns, "VanillaProfiler"))
                return PROFILER;

            if (IsVanilla(type))
                return VANILLA;

            return ResolveAssembly(type.Assembly, allowReflection: false);
        }

        private static string ResolveAssembly(Assembly asm, bool allowReflection)
        {
            if (asm == null) return UNKNOWN;
            if (s_AssemblyCache.TryGetValue(asm, out var modName))
                return modName;

            bool cacheable = true;
            if (allowReflection)
                modName = BuildAssemblyName(asm, out cacheable);
            else
                modName = FallbackAssemblyName(asm);
            bool shouldCache = !allowReflection || cacheable;
            if (shouldCache)
                s_AssemblyCache[asm] = modName;
            return modName;
        }

        private static string BuildAssemblyName(Assembly asm, out bool cacheable)
        {
            cacheable = true;
            try
            {
                var modType = FindModType(asm);

                if (modType != null)
                    return modType.Assembly.GetName().Name ?? UNKNOWN;

                string asmName = asm.GetName().Name ?? UNKNOWN;
                return IsVanillaAssemblyName(asmName) ? VANILLA : asmName;
            }
            catch (ReflectionTypeLoadException)
            {
                cacheable = false;
                return FallbackAssemblyName(asm);
            }
            catch (TypeLoadException)
            {
                cacheable = false;
                return FallbackAssemblyName(asm);
            }
            catch (FileNotFoundException)
            {
                cacheable = false;
                return FallbackAssemblyName(asm);
            }
            catch (BadImageFormatException)
            {
                cacheable = false;
                return FallbackAssemblyName(asm);
            }
            catch
            {
                cacheable = false;
                return UNKNOWN;
            }
        }

        private static string FallbackAssemblyName(Assembly asm)
        {
            string asmName = asm?.GetName().Name ?? UNKNOWN;
            return IsVanillaAssemblyName(asmName) ? VANILLA : asmName;
        }

        private static string TryGetModAssemblyName(Assembly asm)
        {
            try
            {
                var modType = FindModType(asm);
                return modType?.Assembly.GetName().Name ?? UNKNOWN;
            }
            catch
            {
                return UNKNOWN;
            }
        }

        private static Type FindModType(Assembly asm)
        {
            if (asm == null) return null;
            return asm.GetTypes()
                .FirstOrDefault(t => !t.IsAbstract && !t.IsInterface && typeof(IMod).IsAssignableFrom(t));
        }

        private static bool IsVanillaAssemblyName(string asmName)
        {
            return HasPrefixSegment(asmName, "Game")
                || HasPrefixSegment(asmName, "Unity")
                || HasPrefixSegment(asmName, "UnityEngine")
                || HasPrefixSegment(asmName, "Colossal")
                || IsFrameworkSystemAssembly(asmName)
                || string.Equals(asmName, "mscorlib", StringComparison.Ordinal);
        }

        private static bool IsFrameworkSystemAssembly(string asmName)
        {
            return string.Equals(asmName, "System", StringComparison.Ordinal)
                || string.Equals(asmName, "System.Core", StringComparison.Ordinal)
                || string.Equals(asmName, "System.Xml", StringComparison.Ordinal)
                || string.Equals(asmName, "System.Xml.Linq", StringComparison.Ordinal)
                || string.Equals(asmName, "System.Data", StringComparison.Ordinal)
                || string.Equals(asmName, "System.Drawing", StringComparison.Ordinal)
                || string.Equals(asmName, "System.Numerics", StringComparison.Ordinal)
                || string.Equals(asmName, "System.Runtime.Serialization", StringComparison.Ordinal)
                || string.Equals(asmName, "System.IO.Compression.FileSystem", StringComparison.Ordinal);
        }

        private static bool HasPrefixSegment(string value, string prefix)
        {
            return string.Equals(value, prefix, StringComparison.Ordinal)
                || value.StartsWith(prefix + ".", StringComparison.Ordinal);
        }
    }
}
