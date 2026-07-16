using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Sts2SkinManager.Discovery;

// Single source of truth for the STS2 asset-path patterns the manager recognises.
// Both SkinModScanner (classification) and UnclassifiedModInventory (forensic counting) read
// from here, so adding/loosening a pattern propagates to both without drift.
public static class AssetDomainCatalog
{
    // Combat-spine asset path. Captured group is the base character whose visuals the mod
    // overrides — e.g. `animations/characters/defect/defect.atlas` yields `defect`.
    public static readonly Regex CharacterSpineRegex = new(
        @"animations/characters/([a-z_][a-z0-9_]*)/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // Card art named after the base-game card class — `card_art/MegaCrit.Sts2.Core.Models.Cards.X_card_art.png`
    // (RegentCardsAnimeRework), `assets/images/cards/MegaCrit.Sts2.Core.Models.Cards.X_portrait.png`
    // (TheDefectCardArtMod).
    //
    // The `_card_art`/`_portrait` suffix is REQUIRED, not decoration: the bare namespace also appears
    // in the IL metadata of any pck that embeds a DLL referencing base card types, which has nothing
    // to do with card visuals. SlayTheUniverse (a content mod) carries 681 bare occurrences — strings
    // like `MegaCrit.Sts2.Core.Models.Cards.Mocks` and `...Abrasive+<OnPlay>d__7` — and the unanchored
    // form classified it as a card mod, surfacing it in the Cards panel where the user could reorder
    // or disable it to no effect. Requiring the suffix drops it to 0 hits while keeping the real card
    // packs (AliceDefectCard 736, RegentCardsAnimeRework 300).
    public static readonly Regex CardArtBaseRegex = new(
        @"MegaCrit\.Sts2\.Core\.Models\.Cards\.\w+_(?:card_art|portrait)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // Mods that define their own card namespace and rely on a Harmony DLL to redirect portrait
    // lookups (RegentFemPortraits pattern). The literal `/card_portraits/` segment is the marker.
    // CAUTION: by itself this also matches custom-character mods that pack their own cards under
    // a similar namespace (e.g. The Watcher STS1→STS2 port stores 184 portraits under
    // `res://Watcher/images/card_portraits/`). Classification must combine this with the custom-
    // character indicator below to tell the two apart.
    public static readonly Regex CardPortraitsRegex = new(
        @"/card_portraits/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // Root segment of any card-art resource path: `res://{root}/.../card_portraits/`,
    // `res://{root}/.../card_art/`, `res://{root}/.../{name}cards{name}.sprites/`.
    // The root is what decides whether the Cards panel's priority order can do anything at all —
    // see CardOverrideMode.
    private static readonly Regex CardPathRootRegex = new(
        @"res://([^/""]+)/(?:[^/""]+/)*(?:card_portraits/|card_art/|[^/""]*card[^/""]*\.sprites/)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // How a card pack gets its art in front of the player — and therefore whether reordering it in
    // the Cards panel does anything.
    //
    //   SharedPath  `res://images/packed/card_portraits/ironclad/break.png` (base-owned) or
    //               `res://generated/assets/card_art/...` (shared tooling convention).
    //               Two packs that reskin the same card write the SAME resource path, so they
    //               genuinely collide and Godot's last-mount-wins picks the winner. mod_list order
    //               — which is exactly what the panel's priority controls — decides. Priority WORKS.
    //
    //   ModPrivate  `res://ATA_IronClad/images/card_portraits/ironclad/break.png`.
    //               Every pack's art sits at its own distinct path, so nothing collides and there is
    //               nothing for mount order to arbitrate. A Harmony DLL picks what
    //               CardModel.PortraitPath returns (RitsuLib resolves competing packs by its own
    //               registration-order registry, ATA via its own patch). Mount order cannot reach
    //               that decision, so priority is INERT — reordering these rows changes nothing.
    //
    // Verified against every installed pack: images/generated roots for AncientWaifus, Ryoshu, raye,
    // AliceDefectCard, RegentCardsAnimeRework; own-id roots for ATA_IronClad, ATA_Silent, FGOCore,
    // ArtoriaCaster.
    public enum CardOverrideMode { None, SharedPath, ModPrivate, Mixed }

    // A pack cannot collide with anything when it files its art under its own mod id — that root is
    // unique to it by construction. Any other root (`images`, `generated`, `.godot`) is one other
    // packs also write to, so treat it as shared. Comparing against the pck's own id keeps this
    // framework-agnostic: it reads where the mod puts its files, not which library it links.
    private static bool IsPrivateRoot(string root, string? ownNamespace) =>
        ownNamespace != null && string.Equals(root, ownNamespace, StringComparison.OrdinalIgnoreCase);

    // Strong signals that a mod adds a brand-new character via the BaseLib framework rather than
    // skinning an existing one. Any single hit is enough — these strings shouldn't appear in
    // pure skin/card mods:
    //   - `Code/Character/` — mod ships its own character C# class (Watcher.cs, ZilchD.cs, etc.)
    //   - `CustomCharacterModel` — BaseLib's base class for derived custom characters
    //   - `characters.json` — BaseLib's character-registration manifest convention
    //
    // The `characters.json` alternative uses a negative lookbehind to EXCLUDE paths under
    // `localization/{lang}/`, where a file by that name is just a name dictionary for
    // event/dialog NPCs, NOT a BaseLib registration manifest. AncientRetexture (event
    // retexture mod) ships `AncientRetexture/localization/eng/characters.json` — without
    // this exclusion it gets misclassified as a custom-character mod and silently skipped.
    // Real BaseLib `characters.json` lives at the mod root, never under `localization/`.
    public static readonly Regex CustomCharacterIndicatorRegex = new(
        @"Code/Character/|CustomCharacterModel|(?<!localization/[a-z]{2,4}/)characters\.json",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // Character-select screen assets — both the spine animations under
    // `animations/character_select/{char}/` and visual overrides like
    // `images/packed/character_select/char_select_{char}.png` or
    // `assets/scenes/ui/char_select_bg_{char}.tscn`. A mod touching these without touching
    // combat spine (`animations/characters/`) typically signals: a card mod that also restyles
    // the character-select screen for that character (TheDefectCardArtMod pattern). Used by
    // the mixed-detection rule to flag cross-domain mods so the user can spot visual conflicts
    // with character skins targeting the same base character.
    public static readonly Regex CharSelectAssetRegex = new(
        @"char_select_[a-z_]+|animations/character_select/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // Character ids extracted from character-select asset filenames. Three layout conventions:
    //   {id}_char_select(_bg/_locked).png    — modded new-character portrait (HornetMod ships
    //                                           `images/hornet_char_select.png` → id `hornet`)
    //   char_select_bg_{id}.png/.tscn         — STS2 base char-select background (`char_select_bg_defect`)
    //   animations/character_select/{id}/     — char-select spine directory
    // A mod that ships a char-select asset for an id NOT in the base roster is introducing a brand-
    // new playable character — skins reuse the BASE character's id, they never mint a new one. This
    // is the framework-AGNOSTIC "adds a character" signal: it fires for RitsuLib, BaseLib, or any
    // future custom-character framework without needing to know the framework, because it reads the
    // character the mod ships rather than the library it links. Complements
    // CustomCharacterIndicatorRegex (which only catches BaseLib's Code/Character / CustomCharacterModel
    // / root characters.json conventions and so misses RitsuLib-packed characters like Hornet).
    private static readonly Regex[] CharSelectIdRegexes =
    {
        new(@"([a-z][a-z0-9]+)_char_select", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"char_select_bg_([a-z][a-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"animations/character_select/([a-z][a-z0-9_]+)/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    // Generic char-select asset tokens that are layer/state names, never a character id. Guards the
    // capture groups above from treating `char_select_bg` / `..._locked` as a "new character".
    private static readonly HashSet<string> CharSelectIdStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "bg", "locked", "hover", "frame", "glow", "mask", "default", "portrait",
        "icon", "button", "panel", "select", "new", "base", "temp", "placeholder",
    };

    // Event / Ancient retexture mods — Neow-equivalent NPC portraits under
    // `images/ancients/{name}_placeholder.png` and event scene backgrounds under
    // `images/events/{event_name}.png`. Mods using namespaced asset paths
    // (`MyMod/images/ancients/...`) plus a DLL-side path redirect (AncientRetexture pattern)
    // still match because we do unanchored substring search. Distinct from card/spine
    // domains: a mod that ONLY touches these paths would otherwise fall through every
    // classification branch and be invisible to the manager.
    public static readonly Regex EventArtRegex = new(
        @"images/ancients/|images/events/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // Per-pck scan result. Hit counts are exposed for diagnostic logging so users can see at a
    // glance which signal drove a mod's classification (or didn't).
    public sealed record PathScan(
        IReadOnlySet<string> Characters,
        int CharacterSpineHits,
        int CardArtHits,
        int CardPortraitsHits,
        int CustomCharacterIndicatorHits,
        int CharSelectAssetHits,
        int EventArtHits,
        IReadOnlySet<string> CharSelectIds,
        int SharedCardPathHits = 0,
        int ModPrivateCardPathHits = 0)
    {
        public bool HasCardArt => CardArtHits > 0;
        public bool HasCardPortraits => CardPortraitsHits > 0;
        public bool IsCardMod => HasCardArt || HasCardPortraits;
        public bool IsCustomCharacterMod => CustomCharacterIndicatorHits > 0;
        public bool HasCharSelectAsset => CharSelectAssetHits > 0;
        public bool IsEventArtMod => EventArtHits > 0;

        // Mixed means the pack writes some art to a shared path and some to its own namespace, so
        // priority moves part of its art and not the rest. Reported honestly rather than rounded to
        // one of the two — a half-working slider is exactly the case users report as "sometimes it
        // does nothing". Falls back to SharedPath when a card mod matched only via CardArtBaseRegex
        // with no parseable root, which is the pre-existing last-mount-wins assumption.
        public CardOverrideMode CardOverrideMode =>
            !IsCardMod ? CardOverrideMode.None
            : SharedCardPathHits > 0 && ModPrivateCardPathHits > 0 ? CardOverrideMode.Mixed
            : ModPrivateCardPathHits > 0 ? CardOverrideMode.ModPrivate
            : CardOverrideMode.SharedPath;

        // Compact one-line summary for boot log — only non-zero domains appear, so the line
        // stays short for mods that only touch one or two categories.
        public string ToLabel()
        {
            var parts = new List<string>(6);
            if (CharacterSpineHits > 0) parts.Add($"spine:{CharacterSpineHits}");
            if (CharSelectAssetHits > 0) parts.Add($"char_select:{CharSelectAssetHits}");
            if (CardArtHits > 0) parts.Add($"card_art:{CardArtHits}");
            if (CardPortraitsHits > 0) parts.Add($"card_portraits:{CardPortraitsHits}");
            // Surfaced in the boot log so a "priority does nothing" report can be triaged from the
            // user's log alone, without needing their pcks.
            if (IsCardMod) parts.Add($"card_mode:{CardOverrideMode}");
            if (EventArtHits > 0) parts.Add($"event_art:{EventArtHits}");
            if (CustomCharacterIndicatorHits > 0) parts.Add($"custom_char:{CustomCharacterIndicatorHits}");
            return parts.Count == 0 ? "(no recognized domain)" : string.Join(" ", parts);
        }
    }

    // Walks `paths` once and returns hit counts per domain plus character ids extracted from
    // the spine domain. Caller decides how to use the result (SkinModScanner classifies;
    // UnclassifiedModInventory just counts).
    //
    // `ownNamespace` is the pck's own mod id. Pass it to get a meaningful CardOverrideMode —
    // without it every card path reads as shared, since a root can only be recognised as private by
    // comparing it against the id that owns it. Optional so counting-only callers can skip it.
    public static PathScan ScanPaths(IEnumerable<string> paths, string? ownNamespace = null)
    {
        var chars = new HashSet<string>();
        var charSelectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int spineHits = 0, cardArtHits = 0, cardPortraitsHits = 0, customCharHits = 0, charSelectHits = 0, eventArtHits = 0;
        int sharedCardHits = 0, privateCardHits = 0;

        foreach (var p in paths)
        {
            foreach (Match rm in CardPathRootRegex.Matches(p))
            {
                if (IsPrivateRoot(rm.Groups[1].Value, ownNamespace)) privateCardHits++;
                else sharedCardHits++;
            }
            var m = CharacterSpineRegex.Match(p);
            if (m.Success)
            {
                chars.Add(m.Groups[1].Value.ToLowerInvariant());
                spineHits++;
            }
            if (CardArtBaseRegex.IsMatch(p)) cardArtHits++;
            if (CardPortraitsRegex.IsMatch(p)) cardPortraitsHits++;
            if (CustomCharacterIndicatorRegex.IsMatch(p)) customCharHits++;
            if (CharSelectAssetRegex.IsMatch(p)) charSelectHits++;
            if (EventArtRegex.IsMatch(p)) eventArtHits++;
            foreach (var rx in CharSelectIdRegexes)
            {
                var cm = rx.Match(p);
                if (!cm.Success) continue;
                var id = cm.Groups[1].Value.ToLowerInvariant();
                if (!CharSelectIdStopwords.Contains(id)) charSelectIds.Add(id);
            }
        }

        return new PathScan(chars, spineHits, cardArtHits, cardPortraitsHits, customCharHits, charSelectHits, eventArtHits, charSelectIds, sharedCardHits, privateCardHits);
    }
}
