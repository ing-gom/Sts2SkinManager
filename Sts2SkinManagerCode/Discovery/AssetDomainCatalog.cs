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

    // Every Godot resource path is `res://{root}/{rest}`. Capture both so the root can be judged
    // against the pck's own id — see AssetOverrideMode.
    private static readonly Regex ResPathRegex = new(
        @"res://([^/""'\s]+)/([^""'\s]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // Card-art asset under a resource path: `.../card_portraits/`, `.../card_art/`,
    // `.../{something}card{something}.sprites/` (base `card_atlas.sprites`, ATA `lance_cards.sprites`).
    private static readonly Regex CardAssetMarkerRegex = new(
        @"(?:^|/)card_portraits/|(?:^|/)card_art/|(?:^|/)[^/]*card[^/]*\.sprites/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // Body asset: the combat spine (`animations/characters/{char}/`) and the scene that positions it
    // (`scenes/creature_visuals/{char}.tscn`). Reverting a body needs the scene, not just the spine
    // leaves — node transforms live there.
    private static readonly Regex SpineAssetMarkerRegex = new(
        @"(?:^|/)animations/characters/|(?:^|/)creature_visuals/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // How a mod gets an asset in front of the player — and therefore whether reordering it in a
    // priority list does anything. Computed per domain (cards, body) because mount order is a
    // per-path mechanism, not a per-mod one.
    //
    //   SharedPath  `res://images/packed/card_portraits/ironclad/break.png` (base-owned),
    //               `res://animations/characters/ironclad/` (base-owned spine), or
    //               `res://generated/assets/card_art/...` (shared tooling convention).
    //               Two mods overriding the same asset write the SAME resource path, so they
    //               genuinely collide and Godot's last-mount-wins picks the winner. Mount order —
    //               which is exactly what the priority lists control — decides. Priority WORKS.
    //
    //   ModPrivate  `res://ATA_IronClad/images/card_portraits/ironclad/break.png`,
    //               `res://ATA_IronClad/animations/characters/ironclad/`.
    //               Every mod's asset sits at its own distinct path, so nothing collides and there
    //               is nothing for mount order to arbitrate. A Harmony DLL picks what the game asks
    //               for (RitsuLib resolves competing mods through its own registration-order
    //               registry; ATA via its own patch). Mount order cannot reach that decision, so
    //               priority is INERT — reordering these rows changes nothing.
    //
    // Verified against every installed pck. Cards: shared for AncientWaifus/Ryoshu/raye (`images`)
    // and AliceDefectCard/RegentCardsAnimeRework (`generated`); private for ATA_IronClad/ATA_Silent/
    // FGOCore/ArtoriaCaster (own id). Body: shared for AncientWaifus/Ryoshu/raye (`animations`);
    // private for ATA_IronClad/ATA_Silent (own id, incl. `creature_visuals`) — so for ATA the mixed
    // list's order governs neither the body nor the cards.
    public enum AssetOverrideMode { None, SharedPath, ModPrivate, Mixed }

    // A mod cannot collide with anything when it files an asset under its own mod id — that root is
    // unique to it by construction. Any other root (`images`, `animations`, `generated`, `.godot`)
    // is one other mods write to as well, so treat it as shared. Comparing against the pck's own id
    // keeps this framework-agnostic: it reads where the mod puts its files, not which library it links.
    private static bool IsPrivateRoot(string root, string? ownNamespace) =>
        ownNamespace != null && string.Equals(root, ownNamespace, StringComparison.OrdinalIgnoreCase);

    // The mixed list's order is a single pck-mount lever governing every domain the mod ships, so a
    // row's honest label is the combination of its domains: all-private = the order does nothing at
    // all (ATA namespaces both its body and its cards), all-shared = it works (raye, AncientWaifus),
    // anything else = it moves part of the mod and not the rest.
    public static AssetOverrideMode Combine(AssetOverrideMode a, AssetOverrideMode b)
    {
        if (a == AssetOverrideMode.None) return b;
        if (b == AssetOverrideMode.None) return a;
        return a == b ? a : AssetOverrideMode.Mixed;
    }

    private static AssetOverrideMode ResolveMode(bool present, int sharedHits, int privateHits) =>
        !present ? AssetOverrideMode.None
        : sharedHits > 0 && privateHits > 0 ? AssetOverrideMode.Mixed
        : privateHits > 0 ? AssetOverrideMode.ModPrivate
        : AssetOverrideMode.SharedPath;

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
        int ModPrivateCardPathHits = 0,
        int SharedSpinePathHits = 0,
        int ModPrivateSpinePathHits = 0)
    {
        public bool HasCardArt => CardArtHits > 0;
        public bool HasCardPortraits => CardPortraitsHits > 0;
        public bool IsCardMod => HasCardArt || HasCardPortraits;
        public bool IsCustomCharacterMod => CustomCharacterIndicatorHits > 0;
        public bool HasCharSelectAsset => CharSelectAssetHits > 0;
        public bool IsEventArtMod => EventArtHits > 0;

        // Mixed means the mod writes some assets to a shared path and some to its own namespace, so
        // priority moves part of them and not the rest. Reported honestly rather than rounded to one
        // of the two — a half-working slider is exactly the case users report as "sometimes it does
        // nothing". Both fall back to SharedPath when the domain was detected but no root parsed,
        // which is the pre-existing last-mount-wins assumption.
        public AssetOverrideMode CardOverrideMode => ResolveMode(IsCardMod, SharedCardPathHits, ModPrivateCardPathHits);
        public AssetOverrideMode SpineOverrideMode => ResolveMode(CharacterSpineHits > 0, SharedSpinePathHits, ModPrivateSpinePathHits);

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
            if (CharacterSpineHits > 0) parts.Add($"body_mode:{SpineOverrideMode}");
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
        int sharedCardHits = 0, privateCardHits = 0, sharedSpineHits = 0, privateSpineHits = 0;

        foreach (var p in paths)
        {
            // One pass per res:// occurrence: judge the root once, then attribute it to whichever
            // domains the path belongs to. A single line can carry several paths (a .tres lists its
            // ext_resources inline), hence Matches rather than Match.
            foreach (Match rm in ResPathRegex.Matches(p))
            {
                var full = rm.Groups[1].Value + "/" + rm.Groups[2].Value;
                var isPrivate = IsPrivateRoot(rm.Groups[1].Value, ownNamespace);
                if (CardAssetMarkerRegex.IsMatch(full))
                {
                    if (isPrivate) privateCardHits++; else sharedCardHits++;
                }
                if (SpineAssetMarkerRegex.IsMatch(full))
                {
                    if (isPrivate) privateSpineHits++; else sharedSpineHits++;
                }
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

        return new PathScan(chars, spineHits, cardArtHits, cardPortraitsHits, customCharHits, charSelectHits, eventArtHits, charSelectIds, sharedCardHits, privateCardHits, sharedSpineHits, privateSpineHits);
    }
}
