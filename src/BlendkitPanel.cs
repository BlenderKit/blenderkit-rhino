using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Eto.Forms;
using Eto.Drawing;
using Rhino;
using Blendkit.Rhino.Infra;
using Blendkit.Rhino.Ui;

namespace Blendkit.Rhino
{
    /// <summary>
    /// Dockable panel — the real UI for BlenderKit in Rhino.
    ///
    /// v0.1: search box + asset-type picker + results list + download + import.
    /// Double-click a result to download it; if the server returns a
    /// Rhino-compatible format (.gltf/.glb/.obj/.fbx/.stl/.3dm/…) it's imported
    /// into the active document. .blend files are cached but can't be imported.
    /// </summary>
    [Guid("e2a9c7b0-9d7e-4b5c-a42e-92bb7f3d2f01")]
    public class BlendkitPanel : Panel
    {
        public static Guid PanelId => typeof(BlendkitPanel).GUID;
        // Tracks the most recently-constructed panel instance. Used by the
        // BlenderKitTest* commands to drive a search even when the panel
        // was already open (Panels.OpenPanel is a no-op then, so the
        // constructor's auto-search path doesn't re-fire).
        public static BlendkitPanel ActiveInstance { get; private set; }

        private readonly DropDown _assetType = new DropDown();
        private readonly TextBox _searchBox = new TextBox();
        private readonly Button _searchBtn = new Button { Text = "Search" };
        private readonly Button _recentBtn = new Button { Text = "▾", ToolTip = "Recent searches" };
        private readonly CheckBox _gltfOnly = new CheckBox { Text = "glTF only", Checked = true };
        private readonly CheckBox _freeOnly = new CheckBox { Text = "Free only", Checked = false };
        private readonly CheckBox _animated = new CheckBox { Text = "Animated only", Checked = false };
        private readonly CheckBox _bookmarksOnly = new CheckBox { Text = "My bookmarks", Checked = false };
        // Per-asset-type extras matching blenderkit/ui_panels.py:
        //   MODEL only: geometry_nodes (modifiers:nodes URL token).
        //   HDR only:   true_hdr (trueHDR URL token).
        //   MATERIAL:   procedural radio — Any / Procedural / Texture-based.
        //   Common:     own_only (filter to logged-in user's authored
        //               assets; only meaningful when authenticated).
        private readonly CheckBox _geomNodes  = new CheckBox { Text = "Geometry Nodes", Checked = false };
        private readonly CheckBox _trueHdr    = new CheckBox { Text = "True HDR (linear, 32-bit)", Checked = false };
        private readonly CheckBox _ownOnly    = new CheckBox { Text = "My uploads", Checked = false };
        // Material-only "procedural" picker, three-way:
        //   ANY (default), PROCEDURAL, TEXTURE_BASED.
        private readonly DropDown _procedural = new DropDown();
        private readonly DropDown _resolution = new DropDown();
        private readonly DropDown _order = new DropDown();
        private readonly DropDown _license = new DropDown();
        // Quality slider — BlenderKit's quality_count is 0-10. 0 means
        // "any quality" (filter omitted); any positive value translates to
        // `quality_count_gte:N`.
        private readonly Slider _quality = new Slider
        {
            MinValue = 0, MaxValue = 10, Value = 0,
            TickFrequency = 1, SnapToTick = true,
        };
        private readonly Label _qualityLabel = new Label { Text = "Quality: any" };
        private readonly DropDown _style = new DropDown();
        private readonly DropDown _condition = new DropDown();
        private readonly DropDown _texRes = new DropDown();
        private readonly DropDown _polycount = new DropDown();
        private readonly NumericStepper _designYearMin = new NumericStepper { MinValue = 0, MaxValue = 2100, DecimalPlaces = 0 };
        private readonly NumericStepper _designYearMax = new NumericStepper { MinValue = 0, MaxValue = 2100, DecimalPlaces = 0 };
        // Opt-in toggle for the design-year range — hides the "0 to 0"
        // default and only seeds 1900..now once the user activates it.
        private readonly CheckBox _designYearEnable = new CheckBox { Text = "Design year", Checked = false };
        // Cascading category picker: a button that opens a ContextMenu built
        // dynamically from CategoriesService.TreeForAssetType. Closer to the
        // Blender addon's nested category enum than a single flat dropdown.
        private readonly Button _category = new Button { Text = "All categories ▾" };
        private string _categorySlug = "";
        private readonly Label _status = new Label { Text = "Ready." };
        // Horizontal bar of "filter chips" (e.g. [✕ Free] [✕ Quality 5+]
        // [✕ author: Vilém Duha]). Click ✕ to clear that one filter and
        // re-search. Mirrors the Blender addon's filter-chip strip.
        private readonly StackLayout _chipBar = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Padding = new Padding(0, 2),
        };
        // Validator-only debug widget: shows the last URL sent to the
        // BlenderKit search API. Hidden by default; flipped on once the
        // profile arrives with canEditAllAssets=true (mirrors the Blender
        // addon's profile_is_validator helper).
        private readonly TextBox _searchUrlBox = new TextBox
        {
            ReadOnly = true,
            PlaceholderText = "(no search yet)",
            ToolTip = "Last URL sent to BlenderKit's search API (validator-only)",
            Visible = false,
        };
        private readonly ThumbnailGrid _grid = new ThumbnailGrid();
        private readonly Button _downloadBtn = new Button { Text = "Download & import" };
        // Pagination replaced with infinite scroll — see ThumbnailGrid.NeedMore.
        private readonly Button _loginBtn = new Button { Text = "Login" };
        private readonly Label _profileLabel = new Label { Text = "Not logged in." };
        // Notifications + comments live on the website. Mirroring those in
        // the panel would be a maintenance burden and we want users on the
        // site for that anyway, so we just provide quick links.
        private readonly Button _notifBtn = new Button { Text = "🔔 Notifications", Visible = false };

        private ReportPoller _poller;
        private string _pendingSearchId;
        private string _pendingDownloadId;
        private readonly List<JsonElement> _hits = new List<JsonElement>();
        private string _nextUrl;
        private int _resultCount;
        private string _apiKey = "";
        private bool _loadingMore;
        // List of in-flight drag-and-drop sessions. Each owns its own preview
        // cube + captured drop point + task ids, so multiple downloads can
        // run in parallel and each one's progress cube fills independently.
        private readonly List<ActiveDrop> _drops = new List<ActiveDrop>();
        // .blend → .glb conversions run on the Go client; results arrive via
        // the /report poller. Maps task_id → "what to do with the resulting
        // .glb path".
        private readonly Dictionary<string, Action<string>> _pendingConvertActions
            = new Dictionary<string, Action<string>>();
        // Orphan buffer: convert results that arrive *before* the action has
        // been registered (Go's cache fast-path emits "finished" almost
        // synchronously with the POST returning, racing the action store).
        // Keyed by task_id → resulting .glb path; consumed by ConvertForDrop
        // when it stores its action, so no convert is silently lost.
        private readonly Dictionary<string, string> _orphanedConvertResults
            = new Dictionary<string, string>();

        // Recent queries — persisted to settings, surfaced as a dropdown
        // attached to the search button.
        private readonly List<string> _recentQueries = new List<string>();
        private const int RecentQueriesMax = 12;

        // Search tabs. Each tab has its own query/results/state; swapping
        // tabs swaps the visible UI accordingly. _tabs[0] always exists.
        private readonly List<TabState> _tabs = new List<TabState> { new TabState() };
        private int _activeTab;
        private readonly StackLayout _tabBar = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Padding = new Padding(0, 2),
        };

        // Guard so the panel constructor's "restore last session" UI
        // assignments (which fire SelectedIndexChanged / TextChanged
        // handlers) don't kick off a real search before the port-wait
        // task gets a chance to run. Searches that fire too early get
        // "Go client port not discovered yet" and never reach
        // OnSearchHits — which means the autotest hook's TestQuery
        // never gets consumed and the auto-download silently dies.
        private bool _suppressSearchEvents;

        public BlendkitPanel()
        {
            ActiveInstance = this;
            // Restore saved api_key from prior session, if any.
            var (ak, _) = AuthService.LoadTokens();
            _apiKey = ak;
            // Restore last-session search context: query, asset type, category.
            // Filter checkboxes are deliberately NOT restored so the user
            // doesn't get surprised by stale toggles months later.
            _recentQueries.AddRange(Settings.GetStringList("recent_queries"));
            var lastQuery = Settings.GetString("last_query");
            var lastType = Settings.GetString("last_asset_type", "MODEL");
            _categorySlug = Settings.GetString("last_category_slug");

            _suppressSearchEvents = true;
            BuildUi();
            // Apply restored values now that controls exist.
            _searchBox.Text = lastQuery;
            for (int i = 0; i < _assetType.Items.Count; i++)
            {
                if (string.Equals(_assetType.Items[i].Text, lastType, StringComparison.OrdinalIgnoreCase))
                {
                    _assetType.SelectedIndex = i;
                    break;
                }
            }
            _suppressSearchEvents = false;
            // Category label gets repaired once categories_update lands.
            if (!string.IsNullOrEmpty(_categorySlug))
                _category.Text = _categorySlug + " ▾";

            StartPoller();

            if (!string.IsNullOrEmpty(_apiKey))
            {
                _profileLabel.Text = "Logged in (token loaded).";
                _loginBtn.Text = "Logout";
            }

            // Default search at startup — wait until the Go client port is
            // discovered before firing. The plugin spawns the client on a
            // background task; from a cold start that can be 3-4s.
            Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < 60; i++)
                    {
                        if (Infra.ClientLib.ActivePort != null) break;
                        await Task.Delay(500);
                    }
                    RhinoApp.InvokeOnUiThread((Action)(() =>
                    {
                        try
                        {
                            // Apply the BlenderKitTest* command's seeded state so
                            // the auto-search uses the right asset type and query.
                            // Suppress the cascade of TextChanged /
                            // SelectedIndexChanged → DoSearch fan-outs; we run
                            // exactly one DoSearch below.
                            _suppressSearchEvents = true;
                            if (!string.IsNullOrEmpty(BlendkitPlugIn.TestQuery))
                            {
                                _searchBox.Text = BlendkitPlugIn.TestQuery;
                                var t = BlendkitPlugIn.TestAssetType ?? "MODEL";
                                for (int i = 0; i < _assetType.Items.Count; i++)
                                {
                                    if (string.Equals(_assetType.Items[i].Text, t, StringComparison.OrdinalIgnoreCase))
                                    { _assetType.SelectedIndex = i; break; }
                                }
                            }
                            _suppressSearchEvents = false;
                            DoSearch(null, append: false).ConfigureAwait(false);
                        }
                        catch (Exception uiEx) { BkLog.W("Panel startup search failed: " + uiEx.Message); }
                    }));
                }
                catch (Exception ex) { BkLog.W("Panel port-wait task crashed: " + ex.Message); }
            });
        }

        private void BuildUi()
        {
            _assetType.Items.Add("MODEL");
            _assetType.Items.Add("MATERIAL");
            _assetType.Items.Add("HDR");
            _assetType.Items.Add("PRINTABLE");
            _assetType.SelectedIndex = 0;

            _searchBox.PlaceholderText = "Search assets…  (right-click for recent)";
            _searchBox.KeyDown += (s, e) =>
            {
                if (e.Key == Keys.Enter) { OnSearch(); e.Handled = true; }
            };
            // Live debounced re-search as the user types — keyword takes
            // effect within ~500ms even if Enter / Search isn't pressed.
            _searchBox.TextChanged += (s, e) =>
            {
                if (_suppressSearchEvents) return;
                DebouncedResearch();
            };
            // Right-click on the search box surfaces the recent-queries
            // history. The dedicated dropdown button got removed for
            // space; the menu lives here now so users can still recall
            // an earlier query without retyping it. Eto.TextBox doesn't
            // expose a right-click event directly — we hook it via the
            // broader MouseUp.
            _searchBox.MouseUp += (s, e) =>
            {
                if (e.Buttons == MouseButtons.Alternate)
                {
                    e.Handled = true;
                    ShowRecentQueriesMenu();
                }
            };
            _searchBtn.Click += (s, e) => OnSearch();
            _recentBtn.Click += (s, e) => ShowRecentQueriesMenu();

            _grid.CellActivated += (s, e) =>
            {
                // Double-click on an author chip = filter results by that
                // author. Double-click on an asset = download + import.
                if (_grid.SelectedHit is JsonElement sh && IsAuthorHit(sh))
                    FilterByAuthor(sh);
                else OnDownload();
            };
            _grid.CellDragStarted += (s, hit) =>
            {
                // Authors aren't draggable; they only filter. Suppress drag.
                if (IsAuthorHit(hit)) FilterByAuthor(hit);
                else OnDragStart(hit);
            };
            // Right-click → open the asset details popup directly. The old
            // small "Show details / Bookmark / Open page" intermediate menu
            // was an unnecessary step; everything lives in the popup itself
            // (bookmark, author profile, web, comments, ratings).
            _grid.CellRightClicked += (s, hit) => ShowAssetDetails(hit);
            _grid.NeedMore += (s, e) => OnNeedMore();
            _downloadBtn.Click += (s, e) => OnDownload();
            _loginBtn.Click += (s, e) => OnLoginToggle();

            foreach (var r in new[] { "0.5K", "1K", "2K", "4K", "8K", "ORIGINAL" })
                _resolution.Items.Add(r);
            // Default to 1K — Rhino's render meshes get heavy fast and 2K
            // textures triple the import time and memory footprint without
            // adding visible quality at typical viewport zooms.
            _resolution.SelectedIndex = 1; // 1K

            // Order: BlenderKit accepts these as `+order:<value>` in the URL.
            foreach (var (label, _) in OrderOptions) _order.Items.Add(label);
            _order.SelectedIndex = 0;

            foreach (var (label, _) in LicenseOptions) _license.Items.Add(label);
            _license.SelectedIndex = 0;

            // Quality slider 0..10 mirrors BlenderKit's quality_count range.
            _quality.ValueChanged += (s, e) =>
            {
                _qualityLabel.Text = _quality.Value == 0
                    ? "Quality: any"
                    : $"Quality: {_quality.Value}+";
            };

            foreach (var (label, _) in StyleOptions)            _style.Items.Add(label);
            foreach (var (label, _) in ConditionOptions)        _condition.Items.Add(label);
            foreach (var (label, _) in PolycountOptions)        _polycount.Items.Add(label);
            foreach (var (label, _) in TextureResolutionOptions) _texRes.Items.Add(label);
            _style.SelectedIndex = 0;
            _condition.SelectedIndex = 0;
            // Default polycount cap at 10k. Rhino's viewport gets sluggish
            // fast on heavy meshes, so unfiltered defaults bring up too
            // many assets that crawl when imported. Users can widen via
            // the dropdown when they need higher detail.
            _polycount.SelectedIndex = 1; // "Low (≤10k)"
            _texRes.SelectedIndex = 0;

            // Category dropdown is filled when categories_update lands; until
            // then it's just "(any)".
            RefreshCategoryDropdown();
            _assetType.SelectedIndexChanged += async (s, e) =>
            {
                // Capture the new asset type RIGHT HERE — at the moment
                // of the change — so we have an authoritative value
                // independent of any dropdown-state lag during the
                // subsequent search.
                var newType = CurrentAssetType();
                BkLog.W($"_assetType.SelectedIndexChanged: → {newType} (idx={_assetType.SelectedIndex})");
                RefreshCategoryDropdown();
                ApplyFilterVisibility();
                // Keep the chip bar in sync with the per-asset-type
                // filter gating: a polycount chip lingering from a
                // previous MODEL search would suggest the URL was
                // restricted when actually it wasn't (BuildUrlQuery
                // strips it for HDR/MATERIAL).
                RebuildChipBar();
                if (_suppressSearchEvents)
                {
                    BkLog.W("_assetType.SelectedIndexChanged: search suppressed");
                    return;
                }
                // Squash any pending TextChanged debounce that would
                // otherwise enqueue a redundant search a few hundred
                // milliseconds after this one.
                System.Threading.Interlocked.Increment(ref _researchVersion);
                // Pass the captured type explicitly through DoSearch
                // — no field shared with other search paths means no
                // race condition consuming the pin before we reach
                // the search dispatch.
                await DoSearch(nextUrl: null, append: false, overrideAssetType: newType);
            };
            CategoriesService.Updated += (s, e) =>
                RhinoApp.InvokeOnUiThread((Action)RefreshCategoryDropdown);

            // Single compact row: asset-type · search · search button ·
            // recent · category · filter toggle. Saves three vertical rows.
            // Category and filters use icon-only buttons; the active state
            // shows up in the chip bar below anyway.
            // Icon-only buttons sit in a tight 24px column so the search
            // box keeps as much of the row as possible. Width 24 looks
            // right against Eto/WPF's 30-ish-px default button height.
            const int IconBtnW = 24;
            var filtersToggleBtn = new Button
            {
                Text = "⚙",
                ToolTip = "Filters & import settings",
                MinimumSize = new Eto.Drawing.Size(IconBtnW, 0),
            };
            // Clear-the-query button: tight ✕ button next to the search
            // box. Click to wipe the query and re-run search. Visible
            // even with an empty query (clicking is a no-op then) — it's
            // less surface than the dynamic-show-only-when-text behavior
            // and easier on Eto's layout.
            var clearSearchBtn = new Button
            {
                Text = "✕",
                ToolTip = "Clear search query",
                MinimumSize = new Eto.Drawing.Size(IconBtnW, 0),
            };
            clearSearchBtn.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(_searchBox.Text)) return;
                _searchBox.Text = "";
                OnSearch();
            };
            // Categories button uses the folder icon — also tightened.
            _category.MinimumSize = new Eto.Drawing.Size(IconBtnW, 0);
            // Recent-searches dropdown is removed from the UI per
            // user's request — recent queries still feed the URL
            // builder via _recentQueries (stored in Settings) and are
            // surfaced through the asset-type swap re-search anyway.
            var searchRow = new DynamicLayout();
            searchRow.BeginHorizontal();
            searchRow.Add(_assetType);
            searchRow.Add(_searchBox, true);
            searchRow.Add(clearSearchBtn);
            searchRow.Add(_searchBtn);
            searchRow.Add(_category);
            searchRow.Add(filtersToggleBtn);
            searchRow.EndHorizontal();

            // glTF-only filter is dropped from the UI — we convert .blend
            // locally via Blender, so it's no longer needed.
            _gltfOnly.Checked = false;

            // Make the category button compact: icon + (selected name | "Categories").
            _category.Text = "📁";
            _category.ToolTip = "Browse categories";

            // Expander content: order, license, quality slider, animated, free.
            //
            // Every row goes through advanced.AddRow(<Panel>) where the Panel
            // owns its own inner DynamicLayout. Eto's DynamicLayout renders
            // direct BeginHorizontal/Add/EndHorizontal cells on the outer
            // layout inconsistently in Rhino 8 (only the first cell shows;
            // dropdowns disappear). Wrapping each row in its own Panel +
            // sub-layout sidesteps that completely — same pattern the
            // asset-type-specific rows below already used.
            var advanced = new DynamicLayout();
            advanced.Padding = new Padding(8, 4);
            advanced.Spacing = new Size(6, 4);

            var resRow = new Panel();
            {
                var inner = new DynamicLayout();
                inner.BeginHorizontal();
                inner.Add(new Label { Text = "Resolution:", VerticalAlignment = VerticalAlignment.Center });
                inner.Add(_resolution, true);
                inner.EndHorizontal();
                resRow.Content = inner;
            }
            advanced.AddRow(resRow);

            var orderRow = new Panel();
            {
                var inner = new DynamicLayout();
                inner.BeginHorizontal();
                inner.Add(new Label { Text = "Order:", VerticalAlignment = VerticalAlignment.Center });
                inner.Add(_order, true);
                inner.EndHorizontal();
                orderRow.Content = inner;
            }
            advanced.AddRow(orderRow);

            var licenseRow = new Panel();
            {
                var inner = new DynamicLayout();
                inner.BeginHorizontal();
                inner.Add(new Label { Text = "License:", VerticalAlignment = VerticalAlignment.Center });
                inner.Add(_license, true);
                inner.EndHorizontal();
                licenseRow.Content = inner;
            }
            advanced.AddRow(licenseRow);

            var qualityRow = new Panel();
            {
                var inner = new DynamicLayout();
                inner.BeginHorizontal();
                inner.Add(_qualityLabel, false);
                inner.Add(_quality, true);
                inner.EndHorizontal();
                qualityRow.Content = inner;
            }
            advanced.AddRow(qualityRow);

            var togglesRow = new Panel();
            {
                var inner = new DynamicLayout();
                inner.BeginHorizontal();
                inner.Add(WrapCheck(_freeOnly, "Free only"));
                inner.Add(WrapCheck(_animated, "Animated only"));
                inner.Add(WrapCheck(_bookmarksOnly, "My bookmarks"));
                inner.EndHorizontal();
                togglesRow.Content = inner;
            }
            advanced.AddRow(togglesRow);
            // Wrap each asset-type-specific row in a Panel so we can hide
            // it for asset types where the filter doesn't apply. Mirrors
            // the Blender addon: polycount + condition are model-only,
            // style + texture-resolution apply to model and material, etc.
            _styleConditionRow = new Panel();
            {
                var inner = new DynamicLayout();
                inner.BeginHorizontal();
                inner.Add(new Label { Text = "Style:", VerticalAlignment = VerticalAlignment.Center });
                inner.Add(_style, true);
                inner.Add(_conditionLabel);
                inner.Add(_condition, true);
                inner.EndHorizontal();
                _styleConditionRow.Content = inner;
            }
            advanced.AddRow(_styleConditionRow);

            _polyTextureRow = new Panel();
            {
                var inner = new DynamicLayout();
                inner.BeginHorizontal();
                inner.Add(_polycountLabel);
                inner.Add(_polycount, true);
                inner.Add(new Label { Text = "Texture:", VerticalAlignment = VerticalAlignment.Center });
                inner.Add(_texRes, true);
                inner.EndHorizontal();
                _polyTextureRow.Content = inner;
            }
            advanced.AddRow(_polyTextureRow);
            // Design-year row: gated by an opt-in checkbox so the steppers
            // don't sit at "0 to 0" in the default state. When the user
            // ticks the box we seed reasonable bounds (1900..now).
            _designYearEnable.CheckedChanged += (s, e) =>
            {
                var on = _designYearEnable.Checked == true;
                _designYearMin.Enabled = _designYearMax.Enabled = on;
                if (on)
                {
                    if (_designYearMin.Value <= 0) _designYearMin.Value = 1900;
                    if (_designYearMax.Value <= 0) _designYearMax.Value = DateTime.Now.Year;
                }
                else
                {
                    _designYearMin.Value = 0;
                    _designYearMax.Value = 0;
                }
                if (_suppressSearchEvents) return;
                OnSearch();
            };
            _designYearMin.Enabled = _designYearMax.Enabled = false;

            _designYearRow = new Panel();
            {
                var inner = new DynamicLayout();
                inner.BeginHorizontal();
                inner.Add(WrapCheck(_designYearEnable, "Design year"));
                inner.Add(_designYearMin);
                inner.Add(new Label { Text = "to" });
                inner.Add(_designYearMax);
                inner.EndHorizontal();
                _designYearRow.Content = inner;
            }
            advanced.AddRow(_designYearRow);

            // ---- Per-asset-type extras (matches blenderkit/ui_panels.py) ----

            // MODEL: Geometry Nodes checkbox.
            _modelExtrasRow = new Panel();
            {
                var inner = new DynamicLayout();
                inner.BeginHorizontal();
                inner.Add(WrapCheck(_geomNodes, "Geometry Nodes"));
                inner.EndHorizontal();
                _modelExtrasRow.Content = inner;
            }
            advanced.AddRow(_modelExtrasRow);

            // MATERIAL: Procedural / Texture-based / Any radio (DropDown).
            foreach (var label in new[] { "Any procedural type", "Procedural only", "Texture-based only" })
                _procedural.Items.Add(label);
            _procedural.SelectedIndex = 0;
            _materialProceduralRow = new Panel();
            {
                var inner = new DynamicLayout();
                inner.BeginHorizontal();
                inner.Add(new Label { Text = "Procedural:", VerticalAlignment = VerticalAlignment.Center });
                inner.Add(_procedural, true);
                inner.EndHorizontal();
                _materialProceduralRow.Content = inner;
            }
            advanced.AddRow(_materialProceduralRow);

            // HDR: True HDR checkbox.
            _hdrExtrasRow = new Panel();
            {
                var inner = new DynamicLayout();
                inner.BeginHorizontal();
                inner.Add(WrapCheck(_trueHdr, "True HDR (linear, 32-bit)"));
                inner.EndHorizontal();
                _hdrExtrasRow.Content = inner;
            }
            advanced.AddRow(_hdrExtrasRow);

            // Common: My uploads (login-required).
            _commonOwnOnlyRow = new Panel();
            {
                var inner = new DynamicLayout();
                inner.BeginHorizontal();
                inner.Add(WrapCheck(_ownOnly, "My uploads"));
                inner.EndHorizontal();
                _commonOwnOnlyRow.Content = inner;
            }
            advanced.AddRow(_commonOwnOnlyRow);

            // Plain panel instead of Expander — Expander always renders a
            // header/separator row even when collapsed, eating a line. A
            // Panel.Visible toggle gives us truly zero footprint when off.
            var filtersExpander = new Panel
            {
                Content = advanced,
                Visible = false,
            };
            filtersToggleBtn.Click += (s, e) =>
                filtersExpander.Visible = !filtersExpander.Visible;

            // Auto-research when any of these change. Honor the
            // _suppressSearchEvents guard so bulk state assignments
            // (TriggerTestSearch, ClearAllFilters, LoadActiveTab, etc.)
            // don't fan out one search per cleared control.
            EventHandler<EventArgs> refire = (s, e) =>
            {
                if (_suppressSearchEvents) return;
                OnSearch();
            };
            _order.SelectedIndexChanged += refire;
            _license.SelectedIndexChanged += refire;
            _quality.ValueChanged += refire;
            _category.Click += (s, e) => ShowCategoryMenu();
            _gltfOnly.CheckedChanged += refire;
            // Eto.Wpf CheckBox simply doesn't paint TextColor (its template
            // re-binds the OS theme foreground on every layout pass). The
            // workaround that actually works: blank the CheckBox text and
            // pair it with a separate Label whose TextColor we control.
            // Done elsewhere on construction via WrapCheck(). Here we just
            // make sure the boxes themselves don't carry stale text.
            foreach (var cb in new[] { _freeOnly, _animated, _bookmarksOnly, _designYearEnable, _gltfOnly })
                cb.TextColor = BkColors.DarkText;
            _freeOnly.CheckedChanged += refire;
            _animated.CheckedChanged += refire;
            // _resolution is for the *download* size (1K / 2K / etc.) —
            // not a search filter. Don't re-search when it changes.
            // (User explicitly flagged this; previously we re-fired
            // every search when they picked a download resolution.)
            _bookmarksOnly.CheckedChanged += refire;
            _style.SelectedIndexChanged += refire;
            _condition.SelectedIndexChanged += refire;
            _polycount.SelectedIndexChanged += refire;
            _texRes.SelectedIndexChanged += refire;
            _designYearMin.ValueChanged += refire;
            _designYearMax.ValueChanged += refire;
            _geomNodes.CheckedChanged += refire;
            _trueHdr.CheckedChanged += refire;
            _ownOnly.CheckedChanged += refire;
            _procedural.SelectedIndexChanged += refire;

            _notifBtn.Click += (s, e) =>
                Process.Start(new ProcessStartInfo("https://www.blenderkit.com/profile/notifications/")
                    { UseShellExecute = true });

            var loginRow = new DynamicLayout();
            loginRow.BeginHorizontal();
            loginRow.Add(_profileLabel, true);
            loginRow.Add(_notifBtn);
            loginRow.Add(_loginBtn);
            loginRow.EndHorizontal();

            var layout = new DynamicLayout();
            layout.Padding = new Padding(8);
            layout.Spacing = new Size(0, 6);
            // v0.1 banner removed — version lives in the panel registration
            // name and on hover instead, so the search row sits at the top
            // and saves a row of vertical real estate.
            layout.AddRow(loginRow);
            layout.AddRow(_tabBar);
            // _assetType, _category and the filters toggle live in searchRow
            // now (single-row UI).
            layout.AddRow(searchRow);
            layout.AddRow(filtersExpander);
            layout.AddRow(_chipBar);
            layout.AddRow(_status);
            layout.AddRow(_searchUrlBox);
            layout.Add(_grid, true, true);
            layout.AddRow(_downloadBtn);

            BackgroundColor = BkColors.DarkBg;
            Content = layout;
            ApplyDarkMode(this);
            ApplyFilterVisibility();
            RebuildTabBar();
        }

        /// <summary>
        /// Force every Label we own to white, since they all sit on the dark
        /// panel background. Native input widgets (TextBox/DropDown/etc.)
        /// keep their OS look — they have their own white background.
        /// Re-run after dynamically rebuilding any subtree (chip bar, grid).
        /// </summary>
        private static void ApplyDarkMode(Control root)
        {
            bool IsDefaultDark(Color t)
            {
                if (t == Colors.Transparent || t.A < 0.01f) return true;
                // Default Label / CheckBox text is black across Eto backends.
                return (t.R + t.G + t.B) < 0.05f;
            }
            void Walk(Control c)
            {
                if (c is Label l && IsDefaultDark(l.TextColor))
                    l.TextColor = BkColors.DarkText;
                // CheckBox.TextColor reports a non-default value on Eto.Wpf
                // (it picks up the system theme), so the IsDefaultDark gate
                // would skip these. Set unconditionally — we always want
                // bright text in dark mode.
                if (c is CheckBox cb)
                    cb.TextColor = BkColors.DarkText;
                if (c is Expander ex)
                {
                    // Expander's header is its own Label; recolor it too.
                    // (Header is empty in our use, but keep this for future.)
                }
                if (c is Container container)
                    foreach (var ch in container.Children) Walk(ch);
            }
            Walk(root);
        }

        private async void OnLoginToggle()
        {
            if (!string.IsNullOrEmpty(_apiKey))
            {
                _apiKey = "";
                AuthService.SaveTokens("", "");
                _profileLabel.Text = "Not logged in.";
                _loginBtn.Text = "Login";
                _notifBtn.Visible = false;
                _searchUrlBox.Visible = false;
                _hasFullPlan = false;
                _profileUserId = 0;
                _bookmarkedIds.Clear();
                // Logout invalidates plan-gated visibility and the
                // "My uploads" / "My bookmarks" filters; re-render the
                // grid + filter rows + chip bar to reflect the new
                // anonymous state.
                _grid.SetHasFullPlan(false);
                _grid.SetBookmarkedIds(_bookmarkedIds);
                ApplyFilterVisibility();
                RebuildChipBar();
                SetStatus("Logged out.");
                // Re-run the current search so the lock overlays update
                // (anonymous users see locks on Full-plan assets again).
                OnSearch();
                return;
            }
            try
            {
                SetStatus("Opening BlenderKit login in browser…");
                await AuthService.BeginAsync(Process.GetCurrentProcess().Id, SearchService.AddonVersion);
            }
            catch (Exception ex) { SetStatus("Login error: " + ex.Message); }
        }

        private void StartPoller()
        {
            _poller = new ReportPoller(
                appId: Process.GetCurrentProcess().Id,
                apiKey: _apiKey,
                addonVersion: SearchService.AddonVersion,
                onTask: HandleTask
            );
            _poller.Start();
        }

        // (Label, value to send) pairs. Empty value = "no filter".
        private static readonly (string Label, string Value)[] OrderOptions = new[]
        {
            // "Best match" = smart per-context default (-last_blend_upload
            // for empty queries, _score for keyword searches, -score,_score
            // with a category). Forcing _score on an empty query produces
            // near-random results, so the default has to be the smart fall-
            // through, not the literal _score.
            ("Best match", ""),
            ("Pure relevance (_score)", "_score"),
            ("Recently uploaded", "-last_blend_upload"),
            ("Popular", "-download_count"),
            ("Highest BK score", "-score"),
            ("Free first", "-is_free"),
            ("Quality", "-quality_count"),
        };

        private static readonly (string Label, string Value)[] LicenseOptions = new[]
        {
            ("Any license", ""),
            ("CC0 (public domain)", "cc-zero"),
            ("Royalty Free", "royalty_free"),
        };

        private static readonly (string Label, string Value)[] QualityOptions = new[]
        {
            ("Any", "0"),
            ("3+ ★", "3"),
            ("4+ ★", "4"),
            ("5 ★",  "5"),
        };

        // Style values come straight from BlenderKit's modelStyle enum.
        private static readonly (string Label, string Value)[] StyleOptions = new[]
        {
            ("Any style", ""),
            ("Realistic", "REALISTIC"),
            ("Stylized",  "STYLIZED"),
            ("Cartoon",   "CARTOON"),
            ("Sci-fi",    "SCI-FI"),
            ("Abstract",  "ABSTRACT"),
            ("Other",     "OTHER"),
        };

        private static readonly (string Label, string Value)[] ConditionOptions = new[]
        {
            ("Any condition", ""),
            ("New",   "NEW"),
            ("Used",  "USED"),
            ("Aged",  "AGED"),
            ("Old",   "OLD"),
        };

        // Polycount buckets: store the gte/lte pair as a "min:max" string.
        private static readonly (string Label, string Value)[] PolycountOptions = new[]
        {
            ("Any polycount", ""),
            ("Low (≤10k)",     "0:10000"),
            ("Medium (10k–100k)", "10000:100000"),
            ("High (100k–1M)", "100000:1000000"),
            ("Very high (>1M)", "1000000:0"),
        };

        // Texture resolution: textureResolutionMax_gte
        private static readonly (string Label, string Value)[] TextureResolutionOptions = new[]
        {
            ("Any texture res", "0"),
            ("≥ 512 px",   "512"),
            ("≥ 1024 px",  "1024"),
            ("≥ 2048 px",  "2048"),
            ("≥ 4096 px",  "4096"),
            ("≥ 8192 px",  "8192"),
        };

        /// <summary>
        /// Read the active asset type via the dropdown's index + items
        /// (rather than SelectedValue). Eto.Wpf's SelectedValue can lag
        /// the underlying SelectedIndex during the SelectedIndexChanged
        /// callback — so a search fired from that callback was reading
        /// the *previous* asset type. Reading by index is monotonic.
        /// </summary>
        private string CurrentAssetType()
        {
            int idx = _assetType.SelectedIndex;
            if (idx >= 0 && idx < _assetType.Items.Count)
                return _assetType.Items[idx].Text ?? "MODEL";
            return "MODEL";
        }

        /// <summary>
        /// Show/hide the asset-type-specific filter rows. Mirrors the
        /// Blender addon's per-type panels: polycount + condition only
        /// matter for MODEL/PRINTABLE; style applies to MODEL/MATERIAL;
        /// HDR has no extra filters.
        /// </summary>
        private void ApplyFilterVisibility()
        {
            var at = CurrentAssetType().ToUpperInvariant();
            bool isModelLike = at == "MODEL" || at == "PRINTABLE";
            bool isMaterial  = at == "MATERIAL";
            bool isHdr       = at == "HDR";

            if (_styleConditionRow != null)
            {
                _styleConditionRow.Visible = !isHdr;
                _conditionLabel.Visible = isModelLike;
                _condition.Visible = isModelLike;
            }
            if (_polyTextureRow != null)
            {
                _polyTextureRow.Visible = !isHdr;
                _polycountLabel.Visible = isModelLike;
                _polycount.Visible = isModelLike;
            }
            if (_designYearRow != null)
            {
                _designYearRow.Visible = isModelLike;
            }
            if (_modelExtrasRow != null) _modelExtrasRow.Visible = isModelLike;
            if (_materialProceduralRow != null) _materialProceduralRow.Visible = isMaterial;
            if (_hdrExtrasRow != null) _hdrExtrasRow.Visible = isHdr;
            if (_commonOwnOnlyRow != null)
            {
                // Own-only filter only makes sense when logged in — there's
                // no "self" to scope to otherwise.
                _commonOwnOnlyRow.Visible = !string.IsNullOrEmpty(_apiKey);
            }
        }

        private void RefreshCategoryDropdown()
        {
            // Compact icon-only button when no category, or icon + short
            // label when one is picked. The chip bar shows the full path.
            var at = CurrentAssetType();
            if (!string.IsNullOrEmpty(_categorySlug))
            {
                if (FindCategoryName(at, _categorySlug) is string label)
                    _category.Text = "📁 " + Truncate(label, 14);
                else
                {
                    _categorySlug = "";
                    _category.Text = "📁";
                }
            }
            else
            {
                _category.Text = "📁";
            }
        }

        private static string Truncate(string s, int n)
            => (s != null && s.Length > n) ? s.Substring(0, n - 1) + "…" : (s ?? "");

        /// <summary>Walk the category tree for an asset type to find a node by slug.</summary>
        private static string FindCategoryName(string assetType, string slug)
        {
            string Search(IReadOnlyList<CategoryNode> nodes)
            {
                foreach (var n in nodes)
                {
                    if (n.Slug == slug) return n.Name;
                    var s = Search(n.Children);
                    if (s != null) return s;
                }
                return null;
            }
            return Search(CategoriesService.TreeForAssetType(assetType));
        }

        /// <summary>Build the cascading category menu fresh for the current asset type.</summary>
        private void ShowCategoryMenu()
        {
            var at = CurrentAssetType();
            var tree = CategoriesService.TreeForAssetType(at);
            BkLog.W($"ShowCategoryMenu: assetType={at} tree.Count={tree.Count}");

            var menu = new ContextMenu();
            var anyItem = new ButtonMenuItem { Text = "All categories" };
            anyItem.Click += (s, e) => SelectCategory("", "All categories");
            menu.Items.Add(anyItem);

            if (tree.Count == 0)
            {
                // Empty tree usually means the categories_update task hasn't
                // landed yet — the Go client fetches /api/v1/categories on
                // first /report subscription; on cold-start we can race it.
                // Surface a clear placeholder instead of an unexplained
                // gap below "All categories".
                menu.Items.AddSeparator();
                var placeholder = new ButtonMenuItem
                {
                    Text = "(loading…)",
                    Enabled = false,
                };
                menu.Items.Add(placeholder);
            }
            else
            {
                menu.Items.AddSeparator();
                foreach (var node in tree)
                    menu.Items.Add(BuildCategoryMenuItem(node));
            }
            menu.Show(_category);
        }

        /// <summary>
        /// Right-click context menu on a thumbnail: show details, toggle
        /// bookmark, open author / web links. Mirrors the Blender addon's
        /// asset detail card actions in a discoverable spot.
        /// </summary>
        private void ShowAssetMenu(JsonElement hit)
        {
            var menu = new ContextMenu();
            var details = new ButtonMenuItem { Text = "Show details…" };
            details.Click += (s, e) => ShowAssetDetails(hit);
            menu.Items.Add(details);

            var bookmark = new ButtonMenuItem { Text = "Toggle bookmark" };
            bookmark.Click += (s, e) => ToggleBookmark(hit);
            menu.Items.Add(bookmark);
            menu.Items.AddSeparator();

            var web = new ButtonMenuItem { Text = "Open on blenderkit.com" };
            web.Click += (s, e) => OpenAssetOnWeb(hit);
            menu.Items.Add(web);

            var author = new ButtonMenuItem { Text = "Author profile" };
            author.Click += (s, e) => OpenAuthorOnWeb(hit);
            menu.Items.Add(author);

            menu.Show(_grid);
        }

        private void ShowAssetDetails(JsonElement hit)
        {
            // 4:3 landscape — ~960×720 — matches the addon's wider-than-tall
            // detail card and gives the right column enough room for the
            // text + button stack without the description wrapping into
            // ribbon-thin lines.
            var dlg = new Dialog
            {
                Title = "Asset details",
                ClientSize = new Eto.Drawing.Size(960, 720),
                Padding = new Eto.Drawing.Padding(12),
                Resizable = true,
            };
            string GetS(string key) =>
                hit.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? (v.GetString() ?? "") : "";
            string GetParam(string key)
            {
                if (!hit.TryGetProperty("dictParameters", out var p) || p.ValueKind != JsonValueKind.Object) return "";
                if (!p.TryGetProperty(key, out var v)) return "";
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString() ?? "",
                    JsonValueKind.Number => v.GetDouble().ToString("0.##"),
                    _ => v.ToString(),
                };
            }

            var name = GetS("name");
            // The asset's `displayName` is its own pretty title; the author
            // sits under userDisplayName / nested `user`/`author` objects.
            string authorName = "";
            if (hit.TryGetProperty("userDisplayName", out var udn))
                authorName = udn.GetString() ?? "";
            if (string.IsNullOrEmpty(authorName) && hit.TryGetProperty("user", out var u)
                && u.ValueKind == JsonValueKind.Object)
            {
                if (u.TryGetProperty("fullName", out var fn)) authorName = fn.GetString() ?? "";
                if (string.IsNullOrEmpty(authorName) && u.TryGetProperty("displayName", out var dn))
                    authorName = dn.GetString() ?? "";
            }
            var desc = GetS("description");
            var license = GetS("license");
            var assetType = GetS("assetType");
            var tagList = new System.Collections.Generic.List<string>();
            if (hit.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsEl.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.String) tagList.Add(t.GetString());
            }

            // Geometry-ish facts mirror what the Blender addon's asset card
            // shows. We deliberately drop downloads / score because they aren't
            // meaningful signals for the user picking an asset.
            var rows = new System.Collections.Generic.List<(string K, string V)>();
            void Row(string k, string v) { if (!string.IsNullOrEmpty(v)) rows.Add((k, v)); }

            Row("License", license);
            // Manufacturer + Designer — Rhino users explicitly asked for
            // these (Tags are too noisy for CAD work, and most BlenderKit
            // furniture/lighting/equipment carries proper provenance).
            Row("Manufacturer", GetParam("manufacturer"));
            Row("Designer", GetParam("designer"));
            // Mirror blenderkit/ui_panels.py:3276-3290 — designCollection,
            // designVariant, designYear are all in dictParameters.
            Row("Collection", GetParam("designCollection"));
            Row("Variant", GetParam("designVariant"));
            Row("Style", GetParam("modelStyle"));
            // For materials, materialStyle is the analogue of modelStyle.
            if (string.IsNullOrEmpty(GetParam("modelStyle")))
                Row("Style", GetParam("materialStyle"));
            Row("Condition", GetParam("condition"));
            Row("Design year", GetParam("designYear"));
            Row("Face count", GetParam("faceCount"));
            Row("Vertex count", GetParam("verticesCount"));
            Row("Materials count", GetParam("materialsCount"));
            // Texture resolution — both MAX and MIN come through; show
            // a single "Texture res" with the upper bound (matches the
            // addon's filter UI).
            Row("Texture res", GetParam("textureResolutionMax"));
            // Dimensions — formatted like blenderkit/utils.py:fmt_dimensions
            // (auto-pick m / cm / mm), plus a Rhino-doc-units conversion
            // alongside so Imperial users see feet/inches without doing
            // the math themselves.
            var dims = ReadDimensions(hit);
            if (dims.HasValue)
                Row("Size", FormatDimensionsBlenderStyle(dims.Value)
                    + RhinoUnitsTrailer(dims.Value));
            // filesSize: the search API response stores this divided
            // by 1024 to fit in a 32-bit int (see Blender addon's
            // ui_panels.py:3303 "fs = asset_data['filesSize'] * 1024").
            // We multiply back to get bytes — but cap the multiplier
            // so we don't display a 116MB asset as "116GB" if the
            // server ever switches encodings (a regression observed
            // in the wild). If the raw number already looks
            // byte-sized for the value of textureResolutionMax, treat
            // it as bytes.
            if (hit.TryGetProperty("filesSize", out var fsEl) && fsEl.ValueKind == JsonValueKind.Number)
            {
                double raw = fsEl.GetDouble();
                long fsBytes;
                if (raw > 1024 * 1024 * 100)  // > 100 MB raw → already bytes
                    fsBytes = (long)raw;
                else
                    fsBytes = (long)(raw * 1024.0);
                Row("Original size", FormatBytes(fsBytes));
            }
            // Server-side state. `created` and `lastBlendUpload` come back
            // as ISO strings — show only the date portion to keep the row
            // short; full timestamp is in tooltips on the website.
            string ShortDate(string s) =>
                string.IsNullOrEmpty(s) ? "" : (s.Length >= 10 ? s.Substring(0, 10) : s);
            Row("Uploaded", ShortDate(GetS("lastBlendUpload")));
            if (string.IsNullOrEmpty(GetS("lastBlendUpload")))
                Row("Created", ShortDate(GetS("created")));
            Row("Status", GetS("verificationStatus"));
            // Quality vote count is more useful in the popup than on the
            // thumbnail (where we already show the average).
            if (hit.TryGetProperty("ratingsCount", out var rc) && rc.ValueKind == JsonValueKind.Object
                && rc.TryGetProperty("quality", out var qc)
                && qc.ValueKind == JsonValueKind.Number)
                Row("Quality votes", qc.GetInt32().ToString());

            // ---- Thumbnail (filled later by probing the cached files) ----
            var thumbView = new ImageView { Size = new Size(380, 380) };
            try
            {
                var tempDir = System.IO.Path.Combine(BlendkitPlugIn.DefaultGlobalDir, "temp");
                string Url(string key) =>
                    hit.TryGetProperty(key, out var u) && u.ValueKind == JsonValueKind.String
                        ? (u.GetString() ?? "") : "";
                foreach (var key in new[]
                {
                    "thumbnailMiddleUrl", "thumbnailMiddleUrlWebp",
                    "thumbnailLargeUrl",  "thumbnailLargeUrlNonsquared",
                    "thumbnailSmallUrl",  "thumbnailSmallUrlWebp",
                })
                {
                    var url = Url(key);
                    if (string.IsNullOrEmpty(url)) continue;
                    string fname;
                    try { fname = System.IO.Path.GetFileName(new Uri(url).AbsolutePath); }
                    catch { continue; }
                    var path = System.IO.Path.Combine(tempDir, fname);
                    if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 64)
                    {
                        thumbView.Image = new Bitmap(path);
                        break;
                    }
                }
            }
            catch { /* no big deal if the image isn't cached yet */ }

            // Wraps a control in a "card" panel — slightly lighter
            // background + padding — so dialog sections are visually
            // separated without needing Eto borders (Eto.Wpf's GroupBox
            // / Border rendering is inconsistent enough that a coloured
            // Panel is a more reliable separator).
            Control Card(Control inner, string title = null)
            {
                Control content = inner;
                if (!string.IsNullOrEmpty(title))
                {
                    var dl = new DynamicLayout { Padding = 0, Spacing = new Size(0, 4) };
                    dl.AddRow(new Label
                    {
                        Text = title,
                        Font = SystemFonts.Bold(),
                        TextColor = BkColors.DarkText,
                    });
                    dl.AddRow(inner);
                    content = dl;
                }
                return new Panel
                {
                    Content = content,
                    BackgroundColor = BkColors.CardBg,
                    Padding = new Padding(8),
                };
            }

            // ---- LEFT COLUMN: thumbnail (top) + ratings (below) ----
            var leftCol = new DynamicLayout
            {
                Padding = 0,
                Spacing = new Size(0, 8),
            };
            // Thumbnail card — purely visual separation, no title.
            leftCol.AddRow(Card(thumbView));
            // Rating widgets only useful when logged in. BlenderKit takes
            // quality on a 1-10 scale and working_hours as a per-asset-
            // type preset list; the user enters these once and it
            // persists server-side. Layout matches the Blender addon:
            // a row of 10 ★ buttons (click to set + auto-submit) for
            // quality, plus a row of preset hour buttons for
            // working_hours.
            if (!string.IsNullOrEmpty(_apiKey))
            {
                var assetIdForRating = GetS("id");
                // Build the ratings content into its own DynamicLayout
                // so we can wrap the whole thing in a single Card with
                // a "Your ratings" title.
                var ratingsCol = new DynamicLayout
                {
                    Padding = 0,
                    Spacing = new Size(0, 6),
                };

                // -- Quality: 10 star buttons --
                ratingsCol.AddRow(new Label
                {
                    Text = "Rate Quality:",
                    TextColor = BkColors.DarkDimText,
                });
                var starButtons = new Button[10];
                var starsRow = new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 1,
                };
                int currentQuality = 0;
                void RefreshStars()
                {
                    for (int i = 0; i < 10; i++)
                        starButtons[i].Text = (i < currentQuality) ? "★" : "☆";
                }
                for (int i = 0; i < 10; i++)
                {
                    int rating = i + 1; // 1-based
                    var b = new Button
                    {
                        Text = "☆",
                        ToolTip = $"Rate {rating}/10",
                        MinimumSize = new Eto.Drawing.Size(28, 0),
                    };
                    b.Click += async (s, e) =>
                    {
                        currentQuality = rating;
                        RefreshStars();
                        try
                        {
                            await RatingsService.SendQualityAsync(assetIdForRating, rating, _apiKey);
                            SetStatus($"Quality rating {rating}/10 sent.");
                        }
                        catch (Exception ex) { SetStatus("Quality rating error: " + ex.Message); }
                    };
                    starButtons[i] = b;
                    starsRow.Items.Add(b);
                }
                ratingsCol.AddRow(starsRow);

                // -- Working hours: preset buttons (matches addon) --
                ratingsCol.AddRow(new Label
                {
                    Text = "Rate Complexity (hours of work):",
                    TextColor = BkColors.DarkDimText,
                });
                // Asset-type-specific complexity-rating presets,
                // mirroring blenderkit/ratings_utils.py:wh_enum_callback.
                //   MODEL/SCENE/PRINTABLE/NODEGROUP — full 0.5..250 range
                //   HDR — 1..10 (HDRs aren't time-consuming to make)
                //   MATERIAL/BRUSH — 0.2..5 (small effort scale)
                var atL = (assetType ?? "model").ToLowerInvariant();
                double[] hoursPresets;
                if (atL == "hdr")
                    hoursPresets = new[] { 1d, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
                else if (atL == "material" || atL == "brush")
                    hoursPresets = new[] { 0.2, 0.5, 1, 2, 3, 4, 5 };
                else // model / scene / printable / nodegroup / fallback
                    hoursPresets = new[] { 0.5, 1d, 2, 3, 4, 5, 6, 8, 10, 15, 20, 30, 50, 100, 150, 200, 250 };
                var hoursRow1 = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 1 };
                var hoursRow2 = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 1 };
                int splitAt = hoursPresets.Length / 2 + 1;
                for (int i = 0; i < hoursPresets.Length; i++)
                {
                    double v = hoursPresets[i];
                    var label = v == Math.Floor(v) ? ((int)v).ToString() : v.ToString("0.#");
                    var b = new Button
                    {
                        Text = label,
                        ToolTip = $"{v} hour{(v == 1 ? "" : "s")} of work",
                        MinimumSize = new Eto.Drawing.Size(32, 0),
                    };
                    b.Click += async (s, e) =>
                    {
                        try
                        {
                            await RatingsService.SendWorkingHoursAsync(assetIdForRating, v, _apiKey);
                            SetStatus($"Working hours {v} sent.");
                        }
                        catch (Exception ex) { SetStatus("Working hours error: " + ex.Message); }
                    };
                    (i < splitAt ? hoursRow1 : hoursRow2).Items.Add(b);
                }
                ratingsCol.AddRow(hoursRow1);
                if (hoursRow2.Items.Count > 0) ratingsCol.AddRow(hoursRow2);
                leftCol.AddRow(Card(ratingsCol, "Your ratings"));
            }

            // ---- RIGHT-TOP-LEFT: title + description + structured data ----
            var dataCol = new DynamicLayout { Padding = 0, Spacing = new Size(0, 4) };
            // Title row: asset name (left) + Free/Full plan badge (right).
            // The Blender addon shows a coloured access badge in the
            // top-right corner of the popup; we surface the same info
            // inline next to the title so it's the first thing the
            // user reads.
            bool hitIsFree = hit.TryGetProperty("isFree", out var fEl)
                            && fEl.ValueKind == JsonValueKind.True;
            var titleRow = new DynamicLayout();
            titleRow.BeginHorizontal();
            titleRow.Add(new Label
            {
                Text = name,
                Font = SystemFonts.Bold(14),
                TextColor = BkColors.DarkText,
            }, true);
            var planBadge = new Label
            {
                Text = hitIsFree ? "  FREE  " : "  FULL PLAN  ",
                BackgroundColor = hitIsFree ? BkColors.FreeBadge : BkColors.PurplePrice,
                TextColor = global::Eto.Drawing.Colors.White,
                Font = SystemFonts.Bold(9),
            };
            titleRow.Add(planBadge);
            titleRow.EndHorizontal();
            dataCol.AddRow(titleRow);

            // Stats line: downloads · rating count · score
            // (the small icons-with-numbers row in the addon).
            try
            {
                var stats = new System.Collections.Generic.List<string>();
                if (hit.TryGetProperty("downloadCount", out var dc) && dc.ValueKind == JsonValueKind.Number)
                    stats.Add($"⤓ {dc.GetInt32()} downloads");
                if (hit.TryGetProperty("ratingsCount", out var rcEl) && rcEl.ValueKind == JsonValueKind.Object
                    && rcEl.TryGetProperty("quality", out var qcEl) && qcEl.ValueKind == JsonValueKind.Number)
                    stats.Add($"★ {qcEl.GetInt32()} ratings");
                if (hit.TryGetProperty("score", out var scEl) && scEl.ValueKind == JsonValueKind.Number
                    && scEl.GetDouble() > 0)
                    stats.Add($"trophy {scEl.GetInt32()}");
                if (stats.Count > 0)
                {
                    dataCol.AddRow(new Label
                    {
                        Text = string.Join("   ", stats),
                        TextColor = BkColors.DarkDimText,
                    });
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(desc))
            {
                // Wrap-aware Label keeps the look consistent with the
                // surrounding dialog instead of the input-field box a
                // ReadOnly TextArea draws.
                var descLabel = new Label
                {
                    Text = desc,
                    Wrap = WrapMode.Word,
                    TextColor = BkColors.DarkText,
                };
                dataCol.AddRow(descLabel);
            }
            // Two-column key/value grid for the structured facts.
            if (rows.Count > 0)
            {
                var t = new TableLayout { Spacing = new Size(8, 2) };
                foreach (var (k, v) in rows)
                {
                    t.Rows.Add(new TableRow(
                        new Label { Text = k + ":", TextColor = Color.FromArgb(150, 150, 150) },
                        new Label { Text = v, TextColor = BkColors.DarkText }));
                }
                dataCol.AddRow(t);
            }

            // ---- RIGHT-TOP-RIGHT: action buttons stack ----
            var actionsCol = new DynamicLayout { Padding = 0, Spacing = new Size(0, 4) };
            var importBtn = new Button { Text = "↓ Download & import" };
            importBtn.Click += (s, e) => { dlg.Close(); OnDownload(); };
            actionsCol.AddRow(importBtn);

            var assetIdForBookmark = GetS("id");
            bool isBookmarked = !string.IsNullOrEmpty(assetIdForBookmark)
                && _bookmarkedIds.Contains(assetIdForBookmark);
            var bookmarkBtn = new Button { Text = isBookmarked ? "❤ Bookmarked" : "♡ Bookmark" };
            bookmarkBtn.Click += async (s, e) =>
            {
                if (string.IsNullOrEmpty(_apiKey))
                {
                    SetStatus("Bookmarking requires login.");
                    return;
                }
                if (string.IsNullOrEmpty(assetIdForBookmark)) return;
                var nowBookmarked = !_bookmarkedIds.Contains(assetIdForBookmark);
                try
                {
                    await RatingsService.SetBookmarkAsync(assetIdForBookmark, nowBookmarked, _apiKey);
                    if (nowBookmarked) _bookmarkedIds.Add(assetIdForBookmark);
                    else _bookmarkedIds.Remove(assetIdForBookmark);
                    bookmarkBtn.Text = nowBookmarked ? "❤ Bookmarked" : "♡ Bookmark";
                    _grid.SetBookmarkedIds(_bookmarkedIds);
                    SetStatus(nowBookmarked ? "Bookmarked." : "Bookmark removed.");
                }
                catch (Exception ex) { SetStatus("Bookmark error: " + ex.Message); }
            };
            actionsCol.AddRow(bookmarkBtn);

            var byAuthorBtn = new Button { Text = "More by this author" };
            byAuthorBtn.Click += (s, e) => { dlg.Close(); FilterByAuthor(hit); };
            actionsCol.AddRow(byAuthorBtn);

            // Search-similar: take the asset's tags and run a search
            // for them. Mirrors the addon's "Search Similar" button —
            // useful for finding visually-related assets when you want
            // alternatives.
            var searchSimilarBtn = new Button { Text = "Search similar" };
            searchSimilarBtn.Click += (s, e) =>
            {
                dlg.Close();
                try
                {
                    var taglist = new System.Collections.Generic.List<string>();
                    if (hit.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tagsEl.EnumerateArray())
                        {
                            if (t.ValueKind != JsonValueKind.String) continue;
                            var tag = t.GetString();
                            if (string.IsNullOrEmpty(tag)) continue;
                            // Skip common noise tags that don't narrow
                            // the search meaningfully.
                            if (tag == "manifold" || tag == "non-manifold" || tag == "uv") continue;
                            taglist.Add(tag);
                            if (taglist.Count >= 4) break;
                        }
                    }
                    var query = string.Join(" ", taglist);
                    if (string.IsNullOrEmpty(query)) query = GetS("name");
                    _searchBox.Text = query;
                    OnSearch();
                }
                catch (Exception ex) { SetStatus("Search-similar error: " + ex.Message); }
            };
            actionsCol.AddRow(searchSimilarBtn);

            var webBtn = new Button { Text = "Open on blenderkit.com" };
            webBtn.Click += (s, e) => OpenAssetOnWeb(hit);
            actionsCol.AddRow(webBtn);

            // Comments button used to live here and just deep-linked to
            // #comments on the asset gallery page. It was a confusing UX
            // (the popup itself can't host comments — that thread is online-
            // only), so it now leaves through "Open on blenderkit.com" only.

            // Close button removed — Rhino dialog windows already get
            // a native ✕ in the title bar plus Esc / Alt+F4 handling,
            // so a duplicate Close button just steals real estate from
            // the action stack.

            // ---- RIGHT-TOP: data | actions side-by-side ----
            // Each column wrapped in its own Card so they read as
            // visually separated panels matching the Blender addon.
            var rightTop = new TableLayout
            {
                Spacing = new Size(12, 0),
                Padding = 0,
                Rows =
                {
                    new TableRow(
                        new TableCell(Card(dataCol), scaleWidth: true),
                        new TableCell(Card(actionsCol, "Actions"))),
                },
            };

            // ---- RIGHT-BOTTOM: author info row ----
            var authorRow = new DynamicLayout { Padding = new Padding(0, 8, 0, 0) };
            authorRow.BeginHorizontal();
            // Gravatar / author avatar. Three sources, in priority:
            //   1. If the Go client has already cached the avatar (from
            //      /profiles/download_gravatar_image), load from temp.
            //   2. If the asset's `author` carries an avatar URL
            //      (avatar128/256/512), download it lazily via HTTP and
            //      update the ImageView when it lands.
            //   3. Otherwise compute the canonical Gravatar URL from
            //      gravatarHash and fetch that.
            var authorThumb = new ImageView { Size = new Size(72, 72) };
            try
            {
                string ahash = "";
                string aurl = "";
                if (hit.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.Object)
                {
                    if (au.TryGetProperty("gravatarHash", out var gh)) ahash = gh.GetString() ?? "";
                    foreach (var k in new[] { "avatar128", "avatar256", "avatar512" })
                    {
                        if (au.TryGetProperty(k, out var avEl)
                            && avEl.ValueKind == JsonValueKind.String)
                        {
                            var avStr = avEl.GetString() ?? "";
                            // BlenderKit avatar urls are sometimes
                            // relative ("/avatar-redirect/...") — make
                            // them absolute against the API host.
                            if (avStr.StartsWith("/")) avStr = "https://www.blenderkit.com" + avStr;
                            if (!string.IsNullOrEmpty(avStr)) { aurl = avStr; break; }
                        }
                    }
                }
                var tempDir = System.IO.Path.Combine(BlendkitPlugIn.DefaultGlobalDir, "temp");
                System.IO.Directory.CreateDirectory(tempDir);
                string cached = !string.IsNullOrEmpty(ahash)
                    ? System.IO.Path.Combine(tempDir, ahash + ".jpg") : null;
                if (cached != null && System.IO.File.Exists(cached)
                    && new System.IO.FileInfo(cached).Length > 64)
                {
                    authorThumb.Image = new Bitmap(cached);
                }
                else
                {
                    string fetchUrl = aurl;
                    if (string.IsNullOrEmpty(fetchUrl) && !string.IsNullOrEmpty(ahash))
                        fetchUrl = $"https://www.gravatar.com/avatar/{ahash}?s=128&d=identicon";
                    if (!string.IsNullOrEmpty(fetchUrl))
                    {
                        var dest = cached ?? System.IO.Path.Combine(tempDir,
                            "avatar_" + Math.Abs(fetchUrl.GetHashCode()) + ".jpg");
                        Task.Run(async () =>
                        {
                            try
                            {
                                using var http = new System.Net.Http.HttpClient();
                                http.DefaultRequestHeaders.UserAgent.ParseAdd("BlenderKitRhino/0.1");
                                var bytes = await http.GetByteArrayAsync(fetchUrl);
                                if (bytes != null && bytes.Length > 64)
                                {
                                    System.IO.File.WriteAllBytes(dest, bytes);
                                    RhinoApp.InvokeOnUiThread((Action)(() =>
                                    {
                                        try { authorThumb.Image = new Bitmap(dest); }
                                        catch (Exception ex) { BkLog.W("avatar bitmap failed: " + ex.Message); }
                                    }));
                                }
                            }
                            catch (Exception ex) { BkLog.W("avatar fetch failed: " + ex.Message); }
                        });
                    }
                }
            }
            catch (Exception ex) { BkLog.W("avatar resolve failed: " + ex.Message); }
            authorRow.Add(authorThumb);
            var authorTextCol = new DynamicLayout { Padding = new Padding(8, 0, 0, 0) };
            if (!string.IsNullOrEmpty(authorName))
            {
                var byBtn = new Button { Text = "by " + authorName + " ▾" };
                byBtn.Click += (s, e) =>
                {
                    var amenu = new ContextMenu();
                    var a1 = new ButtonMenuItem { Text = "More by this author" };
                    a1.Click += (s2, e2) => { dlg.Close(); FilterByAuthor(hit); };
                    amenu.Items.Add(a1);
                    var a2 = new ButtonMenuItem { Text = "Open profile on website" };
                    a2.Click += (s2, e2) => OpenAuthorOnWeb(hit);
                    amenu.Items.Add(a2);
                    amenu.Show(byBtn);
                };
                authorTextCol.AddRow(byBtn);
            }
            // Show the author's "aboutMe" blurb if the API surfaced it
            // (only present on the asset's `author` object when we
            // captured a profile-bound search).
            string aboutMe = "";
            if (hit.TryGetProperty("author", out var aau) && aau.ValueKind == JsonValueKind.Object
                && aau.TryGetProperty("aboutMe", out var am)
                && am.ValueKind == JsonValueKind.String)
                aboutMe = am.GetString() ?? "";
            if (!string.IsNullOrEmpty(aboutMe))
            {
                authorTextCol.AddRow(new Label
                {
                    Text = aboutMe,
                    Wrap = WrapMode.Word,
                    TextColor = BkColors.DarkText,
                });
            }
            authorRow.Add(authorTextCol, xscale: true);
            authorRow.EndHorizontal();

            // ---- RIGHT COLUMN: top split + author below ----
            var rightCol = new DynamicLayout { Padding = 0, Spacing = new Size(0, 8) };
            rightCol.AddRow(rightTop);
            rightCol.AddRow(Card(authorRow, "Author"));

            // ---- OUTER 2-column container ----
            var outer = new TableLayout
            {
                Spacing = new Size(12, 0),
                Padding = new Padding(8),
                Rows =
                {
                    new TableRow(
                        new TableCell(leftCol),
                        new TableCell(rightCol, scaleWidth: true)),
                },
            };

            dlg.Content = outer;
            dlg.BackgroundColor = BkColors.DarkBg;
            dlg.ShowModal(this);
        }

        private async void ToggleBookmark(JsonElement hit)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                SetStatus("Bookmarking requires login.");
                return;
            }
            var assetId = hit.TryGetProperty("id", out var i) ? i.GetString() : "";
            if (string.IsNullOrEmpty(assetId)) return;
            try
            {
                // Optimistic toggle: we don't track bookmark state per asset
                // yet, so always flip-set to 1. Setting an existing bookmark
                // to 1 is a no-op server-side; truly toggling needs us to
                // fetch get_rating first and decide. v2.
                await RatingsService.SetBookmarkAsync(assetId, bookmarked: true, _apiKey);
                SetStatus("Bookmarked.");
            }
            catch (Exception ex) { SetStatus("Bookmark error: " + ex.Message); }
        }

        private void OpenAssetOnWeb(JsonElement hit)
        {
            // The asset-gallery-detail URL is keyed by the asset's UUID, not
            // its slug — same as paths.get_asset_gallery_url() in the Blender
            // addon: it accepts asset_data["id"] or asset_data["assetBaseId"].
            // We try id first (newer search payloads) and fall back to
            // assetBaseId (older / detail-API payloads). Slug used to be
            // here and silently produced 404s.
            string assetId = "";
            if (hit.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                assetId = idEl.GetString() ?? "";
            if (string.IsNullOrEmpty(assetId)
                && hit.TryGetProperty("assetBaseId", out var abEl)
                && abEl.ValueKind == JsonValueKind.String)
                assetId = abEl.GetString() ?? "";
            var url = string.IsNullOrEmpty(assetId)
                ? "https://www.blenderkit.com/asset-gallery/"
                : $"https://www.blenderkit.com/asset-gallery-detail/{assetId}/";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        private void OpenAuthorOnWeb(JsonElement hit)
        {
            var authorId = hit.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("id", out var aid) ? aid.ToString() : "";
            var url = string.IsNullOrEmpty(authorId)
                ? "https://www.blenderkit.com/asset-gallery/"
                : $"https://www.blenderkit.com/profile/{authorId}/";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        private MenuItem BuildCategoryMenuItem(CategoryNode node)
        {
            var item = new ButtonMenuItem { Text = node.Name };
            if (node.Children.Count > 0)
            {
                // Parent with children: do NOT attach Click on the parent
                // itself — Eto's ContextMenu fires it together with the
                // child's Click, so the parent label always wins. Provide an
                // explicit "All <Name>" sub-item to select the whole subtree.
                var all = new ButtonMenuItem { Text = "All " + node.Name };
                all.Click += (s, e) => SelectCategory(node.Slug, node.Name);
                item.Items.Add(all);
                item.Items.AddSeparator();
                foreach (var child in node.Children)
                    item.Items.Add(BuildCategoryMenuItem(child));
            }
            else
            {
                item.Click += (s, e) => SelectCategory(node.Slug, node.Name);
            }
            return item;
        }

        /// <summary>
        /// Pair a CheckBox with an externally-coloured Label. Eto.Wpf's
        /// CheckBox keeps overwriting TextColor from the OS theme, so on a
        /// dark background the labels read as black-on-black. Putting the
        /// text in a separate Label dodges the bug entirely.
        /// </summary>
        private static Control WrapCheck(CheckBox cb, string text)
        {
            cb.Text = "";
            var lbl = new Label
            {
                Text = text,
                TextColor = BkColors.DarkText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // Click the label to also flip the checkbox.
            lbl.MouseDown += (s, e) => cb.Checked = !(cb.Checked == true);
            var row = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            row.Items.Add(cb);
            row.Items.Add(lbl);
            return row;
        }

        /// <summary>
        /// Public entry the test commands call after they re-open an
        /// already-visible panel. Sets the search box + asset type and
        /// runs the search; the existing test-mode auto-download branch in
        /// RenderResults takes over once the first hits arrive.
        /// </summary>
        public void TriggerTestSearch(string query, string assetType)
        {
            if (string.IsNullOrEmpty(query)) return;
            BlendkitPlugIn.TestQuery = query;
            BlendkitPlugIn.TestAssetType = assetType ?? "MODEL";
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                // Suppress the cascade — both _searchBox.Text and
                // _assetType.SelectedIndex assignments fire
                // TextChanged / SelectedIndexChanged → DebouncedResearch
                // / OnSearch handlers. We want exactly one OnSearch
                // below with the new state in place.
                _suppressSearchEvents = true;
                _searchBox.Text = query;
                var t = (assetType ?? "MODEL").ToUpperInvariant();
                for (int i = 0; i < _assetType.Items.Count; i++)
                {
                    if (string.Equals(_assetType.Items[i].Text, t, StringComparison.OrdinalIgnoreCase))
                    { _assetType.SelectedIndex = i; break; }
                }
                ApplyFilterVisibility();
                _suppressSearchEvents = false;
                OnSearch();
            }));
        }

        // ---------- Search tabs ----------

        /// <summary>Build the row of tab buttons + a trailing "+" button.</summary>
        private void RebuildTabBar()
        {
            _tabBar.Items.Clear();
            // Back/Forward navigation buttons — disabled when their
            // respective stacks are empty so the icons gray out. Tight
            // 24px width since they're icon-only.
            var active = _tabs[_activeTab];
            var backBtn = new Button
            {
                Text = "◀",
                ToolTip = "Back (previous search in this tab)",
                Enabled = active.Back.Count > 0,
                MinimumSize = new Eto.Drawing.Size(24, 0),
            };
            backBtn.Click += (s, e) => NavigateHistory(forward: false);
            _tabBar.Items.Add(backBtn);
            var fwdBtn = new Button
            {
                Text = "▶",
                ToolTip = "Forward",
                Enabled = active.Forward.Count > 0,
                MinimumSize = new Eto.Drawing.Size(24, 0),
            };
            fwdBtn.Click += (s, e) => NavigateHistory(forward: true);
            _tabBar.Items.Add(fwdBtn);

            for (int i = 0; i < _tabs.Count; i++)
            {
                int idx = i; // capture
                var t = _tabs[i];
                var label = !string.IsNullOrEmpty(t.TitleOverride)
                    ? t.TitleOverride
                    : (!string.IsNullOrEmpty(t.Query) ? t.Query : $"Tab {i + 1}");
                // Eto.Forms.Button has no per-region click events, so we
                // approximate browser-tab UX with two adjacent buttons:
                // a label button that switches, and (when there's more
                // than one tab) a tight "✕" button that closes. They
                // sit in a horizontal StackLayout with zero spacing so
                // they read as a single visual tab.
                var labelBtn = new Button
                {
                    Text = (idx == _activeTab ? "● " : "") + Truncate(label, 14),
                    ToolTip = "Click to switch tabs",
                };
                labelBtn.Click += (s, e) => SwitchTab(idx);

                if (_tabs.Count > 1)
                {
                    var closeBtn = new Button
                    {
                        Text = "✕",
                        ToolTip = "Close tab",
                        // Square-ish footprint so the X reads as an icon, not
                        // a labelled button.
                        MinimumSize = new Eto.Drawing.Size(24, 0),
                    };
                    closeBtn.Click += (s, e) => CloseTab(idx);
                    var pair = new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 0,
                        Items = { labelBtn, closeBtn },
                    };
                    _tabBar.Items.Add(pair);
                }
                else
                {
                    _tabBar.Items.Add(labelBtn);
                }
            }
            var plus = new Button { Text = "+", ToolTip = "New tab" };
            plus.Click += (s, e) => NewTab();
            _tabBar.Items.Add(plus);
        }

        /// <summary>
        /// Snapshot the search-defining state of the active tab and
        /// push it onto the Back stack. Called by DoSearch right
        /// before persisting the new state. No-op if the snapshot is
        /// identical to the top of the stack already.
        /// </summary>
        private void PushHistoryIfChanged()
        {
            if (_tabs.Count == 0) return;
            var t = _tabs[_activeTab];
            // The "current" search state about to be replaced, captured
            // BEFORE we mutate Settings or _tabs.
            var snapshot = new HistoryEntry
            {
                Query = t.Query ?? "",
                AssetType = t.AssetType ?? "MODEL",
                CategorySlug = t.CategorySlug ?? "",
                AuthorId = t.AuthorId,
                AuthorName = t.AuthorName ?? "",
            };
            // First-ever search on a fresh tab — nothing to go back to.
            if (string.IsNullOrEmpty(snapshot.Query)
                && string.IsNullOrEmpty(snapshot.CategorySlug)
                && snapshot.AuthorId == 0
                && t.Back.Count == 0)
                return;
            // Skip if user re-ran the exact same search.
            if (t.Back.Count > 0 && SameEntry(t.Back.Peek(), snapshot)) return;
            t.Back.Push(snapshot);
            // A fresh search invalidates the forward stack — Web-browser
            // semantics. Otherwise users would be confused that Forward
            // takes them somewhere they didn't expect.
            t.Forward.Clear();
            // Refresh button enable-state next tab paint.
            RhinoApp.InvokeOnUiThread((Action)RebuildTabBar);
        }

        private static bool SameEntry(HistoryEntry a, HistoryEntry b) =>
            a != null && b != null
            && a.Query == b.Query
            && string.Equals(a.AssetType, b.AssetType, StringComparison.OrdinalIgnoreCase)
            && a.CategorySlug == b.CategorySlug
            && a.AuthorId == b.AuthorId;

        /// <summary>
        /// Pop the appropriate stack and restore the search state into
        /// the visible UI controls, then re-run the search. Mirrors the
        /// browser convention: Back pushes current onto Forward, etc.
        /// </summary>
        private void NavigateHistory(bool forward)
        {
            if (_tabs.Count == 0) return;
            var t = _tabs[_activeTab];
            var srcStack  = forward ? t.Forward : t.Back;
            var destStack = forward ? t.Back    : t.Forward;
            if (srcStack.Count == 0) return;

            // Capture current as the destination's new top, so the
            // opposite-direction button takes you back here.
            destStack.Push(new HistoryEntry
            {
                Query = t.Query ?? "",
                AssetType = t.AssetType ?? "MODEL",
                CategorySlug = t.CategorySlug ?? "",
                AuthorId = t.AuthorId,
                AuthorName = t.AuthorName ?? "",
            });

            var entry = srcStack.Pop();
            // Apply to the live UI without firing per-control searches —
            // we run exactly one OnSearch at the end.
            _suppressSearchEvents = true;
            try
            {
                _searchBox.Text = entry.Query ?? "";
                for (int i = 0; i < _assetType.Items.Count; i++)
                {
                    if (string.Equals(_assetType.Items[i].Text, entry.AssetType, StringComparison.OrdinalIgnoreCase))
                    { _assetType.SelectedIndex = i; break; }
                }
                _categorySlug = entry.CategorySlug ?? "";
                _activeAuthorId = entry.AuthorId;
                _activeAuthorName = entry.AuthorName ?? "";
                RefreshCategoryDropdown();
                ApplyFilterVisibility();
            }
            finally { _suppressSearchEvents = false; }
            RebuildChipBar();
            RebuildTabBar();
            // The OnSearch below would normally PushHistoryIfChanged
            // and re-add this entry to the back stack, defeating the
            // navigation. Set a one-shot guard so DoSearch skips that.
            _suppressHistoryPush = true;
            OnSearch();
        }

        // One-shot guard: when set, the next DoSearch run skips
        // PushHistoryIfChanged. Used by NavigateHistory.
        private bool _suppressHistoryPush;

        private void NewTab()
        {
            SaveActiveTab();
            _tabs.Add(new TabState());
            _activeTab = _tabs.Count - 1;
            LoadActiveTab();
            RebuildTabBar();
            OnSearch();
        }

        private void CloseTab(int idx)
        {
            if (_tabs.Count <= 1) return;
            if (idx < 0 || idx >= _tabs.Count) return;
            // If we're closing the currently-active tab, save it first
            // so its state is preserved should the user reopen later.
            // (We don't have a session-tab-history yet; this is just so
            // SaveActiveTab side effects like Settings persistence run.)
            if (idx == _activeTab) SaveActiveTab();
            _tabs.RemoveAt(idx);
            // Adjust the active-tab index for the removal:
            //   - removed BEFORE active → active shifts left by 1
            //   - removed AT active → keep the same slot (which now
            //     contains what used to be the next tab); clamp to last
            //   - removed AFTER active → active unchanged
            if (idx < _activeTab) _activeTab -= 1;
            if (_activeTab >= _tabs.Count) _activeTab = _tabs.Count - 1;
            if (_activeTab < 0) _activeTab = 0;
            LoadActiveTab();
            RebuildTabBar();
        }

        private void SwitchTab(int idx)
        {
            if (idx < 0 || idx >= _tabs.Count || idx == _activeTab) return;
            SaveActiveTab();
            _activeTab = idx;
            LoadActiveTab();
            RebuildTabBar();
        }

        private void SaveActiveTab()
        {
            if (_activeTab < 0 || _activeTab >= _tabs.Count) return;
            var t = _tabs[_activeTab];
            t.Query = _searchBox.Text ?? "";
            t.AssetType = CurrentAssetType();
            t.CategorySlug = _categorySlug ?? "";
            t.AuthorId = _activeAuthorId;
            t.AuthorName = _activeAuthorName ?? "";
            t.Hits = new List<JsonElement>(_hits);
            t.NextUrl = _nextUrl;
            t.ResultCount = _resultCount;
        }

        private void LoadActiveTab()
        {
            var t = _tabs[_activeTab];
            // Suppress the cascade: assigning _searchBox.Text and
            // _assetType.SelectedIndex would each fire DoSearch via
            // their change handlers, clobbering the cached tab hits we
            // just restored. Tab switch shows the previous results
            // verbatim; the user has to hit Search to refresh.
            _suppressSearchEvents = true;
            try
            {
                _searchBox.Text = t.Query;
                for (int i = 0; i < _assetType.Items.Count; i++)
                {
                    if (string.Equals(_assetType.Items[i].Text, t.AssetType, StringComparison.OrdinalIgnoreCase))
                    { _assetType.SelectedIndex = i; break; }
                }
                _categorySlug = t.CategorySlug;
                _activeAuthorId = t.AuthorId;
                _activeAuthorName = t.AuthorName;
                RefreshCategoryDropdown();
                // Asset-type-specific filter rows need to follow the tab too —
                // a HDR tab shouldn't show Polycount on its first paint.
                ApplyFilterVisibility();
            }
            finally { _suppressSearchEvents = false; }
            _hits.Clear();
            _hits.AddRange(t.Hits);
            _nextUrl = t.NextUrl;
            _resultCount = t.ResultCount;
            _grid.SetHits(_hits);
            RebuildChipBar();
        }

        // ---------- Recent searches ----------

        private void ShowRecentQueriesMenu()
        {
            if (_recentQueries.Count == 0) { SetStatus("(no recent searches yet)"); return; }
            var menu = new ContextMenu();
            foreach (var q in _recentQueries)
            {
                var item = new ButtonMenuItem { Text = q };
                item.Click += (s, e) => { _searchBox.Text = q; OnSearch(); };
                menu.Items.Add(item);
            }
            if (_recentQueries.Count > 0)
            {
                menu.Items.AddSeparator();
                var clear = new ButtonMenuItem { Text = "Clear history" };
                clear.Click += (s, e) =>
                {
                    _recentQueries.Clear();
                    Settings.SetStringList("recent_queries", _recentQueries);
                };
                menu.Items.Add(clear);
            }
            // _recentBtn is no longer added to the visual tree (the
            // dedicated dropdown was removed in favor of right-click on
            // the search box), so anchor the menu at the search box
            // instead. Falling back to _recentBtn would silently no-op.
            menu.Show(_searchBox);
        }

        // ---------- Filter chip bar ----------

        /// <summary>
        /// Rebuild the row of removable filter chips that summarize the
        /// active query state. Each chip is a single button; clicking it
        /// clears the corresponding filter and re-runs the search.
        /// </summary>
        private void RebuildChipBar()
        {
            _chipBar.Items.Clear();
            void AddChip(string label, Action onRemove)
            {
                var btn = new Button { Text = "✕ " + label };
                btn.Click += (s, e) => { onRemove(); OnSearch(); };
                _chipBar.Items.Add(btn);
            }
            // Mirror BuildFilters / BuildUrlQuery: chips reflect what
            // *actually* applies for the current asset type. Showing a
            // "polycount: ≤10k" chip on an HDR search is misleading
            // because the URL builder strips that filter.
            var at = CurrentAssetType().ToUpperInvariant();
            bool isModelLike = at == "MODEL" || at == "PRINTABLE";
            bool isMaterial = at == "MATERIAL";
            bool isHdr = at == "HDR";

            var q = _searchBox.Text ?? "";
            if (!string.IsNullOrEmpty(q))
                AddChip("\"" + q + "\"", () => _searchBox.Text = "");
            if (!string.IsNullOrEmpty(_categorySlug))
                AddChip("category: " + (_category.Text?.TrimEnd(' ', '▾') ?? _categorySlug),
                    () => { _categorySlug = ""; _category.Text = "All categories ▾"; });
            if (_activeAuthorId > 0)
                AddChip("author: " + (string.IsNullOrEmpty(_activeAuthorName) ? _activeAuthorId.ToString() : _activeAuthorName),
                    () => { _activeAuthorId = 0; _activeAuthorName = ""; });
            if (_freeOnly.Checked == true)
                AddChip("free only", () => _freeOnly.Checked = false);
            if (isModelLike && _animated.Checked == true)
                AddChip("animated", () => _animated.Checked = false);
            if (_quality.Value > 0)
                AddChip($"quality ≥ {_quality.Value}", () => _quality.Value = 0);
            if (_bookmarksOnly.Checked == true)
                AddChip("my bookmarks", () => _bookmarksOnly.Checked = false);

            // Dropdown-driven filters — show the visible label, clear by
            // returning the dropdown to index 0 ("Any …").
            string OptionLabel(DropDown dd) =>
                dd.SelectedIndex >= 0 && dd.SelectedIndex < dd.Items.Count
                    ? dd.Items[dd.SelectedIndex].Text : "";
            void ChipForDropdown(DropDown dd, string prefix)
            {
                if (dd.SelectedIndex <= 0) return;
                AddChip(prefix + ": " + OptionLabel(dd), () => dd.SelectedIndex = 0);
            }
            // license + order are server-wide and always meaningful.
            ChipForDropdown(_license, "license");
            ChipForDropdown(_order,   "order");
            // style applies to both model and material (under different
            // server fields, handled by BuildUrlQuery).
            if (isModelLike || isMaterial) ChipForDropdown(_style, "style");
            // Model-only.
            if (isModelLike) ChipForDropdown(_condition, "condition");
            if (isModelLike) ChipForDropdown(_polycount, "polycount");
            // Texture resolution is meaningful on model + material + HDR.
            if (isModelLike || isMaterial || isHdr) ChipForDropdown(_texRes, "texture");

            // Design year range — only when the user has opted in AND
            // the asset type uses it (model only).
            if (isModelLike && _designYearEnable.Checked == true && _designYearMin.Value > 0)
            {
                var lo = (int)_designYearMin.Value;
                var hi = (int)_designYearMax.Value;
                AddChip($"design year: {lo}–{hi}", () => _designYearEnable.Checked = false);
            }

            // "Clear all" is shown only when there's actually something to
            // clear (i.e. at least two chips), so it doesn't add noise on a
            // pristine empty search.
            if (_chipBar.Items.Count > 1)
            {
                var clearAll = new Button { Text = "Clear all" };
                clearAll.Click += (s, e) => ClearAllFilters();
                _chipBar.Items.Add(clearAll);
            }
        }

        private void ClearAllFilters()
        {
            // Suppress cascading events so we don't fire 14 partial
            // searches as we clear each control. We run exactly one
            // OnSearch at the end with the final state.
            _suppressSearchEvents = true;
            try
            {
                _searchBox.Text = "";
                _categorySlug = "";
                _category.Text = "📁";
                _activeAuthorId = 0; _activeAuthorName = "";
                _freeOnly.Checked = false;
                _animated.Checked = false;
                _bookmarksOnly.Checked = false;
                _quality.Value = 0;
                _license.SelectedIndex = 0;
                _order.SelectedIndex = 0;
                _style.SelectedIndex = 0;
                _condition.SelectedIndex = 0;
                _polycount.SelectedIndex = 1; // back to the "≤10k" default
                _texRes.SelectedIndex = 0;
                _designYearEnable.Checked = false;
            }
            finally { _suppressSearchEvents = false; }
            OnSearch();
        }

        // ---------- Author filter (clicking an author chip in results) ----------

        private static bool IsAuthorHit(JsonElement hit)
            => hit.TryGetProperty("assetType", out var at)
               && string.Equals(at.GetString(), "author", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Mirror of blenderkit/search.py:search_by_author_id.
        ///
        /// When the user clicks an author chip we set the AuthorId filter,
        /// but the existing keyword query usually contains the author's name
        /// (since the user typed it to find the author in the first place).
        /// Leaving the keyword in place returns zero results — the author's
        /// own assets rarely contain his/her name in their titles. Strip the
        /// author-name words from the keyword box before the new search.
        /// </summary>
        private void FilterByAuthor(JsonElement hit)
        {
            int authorId = 0;
            string authorName = "";

            if (IsAuthorHit(hit))
            {
                // Author result: id is the author id directly.
                if (hit.TryGetProperty("id", out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number) authorId = v.GetInt32();
                    else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n)) authorId = n;
                }
                if (hit.TryGetProperty("displayName", out var dn)) authorName = dn.GetString() ?? "";
                if (string.IsNullOrEmpty(authorName) && hit.TryGetProperty("fullName", out var fn))
                    authorName = fn.GetString() ?? "";
            }
            else
            {
                // Asset result: author lives in nested "author" / "user" /
                // top-level "userDisplayName".
                if (hit.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object
                    && a.TryGetProperty("id", out var aid))
                {
                    if (aid.ValueKind == JsonValueKind.Number) authorId = aid.GetInt32();
                    else if (aid.ValueKind == JsonValueKind.String && int.TryParse(aid.GetString(), out var n)) authorId = n;
                }
                if (authorId == 0 && hit.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                    && u.TryGetProperty("id", out var uid))
                {
                    if (uid.ValueKind == JsonValueKind.Number) authorId = uid.GetInt32();
                    else if (uid.ValueKind == JsonValueKind.String && int.TryParse(uid.GetString(), out var n)) authorId = n;
                }
                if (hit.TryGetProperty("userDisplayName", out var udn))
                    authorName = udn.GetString() ?? "";
            }

            if (authorId == 0) { SetStatus("Could not resolve author id from hit."); return; }
            _activeAuthorId = authorId;
            _activeAuthorName = authorName;

            // Clean the search box of author-name words. Single-word keywords
            // → drop entirely (user was clearly searching for this author).
            var kw = (_searchBox.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(kw) && !string.IsNullOrEmpty(authorName))
            {
                var parts = kw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length <= 1)
                {
                    kw = "";
                }
                else
                {
                    var nameParts = authorName.ToLowerInvariant()
                        .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    var keep = new System.Collections.Generic.List<string>();
                    foreach (var w in parts)
                        if (Array.IndexOf(nameParts, w.ToLowerInvariant()) < 0)
                            keep.Add(w);
                    kw = string.Join(" ", keep);
                }
                _searchBox.Text = kw;
            }

            BkLog.W($"FilterByAuthor: id={authorId} name='{authorName}' keywords-after='{kw}'");
            OnSearch();
        }

        // Active author filter — set by FilterByAuthor, cleared by the
        // matching chip in the active-filters bar. Mirrors the Blender
        // addon's `+author_id:<n>` URL filter.
        private int _activeAuthorId;
        private string _activeAuthorName = "";

        // Set of asset ids the logged-in user has bookmarked. Populated when
        // a `ratings/get_bookmarks` task lands; consumed by ThumbCell to
        // overlay a heart indicator. Empty for non-logged-in users.
        private readonly HashSet<string> _bookmarkedIds = new HashSet<string>();
        // Cached plan flag set by HandleProfileTask. False until the
        // /profiles/get_user_profile task confirms a non-Free plan.
        private bool _hasFullPlan;
        // The logged-in user's BlenderKit user id. Used by the
        // "My uploads" filter to pin author_id to themselves.
        private int _profileUserId;
        // One-shot override for DoSearch. The asset-type SelectedIndex
        // change handler captures the new type synchronously and
        // stashes it here; DoSearch consumes + clears. Without this
        // pin we kept reading stale values from the dropdown across
        // event-handler boundaries — every asset-type switch ran the
        // search with the previous type's URL.
        private string _pinnedAssetType;
        // Per-asset-type rows in the Filters expander — hidden for asset
        // types where the filter is irrelevant (polycount on materials etc.).
        private Panel _styleConditionRow;
        private Panel _polyTextureRow;
        private Panel _designYearRow;
        // Per-asset-type extra filter rows added 2026-04-30 to match
        // blenderkit/ui_panels.py 1:1.
        private Panel _modelExtrasRow;     // geometry_nodes (MODEL only)
        private Panel _materialProceduralRow; // procedural radio (MATERIAL only)
        private Panel _hdrExtrasRow;       // true_hdr (HDR only)
        private Panel _commonOwnOnlyRow;   // own_only (login-required, all types)
        private readonly Label _conditionLabel = new Label
            { Text = "Condition:", VerticalAlignment = VerticalAlignment.Center };
        private readonly Label _polycountLabel = new Label
            { Text = "Polycount:", VerticalAlignment = VerticalAlignment.Center };

        private void SelectCategory(string slug, string label)
        {
            BkLog.W($"SelectCategory: slug='{slug}' label='{label}'");
            _categorySlug = slug;
            _category.Text = (string.IsNullOrEmpty(slug) ? "All categories" : label) + " ▾";
            // Re-search so the user sees results immediately.
            OnSearch();
        }

        private SearchService.Filters BuildFilters(string overrideAssetType = null)
        {
            string Sel((string Label, string Value)[] arr, int idx) =>
                idx >= 0 && idx < arr.Length ? arr[idx].Value : "";
            // Asset-type-aware: skip filling fields that don't apply to
            // the active asset type. The URL builder ignores them anyway,
            // but keeping the Filters bag clean means the chip bar /
            // status logging report the truth (we previously logged
            // "poly=0-10000" on HDR searches, which was misleading).
            var at = (overrideAssetType ?? CurrentAssetType()).ToUpperInvariant();
            bool isModel = at == "MODEL" || at == "PRINTABLE";
            bool isMaterial = at == "MATERIAL";
            bool isHdr = at == "HDR";

            var f = new SearchService.Filters
            {
                FreeOnly = _freeOnly.Checked == true,
                BookmarksOnly = _bookmarksOnly.Checked == true,
                Order = Sel(OrderOptions, _order.SelectedIndex),
                License = Sel(LicenseOptions, _license.SelectedIndex),
                QualityMin = _quality.Value, // 0..10; 0 means "any"
                Category = _categorySlug ?? "",
                AuthorId = _activeAuthorId,
                // TextureResolution applies to MODEL, MATERIAL, HDR
                // (anything that ships textures); SCENE/BRUSH ignore it.
                TextureResolutionMin = (isModel || isMaterial || isHdr) &&
                                       int.TryParse(Sel(TextureResolutionOptions, _texRes.SelectedIndex), out var tr)
                                           ? tr : 0,
            };

            if (isModel)
            {
                // Model-only: glTF lookup, animated, modelStyle/condition,
                // designYear range, polycount range, geometry_nodes.
                f.GltfOnly = _gltfOnly.Checked == true;
                f.Animated = _animated.Checked == true;
                f.GeometryNodes = _geomNodes.Checked == true;
                f.Style = Sel(StyleOptions, _style.SelectedIndex);
                f.Condition = Sel(ConditionOptions, _condition.SelectedIndex);
                f.DesignYearMin = (int)_designYearMin.Value;
                f.DesignYearMax = (int)_designYearMax.Value;
                f.PolycountMin = ParsePolyMin(Sel(PolycountOptions, _polycount.SelectedIndex));
                f.PolycountMax = ParsePolyMax(Sel(PolycountOptions, _polycount.SelectedIndex));
            }
            else if (isMaterial)
            {
                // build_query_material in blenderkit/search.py: PROCEDURAL
                // sets a 1MB file-size cap; TEXTURE_BASED forces a real
                // texture present. The user-facing addon doesn't expose
                // `style` on materials, so we don't either — even though
                // the URL builder still understands it.
                var procIdx = _procedural.SelectedIndex;
                f.Procedural = procIdx == 1 ? "PROCEDURAL"
                            : procIdx == 2 ? "TEXTURE_BASED"
                            : "";
            }
            else if (isHdr)
            {
                f.TrueHdr = _trueHdr.Checked == true;
            }
            // Common: own-only filter (login-required) — owner id comes
            // from the cached profile JSON we got via /report.
            if (_ownOnly.Checked == true && _profileUserId > 0)
                f.OwnUserId = _profileUserId;

            return f;
        }

        // Polycount value = "min:max" string. "0:10000" → min=0, max=10000.
        // "1000000:0" → min=1M, max=0 (meaning "≥1M, no upper bound").
        private static int ParsePolyMin(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var parts = s.Split(':');
            return parts.Length > 0 && int.TryParse(parts[0], out var v) ? v : 0;
        }
        private static int ParsePolyMax(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var parts = s.Split(':');
            return parts.Length > 1 && int.TryParse(parts[1], out var v) ? v : 0;
        }

        private async void OnSearch() => await DoSearch(nextUrl: null, append: false);

        // Coalesce rapid keystrokes into a single search ~500ms after the
        // last keystroke. Avoids one search per character while still making
        // typing feel live.
        private long _researchVersion;
        private void DebouncedResearch()
        {
            var v = System.Threading.Interlocked.Increment(ref _researchVersion);
            Task.Run(async () =>
            {
                await Task.Delay(500);
                if (System.Threading.Interlocked.Read(ref _researchVersion) != v) return;
                RhinoApp.InvokeOnUiThread((Action)OnSearch);
            });
        }

        private async void OnNeedMore()
        {
            if (_loadingMore || string.IsNullOrEmpty(_nextUrl)) return;
            _loadingMore = true;
            await DoSearch(nextUrl: _nextUrl, append: true);
        }

        // Last URL we sent — shown in the panel status line so the user can
        // verify exactly what query went out (helps diagnose ordering /
        // category / filter issues).
        private string _lastSearchUrl = "";

        private async Task DoSearch(string nextUrl, bool append, string overrideAssetType = null)
        {
            var q = _searchBox.Text ?? "";
            // Asset type resolution order:
            //   1. explicit overrideAssetType (passed by the
            //      SelectedIndexChanged handler at the moment of the
            //      user's click — never lies);
            //   2. _pinnedAssetType (legacy fallback, will go away);
            //   3. CurrentAssetType() reading the live dropdown.
            var at = !string.IsNullOrEmpty(overrideAssetType) ? overrideAssetType
                   : !string.IsNullOrEmpty(_pinnedAssetType) ? _pinnedAssetType
                   : CurrentAssetType();
            _pinnedAssetType = null;
            // Asset-type change invalidates the cached "load more"
            // pointer (_nextUrl) — that URL was built for the previous
            // type. Without this, a subsequent scroll-to-load-more
            // would fetch results for the OLD type using the stale
            // nextUrl, producing the "still searching old type"
            // behavior the user reported.
            if (overrideAssetType != null) _nextUrl = null;
            var filters = BuildFilters(at);
            // Show the actual outgoing URL so it's obvious what we're asking
            // the server for. Helps diagnose ordering / category / filter
            // issues without having to dig into rhino_panel.log.
            _lastSearchUrl = nextUrl ?? SearchService.BuildUrlQuery(q, at, pageSize: 15, filters);
            BkLog.W($"SEARCH q='{q}' type={at} cat='{filters.Category}' order='{filters.Order}' license='{filters.License}' qmin={filters.QualityMin} free={filters.FreeOnly} anim={filters.Animated} bkmk={filters.BookmarksOnly} style='{filters.Style}' cond='{filters.Condition}' poly={filters.PolycountMin}-{filters.PolycountMax} tex>={filters.TextureResolutionMin} year={filters.DesignYearMin}-{filters.DesignYearMax}");
            BkLog.W("SEARCH URL: " + _lastSearchUrl);
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                _searchUrlBox.Text = _lastSearchUrl;
                RebuildChipBar();
                // Tab button label tracks the active query.
                SaveActiveTab();
                RebuildTabBar();
            }));
            // Persist context only on a fresh search (not on auto-paginated
            // "load more" calls) and only when the query is non-empty (we
            // don't want to clog the recent list with empty searches).
            if (nextUrl == null)
            {
                // Push the previous search-defining state onto this tab's
                // Back stack so the user can step back. Skipped during
                // NavigateHistory (which already orchestrates the stacks
                // itself); also skips the very-first push and identical-
                // top pushes.
                if (_suppressHistoryPush)
                {
                    _suppressHistoryPush = false;
                }
                else
                {
                    PushHistoryIfChanged();
                }
                Settings.SetString("last_query", q);
                Settings.SetString("last_asset_type", at);
                Settings.SetString("last_category_slug", _categorySlug ?? "");
                if (!string.IsNullOrWhiteSpace(q))
                {
                    _recentQueries.Remove(q);
                    _recentQueries.Insert(0, q);
                    if (_recentQueries.Count > RecentQueriesMax)
                        _recentQueries.RemoveRange(RecentQueriesMax, _recentQueries.Count - RecentQueriesMax);
                    Settings.SetStringList("recent_queries", _recentQueries);
                }
            }
            SetStatus(nextUrl == null
                ? $"Searching {at.ToLower()}s for \"{q}\"…"
                : $"Loading more ({_hits.Count}/{_resultCount})…");
            if (!append)
            {
                _hits.Clear();
                _grid.SetHits(_hits);
            }
            try
            {
                _pendingSearchId = await SearchService.StartAsync(
                    q, at, apiKey: _apiKey, globalDir: BlendkitPlugIn.DefaultGlobalDir,
                    filters: filters,
                    nextUrl: nextUrl);
            }
            catch (Exception ex)
            {
                _loadingMore = false;
                // Translate the most common failure into something
                // actionable. "Go client port not discovered yet" / a
                // raw socket error doesn't tell the user what to do —
                // they need to know the local helper isn't running.
                var msg = ex.Message ?? "";
                if (msg.Contains("port not discovered")
                    || msg.Contains("actively refused")
                    || msg.Contains("connection refused")
                    || msg.Contains("No connection could be made"))
                {
                    SetStatus("BlenderKit helper isn't responding — check that client.exe is running. Retrying in background.");
                }
                else
                {
                    SetStatus("Search error: " + msg);
                }
            }
        }

        /// <summary>
        /// Drag spike: if the asset's .glb is already cached on disk, build a
        /// Uri DataObject and call DoDragDrop on the panel. Rhino's viewport
        /// has a native drop handler for .glb / .gltf and will import at the
        /// release point. If the asset isn't cached, fall back to triggering
        /// a download (user will need to drag again after it lands — for v1).
        /// </summary>
        private void OnDragStart(JsonElement hit)
        {
            // Pre-flight: paid asset on a Free/anonymous account → show the
            // "Full plan required" dialog and bail before kicking off any
            // download or drag preview.
            if (BlockedByPlan(hit)) return;
            var assetType = hit.TryGetProperty("assetType", out var at) ? at.GetString() : "model";
            // Match any of the asset's id-like fields against the cache folder
            // name. BlenderKit uses both `id` (numeric string in some places,
            // UUID in others) and `assetBaseId` (the canonical UUID) — and
            // the Go client names download folders after one or the other
            // depending on the path it took.
            var ids = new System.Collections.Generic.List<string>();
            void Take(string field)
            {
                if (hit.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s)) ids.Add(s);
                }
            }
            Take("id"); Take("assetBaseId"); Take("name");
            if (ids.Count == 0) return;

            var cacheDir = System.IO.Path.Combine(
                BlendkitPlugIn.DefaultGlobalDir, assetType + "s");
            string cachedGlb = null, cachedBlend = null;
            if (Directory.Exists(cacheDir))
            {
                foreach (var sub in Directory.EnumerateDirectories(cacheDir))
                {
                    var subName = System.IO.Path.GetFileName(sub);
                    bool match = false;
                    foreach (var key in ids)
                    {
                        if (subName.Contains(key, StringComparison.OrdinalIgnoreCase))
                        { match = true; break; }
                    }
                    if (!match) continue;
                    foreach (var f in Directory.EnumerateFiles(sub))
                    {
                        if (f.EndsWith(".glb") || f.EndsWith(".gltf")) cachedGlb ??= f;
                        else if (f.EndsWith(".blend")) cachedBlend ??= f;
                    }
                    if (cachedGlb != null || cachedBlend != null) break;
                }
            }
            BkLog.W($"OnDragStart: cachedGlb={cachedGlb ?? "null"} cachedBlend={cachedBlend ?? "null"}");

            // Fastest case: this asset already exists as an
            // InstanceDefinition in the active doc (a previous drop
            // blockified it). Skip download + file import entirely; the
            // OnDrop handler just AddInstanceObjects at the captured
            // point. Drag starts immediately, status reflects ready.
            string baseId = "";
            if (hit.TryGetProperty("assetBaseId", out var b)) baseId = b.GetString() ?? "";
            int cachedInstDef = LookupCachedInstDef(global::Rhino.RhinoDoc.ActiveDoc, baseId);
            if (cachedInstDef >= 0)
            {
                BkLog.W($"OnDragStart: cached InstDef #{cachedInstDef} for {baseId} — skipping download + import");
                StartDrag(glbPath: null, alreadyDownloaded: true,
                    cachedInstanceDef: cachedInstDef);
                return;
            }

            // Best case: .glb already on disk. Start a drag session — a
            // wireframe cube follows the cursor; on release we raycast the
            // scene and import + translate the asset to that hit point.
            if (cachedGlb != null)
            {
                StartDrag(cachedGlb, alreadyDownloaded: true);
                return;
            }

            // Second best: .blend cached but no glb yet. Path forks
            // by asset type — materials have no geometry to glTF-
            // export, so glb conversion produces an empty file and
            // import bombs out with "Import command returned false"
            // (previously the regression: log line "drop ... hit=...
            // Import command returned false" when dragging a cached
            // material). Materials go through StartMaterialConvert
            // instead so the .blend → JSON manifest pipeline runs.
            if (cachedBlend != null)
            {
                if (BlenderService.FindBlenderExe() == null)
                {
                    SetStatus("Blender not found — install Blender to convert .blend assets.");
                    return;
                }
                bool isMaterialCached = string.Equals(assetType, "material", StringComparison.OrdinalIgnoreCase);
                if (isMaterialCached)
                {
                    // Open a drag session so the user gets the material
                    // disc preview + the on-drop hit-target capture.
                    // No glb path — the blend will be re-extracted as
                    // a JSON manifest when the drop fires.
                    var matDrop = StartDrag(glbPath: null, alreadyDownloaded: true);
                    SetStatus("Drop on an object to assign material…");
                    // Kick off the material extraction now in the
                    // background so by the time the user drops the
                    // JSON manifest is already on disk. The drop
                    // handler then reads the material and assigns to
                    // matDrop.HitObjectId.
                    StartMaterialConvert(cachedBlend, matDrop);
                    return;
                }
                SetStatus("Converting cached .blend → .glb for drop…");
                Task.Run(async () =>
                {
                    try
                    {
                        var taskId = await BlenderConvertService.StartAsync(
                            cachedBlend, Process.GetCurrentProcess().Id);
                        if (string.IsNullOrEmpty(taskId))
                        {
                            SetStatus("Convert request returned no task_id.");
                            return;
                        }
                        RhinoApp.InvokeOnUiThread((Action)(() =>
                        {
                            _pendingConvertActions[taskId] =
                                glb => StartDrag(glb, alreadyDownloaded: true);
                        }));
                    }
                    catch (Exception ex) { SetStatus("Convert request failed: " + ex.Message); }
                });
                return;
            }

            // Cold case: nothing cached. Start a drag session that captures
            // the release point while the download runs in the background;
            // place the asset at the captured point once it lands.
            var drop = StartDrag(glbPath: null, alreadyDownloaded: false);
            // Bind: when the download for this drop finishes, run import via
            // the drop's saved point + normal. StartDownloadForDrop does the
            // task_id wiring.
            _ = StartDownloadForDrop(hit, drop);
        }

        /// <summary>
        /// Read the asset's bounding-box from the search hit's dictParameters
        /// (BlenderKit stores these in meters), and convert to the active
        /// Rhino doc's model units. Returns a sane default when the hit
        /// doesn't carry the fields.
        /// </summary>
        private static global::Rhino.Geometry.Vector3d BBoxFromHit(JsonElement hit)
        {
            var fallback = new global::Rhino.Geometry.Vector3d(50, 50, 50);
            if (!hit.TryGetProperty("dictParameters", out var p) || p.ValueKind != JsonValueKind.Object)
                return fallback;
            double Get(string k)
            {
                if (!p.TryGetProperty(k, out var v)) return double.NaN;
                if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
                if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d)) return d;
                return double.NaN;
            }
            double mnX = Get("boundBoxMinX"), mxX = Get("boundBoxMaxX");
            double mnY = Get("boundBoxMinY"), mxY = Get("boundBoxMaxY");
            double mnZ = Get("boundBoxMinZ"), mxZ = Get("boundBoxMaxZ");
            if (double.IsNaN(mnX) || double.IsNaN(mxX)) return fallback;

            // BlenderKit dimensions are in meters. Convert to the doc's unit.
            var doc = global::Rhino.RhinoDoc.ActiveDoc;
            var scale = doc != null
                ? global::Rhino.RhinoMath.UnitScale(global::Rhino.UnitSystem.Meters, doc.ModelUnitSystem)
                : 1.0;
            return new global::Rhino.Geometry.Vector3d(
                (mxX - mnX) * scale,
                (mxY - mnY) * scale,
                (mxZ - mnZ) * scale);
        }

        /// <summary>
        /// Read the asset's dimension triple (in meters) from the
        /// `dimensionX/Y/Z` keys in dictParameters. These are the
        /// authoritative server-stored dimensions; the bbox helper above
        /// is a fallback that derives them from boundBoxMinX/MaxX.
        /// </summary>
        private static (double X, double Y, double Z)? ReadDimensions(JsonElement hit)
        {
            if (!hit.TryGetProperty("dictParameters", out var p) || p.ValueKind != JsonValueKind.Object)
                return null;
            double Get(string k)
            {
                if (!p.TryGetProperty(k, out var v)) return double.NaN;
                if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
                if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
                return double.NaN;
            }
            double dx = Get("dimensionX"), dy = Get("dimensionY"), dz = Get("dimensionZ");
            if (double.IsNaN(dx) || double.IsNaN(dy) || double.IsNaN(dz)) return null;
            // Some assets ship zero/negative junk; skip silently.
            if (Math.Max(Math.Max(dx, dy), dz) <= 0) return null;
            return (dx, dy, dz);
        }

        /// <summary>
        /// Mirror of blenderkit/utils.py:fmt_dimensions — auto-pick the
        /// pretty unit (m / cm / mm) based on the largest dimension.
        /// Always emits "X×Y×Z unit", e.g. "0.46×0.46×1.04 m" or
        /// "1.4×1.4×8.7 cm".
        /// </summary>
        private static string FormatDimensionsBlenderStyle((double X, double Y, double Z) dims)
        {
            double max = Math.Max(Math.Max(dims.X, dims.Y), dims.Z);
            string unit; double scale;
            if (max > 1)        { unit = "m";  scale = 1; }
            else if (max > 0.01){ unit = "cm"; scale = 100; }
            else                 { unit = "mm"; scale = 1000; }
            string F(double v) => Math.Round(v * scale, 2)
                .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return $"{F(dims.X)}×{F(dims.Y)}×{F(dims.Z)} {unit}";
        }

        /// <summary>
        /// Optional second column with the dimensions converted into
        /// the active Rhino doc's units. Empty when the doc unit is
        /// already meters or the doc isn't available.
        /// </summary>
        private static string RhinoUnitsTrailer((double X, double Y, double Z) dimsMeters)
        {
            try
            {
                var doc = global::Rhino.RhinoDoc.ActiveDoc;
                if (doc == null) return "";
                var us = doc.ModelUnitSystem;
                if (us == global::Rhino.UnitSystem.Meters) return "";
                double s = global::Rhino.RhinoMath.UnitScale(global::Rhino.UnitSystem.Meters, us);
                string sym = ShortUnitSymbol(us);
                string F(double v) => Math.Round(v * s, 2)
                    .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                return $"  ({F(dimsMeters.X)}×{F(dimsMeters.Y)}×{F(dimsMeters.Z)} {sym})";
            }
            catch { return ""; }
        }

        private static string ShortUnitSymbol(global::Rhino.UnitSystem us) => us switch
        {
            global::Rhino.UnitSystem.Millimeters => "mm",
            global::Rhino.UnitSystem.Centimeters => "cm",
            global::Rhino.UnitSystem.Meters => "m",
            global::Rhino.UnitSystem.Kilometers => "km",
            global::Rhino.UnitSystem.Inches => "in",
            global::Rhino.UnitSystem.Feet => "ft",
            global::Rhino.UnitSystem.Yards => "yd",
            global::Rhino.UnitSystem.Miles => "mi",
            _ => us.ToString().ToLowerInvariant(),
        };

        /// <summary>Pretty-print bytes as KB / MB / GB.</summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.#") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB";
        }

        /// <summary>
        /// Begin a new drag session and return the per-drop state object.
        /// Each invocation creates a fresh ActiveDrop with its own preview
        /// cube, so multiple drags can coexist with their own progress bars
        /// in the viewport.
        /// </summary>
        private ActiveDrop StartDrag(string glbPath, bool alreadyDownloaded, int cachedInstanceDef = -1)
        {
            var drop = new ActiveDrop();
            _drops.Add(drop);

            drop.Preview.Progress = alreadyDownloaded ? 1.0 : 0.0;
            drop.Preview.Label = alreadyDownloaded ? "Drop to place" : "Downloading…";
            var hit = _grid.SelectedHit;
            if (hit.HasValue)
            {
                drop.Preview.Size = BBoxFromHit(hit.Value);
                if (hit.Value.TryGetProperty("name", out var n)) drop.AssetName = n.GetString() ?? "";
                if (hit.Value.TryGetProperty("assetType", out var at))
                    drop.AssetType = at.GetString() ?? "model";
                if (hit.Value.TryGetProperty("assetBaseId", out var abid))
                    drop.AssetBaseId = abid.GetString() ?? "";
                _grid_static_currentHit = hit; // for the metadata stamper
                _grid_static_currentAssetBaseId = drop.AssetBaseId;
            }
            // Pick a preview style appropriate to the asset type.
            //   MODEL/PRINTABLE → bbox cube (matches placement intent).
            //   MATERIAL → flat disc on the surface (= "I'll paint this
            //              object"; the bbox cube would lie about a 3D
            //              placement that doesn't actually happen).
            //   HDR → no in-viewport preview (we just bind the env on
            //         drop; the drop point is irrelevant).
            switch ((drop.AssetType ?? "model").ToLowerInvariant())
            {
                case "material":
                    drop.Preview.Style = DragPreviewConduit.DragStyle.MaterialDisc;
                    break;
                case "hdr":
                    drop.Preview.Style = DragPreviewConduit.DragStyle.HdrNothing;
                    break;
                default:
                    drop.Preview.Style = DragPreviewConduit.DragStyle.ModelBox;
                    break;
            }
            drop.Preview.Enabled = true;

            var session = new DragSession { Preview = drop.Preview };
            session.OnDrop = (view, pt, normal, spin, hitId) =>
            {
                BkLog.W($"drop at ({pt.X:F2},{pt.Y:F2},{pt.Z:F2}) normal=({normal.X:F2},{normal.Y:F2},{normal.Z:F2}) spin={spin:F2} hit={hitId}");
                drop.DropPoint = pt;
                drop.Normal = normal;
                drop.SpinRadians = spin;
                drop.HitObjectId = hitId;
                if (cachedInstanceDef >= 0)
                {
                    PlaceInstanceForDrop(drop, cachedInstanceDef, pt, normal, spin);
                }
                else if (glbPath != null)
                {
                    ImportForDrop(drop, glbPath);
                }
                else
                {
                    SetStatus($"Drop captured — '{drop.AssetName}' will land here when ready.");
                }
            };
            session.OnCancel = () =>
            {
                drop.Preview.Enabled = false;
                _drops.Remove(drop);
                SetStatus(alreadyDownloaded
                    ? "Drop cancelled — released outside any viewport."
                    : $"Drop cancelled for '{drop.AssetName}'.");
            };
            session.Start();
            // Tooltip-style hint on the preview cube too, in case the user
            // doesn't watch the status line.
            drop.Preview.Label = (alreadyDownloaded ? "Drop to place" : "Downloading…")
                + "  · scroll wheel to rotate";
            SetStatus(alreadyDownloaded
                ? "Drop in viewport to place asset · scroll mousewheel to rotate."
                : "Drag — drop in viewport now · scroll mousewheel to rotate. Asset arrives when download finishes.");
            return drop;
        }

        /// <summary>
        /// Variant of <see cref="StartDownloadFor"/> that binds the resulting
        /// download task_id back onto the <see cref="ActiveDrop"/> so
        /// HandleDownloadTask can route progress + completion to the right
        /// preview cube.
        /// </summary>
        private async Task StartDownloadForDrop(JsonElement hit, ActiveDrop drop)
        {
            var name = hit.TryGetProperty("name", out var nm) ? nm.GetString() : "(asset)";
            var sel = _resolution.SelectedValue?.ToString() ?? "2K";
            var resolution = "resolution_" + sel.Replace("0.5K", "0_5K");
            SetStatus($"Starting download: {name} @ {sel}…");
            try
            {
                var taskId = await DownloadService.StartAsync(
                    hit, apiKey: _apiKey, globalDir: BlendkitPlugIn.DefaultGlobalDir,
                    resolution: resolution);
                drop.DownloadTaskId = taskId;
            }
            catch (Exception ex)
            {
                drop.Preview.Enabled = false;
                _drops.Remove(drop);
                SetStatus("Download error: " + ex.Message);
            }
        }

        private void ImportForDrop(ActiveDrop drop, string filePath)
        {
            if (drop.DropPoint.HasValue)
                ImportAtPoint(filePath, drop.DropPoint.Value, drop.Normal, drop.SpinRadians);
            else
                ImportAtPickedPoint(filePath);
            drop.Done = true;
            drop.Preview.Enabled = false;
            _drops.Remove(drop);
        }

        /// <summary>
        /// Drop handler for the cached-InstDef fast path. Skips the
        /// download + file-import entirely and just adds an InstanceObject
        /// at the captured point/normal/spin. Used when OnDragStart finds
        /// an existing block for the dragged asset_base_id.
        /// </summary>
        private void PlaceInstanceForDrop(ActiveDrop drop, int instDefIdx,
            global::Rhino.Geometry.Point3d pt,
            global::Rhino.Geometry.Vector3d normal,
            double spinRadians)
        {
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    var doc = global::Rhino.RhinoDoc.ActiveDoc;
                    WithUndo(doc, "BlenderKit: Place cached block", () =>
                    {
                    if (normal.IsZero) normal = global::Rhino.Geometry.Vector3d.ZAxis;
                    var rot = global::Rhino.Geometry.Transform.Rotation(
                        global::Rhino.Geometry.Vector3d.ZAxis, normal,
                        global::Rhino.Geometry.Point3d.Origin);
                    var spin = global::Rhino.Geometry.Transform.Rotation(
                        spinRadians, normal,
                        global::Rhino.Geometry.Point3d.Origin);
                    var trans = global::Rhino.Geometry.Transform.Translation(
                        pt - global::Rhino.Geometry.Point3d.Origin);
                    var xform = trans * spin * rot;
                    // Validate cache hasn't gone stale between drag-start
                    // and drop (user could have purged the InstDef).
                    if (instDefIdx < 0 || instDefIdx >= doc.InstanceDefinitions.Count
                        || doc.InstanceDefinitions[instDefIdx] == null
                        || doc.InstanceDefinitions[instDefIdx].IsDeleted)
                    {
                        BkLog.W($"PlaceInstanceForDrop: cached InstDef #{instDefIdx} no longer valid; aborting drop");
                        SetStatus("Cached block disappeared — try the drop again.");
                        return;
                    }
                    var iid = doc.Objects.AddInstanceObject(instDefIdx, xform);
                    if (iid != Guid.Empty)
                    {
                        StampBlenderKitMetadata(doc, new[] { iid }, sourcePath: "(cached InstDef)");
                        doc.Views.Redraw();
                        SetStatus($"Placed cached block at ({pt.X:F2}, {pt.Y:F2}, {pt.Z:F2}): {drop.AssetName}");
                    }
                    else
                    {
                        SetStatus("AddInstanceObject failed — block placement aborted.");
                    }
                    });
                }
                catch (Exception ex) { SetStatus("Drop error: " + ex.Message); }
                finally
                {
                    drop.Done = true;
                    drop.Preview.Enabled = false;
                    _drops.Remove(drop);
                }
            }));
        }

        private void ImportAtPoint(string path, global::Rhino.Geometry.Point3d pt)
            => ImportAtPoint(path, pt, global::Rhino.Geometry.Vector3d.ZAxis, 0);

        private void ImportAtPoint(string path, global::Rhino.Geometry.Point3d pt, global::Rhino.Geometry.Vector3d normal)
            => ImportAtPoint(path, pt, normal, 0);

        /// <summary>
        /// Import the asset and place it so its local origin sits at
        /// <paramref name="pt"/>, its local +Z aligns to <paramref name="normal"/>,
        /// and a final rotation of <paramref name="spinRadians"/> is applied
        /// about that normal (the user's mousewheel rotation during drag).
        /// </summary>
        private void ImportAtPoint(string path, global::Rhino.Geometry.Point3d pt, global::Rhino.Geometry.Vector3d normal, double spinRadians)
        {
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    var doc = global::Rhino.RhinoDoc.ActiveDoc;
                    WithUndo(doc, "BlenderKit: Drop asset at point", () =>
                    ImportAtPointCore(doc, path, pt, normal, spinRadians));
                }
                catch (Exception ex) { SetStatus("Import error: " + ex.Message); }
            }));
        }

        private void ImportAtPointCore(global::Rhino.RhinoDoc doc, string path,
            global::Rhino.Geometry.Point3d pt, global::Rhino.Geometry.Vector3d normal,
            double spinRadians)
        {
            // Compose the placement transform once — both the
            // cache fast-path and the first-import blockify
            // pipeline use the same chain.
            if (normal.IsZero) normal = global::Rhino.Geometry.Vector3d.ZAxis;
            var rot = global::Rhino.Geometry.Transform.Rotation(
                global::Rhino.Geometry.Vector3d.ZAxis, normal,
                global::Rhino.Geometry.Point3d.Origin);
            // User's mousewheel spin around the surface normal —
            // applied at world origin so the rest of the chain
            // (translation last) lands the asset where they aimed.
            var spin = global::Rhino.Geometry.Transform.Rotation(
                spinRadians, normal,
                global::Rhino.Geometry.Point3d.Origin);
            var trans = global::Rhino.Geometry.Transform.Translation(
                pt - global::Rhino.Geometry.Point3d.Origin);
            var xform = trans * spin * rot;

            // Fast path: a previous drop of the same asset_base_id
            // already created an InstanceDefinition in this doc.
            // Reuse it — no file import, no geometry duplication.
            string assetBaseId = _grid_static_currentAssetBaseId;
            int cachedIdx = LookupCachedInstDef(doc, assetBaseId);
            if (cachedIdx >= 0)
            {
                var iid = doc.Objects.AddInstanceObject(cachedIdx, xform);
                if (iid != Guid.Empty)
                {
                    StampBlenderKitMetadata(doc, new[] { iid }, path);
                    doc.Views.Redraw();
                    BkLog.W($"ImportAtPoint: reused cached InstDef #{cachedIdx} for {assetBaseId}");
                    SetStatus($"Re-used block at ({pt.X:F2}, {pt.Y:F2}, {pt.Z:F2}): {System.IO.Path.GetFileName(path)}");
                    return;
                }
                // AddInstanceObject failed — fall through to the
                // re-import path so the user still gets their drop.
                BkLog.W($"ImportAtPoint: cached InstDef #{cachedIdx} present but AddInstanceObject failed; re-importing");
            }

            var preIds = new System.Collections.Generic.HashSet<Guid>();
            foreach (var o in doc.Objects) preIds.Add(o.Id);

            var script = $"_-Import \"{path}\" _Enter _Enter";
            if (!RhinoApp.RunScript(script, false))
            {
                SetStatus("Import command returned false.");
                return;
            }

            // Find newly-imported objects.
            var newIds = new System.Collections.Generic.List<Guid>();
            foreach (var o in doc.Objects)
            {
                if (preIds.Contains(o.Id)) continue;
                newIds.Add(o.Id);
                SuppressWireframe(o);
            }
            if (newIds.Count == 0)
            {
                SetStatus("Import produced no objects.");
                return;
            }

            // Pull the asset name from the captured drag hit so the
            // InstDef carries a human-readable label.
            string assetName = null;
            if (_grid_static_currentHit.HasValue
                && _grid_static_currentHit.Value.TryGetProperty("name", out var nv))
                assetName = nv.GetString();

            // Wrap the imported geometry into an InstanceDefinition
            // keyed by asset_base_id, then drop a single InstanceObject
            // at xform. Subsequent drops of the same asset reuse the
            // block via the fast path above.
            int defIdx = BlockifyImported(doc, newIds, assetBaseId, assetName, xform);
            if (defIdx >= 0)
            {
                StoreCachedInstDef(doc, assetBaseId, defIdx);
                // The new InstanceObject is the only object that
                // wasn't in preIds. Suppress wireframe on it
                // explicitly — InstanceObject doesn't inherit
                // attribute overrides from its component members,
                // so the per-object DisplayModeOverride needs to be
                // set on the instance itself. Then stamp metadata
                // for later attribution.
                var instIds = new System.Collections.Generic.List<Guid>();
                foreach (var o in doc.Objects)
                {
                    if (preIds.Contains(o.Id)) continue;
                    instIds.Add(o.Id);
                    SuppressWireframe(o);
                }
                StampBlenderKitMetadata(doc, instIds, path);
                BkLog.W($"ImportAtPoint: blockified {newIds.Count} objects into InstDef #{defIdx} ({assetName})");
            }
            else
            {
                // Blockify failed (no asset_base_id, or InstDef.Add
                // returned -1). Fall back to the original behaviour:
                // translate originals in place.
                foreach (var id in newIds) doc.Objects.Transform(id, xform, true);
                StampBlenderKitMetadata(doc, newIds, path);
            }
            doc.Views.Redraw();
            // Preview-cube cleanup is handled by ImportForDrop, which
            // wraps this method for drag flows. Click-import callers
            // never had a preview cube to begin with.
            SetStatus($"Dropped at ({pt.X:F2}, {pt.Y:F2}, {pt.Z:F2}): {System.IO.Path.GetFileName(path)}");
        }

        // ----- InstanceDefinition reuse cache -----
        // Maps RhinoDoc.RuntimeSerialNumber → (asset_base_id → InstanceDef
        // index). Lets repeated drops of the same asset reuse a single
        // block instead of duplicating geometry every time. Doc-scoped
        // because InstDef indexes mean nothing across documents.
        private static readonly System.Collections.Generic.Dictionary<uint,
            System.Collections.Generic.Dictionary<string, int>> _instDefCache = new();
        // Asset-base-id of the most recently grabbed hit. Captured by
        // StartDrag so the import path (which runs across closures and
        // task callbacks) can key its cache without re-reading the hit.
        private static string _grid_static_currentAssetBaseId;

        /// <summary>
        /// Run <paramref name="action"/> inside a single Rhino undo
        /// record so the entire BlenderKit operation (import + block
        /// creation + material assignment) collapses to one Ctrl+Z
        /// step instead of leaving the user with dozens of orphan
        /// AddObject events to undo individually. Mirrors the
        /// canonical Rhino-plugin pattern.
        /// </summary>
        private static void WithUndo(global::Rhino.RhinoDoc doc, string label, Action action)
        {
            if (doc == null) { action(); return; }
            uint serial = 0;
            try
            {
                serial = doc.BeginUndoRecord(label);
            }
            catch { /* very rarely returns 0 (max-records exhausted) — proceed without */ }
            try { action(); }
            finally
            {
                if (serial != 0)
                {
                    try { doc.EndUndoRecord(serial); } catch { }
                }
            }
        }

        private static int LookupCachedInstDef(global::Rhino.RhinoDoc doc, string assetBaseId)
        {
            if (doc == null || string.IsNullOrEmpty(assetBaseId)) return -1;
            if (_instDefCache.TryGetValue(doc.RuntimeSerialNumber, out var bag)
                && bag.TryGetValue(assetBaseId, out var idx))
            {
                // The user may have purged the InstDef from the file.
                // Verify it still exists; drop the stale entry on miss.
                if (idx < 0 || idx >= doc.InstanceDefinitions.Count)
                { bag.Remove(assetBaseId); }
                else
                {
                    var def = doc.InstanceDefinitions[idx];
                    if (def == null || def.IsDeleted) { bag.Remove(assetBaseId); }
                    else return idx;
                }
            }
            // Fallback: the file may have been opened from disk in a fresh
            // session (cache empty) but already contains an InstDef from a
            // previous run. Resolve by canonical name so we don't create
            // a parallel BK_<id>_2 block.
            var byName = doc.InstanceDefinitions.Find("BK_" + assetBaseId);
            if (byName != null && !byName.IsDeleted)
            {
                StoreCachedInstDef(doc, assetBaseId, byName.Index);
                return byName.Index;
            }
            return -1;
        }

        private static void StoreCachedInstDef(global::Rhino.RhinoDoc doc, string assetBaseId, int idx)
        {
            if (doc == null || string.IsNullOrEmpty(assetBaseId) || idx < 0) return;
            if (!_instDefCache.TryGetValue(doc.RuntimeSerialNumber, out var bag))
            {
                bag = new System.Collections.Generic.Dictionary<string, int>();
                _instDefCache[doc.RuntimeSerialNumber] = bag;
            }
            bag[assetBaseId] = idx;
        }

        /// <summary>
        /// Forget the per-doc InstDef cache so the next import re-blockifies.
        /// Returns the number of entries dropped. Doesn't delete the
        /// underlying InstanceDefinitions in the doc.
        /// </summary>
        public static int ClearInstDefCache(global::Rhino.RhinoDoc doc)
        {
            if (doc == null) return 0;
            if (!_instDefCache.TryGetValue(doc.RuntimeSerialNumber, out var bag)) return 0;
            int n = bag.Count;
            _instDefCache.Remove(doc.RuntimeSerialNumber);
            return n;
        }

        /// <summary>
        /// Wrap a freshly-imported group of objects into a Rhino
        /// InstanceDefinition and replace them with a single InstanceObject
        /// at <paramref name="xform"/>. Returns the InstDef index, or -1
        /// if blockification couldn't proceed (caller should fall back to
        /// just transforming the originals).
        /// </summary>
        private static int BlockifyImported(
            global::Rhino.RhinoDoc doc,
            System.Collections.Generic.IList<Guid> ids,
            string assetBaseId,
            string assetName,
            global::Rhino.Geometry.Transform xform)
        {
            if (doc == null || ids == null || ids.Count == 0) return -1;
            // InstanceDefinitions need a unique name — disambiguate by
            // suffixing if a previous run (or the user) created one with
            // the same root name.
            string defNameRoot = !string.IsNullOrEmpty(assetBaseId)
                ? "BK_" + assetBaseId
                : (!string.IsNullOrEmpty(assetName) ? "BK_" + assetName : "BK_imported");
            string defName = defNameRoot;
            int suffix = 1;
            // RhinoCommon: definitions are now always deleted permanently,
            // so the second-arg-bool overload is gone. Single-string Find
            // is the right one.
            while (doc.InstanceDefinitions.Find(defName) != null)
                defName = defNameRoot + "_" + (++suffix);

            try
            {
                var geos = new System.Collections.Generic.List<global::Rhino.Geometry.GeometryBase>();
                var attrs = new System.Collections.Generic.List<global::Rhino.DocObjects.ObjectAttributes>();
                foreach (var id in ids)
                {
                    var o = doc.Objects.Find(id);
                    if (o == null || o.Geometry == null) continue;
                    geos.Add(o.Geometry.Duplicate());
                    attrs.Add(o.Attributes.Duplicate());
                }
                if (geos.Count == 0) return -1;
                int defIdx = doc.InstanceDefinitions.Add(
                    defName, assetName ?? "", global::Rhino.Geometry.Point3d.Origin, geos, attrs);
                if (defIdx < 0) return -1;
                // The InstDef now owns duplicates of the geometry — delete
                // the loose originals so the doc holds exactly one copy.
                foreach (var id in ids)
                {
                    try { doc.Objects.Delete(id, true); } catch { }
                }
                var iid = doc.Objects.AddInstanceObject(defIdx, xform);
                if (iid == Guid.Empty)
                {
                    BkLog.W("BlockifyImported: AddInstanceObject returned Guid.Empty");
                    return -1;
                }
                return defIdx;
            }
            catch (Exception ex)
            {
                BkLog.W("BlockifyImported: " + ex.Message);
                return -1;
            }
        }

        // Cached "Rendered" display-mode description, looked up once.
        private static global::Rhino.Display.DisplayModeDescription _renderedDmd;

        private static global::Rhino.Display.DisplayModeDescription FindRenderedDmd()
        {
            if (_renderedDmd != null) return _renderedDmd;
            try
            {
                foreach (var m in global::Rhino.Display.DisplayModeDescription.GetDisplayModes())
                {
                    if (string.Equals(m.EnglishName, "Rendered", StringComparison.OrdinalIgnoreCase))
                    {
                        _renderedDmd = m;
                        return _renderedDmd;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Make the imported object render *without* mesh edges and Brep
        /// isocurves overlaid. Two switches:
        ///   * WireDensity = -1 hides Brep surface isocurves.
        ///   * Per-object DisplayMode override → "Rendered" hides the mesh
        ///     wireframe even when the viewport is in Shaded mode.
        /// Both are best-effort; failure here is purely cosmetic.
        /// </summary>
        private static void SuppressWireframe(global::Rhino.DocObjects.RhinoObject o)
        {
            try
            {
                var attrs = o.Attributes.Duplicate();
                attrs.WireDensity = -1;
                var dmd = FindRenderedDmd();
                if (dmd != null)
                {
                    // No viewportId arg → applies across every viewport.
                    attrs.SetDisplayModeOverride(dmd);
                }
                global::Rhino.RhinoDoc.ActiveDoc.Objects.ModifyAttributes(o, attrs, true);
            }
            catch (Exception ex)
            {
                BkLog.W("SuppressWireframe failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Tag the imported objects with BlenderKit metadata via the Rhino
        /// per-object UserDictionary. Lets us later recognise that an object
        /// originated from a particular asset (and which one), without
        /// having to inspect filenames or layers.
        /// </summary>
        private static void StampBlenderKitMetadata(global::Rhino.RhinoDoc doc,
            System.Collections.Generic.IList<Guid> ids, string sourcePath)
        {
            var hit = _grid_static_currentHit; // captured at drag-start (best-effort)
            string assetId = "", baseId = "", name = "", assetType = "";
            if (hit.HasValue)
            {
                if (hit.Value.TryGetProperty("id", out var v)) assetId = v.GetString() ?? "";
                if (hit.Value.TryGetProperty("assetBaseId", out v)) baseId = v.GetString() ?? "";
                if (hit.Value.TryGetProperty("name", out v)) name = v.GetString() ?? "";
                if (hit.Value.TryGetProperty("assetType", out v)) assetType = v.GetString() ?? "";
            }
            foreach (var id in ids)
            {
                var rhObj = doc.Objects.Find(id);
                if (rhObj == null) continue;
                var ud = rhObj.Attributes.UserDictionary;
                if (!string.IsNullOrEmpty(assetId)) ud.Set("blenderkit.asset_id", assetId);
                if (!string.IsNullOrEmpty(baseId))  ud.Set("blenderkit.asset_base_id", baseId);
                if (!string.IsNullOrEmpty(name))    ud.Set("blenderkit.name", name);
                if (!string.IsNullOrEmpty(assetType)) ud.Set("blenderkit.asset_type", assetType);
                ud.Set("blenderkit.source_path", sourcePath ?? "");
                ud.Set("blenderkit.imported_at", DateTime.UtcNow.ToString("O"));
                rhObj.CommitChanges();
            }
        }
        // Snapshot of the hit being dragged. Updated by StartDrag so the
        // metadata stamper can attribute imported objects to the right asset
        // even though the import callback fires across closures.
        private static JsonElement? _grid_static_currentHit;

        private void ImportAtPickedPoint(string path)
        {
            // Run on UI thread so RhinoDoc and the GetPoint prompt behave.
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    var doc = global::Rhino.RhinoDoc.ActiveDoc;
                    WithUndo(doc, "BlenderKit: Import asset (pick point)", () =>
                    {

                    // Cache fast-path: same asset_base_id already lives in
                    // the doc as an InstanceDefinition. Just ask for the
                    // placement point and AddInstanceObject — no file
                    // import.
                    string assetBaseIdFast = _grid_static_currentAssetBaseId;
                    int cachedFast = LookupCachedInstDef(doc, assetBaseIdFast);
                    if (cachedFast >= 0)
                    {
                        var gpFast = new global::Rhino.Input.Custom.GetPoint();
                        gpFast.SetCommandPrompt("Place cached block (Esc to leave at origin)");
                        var resFast = gpFast.Get();
                        var ptFast = resFast == global::Rhino.Input.GetResult.Point
                            ? gpFast.Point() : global::Rhino.Geometry.Point3d.Origin;
                        var xfFast = global::Rhino.Geometry.Transform.Translation(
                            ptFast - global::Rhino.Geometry.Point3d.Origin);
                        var iid = doc.Objects.AddInstanceObject(cachedFast, xfFast);
                        if (iid != Guid.Empty)
                        {
                            StampBlenderKitMetadata(doc, new[] { iid }, "(cached InstDef)");
                            doc.Views.Redraw();
                            BkLog.W($"ImportAtPickedPoint: reused cached InstDef #{cachedFast}");
                            SetStatus($"Re-used block at ({ptFast.X:F2}, {ptFast.Y:F2}, {ptFast.Z:F2})");
                            return;
                        }
                        // Fall through to a fresh import if InstDef placement failed.
                    }

                    var beforeIds = new HashSet<Guid>();
                    foreach (var o in doc.Objects) beforeIds.Add(o.Id);

                    SetStatus($"Importing {System.IO.Path.GetFileName(path)} — pick a point in the viewport (Esc = origin).");
                    var script = $"_-Import \"{path}\" _Enter _Enter";
                    var ok = RhinoApp.RunScript(script, false);
                    doc.Views.Redraw();
                    if (!ok) { AddMarkerCube(path); return; }

                    // Identify objects added by the import — we'll move them.
                    var added = new List<Guid>();
                    foreach (var o in doc.Objects)
                    {
                        if (!beforeIds.Contains(o.Id))
                        {
                            added.Add(o.Id);
                            SuppressWireframe(o);
                        }
                    }

                    var gp = new global::Rhino.Input.Custom.GetPoint();
                    gp.SetCommandPrompt("Place imported asset (Esc to leave at origin)");
                    var res = gp.Get();
                    var pt = res == global::Rhino.Input.GetResult.Point
                        ? gp.Point() : global::Rhino.Geometry.Point3d.Origin;
                    var xform = global::Rhino.Geometry.Transform.Translation(
                        pt - global::Rhino.Geometry.Point3d.Origin);

                    // Blockify so a future import of the same asset hits
                    // the cache fast-path. assetBaseId may be empty for
                    // direct file paths, which makes the cache key fall
                    // back to the asset's name; that's still better than
                    // duplicating geometry.
                    string assetName = null;
                    if (_grid_static_currentHit.HasValue
                        && _grid_static_currentHit.Value.TryGetProperty("name", out var nv))
                        assetName = nv.GetString();
                    int defIdx = BlockifyImported(doc, added, assetBaseIdFast, assetName, xform);
                    if (defIdx >= 0)
                    {
                        StoreCachedInstDef(doc, assetBaseIdFast, defIdx);
                        // The new InstanceObject also needs SuppressWireframe;
                        // InstanceObjects don't inherit DisplayModeOverride
                        // from their components.
                        var instIds = new List<Guid>();
                        foreach (var o in doc.Objects)
                        {
                            if (beforeIds.Contains(o.Id)) continue;
                            instIds.Add(o.Id);
                            SuppressWireframe(o);
                        }
                        StampBlenderKitMetadata(doc, instIds, path);
                    }
                    else
                    {
                        foreach (var id in added) doc.Objects.Transform(id, xform, true);
                        StampBlenderKitMetadata(doc, added, path);
                    }
                    doc.Views.Redraw();
                    SetStatus(res == global::Rhino.Input.GetResult.Point
                        ? $"Placed at ({pt.X:F2}, {pt.Y:F2}, {pt.Z:F2}): {System.IO.Path.GetFileName(path)}"
                        : $"Imported at origin: {System.IO.Path.GetFileName(path)}");
                    });
                }
                catch (Exception ex) { SetStatus("Drop error: " + ex.Message); }
            }));
        }

        private async void OnDownload()
        {
            if (_grid.SelectedHit is not JsonElement hit)
            {
                SetStatus("Select a result first.");
                return;
            }
            if (BlockedByPlan(hit)) return;
            await StartDownloadFor(hit);
        }

        /// <summary>
        /// True when the hit is a Full-plan asset and the current user
        /// can't download it. The authoritative signal is
        /// <c>canDownload</c> on the asset JSON — the server already
        /// knows whether the current api key has access (Full plan,
        /// trial, asset-purchased, validator override, etc.). Falls
        /// back to <c>isFree</c> + locally-cached <c>_hasFullPlan</c>
        /// for old API responses or before the profile loads.
        /// </summary>
        private bool BlockedByPlan(JsonElement hit)
        {
            // canDownload may be missing on older response shapes; treat
            // missing as "unknown" and fall through to the heuristic.
            if (hit.TryGetProperty("canDownload", out var cd))
            {
                if (cd.ValueKind == JsonValueKind.True) return false;
                if (cd.ValueKind == JsonValueKind.False)
                {
                    ShowFullPlanRequiredDialog();
                    return true;
                }
            }
            bool isFree = hit.TryGetProperty("isFree", out var f)
                          && f.ValueKind == JsonValueKind.True;
            if (isFree || _hasFullPlan) return false;
            ShowFullPlanRequiredDialog();
            return true;
        }

        /// <summary>
        /// Mirrors the Blender addon's "You need Full plan to get this
        /// item" prompt. Exposes Login / Get Full plan / Cancel buttons.
        /// </summary>
        private void ShowFullPlanRequiredDialog()
        {
            var dlg = new Dialog
            {
                Title = "Full plan required",
                ClientSize = new Eto.Drawing.Size(420, 200),
                Padding = new Eto.Drawing.Padding(16),
                Resizable = false,
            };
            var headline = new Label
            {
                Text = "🔒  This asset is part of the Full plan.",
                Font = SystemFonts.Bold(13),
                TextColor = BkColors.DarkText,
            };
            var body = new Label
            {
                Text = string.IsNullOrEmpty(_apiKey)
                    ? "Log in to BlenderKit and (if needed) subscribe to Full plan to download paid assets."
                    : "Your account is on the Free plan. Subscribe to Full plan to download paid assets.",
                Wrap = WrapMode.Word,
                TextColor = BkColors.DarkText,
            };

            var btnRow = new DynamicLayout();
            btnRow.BeginHorizontal();
            if (string.IsNullOrEmpty(_apiKey))
            {
                var loginBtn = new Button { Text = "Login" };
                loginBtn.Click += async (s, e) => { dlg.Close(); await Task.Yield(); OnLoginToggle(); };
                btnRow.Add(loginBtn);
            }
            var planBtn = new Button { Text = "Get Full plan" };
            planBtn.Click += (s, e) =>
            {
                dlg.Close();
                Process.Start(new ProcessStartInfo("https://www.blenderkit.com/plans/pricing/")
                    { UseShellExecute = true });
            };
            btnRow.Add(planBtn);
            var cancelBtn = new Button { Text = "Cancel" };
            cancelBtn.Click += (s, e) => dlg.Close();
            btnRow.Add(cancelBtn);
            btnRow.EndHorizontal();

            var layout = new DynamicLayout
            {
                Padding = new Eto.Drawing.Padding(8),
                Spacing = new Eto.Drawing.Size(0, 12),
            };
            layout.AddRow(headline);
            layout.AddRow(body);
            layout.AddRow(null);
            layout.AddRow(btnRow);
            dlg.Content = layout;
            dlg.BackgroundColor = BkColors.DarkBg;
            dlg.ShowModal(this);
        }

        private async Task StartDownloadFor(JsonElement hit)
        {
            var name = hit.TryGetProperty("name", out var nm) ? nm.GetString() : "(asset)";
            // Translate UI label like "2K" → "resolution_2K" the Go client wants.
            // "0.5K" maps to "resolution_0_5K".
            var sel = _resolution.SelectedValue?.ToString() ?? "2K";
            var resolution = "resolution_" + sel.Replace("0.5K", "0_5K");
            // Capture hit context for the import-side blockify pipeline.
            // ImportFile / ImportAtPickedPoint read these to look up the
            // InstDef cache and to stamp metadata on the new objects.
            _grid_static_currentHit = hit;
            _grid_static_currentAssetBaseId = hit.TryGetProperty("assetBaseId", out var ab)
                ? (ab.GetString() ?? "") : "";
            SetStatus($"Starting download: {name} @ {sel}…");
            try
            {
                _pendingDownloadId = await DownloadService.StartAsync(
                    hit, apiKey: _apiKey, globalDir: BlendkitPlugIn.DefaultGlobalDir,
                    resolution: resolution);
            }
            catch (Exception ex)
            {
                SetStatus("Download error: " + ex.Message);
            }
        }

        private void HandleTask(JsonElement task)
        {
            var type = task.TryGetProperty("task_type", out var t) ? (t.GetString() ?? "?") : "?";
            var status = task.TryGetProperty("status", out var s) ? (s.GetString() ?? "?") : "?";
            var taskId = task.TryGetProperty("task_id", out var id) ? (id.GetString() ?? "") : "";
            var idShort = taskId.Length > 8 ? taskId.Substring(0, 8) : taskId;

            if (type != "client_status")
                BkLog.W($"task type={type} status={status} id={idShort}");

            if (type == "search" && taskId == _pendingSearchId)
                HandleSearchTask(status, task);
            else if (type == "asset_download")
                // Route ALL download tasks; HandleDownloadTask figures out
                // which active drop (if any) gets the update by task_id, and
                // gracefully ignores tasks we aren't tracking. Critical for
                // parallel drag-drops — the singleton _pendingDownloadId
                // filter only matched the latest click-download.
                HandleDownloadTask(status, task);
            else if (type == "thumbnail_download" && status == "finished")
                HandleThumbnailTask(task);
            else if (type == "login" && status == "finished")
                HandleLoginTask(task);
            else if (type == "categories_update" && status == "finished")
                HandleCategoriesTask(task);
            else if (type == "run_blender_script")
            {
                // The unified /run_blender_script endpoint feeds both
                // GLB conversion (script_id="export_glb") and Rhino's
                // host-specific material extraction (script_path=…).
                // We disambiguate purely by which queue holds the
                // task_id — no enum on the task payload needed.
                // Always route — the Go client's cache fast-path can
                // emit "finished" before our action is registered;
                // HandleConvertTask buffers orphan results.
                if (_pendingConvertActions.ContainsKey(taskId))
                    HandleConvertTask(status, task, taskId);
                else if (_pendingMaterialDrops.ContainsKey(taskId))
                    HandleMaterialJsonTask(status, task, taskId);
            }
            else if (type == "ratings/get_bookmarks" && status == "finished")
                HandleBookmarksTask(task);
            else if (type == "profiles/get_user_profile")
                // Route any status — earlier versions of the Go client emit
                // the result with status="created" rather than "finished",
                // and HandleProfileTask gracefully no-ops if the result isn't
                // shaped like a profile yet.
                HandleProfileTask(task);
        }

        private void HandleConvertTask(string status, JsonElement task, string taskId)
        {
            if (status == "finished")
            {
                string glbPath = null;
                if (task.TryGetProperty("result", out var result)
                    && result.TryGetProperty("file_path", out var fp))
                    glbPath = fp.GetString();

                if (string.IsNullOrEmpty(glbPath))
                {
                    SetStatus("Convert finished but no file_path in result.");
                    return;
                }

                Action<string> action = null;
                if (_pendingConvertActions.TryGetValue(taskId, out action))
                {
                    _pendingConvertActions.Remove(taskId);
                    RhinoApp.InvokeOnUiThread((Action)(() => action(glbPath)));
                }
                else
                {
                    // Result arrived before the action was registered. Park it.
                    _orphanedConvertResults[taskId] = glbPath;
                }
            }
            else if (status == "error")
            {
                _pendingConvertActions.Remove(taskId);
                // Disable the matching drop's preview, if any.
                var drop = _drops.Find(d => d.ConvertTaskId == taskId);
                if (drop != null)
                {
                    drop.Preview.Enabled = false;
                    _drops.Remove(drop);
                }
                var msg = task.TryGetProperty("message", out var m) ? m.GetString() : "";
                SetStatus("Blender export failed: " + msg);
            }
            else
            {
                var msg = task.TryGetProperty("message", out var m) ? m.GetString() : "";
                if (!string.IsNullOrEmpty(msg)) SetStatus("Converting… " + msg);
            }
        }

        private void HandleCategoriesTask(JsonElement task)
        {
            if (!task.TryGetProperty("result", out var result)) return;
            CategoriesService.Ingest(result);
            // Refresh the category dropdown for the current asset type.
            RhinoApp.InvokeOnUiThread((Action)RefreshCategoryDropdown);
        }

        /// <summary>
        /// Surface the logged-in user's name and plan in the profile label.
        /// The Go client emits a `profiles/get_user_profile` task on subscribe
        /// (and after token refresh); the result wraps a `user` object with
        /// fullName, firstName, currentPlanName, etc.
        /// </summary>
        /// <summary>
        /// Result of /ratings/get_bookmarks (a search-shaped response whose
        /// `results[]` lists every asset the user has bookmarked). Cache the
        /// ids so the thumbnail grid can render a heart on each.
        /// </summary>
        private void HandleBookmarksTask(JsonElement task)
        {
            if (!task.TryGetProperty("result", out var result)) return;
            if (!result.TryGetProperty("results", out var arr)
                || arr.ValueKind != JsonValueKind.Array) return;
            _bookmarkedIds.Clear();
            foreach (var hit in arr.EnumerateArray())
            {
                if (hit.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    _bookmarkedIds.Add(id.GetString());
            }
            BkLog.W($"bookmarks: {_bookmarkedIds.Count} ids cached");
            // Re-render the grid so existing cells pick up the heart.
            RhinoApp.InvokeOnUiThread((Action)(() => _grid.SetBookmarkedIds(_bookmarkedIds)));
        }

        private void HandleProfileTask(JsonElement task)
        {
            BkLog.W("HandleProfileTask fired");
            if (!task.TryGetProperty("result", out var result))
            {
                BkLog.W("  no `result` on profile task");
                return;
            }
            if (result.ValueKind == JsonValueKind.Object)
            {
                var rkeys = new System.Collections.Generic.List<string>();
                foreach (var p in result.EnumerateObject()) rkeys.Add(p.Name);
                BkLog.W("  result keys: " + string.Join(",", rkeys));
            }
            else BkLog.W("  result kind=" + result.ValueKind);
            // Some BlenderKit responses wrap the profile in a `user` field;
            // others put fields at the top level. Probe both.
            var user = result.TryGetProperty("user", out var u) ? u : result;
            if (user.ValueKind != JsonValueKind.Object) return;

            string Get(string field) =>
                user.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                    ? (v.GetString() ?? "") : "";

            var name = Get("fullName");
            if (string.IsNullOrEmpty(name)) name = Get("firstName");
            if (string.IsNullOrEmpty(name)) name = "user";
            var plan = Get("currentPlanName");
            if (string.IsNullOrEmpty(plan)) plan = Get("planName");
            if (string.IsNullOrEmpty(plan)) plan = "Free";

            // Log the full profile object so we can see exactly which
            // fields the API returns — useful for diagnosing missing flags.
            var keys = new System.Collections.Generic.List<string>();
            int n = 0;
            foreach (var p in user.EnumerateObject())
            {
                if (++n > 40) break;
                var s = p.Value.ToString();
                keys.Add(p.Name + "=" + s.Substring(0, Math.Min(40, s.Length)));
            }
            BkLog.W("PROFILE keys: " + string.Join(", ", keys));

            // BlenderKit's /api/v1/me/ returns canEditAllAssets at the
            // *top level* of the response, NOT inside the `user` sub-object
            // (mirrors blenderkit/search.py:handle_get_user_profile in the
            // Blender add-on, which reads `task.result.get("canEditAllAssets")`).
            // We probe the top-level result first, then fall back to the user
            // object as a defensive measure for older / nested API shapes.
            // Some accounts also expose isStaff / isSuperuser; treat any
            // truthy match as "show validator widgets".
            bool isValidator = false;
            bool TryFlag(JsonElement obj, string key)
            {
                if (!obj.TryGetProperty(key, out var f)) return false;
                if (f.ValueKind == JsonValueKind.True) return true;
                if (f.ValueKind == JsonValueKind.String
                    && string.Equals(f.GetString(), "true", StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            }
            foreach (var key in new[] { "canEditAllAssets", "isStaff", "is_staff",
                                        "isSuperuser", "is_superuser", "staff", "superuser" })
            {
                if (TryFlag(result, key) || TryFlag(user, key))
                {
                    isValidator = true;
                    BkLog.W($"validator flag matched: {key}");
                    break;
                }
            }

            // BlenderKit's plan slugs in the API: "free", "full". Anything
            // that isn't free unlocks the paid catalogue, so treat any
            // non-free string as Full. Validators implicitly have Full.
            bool hasFull = isValidator
                || (!string.IsNullOrEmpty(plan)
                    && !plan.Equals("Free", StringComparison.OrdinalIgnoreCase));

            // User id from the profile — pinned to the "My uploads"
            // filter when the user enables it. Stored as int because
            // BlenderKit's api/v1/search endpoint accepts a numeric id.
            int userId = 0;
            if (user.TryGetProperty("id", out var idEl))
            {
                if (idEl.ValueKind == JsonValueKind.Number) userId = idEl.GetInt32();
                else if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var pid)) userId = pid;
            }

            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                bool wasAnonymous = !_hasFullPlan && _profileUserId == 0;
                _profileLabel.Text = $"Logged in as {name}. {plan} plan.";
                _loginBtn.Text = "Logout";
                _searchUrlBox.Visible = isValidator;
                _notifBtn.Visible = true;
                _hasFullPlan = hasFull;
                _profileUserId = userId;
                _grid.SetHasFullPlan(hasFull);
                // Re-evaluate filter visibility: "My uploads" is hidden
                // until login, so it needs a visibility refresh now.
                ApplyFilterVisibility();
                // Fresh login → re-run the current search so paid
                // assets that were locked under anonymous browsing
                // unlock immediately. Skip if we already had a profile
                // (transient profile re-fetches shouldn't churn
                // results).
                if (wasAnonymous)
                {
                    BkLog.W("HandleProfileTask: profile changed from anonymous → re-running search");
                    OnSearch();
                }
            }));
        }

        private void HandleLoginTask(JsonElement task)
        {
            if (!task.TryGetProperty("result", out var result)) return;
            var ak = AuthService.ExtractAccessToken(result);
            var rk = AuthService.ExtractRefreshToken(result);
            if (string.IsNullOrEmpty(ak)) { SetStatus("Login finished but no access_token in result."); return; }
            _apiKey = ak;
            AuthService.SaveTokens(ak, rk);
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                _profileLabel.Text = "Logged in.";
                _loginBtn.Text = "Logout";
                // Search re-run as soon as we have a valid api_key —
                // don't wait for the profile task. Subsequent searches
                // will carry the new key and unlock paid-plan assets
                // immediately. (HandleProfileTask also fires OnSearch
                // on the anonymous-transition path, but the profile
                // task can lag noticeably behind login completion.)
                BkLog.W("HandleLoginTask: api_key acquired → re-running search");
                OnSearch();
            }));
            SetStatus("Logged in successfully.");
        }

        private void HandleThumbnailTask(JsonElement task)
        {
            // Task data carries assetBaseId + image_path + thumbnail_type.
            // Only the "small" thumbs are used by the grid.
            if (!task.TryGetProperty("data", out var data)) return;
            var type = data.TryGetProperty("thumbnail_type", out var tt) ? tt.GetString() : "";
            if (type != "small") return;
            var id = data.TryGetProperty("assetBaseId", out var aid) ? aid.GetString() : "";
            var img = data.TryGetProperty("image_path", out var p) ? p.GetString() : "";
            RhinoApp.InvokeOnUiThread((Action)(() => _grid.UpdateThumbnail(id, img)));
        }

        private void HandleSearchTask(string status, JsonElement task)
        {
            if (status == "finished" && task.TryGetProperty("result", out var result))
                RenderResults(result);
            else if (status == "error" && task.TryGetProperty("message", out var msg))
                SetStatus("Search error: " + msg.GetString());
        }

        private void HandleDownloadTask(string status, JsonElement task)
        {
            var msg = task.TryGetProperty("message", out var m) ? m.GetString() : "";
            var progress = task.TryGetProperty("progress", out var p) ? p.GetInt32() : -1;
            var taskId = task.TryGetProperty("task_id", out var t) ? t.GetString() : "";

            // Find the matching active drop (if any). Drops without a task_id
            // are still mid-startup; not our concern yet.
            var drop = _drops.Find(d => !string.IsNullOrEmpty(d.DownloadTaskId) && d.DownloadTaskId == taskId);
            // If neither a tracked drop nor the click-download id, ignore.
            if (drop == null && taskId != _pendingDownloadId) return;

            if (status == "finished" && task.TryGetProperty("result", out var result))
            {
                if (!result.TryGetProperty("file_paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
                    return;
                foreach (var path in paths.EnumerateArray())
                {
                    var filePath = path.GetString();
                    if (string.IsNullOrEmpty(filePath)) continue;
                    if (DownloadService.IsRhinoImportable(filePath))
                    {
                        if (drop != null) ImportForDrop(drop, filePath);
                        else ImportFile(filePath);
                    }
                    else if (DownloadService.IsBlend(filePath))
                    {
                        // Material-type assets carry no geometry to export
                        // as glTF — Blender's gltf exporter rejects them.
                        // Route them through the material extractor that
                        // emits a JSON describing the Principled BSDF.
                        // Three sources for the asset type, in order: the
                        // active drop's captured type (drag flow), the
                        // captured hit (StartDownloadFor stashes this for
                        // the click + auto-test paths), then the live
                        // grid selection as a last resort.
                        bool IsMaterialHit(JsonElement hit) =>
                            hit.TryGetProperty("assetType", out var atv)
                            && string.Equals(atv.GetString(), "material", StringComparison.OrdinalIgnoreCase);
                        bool isMaterial = drop != null
                            ? drop.AssetType.Equals("material", StringComparison.OrdinalIgnoreCase)
                            : (_grid_static_currentHit.HasValue && IsMaterialHit(_grid_static_currentHit.Value))
                            || (_grid.SelectedHit is JsonElement hh && IsMaterialHit(hh));
                        if (isMaterial)
                            StartMaterialConvert(filePath, drop);
                        else if (drop != null) ConvertForDrop(filePath, drop);
                        else ConvertAndImport(filePath, dropMode: false);
                    }
                    else
                    {
                        SetStatus($"Downloaded {System.IO.Path.GetFileName(filePath)}, but {System.IO.Path.GetExtension(filePath)} isn't supported.");
                    }
                }
            }
            else if (status == "error")
            {
                if (drop != null)
                {
                    drop.Preview.Enabled = false;
                    _drops.Remove(drop);
                }
                if (msg != null && msg.Contains("401"))
                    SetStatus("This asset needs a logged-in account (Full plan). Try a free asset, or use Login.");
                else
                    SetStatus("Download error: " + msg);
            }
            else if (progress >= 0)
            {
                SetStatus($"Downloading… {progress}% — {msg}");
                if (drop != null && drop.Preview.Enabled)
                {
                    // Cap at 0.9 so the bar still moves while the convert step runs.
                    drop.Preview.Progress = Math.Min(progress / 100.0, 0.9);
                    drop.Preview.Label = $"Downloading {progress}%";
                    foreach (var v in global::Rhino.RhinoDoc.ActiveDoc.Views) v.Redraw();
                }
            }
        }

        /// <summary>
        /// .blend → .glb conversion bound to a specific ActiveDrop. Once the
        /// Go client returns the resulting .glb, place it via the drop's
        /// captured point + normal.
        /// </summary>
        private void ConvertForDrop(string blendPath, ActiveDrop drop)
        {
            if (BlenderService.FindBlenderExe() == null)
            {
                drop.Preview.Enabled = false;
                _drops.Remove(drop);
                SetStatus("Blender not found — install Blender from blender.org so .blend assets can be converted.");
                Process.Start(new ProcessStartInfo("https://www.blender.org/download/") { UseShellExecute = true });
                return;
            }
            drop.Preview.Label = "Converting…";
            Task.Run(async () =>
            {
                try
                {
                    var taskId = await BlenderConvertService.StartAsync(
                        blendPath, Process.GetCurrentProcess().Id);
                    if (string.IsNullOrEmpty(taskId))
                    {
                        SetStatus("Convert request returned no task_id.");
                        return;
                    }
                    RhinoApp.InvokeOnUiThread((Action)(() =>
                    {
                        drop.ConvertTaskId = taskId;
                        Action<string> action = glb => ImportForDrop(drop, glb);
                        // Did the cache fast-path beat us? If so, fire now.
                        if (_orphanedConvertResults.TryGetValue(taskId, out var orphanGlb))
                        {
                            _orphanedConvertResults.Remove(taskId);
                            action(orphanGlb);
                        }
                        else
                        {
                            _pendingConvertActions[taskId] = action;
                        }
                    }));
                }
                catch (Exception ex) { SetStatus("Convert request failed: " + ex.Message); }
            });
        }

        private void ImportFile(string path)
        {
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    // HDR / EXR aren't geometry — they're image-based
                    // environments. Bind the file as the doc's render
                    // environment instead of dropping it as a Picture.
                    if (DownloadService.IsHdrImage(path))
                    {
                        SetEnvironmentFromFile(path);
                        return;
                    }
                    var doc = global::Rhino.RhinoDoc.ActiveDoc;
                    WithUndo(doc, "BlenderKit: Import asset", () =>
                    {

                    // Cache fast-path: same asset_base_id already
                    // blockified earlier in the session. Place a single
                    // InstanceObject at world origin and skip the file
                    // import entirely.
                    string assetBaseIdFast = _grid_static_currentAssetBaseId;
                    int cachedFast = LookupCachedInstDef(doc, assetBaseIdFast);
                    if (cachedFast >= 0)
                    {
                        var iid = doc.Objects.AddInstanceObject(cachedFast, global::Rhino.Geometry.Transform.Identity);
                        if (iid != Guid.Empty)
                        {
                            StampBlenderKitMetadata(doc, new[] { iid }, "(cached InstDef)");
                            doc.Views.Redraw();
                            BkLog.W($"ImportFile: reused cached InstDef #{cachedFast}");
                            SetStatus($"Re-used block: {System.IO.Path.GetFileName(path)}");
                            return;
                        }
                    }

                    var preIds = new System.Collections.Generic.HashSet<Guid>();
                    foreach (var o in doc.Objects) preIds.Add(o.Id);

                    var script = $"_-Import \"{path}\" _Enter _Enter";
                    var ok = RhinoApp.RunScript(script, false);
                    if (ok)
                    {
                        // Suppress wireframe on every just-imported object.
                        var newIds = new System.Collections.Generic.List<Guid>();
                        foreach (var o in doc.Objects)
                        {
                            if (preIds.Contains(o.Id)) continue;
                            newIds.Add(o.Id);
                            SuppressWireframe(o);
                        }
                        // Blockify so the next click-import of the same
                        // asset hits the fast path above.
                        string assetName = null;
                        if (_grid_static_currentHit.HasValue
                            && _grid_static_currentHit.Value.TryGetProperty("name", out var nv))
                            assetName = nv.GetString();
                        int defIdx = BlockifyImported(doc, newIds, assetBaseIdFast, assetName,
                            global::Rhino.Geometry.Transform.Identity);
                        if (defIdx >= 0)
                        {
                            StoreCachedInstDef(doc, assetBaseIdFast, defIdx);
                            // Apply wireframe suppression to the new
                            // InstanceObject too — see ImportAtPoint
                            // comment. Components don't propagate.
                            var instIds = new System.Collections.Generic.List<Guid>();
                            foreach (var o in doc.Objects)
                            {
                                if (preIds.Contains(o.Id)) continue;
                                instIds.Add(o.Id);
                                SuppressWireframe(o);
                            }
                            StampBlenderKitMetadata(doc, instIds, path);
                            BkLog.W($"ImportFile: blockified {newIds.Count} objs into InstDef #{defIdx} (assetBaseId='{assetBaseIdFast}', name='{assetName}')");
                        }
                        else
                        {
                            StampBlenderKitMetadata(doc, newIds, path);
                            BkLog.W($"ImportFile: blockify skipped/failed for assetBaseId='{assetBaseIdFast}' (newIds={newIds.Count})");
                        }
                    }
                    doc.Views.Redraw();
                    if (ok)
                    {
                        SetStatus($"Imported {System.IO.Path.GetFileName(path)}.");
                    }
                    else
                    {
                        // Drop-test fallback: many BlenderKit-exported .glb
                        // files don't load in Rhino's importer yet. Drop a
                        // marker cube at origin so we can verify the drag-drop
                        // pipeline end-to-end while the export is being fixed.
                        AddMarkerCube(path);
                    }
                    });
                }
                catch (Exception ex) { SetStatus("Import error: " + ex.Message); }
            }));
        }

        /// <summary>
        /// Ask the Go client to convert a downloaded .blend into a Rhino-
        /// friendly uncompressed .glb. The conversion runs as a task on the
        /// client; when it finishes, the /report poller routes back here via
        /// HandleConvertTask, which dispatches the stored callback to either
        /// place the .glb at the captured drop point, prompt for a point, or
        /// import to the origin.
        /// </summary>
        private void ConvertAndImport(string blendPath, bool dropMode)
        {
            var name = System.IO.Path.GetFileName(blendPath);
            if (BlenderService.FindBlenderExe() == null)
            {
                SetStatus("Blender not found — install Blender from blender.org so .blend assets can be converted.");
                Process.Start(new ProcessStartInfo("https://www.blender.org/download/") { UseShellExecute = true });
                return;
            }
            SetStatus($"Converting {name} via Blender → .glb…");
            Task.Run(async () =>
            {
                try
                {
                    var taskId = await BlenderConvertService.StartAsync(
                        blendPath, Process.GetCurrentProcess().Id);
                    if (string.IsNullOrEmpty(taskId))
                    {
                        SetStatus("Convert request returned no task_id.");
                        return;
                    }
                    // Click-download flow only — drag flow goes through
                    // ConvertForDrop. This entry imports at origin.
                    RhinoApp.InvokeOnUiThread((Action)(() =>
                    {
                        Action<string> action = glb => ImportFile(glb);
                        if (_orphanedConvertResults.TryGetValue(taskId, out var orphanGlb))
                        {
                            _orphanedConvertResults.Remove(taskId);
                            action(orphanGlb);
                        }
                        else
                        {
                            _pendingConvertActions[taskId] = action;
                        }
                    }));
                }
                catch (Exception ex)
                {
                    SetStatus("Convert request failed: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Bind an HDR/EXR file as the active document's render
        /// environment via the Rhino 8 RDK API. The legacy `_-Background
        /// _OpenImage` script-command silently fails in Rhino 8 (returns
        /// false), so we build the RenderTexture + RenderEnvironment
        /// directly and assign it as the document background.
        /// </summary>
        private void SetEnvironmentFromFile(string path)
        {
            try
            {
                var doc = global::Rhino.RhinoDoc.ActiveDoc;
                if (doc == null || !File.Exists(path))
                {
                    SetStatus("HDR not found: " + path);
                    return;
                }

                // Step 1: a RenderTexture pointing at the HDR/EXR file.
                var tex = global::Rhino.Render.RenderContentType.NewContentFromTypeId(
                    global::Rhino.Render.ContentUuids.HDRTextureType, doc) as global::Rhino.Render.RenderTexture;
                if (tex == null)
                {
                    SetStatus("Couldn't create RenderTexture for HDR.");
                    return;
                }
                tex.BeginChange(global::Rhino.Render.RenderContent.ChangeContexts.Program);
                tex.SetParameter("filename", path);
                tex.EndChange();

                // Step 2: a basic environment to host the texture. Type
                // GUID is the well-known "basic environment" id from
                // McNeel's RDK samples.
                var envTypeId = new Guid("ba51ce00-ba51-ce00-ba51-ceba51ce0000");
                var env = global::Rhino.Render.RenderContent.Create(envTypeId,
                    global::Rhino.Render.RenderContent.ShowContentChooserFlags.None, doc)
                    as global::Rhino.Render.RenderEnvironment;
                if (env == null)
                {
                    SetStatus("Couldn't create RenderEnvironment.");
                    return;
                }
                env.BeginChange(global::Rhino.Render.RenderContent.ChangeContexts.Program);
                env.SetChild(tex, "texture");
                env.Name = Path.GetFileNameWithoutExtension(path);
                env.EndChange();

                // Step 3: register with the doc and make it the active
                // background. RenderSettings is value-like — re-assign
                // the modified copy back to the doc to commit.
                doc.RenderEnvironments.Add(env);
                var rs = doc.RenderSettings;
                rs.SetRenderEnvironment(global::Rhino.Render.RenderSettings.EnvironmentUsage.Background, env);
                rs.BackgroundStyle = global::Rhino.Display.BackgroundStyle.Environment;
                doc.RenderSettings = rs;
                doc.Views.Redraw();
                SetStatus("Set as render background: " + Path.GetFileName(path));
                return;
            }
            catch (Exception ex)
            {
                BkLog.W("SetEnvironmentFromFile (RDK) failed: " + ex.Message);
            }

            // RDK path failed (mismatched Rhino version, RDK not loaded,
            // etc.). Open the Lighting panel so the user can drop the
            // HDR there manually.
            try { RhinoApp.RunScript("_-Lighting", false); } catch { }
            try { RhinoApp.RunScript("_-RenderEnvironments", false); } catch { }
            SetStatus("Downloaded " + Path.GetFileName(path)
                + " — open Render menu → Lighting and set this file as background.");
        }

        // ---------- Material assets (.blend → JSON → Rhino PBR material) ----------

        // Track in-flight material extractions by Go-client task id, with
        // an optional ActiveDrop binding so the drop preview goes away on
        // completion. Drops aren't strictly required for materials (the
        // user normally just wants the material added to the doc), but
        // tracking lets us close the drop's cube when one was started.
        // ConcurrentDictionary so the /report poller (background task)
        // and the StartMaterialConvert continuation (Task.Run) can both
        // touch it without locks. The previous Dictionary version had a
        // race: status=finished could arrive on the poller thread before
        // the UI-thread continuation registered the taskId, in which
        // case the task was dropped silently.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ActiveDrop> _pendingMaterialDrops
            = new System.Collections.Concurrent.ConcurrentDictionary<string, ActiveDrop>();

        private void StartMaterialConvert(string blendPath, ActiveDrop drop)
        {
            if (BlenderService.FindBlenderExe() == null)
            {
                if (drop != null) { drop.Preview.Enabled = false; _drops.Remove(drop); }
                SetStatus("Blender not found — install Blender to extract material info.");
                Process.Start(new ProcessStartInfo("https://www.blender.org/download/")
                    { UseShellExecute = true });
                return;
            }
            if (drop != null) drop.Preview.Label = "Extracting material…";
            SetStatus($"Extracting material from {System.IO.Path.GetFileName(blendPath)}…");
            Task.Run(async () =>
            {
                try
                {
                    var taskId = await MaterialConvertService.StartAsync(
                        blendPath, Process.GetCurrentProcess().Id);
                    if (string.IsNullOrEmpty(taskId))
                    {
                        SetStatus("Material extract returned no task_id.");
                        return;
                    }
                    // Register synchronously on the calling thread so
                    // the /report poller (which can fire status=created
                    // and even status=finished within milliseconds of
                    // StartAsync returning) sees the taskId and routes
                    // the result through HandleMaterialJsonTask.
                    _pendingMaterialDrops[taskId] = drop; // null is fine
                    if (drop != null)
                    {
                        RhinoApp.InvokeOnUiThread((Action)(() =>
                        {
                            drop.ConvertTaskId = taskId;
                        }));
                    }
                }
                catch (Exception ex)
                {
                    SetStatus("Material convert request failed: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Assign the doc-Materials material at <paramref name="matIdx"/>
        /// to the RhinoObject with id <paramref name="hitId"/>. Mirrors
        /// the Blender addon's drag-drop "target object/slot" behavior.
        ///
        /// Three cases:
        ///   1. Plain RhinoObject (Mesh / Brep / Extrusion): set
        ///      MaterialFromObject + MaterialIndex on its attributes.
        ///   2. InstanceObject (block — what our blockified BlenderKit
        ///      imports become): the InstanceObject's per-attribute
        ///      MaterialIndex is *not* what controls rendering of the
        ///      block's members. The members each carry their own
        ///      attributes with MaterialFromObject; until they're
        ///      flipped to MaterialFromParent the parent material is
        ///      ignored. We update each member to MaterialFromObject +
        ///      our matIdx (changes how every instance of this block
        ///      renders, which for BlenderKit's "one asset = one
        ///      InstDef" pattern is the expected outcome).
        /// </summary>
        private static void AssignMaterialToObject(int matIdx, Guid hitId)
        {
            var doc = global::Rhino.RhinoDoc.ActiveDoc;
            if (doc == null) return;
            uint serial = 0;
            try { serial = doc.BeginUndoRecord("BlenderKit: Assign material"); } catch { }
            try
            {
                var obj = doc.Objects.Find(hitId);
                if (obj == null)
                {
                    BkLog.W($"AssignMaterialToObject: hit id {hitId} not found in doc");
                    return;
                }
                if (matIdx < 0 || matIdx >= doc.Materials.Count)
                {
                    BkLog.W($"AssignMaterialToObject: bad mat idx {matIdx}");
                    return;
                }
                var mat = doc.Materials[matIdx];
                // Find the RenderMaterial that wraps this Material so the
                // Render-content panel reflects the assignment too.
                global::Rhino.Render.RenderMaterial rm = null;
                try
                {
                    foreach (var r in doc.RenderMaterials)
                    {
                        if (r == null) continue;
                        if (string.Equals(r.Name, mat?.Name, StringComparison.Ordinal))
                        { rm = r; break; }
                    }
                }
                catch { }

                // Branch on InstanceObject — per the comment above,
                // editing the parent attrs alone has no visible effect.
                if (obj is global::Rhino.DocObjects.InstanceObject inst
                    && inst.InstanceDefinition != null)
                {
                    int defIdx = inst.InstanceDefinition.Index;
                    var members = inst.InstanceDefinition.GetObjects() ?? Array.Empty<global::Rhino.DocObjects.RhinoObject>();
                    int touched = 0;
                    foreach (var member in members)
                    {
                        if (member == null) continue;
                        try
                        {
                            var ma = member.Attributes.Duplicate();
                            ma.MaterialSource = global::Rhino.DocObjects.ObjectMaterialSource.MaterialFromObject;
                            ma.MaterialIndex = matIdx;
                            doc.Objects.ModifyAttributes(member, ma, true);
                            if (rm != null)
                            {
                                try { member.RenderMaterial = rm; member.CommitChanges(); } catch { }
                            }
                            touched++;
                        }
                        catch (Exception ex) { BkLog.W("InstDef member modify failed: " + ex.Message); }
                    }
                    BkLog.W($"AssignMaterialToObject: mat #{matIdx} → InstDef #{defIdx}, {touched} member(s) updated");
                    doc.Views.Redraw();
                    return;
                }

                // Plain object branch — Mesh / Brep / Extrusion / etc.
                BkLog.W($"AssignMaterialToObject: hit obj={hitId} geom={obj.Geometry?.GetType().Name} matSource={obj.Attributes.MaterialSource} matIdx={obj.Attributes.MaterialIndex}");
                var sa = obj.Attributes.Duplicate();
                sa.MaterialSource = global::Rhino.DocObjects.ObjectMaterialSource.MaterialFromObject;
                sa.MaterialIndex = matIdx;
                bool modified = doc.Objects.ModifyAttributes(obj, sa, true);
                BkLog.W($"AssignMaterialToObject: ModifyAttributes returned {modified}");
                if (rm != null)
                {
                    try
                    {
                        // RenderMaterial setter does the equivalent of
                        // ModifyAttributes for the RDK side — sets the
                        // object's render material and propagates through
                        // doc.RenderMaterials. The two together cover both
                        // the legacy materials API (Materials panel) and
                        // the newer render-content API (Cycles / Rendered
                        // viewport).
                        obj.RenderMaterial = rm;
                        obj.CommitChanges();
                        BkLog.W($"AssignMaterialToObject: also set RenderMaterial '{rm.Name}'");
                    }
                    catch (Exception rmEx) { BkLog.W("RenderMaterial set failed: " + rmEx.Message); }
                }
                // Re-fetch and verify the assignment stuck. Rhino sometimes
                // ignores ModifyAttributes silently when an object is in
                // a special state (locked layer, etc.).
                var verify = doc.Objects.Find(hitId);
                if (verify != null)
                {
                    BkLog.W($"AssignMaterialToObject: post-check matSource={verify.Attributes.MaterialSource} matIdx={verify.Attributes.MaterialIndex}");
                }
                doc.Views.Redraw();
                BkLog.W($"AssignMaterialToObject: mat #{matIdx} → object {hitId}");
            }
            catch (Exception ex)
            {
                BkLog.W("AssignMaterialToObject error: " + ex.Message);
            }
            finally
            {
                if (serial != 0)
                {
                    try { doc.EndUndoRecord(serial); } catch { }
                }
            }
        }

        private void HandleMaterialJsonTask(string status, JsonElement task, string taskId)
        {
            if (status == "finished")
            {
                string outputPath = null;
                if (task.TryGetProperty("result", out var result)
                    && result.TryGetProperty("file_path", out var fp))
                    outputPath = fp.GetString();
                _pendingMaterialDrops.TryRemove(taskId, out var drop);

                if (string.IsNullOrEmpty(outputPath))
                {
                    SetStatus("Material extract finished but no output path returned.");
                    return;
                }
                RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    // Build the material first; for drag-drops we then
                    // assign it to the specific object the user dropped
                    // onto. Mirrors the Blender addon's "target object /
                    // target slot" — drop on a particular mesh, that
                    // mesh gets the material; drop into empty space and
                    // the material just goes into the Materials panel.
                    var idx = MaterialJsonImporter.ImportFromOutput(outputPath);
                    if (idx < 0)
                    {
                        SetStatus("Material output loaded but Rhino import failed; see panel log.");
                    }
                    else if (drop != null && drop.HitObjectId != Guid.Empty)
                    {
                        AssignMaterialToObject(idx, drop.HitObjectId);
                        SetStatus($"Material assigned to dropped-on object (index {idx}).");
                    }
                    else
                    {
                        SetStatus($"Material added (index {idx}). Find it in the Materials panel.");
                    }
                    if (drop != null)
                    {
                        drop.Preview.Enabled = false;
                        _drops.Remove(drop);
                    }
                }));
            }
            else if (status == "error")
            {
                _pendingMaterialDrops.TryRemove(taskId, out var drop);
                if (drop != null) { drop.Preview.Enabled = false; _drops.Remove(drop); }
                var msg = task.TryGetProperty("message", out var m) ? m.GetString() : "";
                SetStatus("Material extract failed: " + msg);
            }
            else
            {
                var msg = task.TryGetProperty("message", out var m) ? m.GetString() : "";
                if (!string.IsNullOrEmpty(msg)) SetStatus("Materials… " + msg);
            }
        }

        private void AddMarkerCube(string sourcePath)
        {
            var doc = global::Rhino.RhinoDoc.ActiveDoc;
            var box = new global::Rhino.Geometry.Box(
                global::Rhino.Geometry.Plane.WorldXY,
                new global::Rhino.Geometry.Interval(-0.5, 0.5),
                new global::Rhino.Geometry.Interval(-0.5, 0.5),
                new global::Rhino.Geometry.Interval(-0.5, 0.5));
            doc.Objects.AddBox(box);
            doc.Views.Redraw();
            SetStatus($"Import failed for {System.IO.Path.GetFileName(sourcePath)} — added a marker cube at origin instead so we can verify the drop pipeline.");
        }

        private void RenderResults(JsonElement result)
        {
            var append = _loadingMore;
            if (!append) _hits.Clear();
            _nextUrl = result.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            _resultCount = result.TryGetProperty("count", out var cnt) && cnt.ValueKind == JsonValueKind.Number ? cnt.GetInt32() : 0;
            _loadingMore = false;

            if (!result.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                SetStatus("No results.");
                RhinoApp.InvokeOnUiThread((Action)(() => _grid.SetHits(_hits, append)));
                return;
            }

            // No more glTF-only filtering — we now download .blend files
            // and convert via a headless Blender pass (BlenderService), so
            // every asset is reachable.
            foreach (var hit in arr.EnumerateArray()) _hits.Add(hit.Clone());
            RhinoApp.InvokeOnUiThread((Action)(() => _grid.SetHits(_hits, append)));
            SetStatus($"{_hits.Count} of {_resultCount} results. Double-click or drag.");

            // Self-test: if the test command primed a query, auto-download
            // the first hit so the whole pipeline runs without UI clicks.
            // Run via the UI thread — StartDownloadFor reads
            // _resolution.SelectedValue which is an Eto control and throws
            // when touched from the search task's background thread.
            if (!string.IsNullOrEmpty(BlendkitPlugIn.TestQuery) && _hits.Count > 0)
            {
                var first = _hits[0];
                BlendkitPlugIn.TestQuery = null; // one-shot
                BkLog.W($"[TEST] auto-downloading first hit: {(first.TryGetProperty("name", out var nm) ? nm.GetString() : "?")}");
                RhinoApp.InvokeOnUiThread((Action)(() => { _ = StartDownloadFor(first); }));
            }
        }

        private void SetStatus(string msg)
        {
            BkLog.W(msg);
            RhinoApp.InvokeOnUiThread((Action)(() => { _status.Text = msg; }));
        }

        protected override void OnUnLoad(EventArgs e)
        {
            // Don't dispose the poller here — Eto fires OnUnLoad when a
            // dockable panel is *re-parented* (dock/undock/move), not only
            // when truly closed. Stopping the poller broke the report stream
            // after a single heartbeat. Let the poller live for the Rhino
            // session; the plugin's OnShutdown stops the Go client.
            base.OnUnLoad(e);
        }
    }
}
