using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace VanillaProfiler.Diagnostics
{
    public enum AttributionConfidence
    {
        Unknown = 0,
        AssemblyName,
        HarmonyOwnerId,
        TrustedRuntimeAssembly,
        ModType,
        ProfilerSelf,
    }

    public enum AssemblyOrigin
    {
        Unknown = 0,
        TrustedGame,
        TrustedUnity,
        TrustedFramework,
        Profiler,
        PlayerMod,
    }

    [SuppressMessage("Usage", "CA1815:Override equals and operator equals on value types", Justification = "Identity values are cached and formatted, not compared as value objects.")]
    [StructLayout(LayoutKind.Auto)]
    public readonly struct ModIdentity
    {
        public ModIdentity(
            string displayName,
            string assemblyName,
            string assemblyPath,
            AssemblyOrigin origin,
            AttributionConfidence confidence,
            bool isVanillaSystemOwner)
        {
            DisplayName = displayName ?? "Unknown";
            AssemblyName = assemblyName ?? string.Empty;
            AssemblyPath = assemblyPath ?? string.Empty;
            Origin = origin;
            Confidence = confidence;
            IsVanillaSystemOwner = isVanillaSystemOwner;
        }

        public readonly string DisplayName;
        public readonly string AssemblyName;
        public readonly string AssemblyPath;
        public readonly AssemblyOrigin Origin;
        public readonly AttributionConfidence Confidence;
        public readonly bool IsVanillaSystemOwner;
    }

    [SuppressMessage("Usage", "CA1815:Override equals and operator equals on value types", Justification = "Patch owner values are evidence carriers, not compared as value objects.")]
    [StructLayout(LayoutKind.Auto)]
    public readonly struct PatchOwnerIdentity
    {
        public PatchOwnerIdentity(
            ModIdentity patchAssembly,
            string harmonyOwnerId,
            AttributionConfidence confidence)
        {
            PatchAssembly = patchAssembly;
            HarmonyOwnerId = harmonyOwnerId ?? string.Empty;
            Confidence = confidence;
        }

        public readonly ModIdentity PatchAssembly;
        public readonly string HarmonyOwnerId;
        public readonly AttributionConfidence Confidence;
    }
}
