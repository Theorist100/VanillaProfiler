using System;
using UnityEngine;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler.Overlay
{
    /// <summary>
    /// Single source of truth for overlay visuals — Classic Gold palette.
    /// All textures and GUIStyles are created lazily on first OnGUI access
    /// because GUI.skin isn't ready in Awake/Start.
    /// </summary>
    public sealed class OverlayTheme
    {
        // Palette (immutable)
        public static readonly Color BG = new(26f / 255f, 26f / 255f, 46f / 255f, 0.85f);
        public static readonly Color BORDER = new(1f, 215f / 255f, 0f, 1f);
        public static readonly Color BORDER_DIM = new(1f, 215f / 255f, 0f, 0.25f);
        public static readonly Color TEXT_PRIMARY = new(240f / 255f, 240f / 255f, 232f / 255f);
        public static readonly Color TEXT_SECONDARY = new(160f / 255f, 160f / 255f, 144f / 255f);
        public static readonly Color TEXT_MUTED = new(144f / 255f, 128f / 255f, 112f / 255f);
        public static readonly Color ACCENT_GOLD = new(1f, 215f / 255f, 0f);
        public static readonly Color SUCCESS = new(74f / 255f, 222f / 255f, 128f / 255f);
        public static readonly Color WARNING = new(1f, 170f / 255f, 0f);
        public static readonly Color ERROR_RED = new(1f, 68f / 255f, 68f / 255f);

        // Lazily-built resources
        private bool m_Initialized;

        public Texture2D BgTexture { get; private set; } = null!;
        public Texture2D BorderTexture { get; private set; } = null!;
        public Texture2D AccentTexture { get; private set; } = null!;

        public GUIStyle BoxStyle { get; private set; } = null!;
        public GUIStyle HeaderStyle { get; private set; } = null!;
        public GUIStyle SectionStyle { get; private set; } = null!;
        public GUIStyle BodyStyle { get; private set; } = null!;
        public GUIStyle DimStyle { get; private set; } = null!;
        public GUIStyle HintStyle { get; private set; } = null!;
        public GUIStyle BadgeStyle { get; private set; } = null!;
        public GUIStyle ButtonStyle { get; private set; } = null!;
        public GUIStyle TextFieldStyle { get; private set; } = null!;
        public GUIStyle ToggleStyle { get; private set; } = null!;

        // Pre-built coloured variants — referenced by HealthLevel / BottleneckKind to avoid
        // allocating a new GUIStyle every OnGUI frame would add avoidable managed growth
        // to the profiler itself.
        private GUIStyle[] m_HealthStyles = Array.Empty<GUIStyle>();        // indexed by (int)HealthLevel
        private GUIStyle[] m_BottleneckStyles = Array.Empty<GUIStyle>();    // indexed by (int)BottleneckKind

        /// <summary>Must be called from OnGUI before any drawing — GUI.skin is null earlier.</summary>
        public void EnsureInitialized()
        {
            if (m_Initialized) return;

            try
            {
                BuildTextures();
                BuildBaseStyles();
                BuildInputStyles();
                BuildVariantStyles();
                m_Initialized = true;
            }
            catch
            {
                Release();
                throw;
            }
        }

        private void BuildTextures()
        {
            BgTexture = MakeTex(BG);
            BorderTexture = MakeTex(BORDER);
            AccentTexture = MakeTex(ACCENT_GOLD);
        }

        private void BuildBaseStyles()
        {
            BoxStyle = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
            };
            BoxStyle.normal.background = BgTexture;

            HeaderStyle = LabelStyle(13, ACCENT_GOLD, FontStyle.Bold, TextAnchor.MiddleLeft);
            SectionStyle = LabelStyle(11, ACCENT_GOLD, FontStyle.Bold, TextAnchor.UpperLeft);
            BodyStyle = LabelStyle(12, TEXT_PRIMARY, FontStyle.Normal, TextAnchor.UpperLeft);
            DimStyle = LabelStyle(12, TEXT_SECONDARY, FontStyle.Normal, TextAnchor.UpperLeft);
            HintStyle = LabelStyle(10, TEXT_MUTED, FontStyle.Normal, TextAnchor.UpperLeft);
            BadgeStyle = LabelStyle(12, ACCENT_GOLD, FontStyle.Normal, TextAnchor.MiddleCenter);
        }

        private void BuildInputStyles()
        {
            ButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(6, 6, 5, 7),
            };

            TextFieldStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 4, 4, 7),
            };

            ToggleStyle = new GUIStyle(GUI.skin.toggle)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(24, 4, 4, 7),
            };
            SetAllToggleTextColors(TEXT_PRIMARY);
        }

        private void BuildVariantStyles()
        {
            m_HealthStyles = new[]
            {
                BodyStyle,
                BuildColored(SUCCESS),
                BuildColored(WARNING),
                BuildColored(ERROR_RED),
            };
            m_BottleneckStyles = new[]
            {
                BodyStyle,
                BodyStyle,
                BuildColored(WARNING),
                BuildColored(WARNING),
                BuildColored(ERROR_RED),
            };
        }

        private static GUIStyle LabelStyle(int fontSize, Color color, FontStyle fontStyle, TextAnchor alignment)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                fontStyle = fontStyle,
                alignment = alignment,
            };
            style.normal.textColor = color;
            return style;
        }

        private void SetAllToggleTextColors(Color color)
        {
            ToggleStyle.normal.textColor = color;
            ToggleStyle.onNormal.textColor = color;
            ToggleStyle.hover.textColor = color;
            ToggleStyle.onHover.textColor = color;
            ToggleStyle.active.textColor = color;
            ToggleStyle.onActive.textColor = color;
            ToggleStyle.focused.textColor = color;
            ToggleStyle.onFocused.textColor = color;
        }

        public GUIStyle StyleForHealth(HealthLevel level)
        {
            EnsureInitialized();
            int i = (int)level;
            return (uint)i < (uint)m_HealthStyles.Length ? m_HealthStyles[i] : BodyStyle;
        }

        public GUIStyle StyleForBottleneck(BottleneckKind kind)
        {
            EnsureInitialized();
            int i = (int)kind;
            return (uint)i < (uint)m_BottleneckStyles.Length ? m_BottleneckStyles[i] : BodyStyle;
        }

        public void Release()
        {
            BgTexture = DestroyTexture(BgTexture);
            BorderTexture = DestroyTexture(BorderTexture);
            AccentTexture = DestroyTexture(AccentTexture);
            BoxStyle = null!;
            HeaderStyle = null!;
            SectionStyle = null!;
            BodyStyle = null!;
            DimStyle = null!;
            HintStyle = null!;
            BadgeStyle = null!;
            ButtonStyle = null!;
            TextFieldStyle = null!;
            ToggleStyle = null!;
            m_HealthStyles = Array.Empty<GUIStyle>();
            m_BottleneckStyles = Array.Empty<GUIStyle>();
            m_Initialized = false;
        }

        private GUIStyle BuildColored(Color color)
        {
            var s = new GUIStyle(BodyStyle);
            s.normal.textColor = color;
            return s;
        }

        public static Color ColorForHealth(HealthLevel level) => level switch
        {
            HealthLevel.Good => SUCCESS,
            HealthLevel.Ok => WARNING,
            HealthLevel.Poor => ERROR_RED,
            HealthLevel.Unknown => TEXT_PRIMARY,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unhandled HealthLevel"),
        };

        private static Texture2D MakeTex(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        private static Texture2D DestroyTexture(Texture2D? texture)
        {
            if (texture == null) return null!;
            UnityEngine.Object.Destroy(texture);
            return null!;
        }
    }
}
