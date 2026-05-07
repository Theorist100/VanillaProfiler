using System.Globalization;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Pure parsing + range validation for SettingsPanel form fields. Separated from
    /// the panel so the rules can be exercised without an IMGUI host. Each validator
    /// returns false with a human-readable message in <c>error</c>; on success
    /// <c>error</c> is null and the parsed value is written to the out parameter.
    /// All numeric parsing is invariant-culture so locale doesn't break decimal input.
    /// </summary>
    internal static class SettingsValidation
    {
        public static bool TryFloatInRange(
            string text, float min, float max, string fieldName,
            out float value, out string error)
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || float.IsNaN(value) || float.IsInfinity(value))
            {
                error = $"{fieldName} must be a number.";
                return false;
            }
            if (value < min || value > max)
            {
                error = $"{fieldName} must be {min}-{max}.";
                return false;
            }
            error = null;
            return true;
        }

        public static bool TryIntInRange(
            string text, int min, int max, string fieldName,
            out int value, out string error)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                error = $"{fieldName} must be a whole number.";
                return false;
            }
            if (value < min || value > max)
            {
                error = $"{fieldName} must be {min}-{max}.";
                return false;
            }
            error = null;
            return true;
        }
    }
}
