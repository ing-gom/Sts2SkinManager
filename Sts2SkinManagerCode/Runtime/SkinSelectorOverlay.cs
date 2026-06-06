using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using Sts2SkinManager.Config;
using Sts2SkinManager.Discovery;
using Sts2SkinManager.Localization;

namespace Sts2SkinManager.Runtime;

public static class SkinSelectorOverlay
{
    private static string _choicesPath = "";
    private static Dictionary<string, List<DetectedSkinMod>>? _byCharacter;
    private static List<DetectedSkinMod> _cardMods = new();
    private static List<DetectedSkinMod> _mixedMods = new();
    // Mixed mods that bundle a revertible body (ATA-style) — only these show the look toggle.
    private static IReadOnlySet<string> _vanillaBodyEligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static List<UnifiedModItem> _allMods = new();
    private static IReadOnlySet<string> _baseCharacters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Custom-character mods (BaseLib-style new characters, e.g. Watcher / Ryoshu). SkinManager
    // doesn't manage these (they stay auto-mounted), but they're surfaced in the Applied summary
    // so the player can see which extra characters are loaded.
    private static List<SkinModScanner.SkippedCustomCharacterMod> _customCharacters = new();

    private static OptionButton? _opt;
    private static Label? _label;
    private static Control? _hbox;
    private static Button? _variantEditBtn;
    private static Button? _variantSaveBtn;
    private static Button? _variantCancelBtn;
    private static LineEdit? _variantEditLine;
    private static Label? _previewHoverIcon;
    private static Control? _previewContainer;
    private static TextureRect? _previewRect;
    private static Label? _previewCaption;
    private static bool _previewHovered;
    private static bool _previewAvailable;
    private static long _previewHoverExitToken;
    private static string? _hoveredCardModId;
    private static readonly Dictionary<string, ImageTexture?> _previewCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageTexture?> _cardPreviewCache = new(StringComparer.OrdinalIgnoreCase);
    // Outer collapsible wrapper: a single toggle header reveals/hides the whole skin manager UI
    // (Save/Discard + the tab panel). Default = collapsed so the character select screen stays clean.
    // Inner layout: one TabContainer with four tabs (Applied / Card skins / Mixed / Other), tab
    // headers along the top. Tab titles are kept short so all four fit without overflow arrows.
    private static VBoxContainer? _accordionVBox;
    private static Button? _outerToggleBtn;
    private static VBoxContainer? _outerBody;
    private static bool _outerExpanded = false;
    private static TabContainer? _tabContainer;
    private static Button? _cardPackSaveBtn;
    private static Button? _cardPackDiscardBtn;
    private static int _appliedTabIndex = -1;
    private static int _cardPackTabIndex = -1;
    private static int _mixedTabIndex = -1;
    private static int _allModsTabIndex = -1;
    private static int _customCharTabIndex = -1;

    private static VBoxContainer? _cardPackRows;
    private static VBoxContainer? _mixedRows;
    private static VBoxContainer? _allModsRows;
    private static VBoxContainer? _customCharRows;

    // Read-only "Applied" tab — shows what the game actually has loaded right now (boot snapshot),
    // with a "(after restart)" annotation when the selection differs and isn't applied yet.
    private static VBoxContainer? _appliedRows;
    private static Button? _appliedCopyBtn;

    // Pending (in-memory) state shared by character dropdown + card pack panel + mixed-addon panel
    // + All Mods decision panel. Mutations here don't touch disk; OnSave commits to choices.json
    // and triggers the modal.
    private static CardPacksConfig? _pendingCardPacks;
    private static CardPacksConfig? _pendingMixedAddons;
    // Mixed mods the user flagged "vanilla body (keep cards)" — surfaced as a per-row 🧍 toggle in
    // the mixed-addon panel. Committed to choices.VanillaBodyMods on OnSave (→ _vanilla_body_mods).
    private static HashSet<string> _pendingVanillaBody = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _pendingActiveByCharacter = new(StringComparer.OrdinalIgnoreCase);
    // Per-mod pending override from the All Mods tab. The action sentinel encodes the user's
    // chosen target classification — currently only "skip" is reachable from the UI (via the
    // mod_list disable path); the rest of the sentinels (auto / skin:<char>) remain wired up
    // for future use or manual skin_choices.json edits, but the tab itself only writes "skip".
    private static readonly Dictionary<string, string> _pendingAllModsDecisions = new(StringComparer.OrdinalIgnoreCase);

    // Boot snapshot — what the game actually has loaded right now. dirty = (effective state != boot snapshot).
    // Stays dirty until the user restarts (which re-captures the snapshot). OnDiscard restores everything to this.
    private static CardPacksConfig? _bootSnapshotCardPacks;
    private static CardPacksConfig? _bootSnapshotMixedAddons;
    private static HashSet<string> _bootSnapshotVanillaBody = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _bootSnapshotActive = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _bootSnapshotDllAssignments = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _bootSnapshotDllSkipped = new(StringComparer.OrdinalIgnoreCase);
    // modId → is_enabled state read from settings.save mod_list at boot. Used as the dirty-check
    // baseline for the Other Mods tab's enable/disable checkbox.
    private static readonly Dictionary<string, bool> _bootSnapshotModEnabled = new(StringComparer.OrdinalIgnoreCase);
    // Pending per-mod is_enabled changes from the Other Mods tab. Save applies these to
    // settings.save via Sts2SettingsWriter.ApplyModEnabledState. Restart picks up the change.
    private static readonly Dictionary<string, bool> _pendingModEnabled = new(StringComparer.OrdinalIgnoreCase);

    // Set by MainFile after the watcher is constructed; OnDiscard calls NoteSavedAsApplied()
    // so the post-discard disk write doesn't trigger a phantom restart modal.
    private static ChoicesFileWatcher? _watcher;
    public static void SetWatcher(ChoicesFileWatcher w) => _watcher = w;

    private static Node? _lastScreen;
    private static string _currentCharacter = "";
    private static bool _suppressNextItemSelected;
    private static bool _localeChangeSubscribed;
    private static bool _alreadyHandledThisEvent;

    public static void Configure(
        string choicesPath,
        Dictionary<string, List<DetectedSkinMod>> byCharacter,
        List<DetectedSkinMod> cardMods,
        List<DetectedSkinMod> mixedMods,
        List<UnifiedModItem>? allMods = null,
        IReadOnlySet<string>? baseCharacters = null,
        IReadOnlyDictionary<string, bool>? bootModEnabled = null,
        IReadOnlySet<string>? vanillaBodyEligible = null,
        List<SkinModScanner.SkippedCustomCharacterMod>? customCharacters = null)
    {
        _choicesPath = choicesPath;
        _byCharacter = byCharacter;
        _cardMods = cardMods;
        _mixedMods = mixedMods;
        _allMods = allMods ?? new List<UnifiedModItem>();
        _baseCharacters = baseCharacters ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _vanillaBodyEligible = vanillaBodyEligible ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _customCharacters = customCharacters ?? new List<SkinModScanner.SkippedCustomCharacterMod>();

        var initial = SkinChoicesConfig.LoadOrEmpty(_choicesPath);
        _pendingCardPacks = ClonePacks(initial.CardPacks);
        _pendingMixedAddons = ClonePacks(initial.MixedAddons);
        _pendingVanillaBody = new HashSet<string>(initial.VanillaBodyMods, StringComparer.OrdinalIgnoreCase);
        _bootSnapshotCardPacks = ClonePacks(initial.CardPacks);
        _bootSnapshotMixedAddons = ClonePacks(initial.MixedAddons);
        _bootSnapshotVanillaBody = new HashSet<string>(initial.VanillaBodyMods, StringComparer.OrdinalIgnoreCase);
        _bootSnapshotActive.Clear();
        foreach (var kv in initial.Characters) _bootSnapshotActive[kv.Key] = kv.Value.Active ?? "default";
        _bootSnapshotDllAssignments.Clear();
        foreach (var kv in initial.DllSkinAssignments) _bootSnapshotDllAssignments[kv.Key] = kv.Value;
        _bootSnapshotDllSkipped.Clear();
        foreach (var s in initial.DllSkinSkipped) _bootSnapshotDllSkipped.Add(s);
        _bootSnapshotModEnabled.Clear();
        if (bootModEnabled != null)
        {
            foreach (var kv in bootModEnabled) _bootSnapshotModEnabled[kv.Key] = kv.Value;
        }
        _pendingActiveByCharacter.Clear();
        _pendingAllModsDecisions.Clear();
        _pendingModEnabled.Clear();
        _previewHovered = false;
    }

    public static void Attach(Node screen)
    {
        Callable.From(() => DoAttach(screen)).CallDeferred();
    }

    public enum OverlayAnchor { Left, Right }
    private static OverlayAnchor _overlayAnchor = OverlayAnchor.Right;

    // ModConfigBridge calls this on startup (persisted value) and on user change.
    // Accepts "Top Left" / "Top Right" (case-insensitive), with prefix match so "Left"/"Right" also work.
    public static void SetAnchor(string? value)
    {
        var anchor = value != null && value.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0
            ? OverlayAnchor.Left
            : OverlayAnchor.Right;
        if (anchor == _overlayAnchor) return;
        _overlayAnchor = anchor;
        Reposition();
    }

    private static void Reposition()
    {
        if (_hbox != null && GodotObject.IsInstanceValid(_hbox)) PositionHbox(_hbox);
        if (_previewContainer != null && GodotObject.IsInstanceValid(_previewContainer)) PositionPreview(_previewContainer);
        if (_accordionVBox != null && GodotObject.IsInstanceValid(_accordionVBox)) PositionAccordion(_accordionVBox);
    }

    private static void PositionHbox(Control c)
    {
        if (_overlayAnchor == OverlayAnchor.Left)
            AnchorTopLeft(c, width: 420, height: 56, offsetLeft: 40, offsetTop: 40);
        else
            AnchorTopRight(c, width: 420, height: 56, offsetRight: 40, offsetTop: 40);
    }

    // Preview sits opposite to the dropdown: right of hbox in Left mode, left of hbox in Right mode.
    // 540 inset preserves the original 80px gap (hbox is 420 wide + 40 base margin + 80 gap).
    private static void PositionPreview(Control c)
    {
        if (_overlayAnchor == OverlayAnchor.Left)
            AnchorTopLeft(c, width: 240, height: 280, offsetLeft: 540, offsetTop: 40);
        else
            AnchorTopRight(c, width: 240, height: 280, offsetRight: 540, offsetTop: 40);
    }

    private static void PositionAccordion(Control c)
    {
        if (_overlayAnchor == OverlayAnchor.Left)
            AnchorTopLeft(c, width: 480, height: 40, offsetLeft: 40, offsetTop: 110);
        else
            AnchorTopRight(c, width: 480, height: 40, offsetRight: 40, offsetTop: 110);
    }

    // Anchors a control to the parent's TOP-LEFT corner.
    private static void AnchorTopLeft(Control c, float width, float height, float offsetLeft, float offsetTop)
    {
        c.AnchorLeft = 0f;
        c.AnchorRight = 0f;
        c.AnchorTop = 0f;
        c.AnchorBottom = 0f;
        c.OffsetLeft = offsetLeft;
        c.OffsetRight = offsetLeft + width;
        c.OffsetTop = offsetTop;
        c.OffsetBottom = offsetTop + height;
        c.GrowHorizontal = Control.GrowDirection.End;
        c.GrowVertical = Control.GrowDirection.End;
    }

    // Anchors a control to the parent's TOP-RIGHT corner.
    private static void AnchorTopRight(Control c, float width, float height, float offsetRight, float offsetTop)
    {
        c.AnchorLeft = 1f;
        c.AnchorRight = 1f;
        c.AnchorTop = 0f;
        c.AnchorBottom = 0f;
        c.OffsetLeft = -(offsetRight + width);
        c.OffsetRight = -offsetRight;
        c.OffsetTop = offsetTop;
        c.OffsetBottom = offsetTop + height;
        c.GrowHorizontal = Control.GrowDirection.Begin;
        c.GrowVertical = Control.GrowDirection.End;
    }

    private static void DoAttach(Node screen)
    {
        try
        {
            if (_opt != null && GodotObject.IsInstanceValid(_opt) && _opt.IsInsideTree())
            {
                MainFile.Logger.Info("overlay already attached and in tree; skipping re-attach");
                return;
            }
            _lastScreen = screen;

            var hbox = new HBoxContainer
            {
                CustomMinimumSize = new Vector2(420, 56),
            };
            PositionHbox(hbox);
            _hbox = hbox;
            _label = new Label
            {
                Text = Strings.Get("skin_label") + ":",
                CustomMinimumSize = new Vector2(80, 56),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _opt = new OptionButton { CustomMinimumSize = new Vector2(320, 56) };
            _opt.ItemSelected += OnVariantSelected;
            _opt.Connect("item_selected", Callable.From<long>(idx => OnVariantSelectedSafe(idx)));
            _opt.Pressed += () => MainFile.Logger.Info("OptionButton pressed (dropdown opened)");
            hbox.AddChild(_label);
            hbox.AddChild(_opt);

            _variantEditLine = new LineEdit
            {
                CustomMinimumSize = new Vector2(240, 56),
                PlaceholderText = Strings.Get("alias_placeholder"),
                Visible = false,
            };
            hbox.AddChild(_variantEditLine);
            _variantEditLine.TextSubmitted += OnVariantAliasSubmitted;
            _variantEditLine.TextChanged += _ =>
            {
                if (_variantEditLine == null || !GodotObject.IsInstanceValid(_variantEditLine)) return;
                _variantEditLine.Modulate = Colors.White;
                _variantEditLine.TooltipText = "";
            };

            _variantEditBtn = new Button
            {
                Text = "✏",
                CustomMinimumSize = new Vector2(40, 56),
                TooltipText = Strings.Get("alias_edit_tooltip"),
            };
            _variantEditBtn.Pressed += ToggleVariantEditMode;
            hbox.AddChild(_variantEditBtn);

            _variantSaveBtn = new Button
            {
                Text = "✓",
                CustomMinimumSize = new Vector2(40, 56),
                TooltipText = Strings.Get("alias_save_tooltip"),
                Visible = false,
            };
            _variantSaveBtn.Pressed += () => OnVariantAliasSubmitted(_variantEditLine.Text);
            hbox.AddChild(_variantSaveBtn);

            _variantCancelBtn = new Button
            {
                Text = "✕",
                CustomMinimumSize = new Vector2(40, 56),
                TooltipText = Strings.Get("alias_cancel_tooltip"),
                Visible = false,
            };
            _variantCancelBtn.Pressed += ExitVariantEditMode;
            hbox.AddChild(_variantCancelBtn);

            var hoverIcon = new Label
            {
                Text = "👁",
                CustomMinimumSize = new Vector2(48, 56),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Stop,
                TooltipText = Strings.Get("preview_toggle_tooltip"),
                Visible = false,
            };
            _previewHoverIcon = hoverIcon;
            hoverIcon.MouseEntered += OnPreviewHoverStart;
            hoverIcon.MouseExited += OnPreviewHoverEnd;
            hbox.AddChild(hoverIcon);

            hbox.ZIndex = 1000;
            screen.AddChild(hbox);
            MainFile.Logger.Info($"SkinSelectorOverlay attached (OptionButton) to {screen.Name}");

            BuildPreviewPanel(screen);
            ApplyPreviewVisibility();
            RefreshItems();

            var hasCharacterVariants = _byCharacter != null && _byCharacter.Values.Any(v => v.Count > 0);
            // Build the accordion if there's anything to manage — characters, card packs, mixed
            // mods, or any All Mods entry (event-art, skipped, or pending). Without _allMods in
            // the condition, a user with only event-art mods (AncientRetexture pattern) would
            // see no manager UI at all.
            if (_cardMods.Count > 0 || _mixedMods.Count > 0 || hasCharacterVariants || _allMods.Count > 0
                || ToggleableCustomCharacters().Count > 0)
                BuildAccordionPanel(screen);

            EnsureLocaleSubscribed();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"overlay attach failed: {ex.Message}");
        }
    }

    private static void BuildPreviewPanel(Node screen)
    {
        var container = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(240, 280),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        PositionPreview(container);
        _previewContainer = container;
        container.MouseEntered += OnPreviewHoverStart;
        container.MouseExited += OnPreviewHoverEnd;

        var rect = new TextureRect
        {
            CustomMinimumSize = new Vector2(240, 240),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        _previewRect = rect;
        container.AddChild(rect);

        var caption = new Label
        {
            CustomMinimumSize = new Vector2(240, 32),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _previewCaption = caption;
        container.AddChild(caption);

        container.ZIndex = 1000;
        screen.AddChild(container);
        MainFile.Logger.Info("preview panel attached");
    }

    private static void UpdatePreview(string variant)
    {
        if (_previewRect == null || !GodotObject.IsInstanceValid(_previewRect)) goto finalize;
        if (_previewCaption == null || !GodotObject.IsInstanceValid(_previewCaption)) goto finalize;

        DetectedSkinMod? mod = null;
        if (_byCharacter != null && _byCharacter.TryGetValue(_currentCharacter, out var variants))
        {
            mod = variants.FirstOrDefault(v => string.Equals(v.ModId, variant, StringComparison.OrdinalIgnoreCase));
        }

        var bootActive = _bootSnapshotActive.TryGetValue(_currentCharacter, out var b) ? b : "default";
        var isCurrentActive = string.Equals(variant, bootActive, StringComparison.OrdinalIgnoreCase);

        var tex = isCurrentActive ? null : LoadPreviewTexture(mod, variant);
        _previewAvailable = tex != null;

        if (_previewAvailable)
        {
            _previewRect.Texture = tex;
            _previewCaption.Text = AliasService.Resolve(variant, LoadAliases());
        }
        else
        {
            _previewRect.Texture = null;
            _previewCaption.Text = "";
        }

    finalize:
        if (_previewHoverIcon != null && GodotObject.IsInstanceValid(_previewHoverIcon))
        {
            _previewHoverIcon.Visible = _previewAvailable;
        }
        if (!_previewAvailable) _previewHovered = false;
        ApplyPreviewVisibility();
    }

    private static ImageTexture? LoadPreviewTexture(DetectedSkinMod? mod, string variant)
    {
        if (mod == null) return null;

        var cacheKey = $"{mod.PckPath}|{_currentCharacter}";
        if (_previewCache.TryGetValue(cacheKey, out var cached)) return cached;

        var tex = TryLoadFromConventionFile(mod.PreviewPath) ?? TryLoadFromPckCharSelect(mod.PckPath, _currentCharacter);
        _previewCache[cacheKey] = tex;
        return tex;
    }

    private static ImageTexture? TryLoadFromConventionFile(string? previewPath)
    {
        if (string.IsNullOrEmpty(previewPath) || !File.Exists(previewPath)) return null;
        try
        {
            var image = new Image();
            var err = image.Load(previewPath);
            if (err != Error.Ok)
            {
                MainFile.Logger.Warn($"preview.png load failed: {previewPath} → {err}");
                return null;
            }
            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"preview.png load error: {previewPath}: {ex.Message}");
            return null;
        }
    }

    private static ImageTexture? TryLoadFromPckCharSelect(string pckPath, string character)
    {
        if (string.IsNullOrEmpty(character)) return null;
        try
        {
            var charLower = character.ToLowerInvariant();
            var prefix = $".godot/imported/char_select_{charLower}.png-";
            var ctex = PckFileExtractor.TryReadFirstMatch(pckPath, p =>
                p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                p.EndsWith(".ctex", StringComparison.OrdinalIgnoreCase) &&
                !p.Contains("_locked", StringComparison.OrdinalIgnoreCase));
            if (ctex == null)
            {
                MainFile.Logger.Info($"no char_select_{charLower} ctex found in {pckPath}");
                return null;
            }

            var (fmt, data) = CtexImageExtractor.ExtractEmbedded(ctex);
            if (data == null || fmt == CtexImageExtractor.CtexFormat.Unknown)
            {
                MainFile.Logger.Warn($"could not extract embedded image from ctex in {pckPath}");
                return null;
            }

            var image = new Image();
            var err = fmt == CtexImageExtractor.CtexFormat.Png
                ? image.LoadPngFromBuffer(data)
                : image.LoadWebpFromBuffer(data);
            if (err != Error.Ok)
            {
                MainFile.Logger.Warn($"{fmt} decode failed for {pckPath}: {err}");
                return null;
            }
            MainFile.Logger.Info($"loaded {fmt} char_select preview from {Path.GetFileName(pckPath)} ({image.GetWidth()}x{image.GetHeight()})");
            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"pck char_select fallback error for {pckPath}: {ex.Message}");
            return null;
        }
    }

    private static ImageTexture? LoadCardPreviewTexture(string modId)
    {
        var mod = _cardMods.FirstOrDefault(m => string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase));
        if (mod == null) return null;

        var cacheKey = $"card|{mod.PckPath}";
        if (_cardPreviewCache.TryGetValue(cacheKey, out var cached)) return cached;

        var tex = TryLoadFromConventionFile(mod.PreviewPath) ?? TryLoadFirstCardArt(mod.PckPath);
        _cardPreviewCache[cacheKey] = tex;
        return tex;
    }

    // First card-art .ctex in alphabetical order. Two real-world patterns supported:
    //   A) base override:   .godot/imported/MegaCrit.Sts2.Core.Models.Cards.{Name}_card_art.png-*.ctex
    //   B) own namespace:   .godot/imported/{Name}.png-*.ctex   (and not char_select)
    private static ImageTexture? TryLoadFirstCardArt(string pckPath)
    {
        try
        {
            var idx = PckFileExtractor.TryReadIndex(pckPath);
            if (idx == null) return null;

            var sortedKeys = idx.Keys
                .Where(k => k.StartsWith(".godot/imported/", StringComparison.OrdinalIgnoreCase)
                            && k.EndsWith(".ctex", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string? chosen = null;
            foreach (var k in sortedKeys)
            {
                if (k.Contains("MegaCrit.Sts2.Core.Models.Cards.", StringComparison.OrdinalIgnoreCase)
                    && k.Contains("_card_art", StringComparison.OrdinalIgnoreCase))
                {
                    chosen = k;
                    break;
                }
            }
            if (chosen == null)
            {
                foreach (var k in sortedKeys)
                {
                    if (k.Contains("char_select", StringComparison.OrdinalIgnoreCase)) continue;
                    if (k.Contains("characterselect", StringComparison.OrdinalIgnoreCase)) continue;
                    chosen = k;
                    break;
                }
            }
            if (chosen == null) { MainFile.Logger.Info($"no card art ctex found in {pckPath}"); return null; }

            var ctex = PckFileExtractor.TryRead(pckPath, idx[chosen]);
            if (ctex == null) return null;

            var (fmt, data) = CtexImageExtractor.ExtractEmbedded(ctex);
            if (data == null || fmt == CtexImageExtractor.CtexFormat.Unknown) return null;

            var image = new Image();
            var err = fmt == CtexImageExtractor.CtexFormat.Png
                ? image.LoadPngFromBuffer(data)
                : image.LoadWebpFromBuffer(data);
            if (err != Error.Ok) return null;
            MainFile.Logger.Info($"loaded {fmt} card preview from {Path.GetFileName(pckPath)} ({image.GetWidth()}x{image.GetHeight()}): {chosen}");
            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"card art preview load error for {pckPath}: {ex.Message}");
            return null;
        }
    }

    private static void OnCardRowHoverStart(string modId)
    {
        _previewHoverExitToken++;
        _hoveredCardModId = modId;
        var tex = LoadCardPreviewTexture(modId);
        _previewAvailable = tex != null;
        if (_previewRect != null && GodotObject.IsInstanceValid(_previewRect))
        {
            _previewRect.Texture = tex;
        }
        if (_previewCaption != null && GodotObject.IsInstanceValid(_previewCaption))
        {
            _previewCaption.Text = _previewAvailable ? AliasService.Resolve(modId, LoadAliases()) : "";
        }
        _previewHovered = _previewAvailable;
        ApplyPreviewVisibility();
    }

    private static void OnCardRowHoverEnd(string modId)
    {
        if (!string.Equals(_hoveredCardModId, modId, StringComparison.OrdinalIgnoreCase)) return;
        var hbox = _hbox;
        if (hbox == null || !GodotObject.IsInstanceValid(hbox) || !hbox.IsInsideTree())
        {
            _previewHovered = false;
            _hoveredCardModId = null;
            ApplyPreviewVisibility();
            return;
        }
        var myToken = ++_previewHoverExitToken;
        var timer = hbox.GetTree().CreateTimer(0.12);
        timer.Timeout += () =>
        {
            if (_previewHoverExitToken != myToken) return;
            _previewHovered = false;
            _hoveredCardModId = null;
            ApplyPreviewVisibility();
        };
    }

    private static void OnPreviewHoverStart()
    {
        _previewHoverExitToken++;
        _previewHovered = true;
        ApplyPreviewVisibility();
    }

    // Debounce hide: Godot fires spurious MouseExited when sibling controls toggle visibility
    // (panel show/hide triggers input pick re-eval). Wait ~120 ms; if a new Enter arrives in
    // that window, the token mismatches and we keep the panel up. Also covers the 52 px gap
    // between icon and panel during normal hover-traversal.
    private static void OnPreviewHoverEnd()
    {
        var hbox = _hbox;
        if (hbox == null || !GodotObject.IsInstanceValid(hbox) || !hbox.IsInsideTree())
        {
            _previewHovered = false;
            ApplyPreviewVisibility();
            return;
        }
        var myToken = ++_previewHoverExitToken;
        var timer = hbox.GetTree().CreateTimer(0.12);
        timer.Timeout += () =>
        {
            if (_previewHoverExitToken != myToken) return;
            _previewHovered = false;
            ApplyPreviewVisibility();
        };
    }

    private static void ApplyPreviewVisibility()
    {
        if (_previewContainer != null && GodotObject.IsInstanceValid(_previewContainer))
        {
            _previewContainer.Visible = _previewHovered && _previewAvailable;
        }
    }

    // Single panel that hosts both card-skin and mixed-addon sections as tabs. Save/Discard live
    // above the TabContainer so they stay reachable regardless of the active tab. Only one tab's
    // rows are visible at a time — the user clicks the tab header to switch.
    private static void BuildAccordionPanel(Node screen)
    {
        var vbox = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(480, 40),
        };
        PositionAccordion(vbox);
        _accordionVBox = vbox;

        // The read-only "Applied" section is always present. The Card / Mixed sections appear when
        // those mod kinds exist; the Other Mods section only when there's something to surface.
        var hasManageTabs = _cardMods.Count > 0 || _mixedMods.Count > 0 || _allMods.Count > 0;

        // Top row — outer expand/collapse toggle + Save / Discard.
        var topRow = new HBoxContainer { CustomMinimumSize = new Vector2(480, 36) };

        var outerToggle = new Button
        {
            CustomMinimumSize = new Vector2(220, 36),
            Alignment = HorizontalAlignment.Left,
        };
        _outerToggleBtn = outerToggle;
        outerToggle.Pressed += ToggleOuterExpanded;
        topRow.AddChild(outerToggle);

        var saveBtn = new Button { Text = Strings.Get("save_changes"), CustomMinimumSize = new Vector2(120, 36) };
        _cardPackSaveBtn = saveBtn;
        saveBtn.Pressed += OnSave;
        topRow.AddChild(saveBtn);

        var discardBtn = new Button { Text = Strings.Get("discard_changes"), CustomMinimumSize = new Vector2(120, 36) };
        _cardPackDiscardBtn = discardBtn;
        discardBtn.Pressed += OnDiscard;
        topRow.AddChild(discardBtn);

        vbox.AddChild(topRow);

        _appliedTabIndex = -1;
        _cardPackTabIndex = -1;
        _mixedTabIndex = -1;
        _allModsTabIndex = -1;
        _customCharTabIndex = -1;

        // Body wraps the tab panel — collapsed by default so the character select screen stays clean.
        var body = new VBoxContainer { CustomMinimumSize = new Vector2(480, 0) };
        _outerBody = body;
        vbox.AddChild(body);

        var tabs = new TabContainer { CustomMinimumSize = new Vector2(480, 420) };
        // Keep all four tab headers visible at once instead of collapsing into an overflow menu.
        tabs.ClipTabs = false;
        _tabContainer = tabs;
        body.AddChild(tabs);

        // Applied summary — always the first tab so the panel opens on "what's loaded now".
        {
            var appliedTab = new VBoxContainer { Name = "AppliedTab" };

            var appliedScroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(460, 360),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            };
            appliedTab.AddChild(appliedScroll);

            var appliedRows = new VBoxContainer { CustomMinimumSize = new Vector2(440, 0) };
            _appliedRows = appliedRows;
            appliedScroll.AddChild(appliedRows);

            // Copy button sits at the bottom of the tab, right-aligned, out of the way of the
            // summary itself. Hovering it shows a localized tooltip explaining what it copies.
            var copyRow = new HBoxContainer { CustomMinimumSize = new Vector2(460, 36) };
            copyRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }); // push button right
            var copyBtn = new Button
            {
                Text = Strings.Get("applied_copy"),
                CustomMinimumSize = new Vector2(140, 32),
                TooltipText = Strings.Get("applied_copy_tooltip"),
            };
            _appliedCopyBtn = copyBtn;
            copyBtn.Pressed += OnCopyAppliedSummary;
            copyRow.AddChild(copyBtn);
            appliedTab.AddChild(copyRow);

            tabs.AddChild(appliedTab);
            _appliedTabIndex = appliedTab.GetIndex();
            BuildAppliedSummaryRows();
        }

        if (hasManageTabs)
        {
            var cardTab = new VBoxContainer { Name = "CardSkinTab" };
            var cardScroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(460, 360),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            };
            cardTab.AddChild(cardScroll);

            var cardRows = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
            _cardPackRows = cardRows;
            cardScroll.AddChild(cardRows);

            tabs.AddChild(cardTab);
            _cardPackTabIndex = cardTab.GetIndex();
            BuildCardPackRows();

            var mixedTab = new VBoxContainer { Name = "MixedAddonTab" };

            var mixedHelpLabel = new Label
            {
                Text = Strings.Get("mixed_panel_help"),
                CustomMinimumSize = new Vector2(460, 0),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(0.75f, 0.75f, 0.75f),
            };
            mixedTab.AddChild(mixedHelpLabel);

            var mixedScroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(460, 340),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            };
            mixedTab.AddChild(mixedScroll);

            var mixedRows = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
            _mixedRows = mixedRows;
            mixedScroll.AddChild(mixedRows);

            tabs.AddChild(mixedTab);
            _mixedTabIndex = mixedTab.GetIndex();
            BuildMixedAddonRows();
        }

        if (_allMods.Count > 0)
        {
            var allTab = new VBoxContainer { Name = "AllModsTab" };

            var helpLabel = new Label
            {
                Text = Strings.Get("all_mods_panel_help"),
                CustomMinimumSize = new Vector2(460, 0),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(0.75f, 0.75f, 0.75f),
            };
            allTab.AddChild(helpLabel);

            var allScroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(460, 340),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            };
            allTab.AddChild(allScroll);

            var allRows = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
            _allModsRows = allRows;
            allScroll.AddChild(allRows);

            tabs.AddChild(allTab);
            _allModsTabIndex = allTab.GetIndex();
            BuildAllModsRows();
        }

        // Custom Characters tab — BaseLib-style mods that add brand-new characters. SkinManager
        // doesn't manage these as skins (they auto-mount), but the user can enable/disable each one
        // here. The checkbox drives the same settings.save mod_list IsEnabled path as the Other tab
        // (via _pendingModEnabled), so Save/Discard already cover it with no extra wiring.
        if (ToggleableCustomCharacters().Count > 0)
        {
            var ccTab = new VBoxContainer { Name = "CustomCharTab" };

            var ccHelp = new Label
            {
                Text = Strings.Get("custom_char_panel_help"),
                CustomMinimumSize = new Vector2(460, 0),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(0.75f, 0.75f, 0.75f),
            };
            ccTab.AddChild(ccHelp);

            var ccScroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(460, 340),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            };
            ccTab.AddChild(ccScroll);

            var ccRows = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
            _customCharRows = ccRows;
            ccScroll.AddChild(ccRows);

            tabs.AddChild(ccTab);
            _customCharTabIndex = ccTab.GetIndex();
            BuildCustomCharacterRows();
        }

        UpdateAppliedHeader();
        UpdateCardPackHeader();
        UpdateMixedHeader();
        UpdateAllModsHeader();
        UpdateCustomCharHeader();
        ApplyOuterExpanded();
        UpdateOuterToggleText();

        vbox.ZIndex = 1000;
        screen.AddChild(vbox);
        MainFile.Logger.Info($"tab panel attached (card={_cardMods.Count}, mixed={_mixedMods.Count})");
    }

    private static void ToggleOuterExpanded()
    {
        _outerExpanded = !_outerExpanded;
        ApplyOuterExpanded();
        UpdateOuterToggleText();
    }

    private static void ApplyOuterExpanded()
    {
        if (_outerBody != null && GodotObject.IsInstanceValid(_outerBody))
            _outerBody.Visible = _outerExpanded;
    }

    private static void UpdateOuterToggleText()
    {
        if (_outerToggleBtn == null || !GodotObject.IsInstanceValid(_outerToggleBtn)) return;
        var arrow = _outerExpanded ? "▼" : "▶";
        var dirty = IsAnyDirty();
        var dirtyMark = dirty ? " *" : "";
        _outerToggleBtn.Text = $"{arrow}  {Strings.Get("skin_manager_section_header")}{dirtyMark}";
    }

    private static void BuildCardPackPanel(Node screen) => BuildAccordionPanel(screen);
    private static void BuildMixedAddonPanel(Node screen) { /* folded into BuildAccordionPanel */ }

    private static void UpdateCardPackHeader()
    {
        var pending = _pendingCardPacks ?? new CardPacksConfig();
        var total = pending.Ordering.Count;
        var enabled = pending.Enabled.Count(kv => kv.Value);
        var dirty = IsAnyDirty();
        var dirtyMark = dirty ? " *" : "";
        var title = $"🃏 {Strings.Get("tab_cards")} ({enabled}/{total}){dirtyMark}";

        if (_tabContainer != null && GodotObject.IsInstanceValid(_tabContainer) && _cardPackTabIndex >= 0)
            _tabContainer.SetTabTitle(_cardPackTabIndex, title);

        if (_cardPackSaveBtn != null && GodotObject.IsInstanceValid(_cardPackSaveBtn))
        {
            _cardPackSaveBtn.Disabled = !dirty;
            _cardPackSaveBtn.Text = Strings.Get("save_changes");
        }
        if (_cardPackDiscardBtn != null && GodotObject.IsInstanceValid(_cardPackDiscardBtn))
        {
            _cardPackDiscardBtn.Disabled = !dirty;
            _cardPackDiscardBtn.Text = Strings.Get("discard_changes");
        }
        UpdateOuterToggleText();
        RefreshAppliedSummary();
    }

    private static void BuildCardPackRows()
    {
        if (_cardPackRows == null || !GodotObject.IsInstanceValid(_cardPackRows)) return;

        for (var i = _cardPackRows.GetChildCount() - 1; i >= 0; i--)
        {
            var child = _cardPackRows.GetChild(i);
            _cardPackRows.RemoveChild(child);
            child.QueueFree();
        }

        var packs = _pendingCardPacks ?? new CardPacksConfig();
        if (packs.Ordering.Count == 0)
        {
            var placeholder = new Label
            {
                Text = Strings.Get("card_panel_empty"),
                CustomMinimumSize = new Vector2(460, 60),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(0.6f, 0.6f, 0.6f),
            };
            _cardPackRows.AddChild(placeholder);
        }
        for (var i = 0; i < packs.Ordering.Count; i++)
        {
            var modId = packs.Ordering[i];
            var row = BuildCardPackRow(modId, packs, i, packs.Ordering.Count);
            _cardPackRows.AddChild(row);
        }
        UpdateCardPackHeader();
    }

    private static Control BuildCardPackRow(string modId, CardPacksConfig packs, int index, int total)
    {
        var hbox = new CardPackRow
        {
            ModId = modId,
            MouseFilter = Control.MouseFilterEnum.Pass,
            MouseDefaultCursorShape = Control.CursorShape.Move,
        };
        var enabled = packs.Enabled.TryGetValue(modId, out var e) ? e : true;

        var dragHandle = new Label
        {
            Text = "⋮⋮",
            CustomMinimumSize = new Vector2(20, 32),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hbox.AddChild(dragHandle);

        var check = new CheckBox
        {
            ButtonPressed = enabled,
            CustomMinimumSize = new Vector2(32, 32),
        };
        hbox.AddChild(check);

        var status = new Label
        {
            CustomMinimumSize = new Vector2(28, 32),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hbox.AddChild(status);

        var orderLabel = new Label
        {
            Text = $"{index + 1}",
            CustomMinimumSize = new Vector2(32, 32),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        hbox.AddChild(orderLabel);
        hbox.AddChild(new Control { CustomMinimumSize = new Vector2(12, 0) }); // gap between order number and name

        var aliases = LoadAliases();
        var label = new Label
        {
            Text = AliasService.Resolve(modId, aliases),
            CustomMinimumSize = new Vector2(248, 32),
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = modId, // raw key always reachable via hover
        };
        label.MouseEntered += () => OnCardRowHoverStart(modId);
        label.MouseExited += () => OnCardRowHoverEnd(modId);
        hbox.AddChild(label);

        var aliasEdit = new LineEdit
        {
            CustomMinimumSize = new Vector2(192, 32),
            PlaceholderText = Strings.Get("alias_placeholder"),
            Visible = false,
        };
        hbox.AddChild(aliasEdit);

        var editBtn = new Button
        {
            Text = "✏",
            CustomMinimumSize = new Vector2(28, 32),
            TooltipText = Strings.Get("alias_edit_tooltip"),
        };
        hbox.AddChild(editBtn);

        var saveBtn = new Button
        {
            Text = "✓",
            CustomMinimumSize = new Vector2(28, 32),
            TooltipText = Strings.Get("alias_save_tooltip"),
            Visible = false,
        };
        hbox.AddChild(saveBtn);

        var cancelBtn = new Button
        {
            Text = "✕",
            CustomMinimumSize = new Vector2(28, 32),
            TooltipText = Strings.Get("alias_cancel_tooltip"),
            Visible = false,
        };
        hbox.AddChild(cancelBtn);

        void EnterEditMode()
        {
            aliasEdit.Text = LoadAliases().TryGetValue(modId, out var cur) ? cur : "";
            label.Visible = false;
            editBtn.Visible = false;
            aliasEdit.Visible = true;
            saveBtn.Visible = true;
            cancelBtn.Visible = true;
            aliasEdit.Modulate = Colors.White;
            aliasEdit.TooltipText = "";
            aliasEdit.GrabFocus();
            aliasEdit.CaretColumn = aliasEdit.Text.Length;
        }

        void ExitEditMode()
        {
            aliasEdit.Visible = false;
            saveBtn.Visible = false;
            cancelBtn.Visible = false;
            label.Visible = true;
            editBtn.Visible = true;
            label.Text = AliasService.Resolve(modId, LoadAliases());
        }

        void TrySave()
        {
            if (TrySaveAlias(modId, aliasEdit.Text, aliasEdit)) ExitEditMode();
        }

        editBtn.Pressed += EnterEditMode;
        saveBtn.Pressed += TrySave;
        cancelBtn.Pressed += ExitEditMode;
        aliasEdit.TextSubmitted += _ => TrySave();
        aliasEdit.TextChanged += _ =>
        {
            // Clear error styling while user is still typing.
            aliasEdit.Modulate = Colors.White;
            aliasEdit.TooltipText = "";
        };

        void ApplyVisual(bool isOn)
        {
            status.Text = isOn ? "✓" : "✗";
            status.Modulate = isOn ? new Color(0.4f, 0.95f, 0.45f) : new Color(0.95f, 0.45f, 0.45f);
            check.Modulate = isOn ? new Color(0.6f, 1.0f, 0.6f) : new Color(0.55f, 0.55f, 0.55f);
            label.Modulate = isOn ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
            dragHandle.Modulate = isOn ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
            orderLabel.Modulate = isOn ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
        }
        ApplyVisual(enabled);

        check.Toggled += isOn =>
        {
            OnCardPackToggle(modId, isOn);
            ApplyVisual(isOn);
        };

        var upBtn = new Button
        {
            Text = "↑",
            CustomMinimumSize = new Vector2(32, 32),
            Disabled = index == 0,
        };
        upBtn.Pressed += () => MoveCardPack(modId, -1);
        hbox.AddChild(upBtn);

        var downBtn = new Button
        {
            Text = "↓",
            CustomMinimumSize = new Vector2(32, 32),
            Disabled = index == total - 1,
        };
        downBtn.Pressed += () => MoveCardPack(modId, +1);
        hbox.AddChild(downBtn);

        return hbox;
    }

    private static void OnCardPackToggle(string modId, bool isOn)
    {
        try
        {
            _pendingCardPacks ??= ClonePacks(SkinChoicesConfig.LoadOrEmpty(_choicesPath).CardPacks);
            var current = _pendingCardPacks.Enabled.TryGetValue(modId, out var c) ? c : true;
            if (current == isOn) return;
            _pendingCardPacks.Enabled[modId] = isOn;
            MainFile.Logger.Info($"card pack pending toggle: {modId} → {isOn}");
            UpdateCardPackHeader();
        }
        catch (Exception ex) { MainFile.Logger.Warn($"card pack toggle error: {ex.Message}"); }
    }

    private static void MoveCardPack(string modId, int delta)
    {
        try
        {
            _pendingCardPacks ??= ClonePacks(SkinChoicesConfig.LoadOrEmpty(_choicesPath).CardPacks);
            var idx = _pendingCardPacks.Ordering.IndexOf(modId);
            if (idx < 0) return;
            var newIdx = idx + delta;
            if (newIdx < 0 || newIdx >= _pendingCardPacks.Ordering.Count) return;

            var item = _pendingCardPacks.Ordering[idx];
            _pendingCardPacks.Ordering.RemoveAt(idx);
            _pendingCardPacks.Ordering.Insert(newIdx, item);
            MainFile.Logger.Info($"card pack pending reorder: {modId} → index {newIdx}");
            Callable.From(BuildCardPackRows).CallDeferred();
        }
        catch (Exception ex) { MainFile.Logger.Warn($"card pack reorder error: {ex.Message}"); }
    }

    public static void HandleDragDropReorder(string sourceModId, string targetModId, bool insertAbove)
    {
        try
        {
            _pendingCardPacks ??= ClonePacks(SkinChoicesConfig.LoadOrEmpty(_choicesPath).CardPacks);
            var srcIdx = _pendingCardPacks.Ordering.IndexOf(sourceModId);
            var targetIdx = _pendingCardPacks.Ordering.IndexOf(targetModId);
            if (srcIdx < 0 || targetIdx < 0 || srcIdx == targetIdx) return;

            var insertIdx = insertAbove ? targetIdx : targetIdx + 1;
            var item = _pendingCardPacks.Ordering[srcIdx];
            _pendingCardPacks.Ordering.RemoveAt(srcIdx);
            if (srcIdx < insertIdx) insertIdx--;
            if (insertIdx < 0) insertIdx = 0;
            if (insertIdx > _pendingCardPacks.Ordering.Count) insertIdx = _pendingCardPacks.Ordering.Count;
            _pendingCardPacks.Ordering.Insert(insertIdx, item);

            MainFile.Logger.Info($"card pack drag-drop: {sourceModId} → {(insertAbove ? "above" : "below")} {targetModId} (idx {insertIdx})");
            Callable.From(BuildCardPackRows).CallDeferred();
        }
        catch (Exception ex) { MainFile.Logger.Warn($"drag-drop reorder error: {ex.Message}"); }
    }

    // === Mixed-addon helpers (panel is built inside BuildAccordionPanel) ===

    private static void UpdateMixedHeader()
    {
        var pending = _pendingMixedAddons ?? new CardPacksConfig();
        var total = pending.Ordering.Count;
        var enabled = pending.Enabled.Count(kv => kv.Value);
        var title = $"🧩 {Strings.Get("tab_mixed")} ({enabled}/{total})";

        if (_tabContainer != null && GodotObject.IsInstanceValid(_tabContainer) && _mixedTabIndex >= 0)
        {
            _tabContainer.SetTabTitle(_mixedTabIndex, title);
            _tabContainer.SetTabTooltip(_mixedTabIndex, Strings.Get("mixed_panel_tooltip"));
        }
    }

    private static void UpdateAllModsHeader()
    {
        if (_allMods.Count == 0) return;
        var enabled = 0;
        foreach (var item in _allMods)
        {
            var bootEnabled = _bootSnapshotModEnabled.TryGetValue(item.ModId, out var be) ? be : true;
            var eff = _pendingModEnabled.TryGetValue(item.ModId, out var p) ? p : bootEnabled;
            if (eff) enabled++;
        }
        var title = $"🚫 {Strings.Get("tab_other")} ({enabled}/{_allMods.Count})";
        if (_tabContainer != null && GodotObject.IsInstanceValid(_tabContainer) && _allModsTabIndex >= 0)
        {
            _tabContainer.SetTabTitle(_allModsTabIndex, title);
            _tabContainer.SetTabTooltip(_allModsTabIndex, $"{enabled} enabled / {_allMods.Count} total");
        }
        UpdateOuterToggleText();
    }

    // === Applied summary (read-only "what's actually loaded right now") ===

    private enum AppliedLineKind { Section, Normal, Dim, Pending, Warn }
    private readonly record struct AppliedLine(string Text, AppliedLineKind Kind);

    private static void UpdateAppliedHeader()
    {
        if (_tabContainer == null || !GodotObject.IsInstanceValid(_tabContainer) || _appliedTabIndex < 0) return;
        var mark = IsAnyDirty() ? " ⟳" : "";
        _tabContainer.SetTabTitle(_appliedTabIndex, $"✅ {Strings.Get("applied_tab_title")}{mark}");
    }

    private static void RefreshAppliedSummary()
    {
        if (_appliedRows == null || !GodotObject.IsInstanceValid(_appliedRows)) return;
        BuildAppliedSummaryRows();
    }

    private static void BuildAppliedSummaryRows()
    {
        if (_appliedRows == null || !GodotObject.IsInstanceValid(_appliedRows)) return;

        for (var i = _appliedRows.GetChildCount() - 1; i >= 0; i--)
        {
            var child = _appliedRows.GetChild(i);
            _appliedRows.RemoveChild(child);
            child.QueueFree();
        }

        var lines = BuildAppliedLines(LoadAliases(), out _);
        foreach (var line in lines)
        {
            var lbl = new Label
            {
                Text = line.Text,
                CustomMinimumSize = new Vector2(440, 0),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = line.Kind switch
                {
                    AppliedLineKind.Section => new Color(0.78f, 0.86f, 1f),
                    AppliedLineKind.Dim => new Color(0.6f, 0.6f, 0.6f),
                    AppliedLineKind.Pending => new Color(1f, 0.84f, 0.4f),
                    AppliedLineKind.Warn => new Color(1f, 0.5f, 0.5f),
                    _ => Colors.White,
                },
            };
            _appliedRows.AddChild(lbl);
        }
        UpdateAppliedHeader();
    }

    private static void OnCopyAppliedSummary()
    {
        try
        {
            BuildAppliedLines(LoadAliases(), out var plain);
            DisplayServer.ClipboardSet(plain);
            MainFile.Logger.Info("applied summary copied to clipboard");
            if (_appliedCopyBtn != null && GodotObject.IsInstanceValid(_appliedCopyBtn))
            {
                _appliedCopyBtn.Text = Strings.Get("applied_copied");
                var t = _appliedCopyBtn.GetTree().CreateTimer(1.5);
                t.Timeout += () =>
                {
                    if (_appliedCopyBtn != null && GodotObject.IsInstanceValid(_appliedCopyBtn))
                        _appliedCopyBtn.Text = Strings.Get("applied_copy");
                };
            }
        }
        catch (Exception ex) { MainFile.Logger.Warn($"copy applied summary failed: {ex.Message}"); }
    }

    // Builds the lines for both the on-screen panel and the clipboard text from the boot snapshot
    // (= what's loaded right now), annotating any character whose selection differs and is pending.
    private static List<AppliedLine> BuildAppliedLines(Dictionary<string, string> aliases, out string plainText)
    {
        var lines = new List<AppliedLine>();
        var plain = new List<string>();
        void Add(string text, AppliedLineKind kind) { lines.Add(new AppliedLine(text, kind)); plain.Add(text); }

        if (IsAnyDirty()) Add("⟳ " + Strings.Get("applied_pending_note"), AppliedLineKind.Pending);

        var mixedIds = new HashSet<string>(_mixedMods.Select(x => x.ModId), StringComparer.OrdinalIgnoreCase);
        var disk = SkinChoicesConfig.LoadOrEmpty(_choicesPath);
        var hasAnySkinState = false;

        // Characters: those with detected variants, plus any with a non-default applied skin.
        var charSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_byCharacter != null)
            foreach (var kv in _byCharacter)
                if (kv.Value.Count > 0) charSet.Add(kv.Key);
        foreach (var kv in _bootSnapshotActive)
            if (!string.Equals(kv.Value, "default", StringComparison.OrdinalIgnoreCase)) charSet.Add(kv.Key);

        if (charSet.Count > 0)
        {
            Add("🧍 " + Strings.Get("applied_characters"), AppliedLineKind.Section);
            foreach (var ch in charSet)
            {
                var boot = _bootSnapshotActive.TryGetValue(ch, out var b) ? b : "default";
                var bootDefault = string.Equals(boot, "default", StringComparison.OrdinalIgnoreCase);
                if (!bootDefault) hasAnySkinState = true;
                var appliedLabel = bootDefault
                    ? Strings.Get("applied_vanilla")
                    : AliasService.Resolve(boot, aliases) + VariantTag(boot, mixedIds);

                var diskActive = disk.Characters.TryGetValue(ch, out var dc) ? (dc.Active ?? "default") : boot;
                var selected = _pendingActiveByCharacter.TryGetValue(ch, out var p) ? p : diskActive;
                var pendingDiffers = !string.Equals(selected, boot, StringComparison.OrdinalIgnoreCase);
                var missing = !bootDefault && IsAppliedVariantMissing(ch, boot);

                Add($"   {Capitalize(ch)} → {appliedLabel}{(missing ? "  ⚠" : "")}",
                    bootDefault ? AppliedLineKind.Dim : AppliedLineKind.Normal);
                if (missing)
                    Add($"      ⚠ {Strings.Get("applied_not_mounted")}", AppliedLineKind.Warn);
                if (pendingDiffers)
                {
                    var selDefault = string.Equals(selected, "default", StringComparison.OrdinalIgnoreCase);
                    var selLabel = selDefault
                        ? Strings.Get("applied_vanilla")
                        : AliasService.Resolve(selected, aliases) + VariantTag(selected, mixedIds);
                    Add($"      → {selLabel} ({Strings.Get("applied_after_restart")}) ⟳", AppliedLineKind.Pending);
                }
            }
        }

        // Custom-character mods (new characters added by BaseLib-style mods). Not managed by the
        // skin manager, but listed here so the player sees which extra characters are loaded. A
        // disabled one drops to the "Disabled mods" section below instead.
        // Exclude framework/library mods (BaseLib, Sts2* sisters): BaseLib defines
        // CustomCharacterModel so it trips the custom-character signal, but it isn't a character.
        var customEnabled = _customCharacters
            .Where(c => !UnifiedModBuilder.IsKnownNonSkin(c.ModId))
            .Where(c => !_bootSnapshotModEnabled.TryGetValue(c.ModId, out var en) || en)
            .ToList();
        if (customEnabled.Count > 0)
        {
            hasAnySkinState = true;
            Add("🆕 " + Strings.Get("applied_custom_characters"), AppliedLineKind.Section);
            foreach (var c in customEnabled)
            {
                var ids = c.CharacterIds != null && c.CharacterIds.Count > 0
                    ? "  — " + string.Join(", ", c.CharacterIds)
                    : "";
                Add($"   {AliasService.Resolve(c.ModId, aliases)}{ids}", AppliedLineKind.Normal);
            }
        }

        // Card skins — enabled, in priority order (top wins).
        if (_bootSnapshotCardPacks != null && _bootSnapshotCardPacks.Ordering.Count > 0)
        {
            hasAnySkinState = true;
            var enabled = _bootSnapshotCardPacks.Ordering
                .Where(id => _bootSnapshotCardPacks.Enabled.TryGetValue(id, out var e) && e).ToList();
            Add("🃏 " + Strings.Get("card_packs_header"), AppliedLineKind.Section);
            if (enabled.Count == 0) Add("   " + Strings.Get("applied_none"), AppliedLineKind.Dim);
            else { var n = 1; foreach (var id in enabled) Add($"   {n++}. {AliasService.Resolve(id, aliases)}", AppliedLineKind.Normal); }
        }

        // Mixed mods — enabled, with their body-look state.
        if (_bootSnapshotMixedAddons != null && _bootSnapshotMixedAddons.Ordering.Count > 0)
        {
            hasAnySkinState = true;
            var enabled = _bootSnapshotMixedAddons.Ordering
                .Where(id => _bootSnapshotMixedAddons.Enabled.TryGetValue(id, out var e) && e).ToList();
            Add("🧩 " + Strings.Get("mixed_panel_header"), AppliedLineKind.Section);
            if (enabled.Count == 0) Add("   " + Strings.Get("applied_none"), AppliedLineKind.Dim);
            else foreach (var id in enabled)
            {
                var bodyTag = _vanillaBodyEligible.Contains(id)
                    ? $"  (🧍 {Strings.Get(_bootSnapshotVanillaBody.Contains(id) ? "vanilla_body_on" : "vanilla_body_off")})"
                    : "";
                Add($"   {AliasService.Resolve(id, aliases)}{bodyTag}", AppliedLineKind.Normal);
            }
        }

        // Other mods the user turned off (informational — explains a missing character/feature).
        var disabled = _bootSnapshotModEnabled.Where(kv => !kv.Value)
            .Select(kv => AliasService.Resolve(kv.Key, aliases)).ToList();
        if (disabled.Count > 0)
        {
            Add("🚫 " + Strings.Get("applied_other_disabled"), AppliedLineKind.Section);
            foreach (var d in disabled) Add("   " + d, AppliedLineKind.Dim);
        }

        if (!hasAnySkinState)
            Add(Strings.Get("applied_all_vanilla"), AppliedLineKind.Dim);

        plainText = "[Sts2SkinManager] " + Strings.Get("applied_tab_title") + "\n" + string.Join("\n", plain);
        return lines;
    }

    private static string VariantTag(string modId, HashSet<string> mixedIds)
    {
        if (_bootSnapshotDllAssignments.ContainsKey(modId)) return $" ({Strings.Get("applied_dll_tag")})";
        if (mixedIds.Contains(modId)) return " 📦";
        return "";
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    // A pck-based skin is "applied" only if its pck is actually mounted. The active variant is
    // mounted at boot, so a managed-but-unmounted pck means the mount failed (e.g. load-order /
    // file issue) and the player isn't seeing the skin they picked. DLL-driven skins apply via
    // their assembly rather than a managed mount, so they're never flagged.
    private static bool IsAppliedVariantMissing(string character, string variantModId)
    {
        try
        {
            if (_byCharacter == null || !_byCharacter.TryGetValue(character, out var variants)) return false;
            var mod = variants.FirstOrDefault(v => string.Equals(v.ModId, variantModId, StringComparison.OrdinalIgnoreCase));
            if (mod == null || string.IsNullOrEmpty(mod.PckPath)) return false;
            if (_bootSnapshotDllAssignments.ContainsKey(variantModId)) return false;
            if (!ManagedPckRegistry.IsManaged(mod.PckPath)) return false;
            return !ManagedPckRegistry.IsMounted(mod.PckPath);
        }
        catch { return false; }
    }

    // Resolves the visible category/character for a mod after applying any pending override.
    // Currently consumed only by tooltip-related helpers; the Other Mods row UI no longer surfaces
    // category badges or cross-category reclassification.
    private static (UnifiedModCategory category, string character) EffectiveCategoryAndChar(UnifiedModItem item)
    {
        if (_pendingAllModsDecisions.TryGetValue(item.ModId, out var action))
        {
            if (action == "skip") return (UnifiedModCategory.NotManaged, "");
            if (action.StartsWith("skin:", StringComparison.Ordinal))
                return (UnifiedModCategory.CharacterSkin, action.Substring(5));
            // "auto" → revert to scanner-detected base; fall through using item.Category as-is
            return (item.Category, item.Character ?? "");
        }
        return (item.Category, item.Character ?? "");
    }

    private static void BuildAllModsRows()
    {
        if (_allModsRows == null || !GodotObject.IsInstanceValid(_allModsRows)) return;

        for (var i = _allModsRows.GetChildCount() - 1; i >= 0; i--)
        {
            var child = _allModsRows.GetChild(i);
            _allModsRows.RemoveChild(child);
            child.QueueFree();
        }

        foreach (var item in _allMods)
        {
            var row = BuildAllModsRow(item);
            _allModsRows.AddChild(row);
        }
        UpdateAllModsHeader();
    }

    private static Control BuildAllModsRow(UnifiedModItem item)
    {
        var hbox = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(460, 36),
            MouseFilter = Control.MouseFilterEnum.Pass,
        };

        // CheckBox controls the mod's is_enabled state in STS2's settings.save mod_list. Off =
        // STS2 won't load this mod on next launch (DLL + pck disabled at the framework level,
        // independent of SkinManager's dll-block). On = STS2 loads it normally.
        var bootEnabled = _bootSnapshotModEnabled.TryGetValue(item.ModId, out var be) ? be : true;
        var pendingEnabled = _pendingModEnabled.TryGetValue(item.ModId, out var pe) ? pe : bootEnabled;
        var check = new CheckBox
        {
            ButtonPressed = pendingEnabled,
            CustomMinimumSize = new Vector2(32, 32),
            TooltipText = Strings.Get("all_mods_toggle_tooltip"),
        };
        hbox.AddChild(check);

        var status = new Label
        {
            CustomMinimumSize = new Vector2(24, 32),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hbox.AddChild(status);

        var displayName = string.IsNullOrEmpty(item.ManifestName) ? item.ModId : item.ManifestName;
        var nameLabel = new Label
        {
            Text = displayName,
            CustomMinimumSize = new Vector2(360, 32),
            VerticalAlignment = VerticalAlignment.Center,
            // MouseFilter.Stop is required for hover tooltips to fire on a Godot Label —
            // the default (Ignore) lets clicks/hover events pass through without triggering
            // the TooltipText display.
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = BuildAllModsTooltip(item),
        };
        hbox.AddChild(nameLabel);

        check.Toggled += isOn =>
        {
            // If toggle returns to boot value, drop the pending entry; otherwise record the
            // override. Save applies them all to settings.save in one shot.
            if (isOn == bootEnabled) _pendingModEnabled.Remove(item.ModId);
            else _pendingModEnabled[item.ModId] = isOn;
            ApplyRowVisual();
            UpdateAllModsHeader();
            UpdateCardPackHeader();
        };

        void ApplyRowVisual()
        {
            var enabled = _pendingModEnabled.TryGetValue(item.ModId, out var p) ? p : bootEnabled;
            nameLabel.Modulate = enabled ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
            status.Text = enabled ? "✓" : "—";
            status.Modulate = enabled ? new Color(0.6f, 0.95f, 0.6f) : new Color(0.55f, 0.55f, 0.55f);
        }
        ApplyRowVisual();

        return hbox;
    }

    private static string BuildAllModsTooltip(UnifiedModItem item)
    {
        var lines = new List<string> { item.ModId };
        if (!string.IsNullOrEmpty(item.ManifestDescription)) lines.Add(item.ManifestDescription);
        if (!string.IsNullOrEmpty(item.DomainsLabel)) lines.Add(item.DomainsLabel);
        if (item.DefinesContentEntities) lines.Add(Strings.Get("all_mods_tooltip_content"));
        return string.Join("\n", lines);
    }

    // Custom-character mods (BaseLib-style new characters) the user may enable/disable. Excludes
    // framework/sister mods (BaseLib defines CustomCharacterModel so it trips the custom-character
    // signal, but it isn't itself a character) via the same filter the Applied summary uses.
    private static List<SkinModScanner.SkippedCustomCharacterMod> ToggleableCustomCharacters() =>
        _customCharacters.Where(c => !UnifiedModBuilder.IsKnownNonSkin(c.ModId)).ToList();

    private static void BuildCustomCharacterRows()
    {
        if (_customCharRows == null || !GodotObject.IsInstanceValid(_customCharRows)) return;

        for (var i = _customCharRows.GetChildCount() - 1; i >= 0; i--)
        {
            var child = _customCharRows.GetChild(i);
            _customCharRows.RemoveChild(child);
            child.QueueFree();
        }

        foreach (var cc in ToggleableCustomCharacters())
            _customCharRows.AddChild(BuildCustomCharacterRow(cc));
        UpdateCustomCharHeader();
    }

    private static Control BuildCustomCharacterRow(SkinModScanner.SkippedCustomCharacterMod cc)
    {
        var hbox = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(460, 36),
            MouseFilter = Control.MouseFilterEnum.Pass,
        };

        // Same enable/disable mechanism as the Other tab: the checkbox drives STS2's settings.save
        // mod_list IsEnabled through _pendingModEnabled, applied in one shot on Save. Off = STS2
        // won't load the custom character on next launch.
        var bootEnabled = _bootSnapshotModEnabled.TryGetValue(cc.ModId, out var be) ? be : true;
        var pendingEnabled = _pendingModEnabled.TryGetValue(cc.ModId, out var pe) ? pe : bootEnabled;
        var check = new CheckBox
        {
            ButtonPressed = pendingEnabled,
            CustomMinimumSize = new Vector2(32, 32),
            TooltipText = Strings.Get("custom_char_toggle_tooltip"),
        };
        hbox.AddChild(check);

        var status = new Label
        {
            CustomMinimumSize = new Vector2(24, 32),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hbox.AddChild(status);

        var ids = cc.CharacterIds != null && cc.CharacterIds.Count > 0 ? string.Join(", ", cc.CharacterIds) : "";
        var displayName = string.IsNullOrEmpty(ids) ? cc.ModId : $"{cc.ModId}  — {ids}";
        var nameLabel = new Label
        {
            Text = displayName,
            CustomMinimumSize = new Vector2(360, 32),
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = string.Join("\n", new[] { cc.ModId, ids, cc.DomainsLabel ?? "" }.Where(s => !string.IsNullOrEmpty(s))),
        };
        hbox.AddChild(nameLabel);

        check.Toggled += isOn =>
        {
            if (isOn == bootEnabled) _pendingModEnabled.Remove(cc.ModId);
            else _pendingModEnabled[cc.ModId] = isOn;
            ApplyRowVisual();
            UpdateCustomCharHeader();
            UpdateCardPackHeader();
        };

        void ApplyRowVisual()
        {
            var enabled = _pendingModEnabled.TryGetValue(cc.ModId, out var p) ? p : bootEnabled;
            nameLabel.Modulate = enabled ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
            status.Text = enabled ? "✓" : "—";
            status.Modulate = enabled ? new Color(0.6f, 0.95f, 0.6f) : new Color(0.55f, 0.55f, 0.55f);
        }
        ApplyRowVisual();

        return hbox;
    }

    private static void UpdateCustomCharHeader()
    {
        var list = ToggleableCustomCharacters();
        if (list.Count == 0) return;
        var enabled = 0;
        foreach (var c in list)
        {
            var bootEnabled = _bootSnapshotModEnabled.TryGetValue(c.ModId, out var be) ? be : true;
            var eff = _pendingModEnabled.TryGetValue(c.ModId, out var p) ? p : bootEnabled;
            if (eff) enabled++;
        }
        var title = $"🆕 {Strings.Get("tab_custom_characters")} ({enabled}/{list.Count})";
        if (_tabContainer != null && GodotObject.IsInstanceValid(_tabContainer) && _customCharTabIndex >= 0)
        {
            _tabContainer.SetTabTitle(_customCharTabIndex, title);
            _tabContainer.SetTabTooltip(_customCharTabIndex, $"{enabled} enabled / {list.Count} total");
        }
        UpdateOuterToggleText();
    }

    private static void BuildMixedAddonRows()
    {
        if (_mixedRows == null || !GodotObject.IsInstanceValid(_mixedRows)) return;

        for (var i = _mixedRows.GetChildCount() - 1; i >= 0; i--)
        {
            var child = _mixedRows.GetChild(i);
            _mixedRows.RemoveChild(child);
            child.QueueFree();
        }

        var packs = _pendingMixedAddons ?? new CardPacksConfig();
        if (packs.Ordering.Count == 0)
        {
            var placeholder = new Label
            {
                Text = Strings.Get("mixed_panel_empty"),
                CustomMinimumSize = new Vector2(460, 60),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(0.6f, 0.6f, 0.6f),
            };
            _mixedRows.AddChild(placeholder);
        }
        for (var i = 0; i < packs.Ordering.Count; i++)
        {
            var modId = packs.Ordering[i];
            var row = BuildMixedAddonRow(modId, packs, i, packs.Ordering.Count);
            _mixedRows.AddChild(row);
        }
        UpdateMixedHeader();
        UpdateCardPackHeader(); // dirty mark on shared Save button
    }

    private static Control BuildMixedAddonRow(string modId, CardPacksConfig packs, int index, int total)
    {
        var hbox = new CardPackRow
        {
            ModId = modId,
            Category = SkinRowCategory.MixedAddon,
            MouseFilter = Control.MouseFilterEnum.Pass,
            MouseDefaultCursorShape = Control.CursorShape.Move,
        };
        var enabled = packs.Enabled.TryGetValue(modId, out var e) ? e : false;

        var dragHandle = new Label
        {
            Text = "⋮⋮",
            CustomMinimumSize = new Vector2(20, 32),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hbox.AddChild(dragHandle);

        var check = new CheckBox { ButtonPressed = enabled, CustomMinimumSize = new Vector2(32, 32) };
        hbox.AddChild(check);

        // "Selected look / Mod look" toggle — only for mixed mods that bundle a revertible body
        // (ATA-style). Hidden for mods where the overlay would be a no-op (e.g. AncientWaifus).
        // Label names whose appearance the body uses: on = the character-select dropdown choice
        // ("🧍 Selected look", vanilla or another skin), off = this mod's own ("🧍 Mod look").
        // Either way the mod's card art is kept (its DLL stays loaded); only the body is governed.
        string VbText(bool on) => $"🧍 {Strings.Get(on ? "vanilla_body_on" : "vanilla_body_off")}";
        Button? vbToggle = null;
        if (_vanillaBodyEligible.Contains(modId))
        {
            var vbActive = _pendingVanillaBody.Contains(modId);
            vbToggle = new Button
            {
                Text = VbText(vbActive),
                ToggleMode = true,
                ButtonPressed = vbActive,
                CustomMinimumSize = new Vector2(96, 32),
                TooltipText = Strings.Get("mixed_vanilla_body_tooltip"),
            };
            // Added to the name column (beneath the name) further down, not inline in the main row.
        }

        var status = new Label
        {
            CustomMinimumSize = new Vector2(28, 32),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hbox.AddChild(status);

        var orderLabel = new Label
        {
            Text = $"{index + 1}",
            CustomMinimumSize = new Vector2(32, 32),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        hbox.AddChild(orderLabel);

        var aliases = LoadAliases();
        var label = new Label
        {
            Text = AliasService.Resolve(modId, aliases),
            CustomMinimumSize = new Vector2(248, 32),
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = modId,
        };
        label.MouseEntered += () => OnCardRowHoverStart(modId);
        label.MouseExited += () => OnCardRowHoverEnd(modId);

        var aliasEdit = new LineEdit
        {
            CustomMinimumSize = new Vector2(192, 32),
            PlaceholderText = Strings.Get("alias_placeholder"),
            Visible = false,
        };

        // Name column: mod name on top, the look toggle (if any) directly beneath it. Keeping the
        // toggle inside this column (instead of a full-width second row) lets the side controls
        // stay vertically centred against the taller two-line height.
        var nameCol = new VBoxContainer { CustomMinimumSize = new Vector2(248, 0) };
        nameCol.AddChild(label);
        nameCol.AddChild(aliasEdit);
        if (vbToggle != null) nameCol.AddChild(vbToggle);
        hbox.AddChild(new Control { CustomMinimumSize = new Vector2(12, 0) }); // gap between order number and name
        hbox.AddChild(nameCol);

        var editBtn = new Button { Text = "✏", CustomMinimumSize = new Vector2(28, 32), TooltipText = Strings.Get("alias_edit_tooltip") };
        hbox.AddChild(editBtn);

        var saveBtn = new Button { Text = "✓", CustomMinimumSize = new Vector2(28, 32), TooltipText = Strings.Get("alias_save_tooltip"), Visible = false };
        hbox.AddChild(saveBtn);

        var cancelBtn = new Button { Text = "✕", CustomMinimumSize = new Vector2(28, 32), TooltipText = Strings.Get("alias_cancel_tooltip"), Visible = false };
        hbox.AddChild(cancelBtn);

        void EnterEditMode()
        {
            aliasEdit.Text = LoadAliases().TryGetValue(modId, out var cur) ? cur : "";
            label.Visible = false;
            editBtn.Visible = false;
            aliasEdit.Visible = true;
            saveBtn.Visible = true;
            cancelBtn.Visible = true;
            aliasEdit.Modulate = Colors.White;
            aliasEdit.TooltipText = "";
            aliasEdit.GrabFocus();
            aliasEdit.CaretColumn = aliasEdit.Text.Length;
        }

        void ExitEditMode()
        {
            aliasEdit.Visible = false;
            saveBtn.Visible = false;
            cancelBtn.Visible = false;
            label.Visible = true;
            editBtn.Visible = true;
            label.Text = AliasService.Resolve(modId, LoadAliases());
        }

        void TrySave()
        {
            if (TrySaveAlias(modId, aliasEdit.Text, aliasEdit)) ExitEditMode();
        }

        editBtn.Pressed += EnterEditMode;
        saveBtn.Pressed += TrySave;
        cancelBtn.Pressed += ExitEditMode;
        aliasEdit.TextSubmitted += _ => TrySave();
        aliasEdit.TextChanged += _ =>
        {
            aliasEdit.Modulate = Colors.White;
            aliasEdit.TooltipText = "";
        };

        void ApplyVisual(bool isOn)
        {
            status.Text = isOn ? "✓" : "✗";
            status.Modulate = isOn ? new Color(0.4f, 0.95f, 0.45f) : new Color(0.95f, 0.45f, 0.45f);
            check.Modulate = isOn ? new Color(0.6f, 1.0f, 0.6f) : new Color(0.55f, 0.55f, 0.55f);
            label.Modulate = isOn ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
            dragHandle.Modulate = isOn ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
            orderLabel.Modulate = isOn ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
            // Vanilla-body only applies while the mod is on (its pck must stay mounted for cards).
            if (vbToggle is { } vb)
            {
                vb.Disabled = !isOn;
                vb.Modulate = isOn ? Colors.White : new Color(0.4f, 0.4f, 0.4f);
            }
        }
        ApplyVisual(enabled);

        check.Toggled += isOn =>
        {
            OnMixedAddonToggle(modId, isOn);
            ApplyVisual(isOn);
        };

        if (vbToggle is { } vbTog)
            vbTog.Toggled += isOn =>
            {
                OnVanillaBodyToggle(modId, isOn);
                vbTog.Text = VbText(isOn);
            };

        var upBtn = new Button { Text = "↑", CustomMinimumSize = new Vector2(32, 32), Disabled = index == 0 };
        upBtn.Pressed += () => MoveMixedAddon(modId, -1);
        hbox.AddChild(upBtn);

        var downBtn = new Button { Text = "↓", CustomMinimumSize = new Vector2(32, 32), Disabled = index == total - 1 };
        downBtn.Pressed += () => MoveMixedAddon(modId, +1);
        hbox.AddChild(downBtn);

        // Vertically centre every side control against the name column's height (taller when the
        // look toggle sits beneath the name), so they don't top-align.
        foreach (var child in hbox.GetChildren())
            if (child is Control c && c != nameCol)
                c.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;

        return hbox;
    }

    private static void OnMixedAddonToggle(string modId, bool isOn)
    {
        try
        {
            _pendingMixedAddons ??= ClonePacks(SkinChoicesConfig.LoadOrEmpty(_choicesPath).MixedAddons);
            var current = _pendingMixedAddons.Enabled.TryGetValue(modId, out var c) ? c : false;
            if (current == isOn) return;
            _pendingMixedAddons.Enabled[modId] = isOn;
            MainFile.Logger.Info($"mixed addon pending toggle: {modId} → {isOn}");
            UpdateMixedHeader();
            UpdateCardPackHeader();
        }
        catch (Exception ex) { MainFile.Logger.Warn($"mixed addon toggle error: {ex.Message}"); }
    }

    private static void OnVanillaBodyToggle(string modId, bool isOn)
    {
        try
        {
            if (isOn) _pendingVanillaBody.Add(modId);
            else _pendingVanillaBody.Remove(modId);
            MainFile.Logger.Info($"vanilla-body pending toggle: {modId} → {isOn}");
            // Re-evaluate dirty state so the shared Save/Discard buttons + dirty mark update.
            UpdateMixedHeader();
            UpdateCardPackHeader();
        }
        catch (Exception ex) { MainFile.Logger.Warn($"vanilla-body toggle error: {ex.Message}"); }
    }

    private static void MoveMixedAddon(string modId, int delta)
    {
        try
        {
            _pendingMixedAddons ??= ClonePacks(SkinChoicesConfig.LoadOrEmpty(_choicesPath).MixedAddons);
            var idx = _pendingMixedAddons.Ordering.IndexOf(modId);
            if (idx < 0) return;
            var newIdx = idx + delta;
            if (newIdx < 0 || newIdx >= _pendingMixedAddons.Ordering.Count) return;
            var item = _pendingMixedAddons.Ordering[idx];
            _pendingMixedAddons.Ordering.RemoveAt(idx);
            _pendingMixedAddons.Ordering.Insert(newIdx, item);
            Callable.From(BuildMixedAddonRows).CallDeferred();
        }
        catch (Exception ex) { MainFile.Logger.Warn($"mixed addon reorder error: {ex.Message}"); }
    }

    public static void HandleMixedAddonDragDropReorder(string sourceModId, string targetModId, bool insertAbove)
    {
        try
        {
            _pendingMixedAddons ??= ClonePacks(SkinChoicesConfig.LoadOrEmpty(_choicesPath).MixedAddons);
            var srcIdx = _pendingMixedAddons.Ordering.IndexOf(sourceModId);
            var targetIdx = _pendingMixedAddons.Ordering.IndexOf(targetModId);
            if (srcIdx < 0 || targetIdx < 0 || srcIdx == targetIdx) return;
            var insertIdx = insertAbove ? targetIdx : targetIdx + 1;
            var item = _pendingMixedAddons.Ordering[srcIdx];
            _pendingMixedAddons.Ordering.RemoveAt(srcIdx);
            if (srcIdx < insertIdx) insertIdx--;
            if (insertIdx < 0) insertIdx = 0;
            if (insertIdx > _pendingMixedAddons.Ordering.Count) insertIdx = _pendingMixedAddons.Ordering.Count;
            _pendingMixedAddons.Ordering.Insert(insertIdx, item);
            Callable.From(BuildMixedAddonRows).CallDeferred();
        }
        catch (Exception ex) { MainFile.Logger.Warn($"mixed addon drag-drop error: {ex.Message}"); }
    }

    private static void OnSave()
    {
        try
        {
            if (!IsAnyDirty())
            {
                MainFile.Logger.Info("save clicked but no pending changes (vs boot snapshot)");
                return;
            }
            var choices = SkinChoicesConfig.LoadOrEmpty(_choicesPath);
            foreach (var kv in _pendingActiveByCharacter)
            {
                if (choices.Characters.TryGetValue(kv.Key, out var c)) c.Active = kv.Value;
            }
            if (_pendingCardPacks != null) choices.CardPacks = ClonePacks(_pendingCardPacks);
            if (_pendingMixedAddons != null) choices.MixedAddons = ClonePacks(_pendingMixedAddons);
            choices.VanillaBodyMods = new HashSet<string>(_pendingVanillaBody, StringComparer.OrdinalIgnoreCase);

            // Apply All Mods reclassifications. Action sentinel encodes target:
            //   "auto"        → clear overrides; scanner re-classifies on next boot
            //   "skip"        → add to _dll_skin_skipped, remove from assignments
            //   "skin:<char>" → add to _dll_skin_assignments with that char, remove from skipped
            foreach (var (modId, action) in _pendingAllModsDecisions)
            {
                if (action == "skip")
                {
                    choices.DllSkinAssignments.Remove(modId);
                    choices.DllSkinSkipped.Add(modId);
                }
                else if (action.StartsWith("skin:", StringComparison.Ordinal))
                {
                    var ch = action.Substring(5);
                    choices.DllSkinSkipped.Remove(modId);
                    choices.DllSkinAssignments[modId] = ch;
                }
                else // "auto" or unknown
                {
                    choices.DllSkinAssignments.Remove(modId);
                    choices.DllSkinSkipped.Remove(modId);
                }
            }

            choices.Save(_choicesPath);
            _pendingActiveByCharacter.Clear();
            _pendingAllModsDecisions.Clear();

            // Apply Other Mods enable/disable to STS2's settings.save mod_list.
            if (_pendingModEnabled.Count > 0)
            {
                var userDataDir = OS.GetUserDataDir();
                var settings = Sts2SettingsWriter.FindAndLoad(userDataDir);
                if (settings != null)
                {
                    var diskChanged = Sts2SettingsWriter.ApplyModEnabledState(settings, _pendingModEnabled);
                    var memChanged = false;
                    var mm = MegaCrit.Sts2.Core.Modding.ModManager._settings;
                    if (mm != null)
                    {
                        foreach (var entry in mm.ModList)
                        {
                            if (string.IsNullOrEmpty(entry.Id)) continue;
                            if (_pendingModEnabled.TryGetValue(entry.Id, out var want) && entry.IsEnabled != want)
                            {
                                entry.IsEnabled = want;
                                memChanged = true;
                            }
                        }
                    }
                    if (diskChanged) Sts2SettingsWriter.Save(settings);
                    MainFile.Logger.Info($"save → applied {_pendingModEnabled.Count} mod_list toggle(s) (disk={diskChanged} mem={memChanged}).");
                }
                else
                {
                    MainFile.Logger.Warn("save → mod_list toggle requested but settings.save not found.");
                }
                // Take a snapshot so this session matches what we just wrote (until full restart).
                foreach (var kv in _pendingModEnabled) _bootSnapshotModEnabled[kv.Key] = kv.Value;
                _pendingModEnabled.Clear();
            }

            MainFile.Logger.Info("save → choices.json updated (watcher may also fire; ShowOrReset will dedupe)");
            UpdateCardPackHeader();
            UpdateMixedHeader();
            UpdateAllModsHeader();

            // Show modal directly so this doesn't depend on the file watcher firing.
            // The watcher may also call ShowOrReset; the second call just resets the countdown.
            var managerDataDir = Path.GetDirectoryName(_choicesPath);
            if (!string.IsNullOrEmpty(managerDataDir))
            {
                RestartCountdownModal.ShowOrReset(managerDataDir, 10, () => { });
            }
        }
        catch (Exception ex) { MainFile.Logger.Warn($"OnSave error: {ex.Message}"); }
    }

    private static void OnDiscard()
    {
        try
        {
            if (_bootSnapshotCardPacks == null) return;
            _pendingActiveByCharacter.Clear();
            _pendingAllModsDecisions.Clear();
            _pendingModEnabled.Clear();
            _pendingCardPacks = ClonePacks(_bootSnapshotCardPacks);
            if (_bootSnapshotMixedAddons != null) _pendingMixedAddons = ClonePacks(_bootSnapshotMixedAddons);
            _pendingVanillaBody = new HashSet<string>(_bootSnapshotVanillaBody, StringComparer.OrdinalIgnoreCase);

            var choices = SkinChoicesConfig.LoadOrEmpty(_choicesPath);
            foreach (var kv in _bootSnapshotActive)
            {
                if (choices.Characters.TryGetValue(kv.Key, out var c)) c.Active = kv.Value;
            }
            choices.CardPacks = ClonePacks(_bootSnapshotCardPacks);
            if (_bootSnapshotMixedAddons != null) choices.MixedAddons = ClonePacks(_bootSnapshotMixedAddons);
            choices.VanillaBodyMods = new HashSet<string>(_bootSnapshotVanillaBody, StringComparer.OrdinalIgnoreCase);
            choices.Save(_choicesPath);

            // Restore settings.save card-pack state too so the next launch matches boot.
            var userDataDir = OS.GetUserDataDir();
            var settings = Sts2SettingsWriter.FindAndLoad(userDataDir);
            if (settings != null && _cardMods.Count > 0)
            {
                CardPackApplier.ApplyToSettings(settings, _bootSnapshotCardPacks, _cardMods);
                CardPackApplier.ApplyToMemoryModList(_bootSnapshotCardPacks);
                Sts2SettingsWriter.Save(settings);
            }

            // Tell watcher the new disk state is the applied state so its imminent fire is a no-op.
            _watcher?.NoteSavedAsApplied();

            MainFile.Logger.Info("discard → all changes reverted to boot snapshot (disk + settings + pending)");
            Callable.From(() =>
            {
                BuildCardPackRows();
                BuildMixedAddonRows();
                BuildAllModsRows();
                BuildCustomCharacterRows();
                BuildAppliedSummaryRows();
                RefreshItems();
            }).CallDeferred();
        }
        catch (Exception ex) { MainFile.Logger.Warn($"OnDiscard error: {ex.Message}"); }
    }

    private static bool IsAnyDirty()
    {
        if (_bootSnapshotCardPacks == null) return false;

        var pending = _pendingCardPacks ?? new CardPacksConfig();
        if (!pending.Ordering.SequenceEqual(_bootSnapshotCardPacks.Ordering, StringComparer.OrdinalIgnoreCase)) return true;
        if (pending.Enabled.Count != _bootSnapshotCardPacks.Enabled.Count) return true;
        foreach (var kv in pending.Enabled)
        {
            if (!_bootSnapshotCardPacks.Enabled.TryGetValue(kv.Key, out var v) || v != kv.Value) return true;
        }

        if (_bootSnapshotMixedAddons != null)
        {
            var pendingMixed = _pendingMixedAddons ?? new CardPacksConfig();
            if (!pendingMixed.Ordering.SequenceEqual(_bootSnapshotMixedAddons.Ordering, StringComparer.OrdinalIgnoreCase)) return true;
            if (pendingMixed.Enabled.Count != _bootSnapshotMixedAddons.Enabled.Count) return true;
            foreach (var kv in pendingMixed.Enabled)
            {
                if (!_bootSnapshotMixedAddons.Enabled.TryGetValue(kv.Key, out var v) || v != kv.Value) return true;
            }
        }

        if (!_pendingVanillaBody.SetEquals(_bootSnapshotVanillaBody)) return true;

        var disk = SkinChoicesConfig.LoadOrEmpty(_choicesPath);
        foreach (var (character, choice) in disk.Characters)
        {
            var bootActive = _bootSnapshotActive.TryGetValue(character, out var b) ? b : choice.Active;
            var effectiveActive = _pendingActiveByCharacter.TryGetValue(character, out var p) ? p : (choice.Active ?? "default");
            if (!string.Equals(effectiveActive, bootActive, StringComparison.OrdinalIgnoreCase)) return true;
        }

        // Pending classification changes (only reachable via skin_choices.json edits in v0.12,
        // but kept for forward compat if a future tab re-adds the dropdown).
        foreach (var (modId, action) in _pendingAllModsDecisions)
        {
            var bootAssigned = _bootSnapshotDllAssignments.TryGetValue(modId, out var ba) ? ba : "";
            var bootSkipped = _bootSnapshotDllSkipped.Contains(modId);

            if (action == "skip")
            {
                if (!bootSkipped) return true;
            }
            else if (action.StartsWith("skin:", StringComparison.Ordinal))
            {
                var ch = action.Substring(5);
                if (!string.Equals(ch, bootAssigned, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else // "auto"
            {
                if (bootSkipped || !string.IsNullOrEmpty(bootAssigned)) return true;
            }
        }

        // Pending mod_list enable/disable changes from the Other Mods tab checkbox.
        foreach (var (modId, want) in _pendingModEnabled)
        {
            var bootEnabled = _bootSnapshotModEnabled.TryGetValue(modId, out var be) ? be : true;
            if (want != bootEnabled) return true;
        }

        return false;
    }

    private static CardPacksConfig ClonePacks(CardPacksConfig src) => new()
    {
        Schema = src.Schema,
        Ordering = new List<string>(src.Ordering),
        Enabled = new Dictionary<string, bool>(src.Enabled, StringComparer.OrdinalIgnoreCase),
    };

    private static string GetSelectedVariantModId()
    {
        if (_opt == null || !GodotObject.IsInstanceValid(_opt) || _opt.ItemCount == 0) return "";
        var idx = _opt.Selected;
        if (idx < 0 || idx >= _opt.ItemCount) return "";
        var meta = _opt.GetItemMetadata(idx);
        return meta.VariantType == Variant.Type.String ? meta.AsString() : _opt.GetItemText(idx);
    }

    private static void UpdateVariantEditBtnState(string variantModId)
    {
        if (_variantEditBtn == null || !GodotObject.IsInstanceValid(_variantEditBtn)) return;
        // "default" is a virtual variant (= unmount everything) — no alias makes sense for it.
        // Dropdown being disabled (no variants / no config) also disables aliasing.
        var dropdownActive = _opt != null && GodotObject.IsInstanceValid(_opt) && !_opt.Disabled;
        var canEdit = dropdownActive
            && !string.IsNullOrEmpty(variantModId)
            && !string.Equals(variantModId, "default", StringComparison.OrdinalIgnoreCase);
        _variantEditBtn.Disabled = !canEdit;
        _variantEditBtn.Modulate = canEdit ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
    }

    private static void ToggleVariantEditMode()
    {
        if (_variantEditLine == null || !GodotObject.IsInstanceValid(_variantEditLine)) return;
        if (_opt == null || !GodotObject.IsInstanceValid(_opt)) return;
        if (_variantEditLine.Visible) { ExitVariantEditMode(); return; }

        var modId = GetSelectedVariantModId();
        if (string.IsNullOrEmpty(modId) || string.Equals(modId, "default", StringComparison.OrdinalIgnoreCase))
            return;

        _variantEditLine.Text = LoadAliases().TryGetValue(modId, out var cur) ? cur : "";
        _variantEditLine.Modulate = Colors.White;
        _variantEditLine.TooltipText = "";
        _opt.Visible = false;
        if (_variantEditBtn != null && GodotObject.IsInstanceValid(_variantEditBtn)) _variantEditBtn.Visible = false;
        _variantEditLine.Visible = true;
        if (_variantSaveBtn != null && GodotObject.IsInstanceValid(_variantSaveBtn)) _variantSaveBtn.Visible = true;
        if (_variantCancelBtn != null && GodotObject.IsInstanceValid(_variantCancelBtn)) _variantCancelBtn.Visible = true;
        _variantEditLine.GrabFocus();
        _variantEditLine.CaretColumn = _variantEditLine.Text.Length;
    }

    private static void ExitVariantEditMode()
    {
        if (_variantEditLine == null || !GodotObject.IsInstanceValid(_variantEditLine)) return;
        if (_opt == null || !GodotObject.IsInstanceValid(_opt)) return;
        _variantEditLine.Visible = false;
        if (_variantSaveBtn != null && GodotObject.IsInstanceValid(_variantSaveBtn)) _variantSaveBtn.Visible = false;
        if (_variantCancelBtn != null && GodotObject.IsInstanceValid(_variantCancelBtn)) _variantCancelBtn.Visible = false;
        _opt.Visible = true;
        if (_variantEditBtn != null && GodotObject.IsInstanceValid(_variantEditBtn)) _variantEditBtn.Visible = true;
    }

    private static void OnVariantAliasSubmitted(string newText)
    {
        if (_variantEditLine == null || !GodotObject.IsInstanceValid(_variantEditLine)) return;
        var modId = GetSelectedVariantModId();
        if (string.IsNullOrEmpty(modId)) { ExitVariantEditMode(); return; }
        if (TrySaveAlias(modId, newText, _variantEditLine)) ExitVariantEditMode();
    }

    private static Dictionary<string, string> LoadAliases()
    {
        var choices = SkinChoicesConfig.LoadOrEmpty(_choicesPath);
        return new Dictionary<string, string>(choices.Aliases, StringComparer.OrdinalIgnoreCase);
    }

    // All modIds the user could possibly assign an alias to — variant pcks + card packs.
    // Used to enforce "alias must not collide with any modId or other alias".
    private static IEnumerable<string> EnumerateAllModIds()
    {
        if (_byCharacter != null)
        {
            foreach (var kv in _byCharacter)
                foreach (var v in kv.Value)
                    yield return v.ModId;
        }
        foreach (var m in _cardMods) yield return m.ModId;
        // Mixed mods are already present in _byCharacter (registered as Character variants),
        // so we don't double-yield them here.
    }

    // Saves an alias attempt. Returns true when the alias is accepted (incl. empty = clear);
    // returns false when validation rejects the input, and styles `edit` to indicate the error.
    private static bool TrySaveAlias(string modId, string newAlias, LineEdit edit)
    {
        try
        {
            var trimmed = (newAlias ?? "").Trim();
            var choices = SkinChoicesConfig.LoadOrEmpty(_choicesPath);

            if (string.IsNullOrEmpty(trimmed))
            {
                // Empty = clear the alias.
                if (choices.Aliases.Remove(modId))
                {
                    choices.Save(_choicesPath);
                    MainFile.Logger.Info($"alias cleared: {modId}");
                    Callable.From(() => { BuildCardPackRows(); BuildMixedAddonRows(); RefreshItems(); }).CallDeferred();
                }
                return true;
            }

            var verdict = AliasService.Validate(modId, trimmed, EnumerateAllModIds(), choices.Aliases);
            if (verdict != AliasService.AliasValidationResult.Ok)
            {
                edit.Modulate = new Color(1f, 0.55f, 0.55f);
                edit.TooltipText = verdict switch
                {
                    AliasService.AliasValidationResult.CollidesWithModId => Strings.Get("alias_dup_modid"),
                    AliasService.AliasValidationResult.CollidesWithOtherAlias => Strings.Get("alias_dup_alias"),
                    AliasService.AliasValidationResult.SameAsOwnModId => Strings.Get("alias_same_as_own"),
                    _ => "",
                };
                MainFile.Logger.Info($"alias rejected for {modId}: {verdict} (input='{trimmed}')");
                return false;
            }

            choices.Aliases[modId] = trimmed;
            choices.Save(_choicesPath);
            MainFile.Logger.Info($"alias saved: {modId} → '{trimmed}'");
            Callable.From(() => { BuildCardPackRows(); BuildMixedAddonRows(); RefreshItems(); }).CallDeferred();
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"TrySaveAlias error: {ex.Message}");
            return false;
        }
    }

    public static void RefreshCardPacks()
    {
        Callable.From(() =>
        {
            _pendingCardPacks = ClonePacks(SkinChoicesConfig.LoadOrEmpty(_choicesPath).CardPacks);
            BuildCardPackRows();
        }).CallDeferred();
    }

    public static void RefreshMixedAddons()
    {
        Callable.From(() =>
        {
            _pendingMixedAddons = ClonePacks(SkinChoicesConfig.LoadOrEmpty(_choicesPath).MixedAddons);
            BuildMixedAddonRows();
        }).CallDeferred();
    }

    private static void EnsureLocaleSubscribed()
    {
        if (_localeChangeSubscribed) return;
        try
        {
            if (LocManager.Instance == null) { MainFile.Logger.Warn("LocManager.Instance null at subscribe time"); return; }
            LocManager.Instance.SubscribeToLocaleChange(OnLocaleChanged);
            _localeChangeSubscribed = true;
            MainFile.Logger.Info("subscribed to LocManager locale change");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"failed to subscribe to locale change: {ex.Message}");
        }
    }

    private static void OnLocaleChanged()
    {
        Callable.From(() =>
        {
            try
            {
                MainFile.Logger.Info($"locale changed → reconciling overlay (was attached to '{_lastScreen?.Name}')");
                if (_hbox != null && GodotObject.IsInstanceValid(_hbox) && _hbox.IsInsideTree())
                {
                    MainFile.Logger.Info("overlay still in tree; refreshing label/items + card pack panel");
                    if (_label != null && GodotObject.IsInstanceValid(_label))
                    {
                        _label.Text = Strings.Get("skin_label") + ":";
                    }
                    if (_previewHoverIcon != null && GodotObject.IsInstanceValid(_previewHoverIcon))
                    {
                        _previewHoverIcon.TooltipText = Strings.Get("preview_toggle_tooltip");
                    }
                    if (_variantEditBtn != null && GodotObject.IsInstanceValid(_variantEditBtn))
                    {
                        _variantEditBtn.TooltipText = Strings.Get("alias_edit_tooltip");
                    }
                    if (_variantSaveBtn != null && GodotObject.IsInstanceValid(_variantSaveBtn))
                    {
                        _variantSaveBtn.TooltipText = Strings.Get("alias_save_tooltip");
                    }
                    if (_variantCancelBtn != null && GodotObject.IsInstanceValid(_variantCancelBtn))
                    {
                        _variantCancelBtn.TooltipText = Strings.Get("alias_cancel_tooltip");
                    }
                    if (_variantEditLine != null && GodotObject.IsInstanceValid(_variantEditLine))
                    {
                        _variantEditLine.PlaceholderText = Strings.Get("alias_placeholder");
                    }
                    RefreshItems();
                    BuildCardPackRows();
                    BuildMixedAddonRows();
                    BuildAppliedSummaryRows();
                    UpdateMixedHeader();
                    if (_appliedCopyBtn != null && GodotObject.IsInstanceValid(_appliedCopyBtn))
                    {
                        _appliedCopyBtn.Text = Strings.Get("applied_copy");
                        _appliedCopyBtn.TooltipText = Strings.Get("applied_copy_tooltip");
                    }
                    return;
                }

                MainFile.Logger.Info("overlay lost from tree → re-attaching");
                _opt = null;
                _label = null;
                _hbox = null;
                _variantEditBtn = null;
                _variantSaveBtn = null;
                _variantCancelBtn = null;
                _variantEditLine = null;
                _previewHoverIcon = null;
                _previewContainer = null;
                _previewRect = null;
                _previewCaption = null;
                _previewHovered = false;
                _accordionVBox = null;
                _outerToggleBtn = null;
                _outerBody = null;
                _tabContainer = null;
                _appliedTabIndex = -1;
                _cardPackTabIndex = -1;
                _mixedTabIndex = -1;
                _allModsTabIndex = -1;
                _cardPackSaveBtn = null;
                _cardPackDiscardBtn = null;
                _cardPackRows = null;
                _mixedRows = null;
                _allModsRows = null;
                _appliedRows = null;
                _appliedCopyBtn = null;
                var mainLoop = Engine.GetMainLoop();
                if (mainLoop is not SceneTree tree) return;
                var screen = FindCharacterSelectScreen(tree.Root);
                if (screen != null) DoAttach(screen);
                else MainFile.Logger.Info("no CharacterSelectScreen in tree currently; will re-attach when user navigates back");
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"OnLocaleChanged error: {ex.Message}");
            }
        }).CallDeferred();
    }

    private static Node? FindCharacterSelectScreen(Node start)
    {
        if (start.Name.ToString().Contains("CharacterSelectScreen", StringComparison.OrdinalIgnoreCase)) return start;
        foreach (var child in start.GetChildren())
        {
            var found = FindCharacterSelectScreen(child);
            if (found != null) return found;
        }
        return null;
    }

    public static void OnCharacterSelected(string characterId)
    {
        _currentCharacter = (characterId ?? "").ToLowerInvariant();
        Callable.From(RefreshItems).CallDeferred();
    }

    public static void RefreshDropdown()
    {
        Callable.From(() =>
        {
            // External disk change (e.g. user-edited choices.json) — drop any pending in-memory char selection
            // so the dropdown reflects what's actually on disk.
            _pendingActiveByCharacter.Clear();
            RefreshItems();
            UpdateCardPackHeader();
        }).CallDeferred();
    }

    private static void RefreshItems()
    {
        if (_opt == null || !GodotObject.IsInstanceValid(_opt)) return;
        _suppressNextItemSelected = true;
        try
        {
            _opt.Clear();
            var skinLabel = Strings.Get("skin_label");

            if (_byCharacter == null || !_byCharacter.TryGetValue(_currentCharacter, out var variants) || variants.Count == 0)
            {
                if (_label != null) _label.Text = $"{skinLabel} [{(string.IsNullOrEmpty(_currentCharacter) ? "—" : _currentCharacter)}]:";
                _opt.AddItem(Strings.Get("no_variants"));
                _opt.Disabled = true;
                ExitVariantEditMode();
                UpdateVariantEditBtnState("");
                UpdatePreview("default");
                return;
            }
            var choices = SkinChoicesConfig.LoadOrEmpty(_choicesPath);
            if (!choices.Characters.TryGetValue(_currentCharacter, out var c))
            {
                if (_label != null) _label.Text = $"{skinLabel} [{(string.IsNullOrEmpty(_currentCharacter) ? "—" : _currentCharacter)}]:";
                _opt.AddItem(Strings.Get("not_configured"));
                _opt.Disabled = true;
                ExitVariantEditMode();
                UpdateVariantEditBtnState("");
                UpdatePreview("default");
                return;
            }
            _opt.Disabled = false;

            // pending > disk
            var effectiveActive = _pendingActiveByCharacter.TryGetValue(_currentCharacter, out var pa) ? pa : (c.Active ?? "default");
            var bootActive = _bootSnapshotActive.TryGetValue(_currentCharacter, out var b) ? b : effectiveActive;
            var charDirty = !string.Equals(effectiveActive, bootActive, StringComparison.OrdinalIgnoreCase);
            var dirtyMark = charDirty ? " *" : "";
            if (_label != null) _label.Text = $"{skinLabel} [{_currentCharacter}]:{dirtyMark}";

            var aliases = LoadAliases();
            var mixedIds = new HashSet<string>(_mixedMods.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < c.AvailableVariants.Count; i++)
            {
                var v = c.AvailableVariants[i];
                var isDefault = string.Equals(v, "default", StringComparison.OrdinalIgnoreCase);
                var resolved = isDefault ? v : AliasService.Resolve(v, aliases);
                // 📦 marks mixed mods (bring card art / events in addition to the spine) so the user
                // knows picking it from the dropdown applies the whole bundle as main + extras.
                var isMixedItem = !isDefault && mixedIds.Contains(v);
                var displayText = isMixedItem ? $"📦 {resolved}" : resolved;
                _opt.AddItem(displayText, i);
                _opt.SetItemMetadata(i, v);
                if (isMixedItem) _opt.SetItemTooltip(i, Strings.Get("mixed_indicator_tooltip"));
                if (string.Equals(v, effectiveActive, StringComparison.OrdinalIgnoreCase))
                {
                    _opt.Selected = i;
                }
            }
            _opt.TooltipText = Strings.Get("mixed_indicator_tooltip");
            UpdateVariantEditBtnState(effectiveActive);
            MainFile.Logger.Info($"OptionButton populated for '{_currentCharacter}': {c.AvailableVariants.Count} items, effective='{effectiveActive}' (disk='{c.Active}', boot='{bootActive}')");
            UpdatePreview(effectiveActive);
        }
        finally
        {
            _suppressNextItemSelected = false;
        }
    }

    private static void OnVariantSelected(long index)
    {
        MainFile.Logger.Info($"OptionButton.ItemSelected event fired: index={index}, suppress={_suppressNextItemSelected}");
        HandleSelection(index);
    }

    private static void OnVariantSelectedSafe(long index)
    {
        if (_alreadyHandledThisEvent) { _alreadyHandledThisEvent = false; return; }
        MainFile.Logger.Info($"OptionButton Connect callback: index={index}");
        HandleSelection(index);
    }

    private static void HandleSelection(long index)
    {
        if (_suppressNextItemSelected)
        {
            MainFile.Logger.Info($"  suppressed (programmatic update)");
            return;
        }
        _alreadyHandledThisEvent = true;
        try
        {
            if (_opt == null) return;
            // GetItemText returns the display label (which may be an alias); GetItemMetadata
            // returns the underlying ModId we stored at AddItem time. We always match on the
            // ModId so aliases stay purely cosmetic.
            var meta = _opt.GetItemMetadata((int)index);
            var chosen = meta.VariantType == Variant.Type.String ? meta.AsString() : _opt.GetItemText((int)index);
            MainFile.Logger.Info($"  chosen='{chosen}' for character='{_currentCharacter}'");
            if (string.IsNullOrEmpty(chosen) || chosen.StartsWith("("))
            {
                MainFile.Logger.Info("  ignoring placeholder/empty option");
                return;
            }

            var choices = SkinChoicesConfig.LoadOrEmpty(_choicesPath);
            if (!choices.Characters.TryGetValue(_currentCharacter, out var c))
            {
                MainFile.Logger.Warn($"  no choice entry for '{_currentCharacter}'");
                return;
            }

            // pending tracks "what the user wants" vs. disk.
            // If equal to disk, remove the pending entry (no-op).
            if (string.Equals(c.Active, chosen, StringComparison.OrdinalIgnoreCase))
            {
                if (_pendingActiveByCharacter.Remove(_currentCharacter))
                {
                    MainFile.Logger.Info($"  pending cleared (matches disk active='{c.Active}')");
                }
            }
            else
            {
                _pendingActiveByCharacter[_currentCharacter] = chosen;
                MainFile.Logger.Info($"  pending set: {_currentCharacter} → {chosen}");
            }

            // Update label dirty mark + Save/Discard buttons.
            RefreshItems();
            UpdateCardPackHeader();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"select error: {ex.Message}");
        }
    }
}
