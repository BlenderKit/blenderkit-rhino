using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Blendkit.Rhino.Infra;
using Eto.Drawing;
using Eto.Forms;

namespace Blendkit.Rhino.Ui
{
    /// <summary>
    /// Scrollable grid of asset thumbnails. Layout is a TableLayout with one
    /// row per N cells, where N adapts to the panel width. Single click
    /// selects, double click activates (download), drag fires DragStarted.
    ///
    /// Infinite scroll: when the user scrolls near the bottom, NeedMore is
    /// raised so the panel can fetch the next page and call SetHits(append:true).
    /// </summary>
    public class ThumbnailGrid : Scrollable
    {
        private const int ThumbSize = 120;
        // Zero gap between cells — title/author live overlaid on the
        // thumbnail itself, so the cell is fully covered by the image and
        // grid density goes up.
        private const int CellSpacing = 0;

        private readonly List<ThumbCell> _cells = new List<ThumbCell>();
        private ThumbCell _selected;
        // Asset ids the user has bookmarked — fed in by the panel after the
        // /ratings/get_bookmarks task lands. Cells consult this in their
        // overlay-builder to show a heart.
        private HashSet<string> _bookmarkedIds = new HashSet<string>();
        private TableLayout _table;
        private int _columns = 3;
        private bool _needMoreFired;

        public JsonElement? SelectedHit => _selected?.Hit;
        public event EventHandler SelectionChanged;
        public event EventHandler CellActivated;
        public event EventHandler<JsonElement> CellDragStarted;
        public event EventHandler<JsonElement> CellRightClicked;
        // Raised when the user scrolls near the bottom of the current results.
        public event EventHandler NeedMore;
        // True when the logged-in user can download Full-plan assets — flips
        // the cell's lock overlay off. Defaults to false (show locks).
        public bool HasFullPlan { get; private set; }
        public void SetHasFullPlan(bool v)
        {
            HasFullPlan = v;
            foreach (var c in _cells) c.SetLocked(!v && !c.IsFree);
        }

        public ThumbnailGrid()
        {
            Border = BorderType.None;
            Content = BuildEmptyPlaceholder();
            SizeChanged += (s, e) => OnResize();
            Scroll += (s, e) => CheckNeedMore();
        }

        private Control BuildEmptyPlaceholder()
        {
            return new Label
            {
                Text = "Search for something to see thumbnails here.",
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private int ComputeColumns()
        {
            var w = ClientSize.Width;
            if (w <= 0) w = 360;
            // Each cell takes ThumbSize + horizontal spacing.
            var cols = w / (ThumbSize + CellSpacing);
            return Math.Max(1, cols);
        }

        private void OnResize()
        {
            var cols = ComputeColumns();
            if (cols == _columns) return;
            _columns = cols;
            Rebuild();
        }

        public void SetHits(IReadOnlyList<JsonElement> hits, bool append = false)
        {
            if (!append)
            {
                _cells.Clear();
                _selected = null;
                _needMoreFired = false;
            }
            // Build cells for any new hits beyond what we already have, and
            // apply the cached bookmarked-state at the same time so freshly
            // appended cells (infinite scroll) also show their hearts.
            for (int i = _cells.Count; i < hits.Count; i++)
            {
                var cell = new ThumbCell(hits[i], ThumbSize);
                WireCell(cell);
                if (_bookmarkedIds.Contains(cell.AssetId)) cell.SetBookmarked(true);
                // Lock when the server says the user can't download.
                // canDownload is authoritative — it already factors in
                // the active plan, asset purchases, trial windows, etc.
                // (Defaults to true when the field is absent, see
                // ThumbCell ctor.)
                if (!cell.CanDownload) cell.SetLocked(true);
                // If the thumbnail is already on disk (typical when
                // switching to a tab whose results were fetched
                // earlier — the Go client's thumbnail_download task
                // doesn't re-fire for files that exist), load it now
                // so the cell shows the picture, not just the
                // overlays/badges.
                LoadCachedThumbnailIfPresent(cell, hits[i]);
                _cells.Add(cell);
            }
            _columns = ComputeColumns();
            Rebuild();
            // Allow another NeedMore once a fresh page has been folded in.
            if (append) _needMoreFired = false;
        }

        public void SetBookmarkedIds(IEnumerable<string> ids)
        {
            _bookmarkedIds = new HashSet<string>(ids);
            // Tell each cell to re-evaluate its bookmark indicator.
            foreach (var c in _cells) c.SetBookmarked(_bookmarkedIds.Contains(c.AssetId));
        }

        public void UpdateThumbnail(string assetBaseId, string imagePath)
        {
            if (string.IsNullOrEmpty(assetBaseId) || string.IsNullOrEmpty(imagePath)) return;
            foreach (var c in _cells)
            {
                if (c.AssetBaseId == assetBaseId) { c.SetImage(imagePath); return; }
            }
        }

        /// <summary>
        /// If the asset's thumbnail PNG/webp is already on disk (typical
        /// when switching tabs whose results were fetched earlier in the
        /// session), load it into the cell now. Without this the cell
        /// stays blank because the Go client's thumbnail_download task
        /// only fires for *new* fetches — disk-cached thumbs never
        /// re-trigger it.
        /// </summary>
        private static void LoadCachedThumbnailIfPresent(ThumbCell cell, JsonElement hit)
        {
            try
            {
                var tempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "blenderkit_data", "temp");
                if (!Directory.Exists(tempDir)) return;
                string Url(string key) =>
                    hit.TryGetProperty(key, out var u) && u.ValueKind == JsonValueKind.String
                        ? (u.GetString() ?? "") : "";
                // Match the addon's thumbnail-key fallback chain. Server
                // sometimes ships only the small variant; on author
                // hits, only gravatarHash; etc.
                foreach (var key in new[]
                {
                    "thumbnailSmallUrl",       "thumbnailSmallUrlWebp",
                    "thumbnailMiddleUrl",      "thumbnailMiddleUrlWebp",
                    "thumbnailLargeUrl",       "thumbnailLargeUrlNonsquared",
                })
                {
                    var url = Url(key);
                    if (string.IsNullOrEmpty(url)) continue;
                    string fname;
                    try { fname = Path.GetFileName(new Uri(url).AbsolutePath); }
                    catch { continue; }
                    if (string.IsNullOrEmpty(fname)) continue;
                    var path = Path.Combine(tempDir, fname);
                    if (File.Exists(path) && new FileInfo(path).Length > 64)
                    {
                        cell.SetImage(path);
                        return;
                    }
                }
            }
            catch { /* missing thumbs are not fatal */ }
        }

        private void Rebuild()
        {
            if (_cells.Count == 0) { Content = BuildEmptyPlaceholder(); return; }

            var table = new TableLayout();
            table.Spacing = new Size(CellSpacing, CellSpacing);
            table.Padding = new Padding(0);

            for (int i = 0; i < _cells.Count; i += _columns)
            {
                var row = new TableRow();
                for (int c = 0; c < _columns; c++)
                {
                    if (i + c < _cells.Count)
                        row.Cells.Add(new TableCell(_cells[i + c], scaleWidth: true));
                    else
                        // Equal-width filler so columns stay even on last row.
                        row.Cells.Add(new TableCell(new Panel(), scaleWidth: true));
                }
                table.Rows.Add(row);
            }
            // Pusher row keeps thumbs aligned to the top.
            table.Rows.Add(new TableRow { ScaleHeight = true });
            _table = table;
            Content = table;
        }

        private void CheckNeedMore()
        {
            if (_needMoreFired) return;
            var p = ScrollPosition;
            var cs = ClientSize;
            var contentH = ScrollSize.Height;
            if (contentH <= 0) return;
            // Within ~200px of bottom — fetch next page.
            if (p.Y + cs.Height >= contentH - 200)
            {
                _needMoreFired = true;
                NeedMore?.Invoke(this, EventArgs.Empty);
            }
        }

        private void WireCell(ThumbCell cell)
        {
            cell.Clicked += (s, e) =>
            {
                if (_selected != null) _selected.SetSelected(false);
                _selected = cell;
                cell.SetSelected(true);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
            cell.DoubleClicked += (s, e) => CellActivated?.Invoke(this, EventArgs.Empty);
            cell.DragStarted += (s, e) => CellDragStarted?.Invoke(this, cell.Hit);
            cell.RightClicked += (s, e) => CellRightClicked?.Invoke(this, cell.Hit);
        }
    }

    internal class ThumbCell : Panel
    {
        public JsonElement Hit { get; }
        public string AssetBaseId { get; }
        // Server-side asset id (matches what /ratings/get_bookmarks returns).
        public string AssetId { get; }
        // True if the asset is in the Free plan. Used by the grid to decide
        // whether to overlay a 🔒 on cells when the user lacks Full.
        public bool IsFree { get; }
        public bool CanDownload { get; }
        // True when this cell represents an author hit (assetType=="author")
        // rather than a model/material/etc. Author cells: no drag, click
        // re-runs the search filtered to that author.
        public bool IsAuthor { get; }
        public event EventHandler Clicked;
        public event EventHandler DoubleClicked;
        public event EventHandler DragStarted;
        public event EventHandler RightClicked;
        private bool _mouseDown;
        private PointF _mouseDownAt;

        private readonly ImageView _img = new ImageView();
        private readonly Label _lbl = new Label();
        private readonly Label _author = new Label();
        // Heart overlay on bookmarked assets — created in the ctor for asset
        // cells, hidden until SetBookmarked(true) is called.
        private Label _heart;
        private Label _lock;

        public ThumbCell(JsonElement hit, int size)
        {
            Hit = hit;
            AssetBaseId = hit.TryGetProperty("assetBaseId", out var id) ? (id.GetString() ?? "") : "";
            AssetId = hit.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.String
                ? aid.GetString() ?? "" : "";
            IsFree = hit.TryGetProperty("isFree", out var fr)
                     && fr.ValueKind == JsonValueKind.True;
            // canDownload is the authoritative server signal — it
            // already accounts for the user's plan, asset purchases,
            // validator overrides, etc. Default true when missing so we
            // don't draw a stray lock on assets the API shipped without
            // the field (older endpoints).
            CanDownload = !hit.TryGetProperty("canDownload", out var cd)
                          || cd.ValueKind != JsonValueKind.False;
            IsAuthor = hit.TryGetProperty("assetType", out var at)
                       && string.Equals(at.GetString(), "author", StringComparison.OrdinalIgnoreCase);

            _img.Size = new Size(size, size);
            if (IsAuthor) BackgroundColor = BkColors.ActiveBlue;
            var name = hit.TryGetProperty("name", out var nm) ? nm.GetString() : "";
            if (IsAuthor && string.IsNullOrEmpty(name))
            {
                if (hit.TryGetProperty("displayName", out var dn1)) name = dn1.GetString();
                if (string.IsNullOrEmpty(name) && hit.TryGetProperty("fullName", out var fn))
                    name = fn.GetString();
            }
            var prefix = IsAuthor ? "👤 " : "";
            // Title now lives overlaid at the bottom of the thumbnail
            // (created below). _lbl is kept for backwards compat but unused.
            _lbl.Text = prefix + Truncate(name, 22);

            // Score / FREE overlays. For author cells these don't apply.
            Control imgWithBadges = _img;
            if (!IsAuthor)
            {
                // PixelLayout collapses to 0x0 inside DynamicLayout.AddRow
                // unless wrapped in a Panel that pins MinimumSize. Without
                // this the overlays stack on top of an invisible image.
                var pix = new PixelLayout();
                pix.Add(_img, 0, 0);
                // Quality is nested at `ratingsAverage.quality` (1-10 avg).
                // The Blender addon's asset bar reads it from the same path
                // — see asset_bar_op.py: `asset_data["ratingsAverage"]["quality"]`.
                // Top-level `quality` was the wrong field; it's almost always 0.
                double quality = 0;
                if (hit.TryGetProperty("ratingsAverage", out var ra)
                    && ra.ValueKind == JsonValueKind.Object
                    && ra.TryGetProperty("quality", out var qv)
                    && qv.ValueKind == JsonValueKind.Number)
                    quality = qv.GetDouble();
                if (quality > 0)
                {
                    var scoreBadge = new Label
                    {
                        Text = "★ " + quality.ToString("0.#"),
                        BackgroundColor = Color.FromArgb(0, 0, 0, 180),
                        TextColor = Colors.White,
                        Font = new Font(SystemFont.Default, 8),
                    };
                    pix.Add(scoreBadge, 4, 4);
                }
                if (hit.TryGetProperty("isFree", out var f) && f.ValueKind == JsonValueKind.True)
                {
                    var freeBadge = new Label
                    {
                        Text = " FREE ",
                        BackgroundColor = BkColors.FreeBadge,
                        TextColor = Colors.White,
                        Font = new Font(SystemFont.Bold, 8),
                    };
                    pix.Add(freeBadge, size - 38, 4);
                }
                // Reserve the bottom-right corner for a bookmark heart that
                // can be toggled later via SetBookmarked.
                _heart = new Label
                {
                    Text = "❤",
                    TextColor = Color.FromArgb(255, 60, 90),
                    Font = new Font(SystemFont.Default, 14),
                    Visible = false,
                };
                pix.Add(_heart, size - 22, size - 22);

                // Lock overlay for paid assets while the user is on Free.
                _lock = new Label
                {
                    Text = "🔒",
                    TextColor = Colors.White,
                    BackgroundColor = Color.FromArgb(0, 0, 0, 140),
                    Font = new Font(SystemFont.Default, 14),
                    Visible = false,
                };
                // Top-right corner — bottom edge is taken by the title band,
                // and FREE badge is also top-right but only on free assets
                // (mutually exclusive with the lock).
                pix.Add(_lock, size - 24, 4);

                // Title + author band along the bottom — translucent black
                // so it reads against any thumbnail. Two stacked labels;
                // sized at construction so the overlay covers the full width
                // without leaving the thumbnail gap-free in the grid.
                var bandH = 30;
                var band = new Panel
                {
                    BackgroundColor = Color.FromArgb(0, 0, 0, 160),
                    MinimumSize = new Size(size, bandH),
                    Size = new Size(size, bandH),
                };
                var bandLayout = new DynamicLayout
                {
                    Padding = new Padding(4, 1),
                    Spacing = new Size(0, 0),
                };
                var titleOverlay = new Label
                {
                    Text = prefix + Truncate(name, 24),
                    TextColor = Colors.White,
                    Font = new Font(SystemFont.Default, 7.5f),
                };
                bandLayout.AddRow(titleOverlay);
                band.Content = bandLayout;
                pix.Add(band, 0, size - bandH);

                imgWithBadges = new Panel
                {
                    Content = pix,
                    MinimumSize = new Size(size, size),
                    Size = new Size(size, size),
                };
            }

            // The asset's `displayName` is its own pretty title (often the same
            // string as `name`), NOT the author. The author lives in
            // `userDisplayName` at the top level, or in a nested `user`
            // object's `displayName` / `fullName` / `firstName`+`lastName`.
            string authorName = "";
            if (!IsAuthor)
            {
                if (hit.TryGetProperty("userDisplayName", out var udn))
                    authorName = udn.GetString() ?? "";
                if (string.IsNullOrEmpty(authorName)
                    && hit.TryGetProperty("user", out var u)
                    && u.ValueKind == JsonValueKind.Object)
                {
                    if (u.TryGetProperty("displayName", out var dn1))
                        authorName = dn1.GetString() ?? "";
                    if (string.IsNullOrEmpty(authorName)
                        && u.TryGetProperty("fullName", out var fn1))
                        authorName = fn1.GetString() ?? "";
                    if (string.IsNullOrEmpty(authorName)
                        && u.TryGetProperty("firstName", out var first))
                    {
                        authorName = first.GetString() ?? "";
                        if (u.TryGetProperty("lastName", out var last)
                            && !string.IsNullOrEmpty(last.GetString()))
                            authorName = (authorName + " " + last.GetString()).Trim();
                    }
                }
            }
            _author.Text = IsAuthor
                ? "(click to filter)"
                : (string.IsNullOrEmpty(authorName) ? "" : "by " + Truncate(authorName, 20));
            _author.TextAlignment = TextAlignment.Center;
            _author.Width = size;
            _author.TextColor = Color.FromArgb(140, 140, 140);
            _author.Font = new Font(SystemFont.Default, 8);

            // Single-row cell, zero padding — the overlay band carries the
            // title + author so we don't need separate Label rows below the
            // image. Author cells keep _lbl/_author below since their
            // overlay-band code path is skipped above.
            var layout = new DynamicLayout
            {
                Padding = new Padding(0),
                Spacing = new Size(0, 0),
            };
            layout.AddRow(imgWithBadges);
            if (IsAuthor)
            {
                layout.AddRow(_lbl);
                layout.AddRow(_author);
            }
            Content = layout;

            MouseDown += (s, e) =>
            {
                if (e.Buttons == MouseButtons.Alternate)
                {
                    // Right-click → asset detail menu. Don't start a drag.
                    Clicked?.Invoke(this, EventArgs.Empty); // also select
                    RightClicked?.Invoke(this, EventArgs.Empty);
                    return;
                }
                _mouseDown = true;
                _mouseDownAt = e.Location;
                Clicked?.Invoke(this, EventArgs.Empty);
                // Drop the hover preview the moment the user presses a
                // button — they're about to either click or drag, both
                // of which mean "stop floating a giant image over my
                // cursor". Without this hide, the tooltip lingered all
                // the way through a drag-drop into the viewport.
                ThumbnailTooltip.Hide();
            };
            MouseUp += (s, e) => _mouseDown = false;
            MouseMove += (s, e) =>
            {
                if (!_mouseDown) return;
                var dx = e.Location.X - _mouseDownAt.X;
                var dy = e.Location.Y - _mouseDownAt.Y;
                if (dx * dx + dy * dy < 900) return; // 30px threshold
                _mouseDown = false;
                ThumbnailTooltip.Hide();
                DragStarted?.Invoke(this, EventArgs.Empty);
            };
            MouseDoubleClick += (s, e) => DoubleClicked?.Invoke(this, EventArgs.Empty);

            // Hover popup — large thumbnail follows the cursor while
            // it's over the cell. Mirrors the Blender addon's "preview
            // tooltip" UX. Eto.Forms' built-in ToolTip is text-only,
            // so we own a custom borderless Form (ThumbnailTooltip).
            MouseEnter += (s, e) =>
            {
                try
                {
                    var bigPath = ResolveLargeThumbPath();
                    if (string.IsNullOrEmpty(bigPath)) return;
                    var cur = Mouse.Position;
                    // Pass the whole hit element so the tooltip can
                    // build the full Blender-addon-style info bar
                    // (name, ★ rating, FREE/FULL badge, author + avatar).
                    ThumbnailTooltip.Show(Hit, bigPath, (int)cur.X, (int)cur.Y);
                }
                catch (Exception ex) { global::Rhino.RhinoApp.WriteLine("[BlenderKit][thumb hover] " + ex.Message); }
            };
            MouseLeave += (s, e) => ThumbnailTooltip.Hide();
        }

        /// <summary>
        /// Find the largest thumbnail variant that's already cached on
        /// disk. Hover preview wants a bigger image than the in-grid
        /// thumb — try Middle / Large / Original / Square in priority
        /// order. Falls back to the small thumb if nothing else lives
        /// on disk yet.
        /// </summary>
        private string ResolveLargeThumbPath()
        {
            try
            {
                var tempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "blenderkit_data", "temp");
                if (!Directory.Exists(tempDir)) return null;
                string Url(string key) =>
                    Hit.TryGetProperty(key, out var u) && u.ValueKind == JsonValueKind.String
                        ? (u.GetString() ?? "") : "";
                foreach (var key in new[]
                {
                    "thumbnailMiddleUrl",      "thumbnailMiddleUrlWebp",
                    "thumbnailLargeUrl",       "thumbnailLargeUrlNonsquared",
                    "thumbnailSmallUrl",       "thumbnailSmallUrlWebp",
                })
                {
                    var url = Url(key);
                    if (string.IsNullOrEmpty(url)) continue;
                    string fname;
                    try { fname = Path.GetFileName(new Uri(url).AbsolutePath); }
                    catch { continue; }
                    if (string.IsNullOrEmpty(fname)) continue;
                    var path = Path.Combine(tempDir, fname);
                    if (File.Exists(path) && new FileInfo(path).Length > 64)
                        return path;
                }
            }
            catch { }
            return null;
        }

        public void SetBookmarked(bool isBookmarked)
        {
            if (_heart != null) _heart.Visible = isBookmarked;
        }

        public void SetLocked(bool isLocked)
        {
            if (_lock != null) _lock.Visible = isLocked;
        }

        public void SetImage(string path)
        {
            global::Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    if (!File.Exists(path)) return;
                    if (new FileInfo(path).Length < 64) return;
                    _img.Image = new Bitmap(path);
                    _img.Invalidate();
                }
                catch (Exception ex)
                {
                    global::Rhino.RhinoApp.WriteLine($"[BlenderKit][thumb] load failed: {ex.Message}");
                }
            }));
        }

        public void SetSelected(bool selected)
        {
            BackgroundColor = selected ? Color.FromArgb(80, 120, 200, 255) : Colors.Transparent;
        }

        private static string Truncate(string s, int n)
            => (s != null && s.Length > n) ? s.Substring(0, n - 1) + "…" : (s ?? "");
    }
}
