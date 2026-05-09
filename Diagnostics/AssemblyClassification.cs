using System;
using System.Diagnostics.CodeAnalysis;

namespace VanillaProfiler.Diagnostics
{
    internal static class AssemblyClassification
    {
        [SuppressMessage("Usage", "CA2249:Consider using string.Contains instead of string.IndexOf", Justification = ".NET Framework 4.8 target does not expose string.Contains with StringComparison.")]
        public static AssemblyOrigin DetermineOrigin(string assemblyName, string assemblyPath)
        {
            if (string.IsNullOrEmpty(assemblyPath))
                return AssemblyOrigin.Unknown;

            if (IsTrustedGameAssemblyName(assemblyName) && IsUnderManagedRuntimePath(assemblyPath))
                return AssemblyOrigin.TrustedGame;

            if (IsTrustedUnityAssemblyName(assemblyName) && IsUnderManagedRuntimePath(assemblyPath))
                return AssemblyOrigin.TrustedUnity;

            if (IsFrameworkAssemblyName(assemblyName) && IsUnderManagedRuntimePath(assemblyPath))
                return AssemblyOrigin.TrustedFramework;

            if (assemblyPath.IndexOf("\\Mods\\", StringComparison.OrdinalIgnoreCase) >= 0)
                return AssemblyOrigin.PlayerMod;

            return AssemblyOrigin.Unknown;
        }

        [SuppressMessage("Usage", "CA2249:Consider using string.Contains instead of string.IndexOf", Justification = ".NET Framework 4.8 target does not expose string.Contains with StringComparison.")]
        public static bool IsUnderManagedRuntimePath(string path)
        {
            string normalized = path.Replace('/', '\\');
            return normalized.IndexOf("\\Cities2_Data\\Managed\\", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("\\Reference Assemblies\\Microsoft\\Framework\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsTrustedGameAssemblyName(string assemblyName)
            => HasPrefixSegment(assemblyName, "Game")
                || HasPrefixSegment(assemblyName, "Colossal");

        public static bool IsTrustedUnityAssemblyName(string assemblyName)
            => HasPrefixSegment(assemblyName, "Unity")
                || HasPrefixSegment(assemblyName, "UnityEngine");

        public static bool IsFrameworkAssemblyName(string assemblyName)
            => string.Equals(assemblyName, "mscorlib", StringComparison.Ordinal)
                || string.Equals(assemblyName, "System", StringComparison.Ordinal)
                || HasPrefixSegment(assemblyName, "System")
                || string.Equals(assemblyName, "netstandard", StringComparison.Ordinal)
                || HasPrefixSegment(assemblyName, "Microsoft");

        public static bool HasTrustedRuntimeNamespace(string ns)
            => HasPrefixSegment(ns, "Game")
                || HasPrefixSegment(ns, "Unity")
                || HasPrefixSegment(ns, "UnityEngine")
                || HasPrefixSegment(ns, "Colossal");

        public static bool HasPrefixSegment(string value, string prefix)
        {
            return string.Equals(value, prefix, StringComparison.Ordinal)
                || value.StartsWith(prefix + ".", StringComparison.Ordinal);
        }
    }
}
