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

    // Any reference to the base-game card class namespace anywhere in the pck — either as an
    // asset path (`card_art/MegaCrit.Sts2.Core.Models.Cards.X_card_art.png` — RegentCardsAnimeRework,
    // `assets/images/cards/MegaCrit.Sts2.Core.Models.Cards.X_portrait.png` — TheDefectCardArtMod)
    // or as a `cardId` reference in an embedded scene/JSON. The namespace itself is specific enough
    // to base-game cards that any pck mentioning it is overriding card visuals.
    public static readonly Regex CardArtBaseRegex = new(
        @"MegaCrit\.Sts2\.Core\.Models\.Cards\.",
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
        int EventArtHits)
    {
        public bool HasCardArt => CardArtHits > 0;
        public bool HasCardPortraits => CardPortraitsHits > 0;
        public bool IsCardMod => HasCardArt || HasCardPortraits;
        public bool IsCustomCharacterMod => CustomCharacterIndicatorHits > 0;
        public bool HasCharSelectAsset => CharSelectAssetHits > 0;
        public bool IsEventArtMod => EventArtHits > 0;

        // Compact one-line summary for boot log — only non-zero domains appear, so the line
        // stays short for mods that only touch one or two categories.
        public string ToLabel()
        {
            var parts = new List<string>(6);
            if (CharacterSpineHits > 0) parts.Add($"spine:{CharacterSpineHits}");
            if (CharSelectAssetHits > 0) parts.Add($"char_select:{CharSelectAssetHits}");
            if (CardArtHits > 0) parts.Add($"card_art:{CardArtHits}");
            if (CardPortraitsHits > 0) parts.Add($"card_portraits:{CardPortraitsHits}");
            if (EventArtHits > 0) parts.Add($"event_art:{EventArtHits}");
            if (CustomCharacterIndicatorHits > 0) parts.Add($"custom_char:{CustomCharacterIndicatorHits}");
            return parts.Count == 0 ? "(no recognized domain)" : string.Join(" ", parts);
        }
    }

    // Walks `paths` once and returns hit counts per domain plus character ids extracted from
    // the spine domain. Caller decides how to use the result (SkinModScanner classifies;
    // UnclassifiedModInventory just counts).
    public static PathScan ScanPaths(IEnumerable<string> paths)
    {
        var chars = new HashSet<string>();
        int spineHits = 0, cardArtHits = 0, cardPortraitsHits = 0, customCharHits = 0, charSelectHits = 0, eventArtHits = 0;

        foreach (var p in paths)
        {
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
        }

        return new PathScan(chars, spineHits, cardArtHits, cardPortraitsHits, customCharHits, charSelectHits, eventArtHits);
    }
}
