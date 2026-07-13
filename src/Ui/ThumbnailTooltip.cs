using System;
using System.IO;
using System.Text.Json;
using Eto.Drawing;
using Eto.Forms;
using Blendkit.Rhino.Infra;

namespace Blendkit.Rhino.Ui
{
    /// <summary>
    /// Single shared image-tooltip Form that displays a large preview
    /// near the cursor while the user hovers a result cell. Layout
    /// mirrors the Blender addon's asset-bar tooltip
    /// (blenderkit/asset_bar/asset_bar_op.py:init_tooltip):
    ///
    ///   ┌────────────────────────────────────────────────┐
    ///   │                                                │
    ///   │              big asset thumbnail               │
    ///   │              (tooltip_image, top)              │
    ///   │                                                │
    ///   ├────────────────────────────────────────────────┤  ← darker info bar
    ///   │ Asset Name                       [avatar]      │
    ///   │ ★ 8/10   FREE                    by Author     │
    ///   └────────────────────────────────────────────────┘
    ///
    /// Eto.Forms' built-in Control.ToolTip is text-only, so this is
    /// a custom borderless top-level Form we show/hide on
    /// MouseEnter / MouseLeave from each ThumbCell.
    /// </summary>
    internal static class ThumbnailTooltip
    {
        // Layout constants — match the addon's proportions roughly.
        // The image is square at ~360px; the info bar is ~80px tall.
        private const int ImageSize = 360;
        private const int InfoBarH  = 80;
        private const int Margin    = 8;
        private const int AvatarSize = 56;

        private static Form _window;
        private static ImageView _thumbView;
        private static Label _nameLabel;
        private static Label _authorLabel;
        private static Label _statsLabel;     // ★ N/10  + plan badge text
        private static Label _planBadge;      // FREE / FULL PLAN
        private static ImageView _avatarView;

        private static void EnsureWindow()
        {
            if (_window != null) return;

            _thumbView = new ImageView { Size = new Size(ImageSize, ImageSize) };

            // Asset name — top-left of info bar.
            _nameLabel = new Label
            {
                Text = "",
                TextColor = Colors.White,
                Font = SystemFonts.Bold(13),
            };
            // Quality + plan line under the name.
            _statsLabel = new Label
            {
                Text = "",
                TextColor = Color.FromArgb(220, 220, 220, 255),
                Font = SystemFonts.Default(9),
            };
            _planBadge = new Label
            {
                Text = "",
                TextColor = Colors.White,
                BackgroundColor = Color.FromArgb(0, 168, 86, 255),
                Font = SystemFonts.Bold(9),
            };
            // Author info — right side of info bar.
            _authorLabel = new Label
            {
                Text = "",
                TextColor = Color.FromArgb(220, 220, 220, 255),
                Font = SystemFonts.Default(9),
                TextAlignment = TextAlignment.Right,
            };
            _avatarView = new ImageView { Size = new Size(AvatarSize, AvatarSize) };

            // Layout matches the addon's tooltip — four "quadrants"
            // in the info bar:
            //   top-left:    asset name (bold)
            //   bottom-left: ★ rating + plan badge
            //   top-right:   "by Author" (right-aligned, top)
            //   bottom-right:avatar (right-aligned, bottom corner)
            //
            // Implemented as a 2×2 TableLayout so each quadrant locks
            // to its corner and the avatar hugs the bottom-right edge
            // instead of getting centered (the Eto default).
            var statsRow = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { _statsLabel, _planBadge },
            };

            // Right-align the author label by giving it explicit
            // alignment + a containing Panel that fills its cell.
            _authorLabel.TextAlignment = TextAlignment.Right;
            _authorLabel.VerticalAlignment = VerticalAlignment.Top;

            // Right-align + bottom-pin the avatar with a StackLayout
            // wrapper. StackLayout with Right alignment + a single
            // child anchors that child to the right edge of its cell.
            var avatarAnchor = new StackLayout
            {
                Orientation = Orientation.Vertical,
                HorizontalContentAlignment = HorizontalAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Bottom,
                Items = { _avatarView },
            };

            var infoBar = new TableLayout
            {
                Spacing = new Size(8, 2),
                Padding = new Padding(Margin, Margin),
                BackgroundColor = Color.FromArgb(0, 0, 0, 220),
                Rows =
                {
                    // Row 1 — name on the left, author by-line top-right.
                    new TableRow(
                        new TableCell(_nameLabel, scaleWidth: true),
                        new TableCell(_authorLabel)),
                    // Row 2 — stats on the left, avatar bottom-right.
                    new TableRow(
                        new TableCell(statsRow, scaleWidth: true),
                        new TableCell(avatarAnchor)) { ScaleHeight = true },
                },
            };

            var outer = new DynamicLayout
            {
                Padding = 0,
                Spacing = new Size(0, 0),
                BackgroundColor = Color.FromArgb(20, 20, 22, 255),
            };
            outer.AddRow(_thumbView);
            outer.AddRow(infoBar);

            _window = new Form
            {
                WindowStyle = WindowStyle.None,
                Topmost = true,
                ShowActivated = false,
                Resizable = false,
                Maximizable = false,
                Minimizable = false,
                ShowInTaskbar = false,
                BackgroundColor = Color.FromArgb(20, 20, 22, 255),
                Padding = new Padding(0),
                Content = outer,
                ClientSize = new Size(ImageSize, ImageSize + InfoBarH),
            };
        }

        /// <summary>
        /// Show the tooltip for the asset described by <paramref name="hit"/>
        /// using <paramref name="thumbnailPath"/> as the big image.
        /// Anchored near (cursorX, cursorY) in physical pixels.
        /// </summary>
        public static void Show(JsonElement hit, string thumbnailPath, int cursorX, int cursorY)
        {
            try
            {
                if (string.IsNullOrEmpty(thumbnailPath) || !File.Exists(thumbnailPath))
                {
                    Hide();
                    return;
                }
                EnsureWindow();

                _thumbView.Image = new Bitmap(thumbnailPath);

                // Asset name.
                string assetName = "";
                if (hit.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String)
                    assetName = dn.GetString() ?? "";
                if (string.IsNullOrEmpty(assetName)
                    && hit.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                    assetName = nm.GetString() ?? "";
                if (assetName.Length > 36) assetName = assetName.Substring(0, 33) + "…";
                _nameLabel.Text = assetName;

                // Quality stars line. Use ratingsAverage.quality (the
                // actual rating); fall back to rating count if zero.
                string statsText = "";
                if (hit.TryGetProperty("ratingsAverage", out var ra) && ra.ValueKind == JsonValueKind.Object
                    && ra.TryGetProperty("quality", out var qv) && qv.ValueKind == JsonValueKind.Number)
                {
                    var q = qv.GetDouble();
                    if (q > 0) statsText = $"★ {q:0.#}/10";
                }
                _statsLabel.Text = statsText;

                // Free / Full plan badge.
                bool isFree = hit.TryGetProperty("isFree", out var fEl)
                              && fEl.ValueKind == JsonValueKind.True;
                _planBadge.Text = isFree ? "  FREE  " : "  FULL  ";
                _planBadge.BackgroundColor = isFree
                    ? BkColors.FreeBadge
                    : BkColors.PurplePrice;

                // Author by-line.
                string authorName = "";
                if (hit.TryGetProperty("userDisplayName", out var udn) && udn.ValueKind == JsonValueKind.String)
                    authorName = udn.GetString() ?? "";
                if (string.IsNullOrEmpty(authorName)
                    && hit.TryGetProperty("author", out var auEl) && auEl.ValueKind == JsonValueKind.Object)
                {
                    if (auEl.TryGetProperty("fullName", out var fn) && fn.ValueKind == JsonValueKind.String)
                        authorName = fn.GetString() ?? "";
                    if (string.IsNullOrEmpty(authorName)
                        && auEl.TryGetProperty("firstName", out var first))
                    {
                        var f = first.GetString() ?? "";
                        var l = auEl.TryGetProperty("lastName", out var last) ? (last.GetString() ?? "") : "";
                        authorName = (f + " " + l).Trim();
                    }
                }
                if (authorName.Length > 28) authorName = authorName.Substring(0, 25) + "…";
                _authorLabel.Text = string.IsNullOrEmpty(authorName) ? "" : "by " + authorName;

                // Author avatar — best-effort from temp dir cache.
                _avatarView.Image = ResolveAvatar(hit);

                // Position near cursor. Default = bottom-right of the
                // pointer. If the popup would run off the screen on
                // the right or bottom, FLIP to the opposite side
                // instead of clamping into place. Clamping puts the
                // popup directly under the cursor, which then enters
                // the popup's region, fires MouseLeave on the cell,
                // hides the popup, fires MouseEnter on the cell
                // again, shows the popup again — i.e. the rapid
                // flicker the user reported when hovering near a
                // screen edge. Flipping leaves the cursor outside the
                // popup, so MouseLeave never fires unintentionally.
                int w = (int)_window.ClientSize.Width;
                int h = (int)_window.ClientSize.Height;
                const int Gap = 16;
                var loc = new Point(cursorX + Gap, cursorY + Gap);
                var screen = Screen.FromPoint(new PointF(cursorX, cursorY));
                if (screen != null)
                {
                    var bounds = screen.Bounds;
                    if (loc.X + w > bounds.Right)
                    {
                        // Flip to the left side of cursor.
                        loc.X = cursorX - w - Gap;
                    }
                    if (loc.Y + h > bounds.Bottom)
                    {
                        // Flip to above the cursor.
                        loc.Y = cursorY - h - Gap;
                    }
                    // Last-resort clamp (covers the case where the
                    // popup is bigger than the screen on either axis).
                    if (loc.X < bounds.Left) loc.X = (int)bounds.Left + 4;
                    if (loc.Y < bounds.Top)  loc.Y = (int)bounds.Top  + 4;
                }
                _window.Location = loc;
                if (!_window.Visible) _window.Show();
            }
            catch (Exception ex)
            {
                global::Rhino.RhinoApp.WriteLine("[Blendkit][tooltip] " + ex.Message);
            }
        }

        public static void Hide()
        {
            try { if (_window != null && _window.Visible) _window.Visible = false; }
            catch { }
        }

        // ---- helpers ----

        private static Bitmap ResolveAvatar(JsonElement hit)
        {
            try
            {
                if (!hit.TryGetProperty("author", out var au) || au.ValueKind != JsonValueKind.Object)
                    return null;
                string ahash = "";
                if (au.TryGetProperty("gravatarHash", out var gh)) ahash = gh.GetString() ?? "";
                if (string.IsNullOrEmpty(ahash)) return null;
                var tempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "blenderkit_data", "temp");
                var grav = Path.Combine(tempDir, ahash + ".jpg");
                if (File.Exists(grav) && new FileInfo(grav).Length > 64)
                    return new Bitmap(grav);
            }
            catch { }
            return null;
        }
    }
}
