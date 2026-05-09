using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.Modding;

namespace VanillaProfiler.Diagnostics
{
    /// <summary>
    /// Maps runtime types and assemblies to owning mod identities with evidence.
    /// Harmony Postfix on SystemBase.Update runs on the main thread, so caches are
    /// plain dictionaries.
    /// </summary>
    public static class ModAttribution
    {
        public const string VANILLA = "Vanilla";
        public const string PROFILER = "VanillaProfiler";
        public const string UNKNOWN = "Unknown";

        private static readonly Dictionary<Type, ModIdentity> s_TypeCache = new();
        private static readonly Dictionary<Assembly, ModIdentity> s_AssemblyCache = new();

        public static ModIdentity ResolveIdentity(Type? type)
        {
            if (type == null) return UnknownIdentity();
            if (s_TypeCache.TryGetValue(type, out var cached))
            {
                var refreshed = ResolveUncached(type, allowReflection: false);
                var upgraded = BetterForType(cached, refreshed);
                s_TypeCache[type] = upgraded;
                return upgraded;
            }

            var identity = ResolveUncached(type, allowReflection: false);
            s_TypeCache[type] = identity;
            return identity;
        }

        public static ModIdentity ResolveAssembly(Assembly? asm, bool allowReflection)
        {
            if (asm == null) return UnknownIdentity();
            if (s_AssemblyCache.TryGetValue(asm, out var cached) && !ShouldAttemptUpgrade(cached, allowReflection))
                return cached;

            var identity = BuildAssemblyIdentity(asm, allowReflection);
            if (s_AssemblyCache.TryGetValue(asm, out cached))
                identity = Better(cached, identity);
            s_AssemblyCache[asm] = identity;
            return identity;
        }

        public static PatchOwnerIdentity ResolvePatchOwner(
            MethodInfo? patchMethod,
            string? harmonyOwnerId)
        {
            var assemblyIdentity = ResolveIdentity(patchMethod?.DeclaringType);
            string owner = harmonyOwnerId ?? string.Empty;
            var confidence = assemblyIdentity.Confidence;
            if (confidence == AttributionConfidence.Unknown && !string.IsNullOrEmpty(owner))
                confidence = AttributionConfidence.HarmonyOwnerId;

            return new PatchOwnerIdentity(assemblyIdentity, owner, confidence);
        }

        public static string FormatIdentity(ModIdentity identity)
        {
            if (identity.Confidence == AttributionConfidence.Unknown)
                return UNKNOWN;
            return string.IsNullOrEmpty(identity.DisplayName) ? UNKNOWN : identity.DisplayName;
        }

        public static string FormatPatchOwner(PatchOwnerIdentity owner)
        {
            var identity = owner.PatchAssembly;
            bool hasOwnerId = !string.IsNullOrEmpty(owner.HarmonyOwnerId);
            if (identity.Confidence >= AttributionConfidence.ModType
                || identity.Confidence == AttributionConfidence.ProfilerSelf
                || identity.Confidence == AttributionConfidence.TrustedRuntimeAssembly)
            {
                return FormatIdentity(identity);
            }

            if (identity.Confidence == AttributionConfidence.AssemblyName
                && !string.IsNullOrEmpty(identity.DisplayName)
                && !string.Equals(identity.DisplayName, UNKNOWN, StringComparison.Ordinal))
            {
                return hasOwnerId
                    ? $"{identity.DisplayName} (owner: {owner.HarmonyOwnerId})"
                    : $"{identity.DisplayName} (?)";
            }

            return hasOwnerId ? owner.HarmonyOwnerId : UNKNOWN;
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

            if (s_TypeCache.Count == 0) return;
            var cachedTypes = s_TypeCache.Keys.ToArray();
            for (int i = 0; i < cachedTypes.Length; i++)
            {
                var type = cachedTypes[i];
                var upgraded = ResolveUncached(type, allowReflection: true);
                s_TypeCache[type] = BetterForType(s_TypeCache[type], upgraded);
            }
        }

        public static bool IsVanilla(Type? type) => ResolveIdentity(type).IsVanillaSystemOwner;

        /// <summary>Snapshot of all loaded mod names (excluding Vanilla and VanillaProfiler).</summary>
        public static IReadOnlyList<string> GetLoadedMods()
        {
            var result = new List<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var identity = ResolveAssembly(asm, allowReflection: true);
                if (identity.Origin != AssemblyOrigin.PlayerMod) continue;
                string name = FormatIdentity(identity);
                if (string.Equals(name, UNKNOWN, StringComparison.Ordinal)) continue;
                if (!result.Contains(name)) result.Add(name);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static ModIdentity ResolveUncached(Type type, bool allowReflection)
        {
            string ns = type.Namespace ?? string.Empty;
            string assemblyName = SafeAssemblyName(type.Assembly);
            string assemblyPath = SafeAssemblyPath(type.Assembly);

            if (AssemblyClassification.HasPrefixSegment(ns, PROFILER))
                return new ModIdentity(
                    PROFILER,
                    assemblyName,
                    assemblyPath,
                    AssemblyOrigin.Profiler,
                    AttributionConfidence.ProfilerSelf,
                    isVanillaSystemOwner: false);

            var assemblyIdentity = ResolveAssembly(type.Assembly, allowReflection);
            bool vanillaNamespace = AssemblyClassification.HasTrustedRuntimeNamespace(ns);
            bool isVanillaOwner = assemblyIdentity.IsVanillaSystemOwner && vanillaNamespace;
            if (assemblyIdentity.IsVanillaSystemOwner == isVanillaOwner)
                return assemblyIdentity;

            return new ModIdentity(
                assemblyIdentity.DisplayName,
                assemblyIdentity.AssemblyName,
                assemblyIdentity.AssemblyPath,
                assemblyIdentity.Origin,
                assemblyIdentity.Confidence,
                isVanillaSystemOwner: isVanillaOwner);
        }

        private static ModIdentity BetterForType(ModIdentity current, ModIdentity next)
        {
            if (next.Confidence > current.Confidence)
                return next;

            if (next.Confidence == current.Confidence
                && next.IsVanillaSystemOwner != current.IsVanillaSystemOwner)
            {
                return next;
            }

            return current;
        }

        private static ModIdentity BuildAssemblyIdentity(Assembly asm, bool allowReflection)
        {
            string assemblyName = SafeAssemblyName(asm);
            string assemblyPath = SafeAssemblyPath(asm);

            if (AssemblyClassification.HasPrefixSegment(assemblyName, PROFILER))
            {
                return new ModIdentity(
                    PROFILER,
                    assemblyName,
                    assemblyPath,
                    AssemblyOrigin.Profiler,
                    AttributionConfidence.ProfilerSelf,
                    isVanillaSystemOwner: false);
            }

            var origin = AssemblyClassification.DetermineOrigin(assemblyName, assemblyPath);
            if (origin == AssemblyOrigin.TrustedGame || origin == AssemblyOrigin.TrustedUnity)
            {
                return new ModIdentity(
                    VANILLA,
                    assemblyName,
                    assemblyPath,
                    origin,
                    AttributionConfidence.TrustedRuntimeAssembly,
                    isVanillaSystemOwner: true);
            }

            if (origin == AssemblyOrigin.TrustedFramework)
            {
                return new ModIdentity(
                    assemblyName,
                    assemblyName,
                    assemblyPath,
                    origin,
                    AttributionConfidence.TrustedRuntimeAssembly,
                    isVanillaSystemOwner: false);
            }

            if (allowReflection && TryFindModType(asm, out var modType))
            {
                string display = modType.Assembly.GetName().Name ?? assemblyName;
                return new ModIdentity(
                    string.IsNullOrEmpty(display) ? UNKNOWN : display,
                    assemblyName,
                    assemblyPath,
                    AssemblyOrigin.PlayerMod,
                    AttributionConfidence.ModType,
                    isVanillaSystemOwner: false);
            }

            if (string.IsNullOrEmpty(assemblyName))
                return UnknownIdentity(assemblyPath);

            return new ModIdentity(
                assemblyName,
                assemblyName,
                assemblyPath,
                origin == AssemblyOrigin.Unknown ? AssemblyOrigin.Unknown : AssemblyOrigin.PlayerMod,
                AttributionConfidence.AssemblyName,
                isVanillaSystemOwner: false);
        }

        private static bool TryFindModType(Assembly asm, out Type modType)
        {
            try
            {
                modType = asm.GetTypes()
                    .FirstOrDefault(t => !t.IsAbstract && !t.IsInterface && typeof(IMod).IsAssignableFrom(t))!;
                return modType != null;
            }
            catch (ReflectionTypeLoadException) { modType = null!; return false; }
            catch (TypeLoadException) { modType = null!; return false; }
            catch (FileNotFoundException) { modType = null!; return false; }
            catch (BadImageFormatException) { modType = null!; return false; }
            catch { modType = null!; return false; }
        }

        private static bool ShouldAttemptUpgrade(ModIdentity cached, bool allowReflection)
            => allowReflection && cached.Confidence < AttributionConfidence.ModType
                && cached.Origin != AssemblyOrigin.TrustedGame
                && cached.Origin != AssemblyOrigin.TrustedUnity
                && cached.Origin != AssemblyOrigin.TrustedFramework
                && cached.Origin != AssemblyOrigin.Profiler;

        private static ModIdentity Better(ModIdentity current, ModIdentity next)
            => next.Confidence > current.Confidence ? next : current;

        private static string SafeAssemblyName(Assembly? asm)
            => asm?.GetName().Name ?? string.Empty;

        private static string SafeAssemblyPath(Assembly? asm)
        {
            if (asm == null || asm.IsDynamic) return string.Empty;
            try { return asm.Location ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static ModIdentity UnknownIdentity(string assemblyPath = "")
            => new ModIdentity(
                UNKNOWN,
                string.Empty,
                assemblyPath,
                AssemblyOrigin.Unknown,
                AttributionConfidence.Unknown,
                isVanillaSystemOwner: false);
    }
}
