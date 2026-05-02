using Eto.Drawing;

namespace Blendkit.Rhino.Infra
{
    /// <summary>
    /// BlenderKit's UI color palette, ported verbatim from
    /// blenderkit/colors.py. Floats 0-1 → bytes 0-255. Use these instead of
    /// guessed greens/blues so the Rhino plugin looks like the rest of the
    /// BlenderKit family (Blender addon, web gallery).
    /// </summary>
    public static class BkColors
    {
        // Floats here mirror the source so the mapping is obvious. Eto's
        // Color.FromArgb is (r, g, b, a) — NOT System.Drawing's (a, r, g, b)
        // — and getting that wrong tints the entire panel pink (alpha lands
        // in the red slot). Don't reorder these arguments.
        private static Color C(double r, double g, double b, double a) =>
            Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255), (int)(a * 255));

        public static readonly Color TopBarBlue  = C(0.2, 0.25, 0.4, 1.0);
        public static readonly Color White       = C(1.0, 1.0, 1.0, 0.9);
        public static readonly Color Text        = C(0.9, 0.9, 0.9, 0.9);
        public static readonly Color TextDim     = C(0.8, 0.8, 0.8, 0.9);
        public static readonly Color Green       = C(0.9, 1.0, 0.9, 0.6);
        public static readonly Color Red         = C(1.0, 0.5, 0.5, 0.8);
        public static readonly Color Blue        = C(0.8, 0.8, 1.0, 0.8);
        public static readonly Color ActiveBlue  = C(0.7, 0.8, 1.0, 1.0);
        public static readonly Color GreenPrice  = C(0.42, 0.49, 0.19, 1.0); // legacy "free" (dark olive)
        public static readonly Color PurplePrice = C(0.59, 0.05, 0.82, 1.0); // paid
        // Bright green badge used on blenderkit.com's gallery for free
        // assets — much more readable on a thumbnail than the legacy olive.
        public static readonly Color FreeBadge   = C(0.0, 0.66, 0.34, 1.0);

        // Dark-mode chrome — same trap as C(): Eto's Color.FromArgb is
        // (r, g, b, a). The earlier (255, 36, 38, 44) put alpha into red and
        // tinted the whole panel pink. Order: r, g, b, a.
        public static readonly Color DarkBg      = Color.FromArgb( 36,  38,  44, 255);
        public static readonly Color DarkPanelBg = Color.FromArgb( 46,  48,  56, 255);
        // Slightly lighter background for "card" panels in dialogs —
        // gives sections visual separation from the dialog bg without
        // needing borders that Eto.Wpf doesn't always honor.
        public static readonly Color CardBg      = Color.FromArgb( 52,  55,  64, 255);
        public static readonly Color DarkText    = Color.FromArgb(230, 230, 230, 255);
        public static readonly Color DarkDimText = Color.FromArgb(170, 170, 175, 255);
    }
}
