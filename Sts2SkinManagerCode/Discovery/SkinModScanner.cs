using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sts2SkinManager.Discovery;

public enum SkinModKind { Character, Cards, EventArt }

public record DetectedSkinMod(
    string ModId,
    string ModFolder,
    string PckPath,
    SkinModKind Kind,
    IReadOnlyList<string> Characters,
    string? PreviewPath,
    bool IsMixed = false,
    string? DomainsLabel = null
);

public static class SkinModScanner
{
    // Reads `animations/characters/{char}/` from the base game pck so we can tell
    // "skin for an existing character" apart from "mod that adds a brand-new character".
    // Touching only the raw bytes here keeps us clear of ModelDb.AllCharacters caching/Harmony timing.
    public static HashSet<string> ScanBaseCharacters(string gameDir)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var basePck = Path.Combine(gameDir, "SlayTheSpire2.pck");
        if (!File.Exists(basePck)) return result;
        var scan = AssetDomainCatalog.ScanPaths(PckPathReader.ReadAssetPaths(basePck));
        foreach (var c in scan.Characters) result.Add(c);
        return result;
    }

    // Probed in order; first hit wins.
    private static readonly string[] PreviewCandidateNames =
    {
        "preview.png", "preview.jpg", "preview.jpeg", "preview.webp",
        "thumbnail.png", "thumbnail.jpg", "thumbnail.jpeg", "thumbnail.webp",
    };

    // Folder names skipped during recursive scan: VCS metadata, macOS archive cruft, hidden dirs.
    private static bool ShouldSkipDir(string dirName) =>
        dirName.StartsWith(".") || string.Equals(dirName, "__MACOSX", StringComparison.OrdinalIgnoreCase);

    public record SkippedCustomCharacterMod(string ModId, IReadOnlyList<string> CharacterIds, string? DomainsLabel = null);

    public static List<DetectedSkinMod> Scan(
        string modsDir,
        IReadOnlySet<string> baseCharacters,
        out List<SkippedCustomCharacterMod> skippedCustomCharacterMods,
        IReadOnlyDictionary<string, string>? dllSkinAssignments = null,
        IReadOnlyCollection<string>? dllSkinSkipped = null)
    {
        var result = new List<DetectedSkinMod>();
        skippedCustomCharacterMods = new List<SkippedCustomCharacterMod>();
        if (!Directory.Exists(modsDir)) return result;

        var skipSet = dllSkinSkipped == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(dllSkinSkipped, StringComparer.OrdinalIgnoreCase);

        // Recursive walk so users can group pcks under category folders (e.g. mods/캐릭터/, mods/아트워크/).
        // Each pck's immediate parent directory is treated as its modDir for preview lookup.
        var seenModIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pck in EnumeratePckFilesRecursive(modsDir))
        {
            var modDir = Path.GetDirectoryName(pck)!;
            var previewPath = FindPreview(modDir);

            var scan = AssetDomainCatalog.ScanPaths(PckPathReader.ReadAsciiRuns(pck));
            var chars = scan.Characters;
            var isCardMod = scan.IsCardMod;
            var domainsLabel = scan.ToLabel();

            var pckId = Path.GetFileNameWithoutExtension(pck);
            // ModId is the pck filename — same name in different subfolders would collide silently.
            // Keep the first occurrence, log + skip duplicates so the user can rename or dedupe.
            if (seenModIds.TryGetValue(pckId, out var firstPath))
            {
                MainFile.Logger.Warn($"duplicate pck name '{pckId}' at {pck} (first seen at {firstPath}) — skipping duplicate.");
                continue;
            }
            seenModIds[pckId] = pck;

            // User-explicit skip: leave the mod alone entirely. The pck still mounts normally
            // through STS2's default mod loader, but Skin Manager won't display, classify, or
            // DLL-block it. Used for content mods (Act4FinalAscent), false-positive auto-
            // detections, and any mod the user opted out via the All Mods panel.
            if (skipSet.Contains(pckId))
            {
                MainFile.Logger.Info($"  [user-skip] {pckId} (in _dll_skin_skipped) — leaving auto-mount intact.");
                continue;
            }

            // DLL-driven character skin assignment overrides path-based detection. Originally
            // intended only for mods whose pck has no standard skin paths (Hcxmmx_King_Skin),
            // but the user can also force any mod into Character classification via the All Mods
            // panel — e.g. mark a card-art mod as "Skin for defect" so its DLL gets blocked
            // when another defect skin is active.
            if (dllSkinAssignments != null && dllSkinAssignments.TryGetValue(pckId, out var assignedChar))
            {
                // Auto-heal: if the pck adds a brand-new character (non-base character paths)
                // OR the mod declares itself as a BaseLib custom-character via manifest dependency
                // / DLL CustomCharacterModel reference, honoring the assignment would DLL-block
                // the custom character whenever the user picks "default" for the base. Ignore
                // and fall through to the empty-chars branch below.
                var hasNonBaseCharPath = baseCharacters.Count > 0 && chars.Any(c => !baseCharacters.Contains(c));
                var declaresCustomCharFramework = chars.Count == 0
                    && CustomCharacterFrameworkDetector.IsCustomCharacterMod(modDir, pckId);
                if (hasNonBaseCharPath || declaresCustomCharFramework)
                {
                    var reason = hasNonBaseCharPath
                        ? $"pck adds non-base character [{string.Join(",", chars)}]"
                        : "manifest declares BaseLib dependency or DLL references CustomCharacterModel";
                    MainFile.Logger.Warn($"dll skin assignment '{pckId}' → '{assignedChar}' contradicts mod signature ({reason}). Ignoring assignment; treating as custom-character mod. Remove the entry from _dll_skin_assignments in skin_choices.json to silence this warning.");
                }
                else if (baseCharacters.Count == 0 || baseCharacters.Contains(assignedChar))
                {
                    // Assignment wins over pck classification. If the pck happens to also carry
                    // card_art (TheDefectCardArt forced as character skin scenario), preserve the
                    // mixed flag so the user still sees it in the mixed panel for visibility —
                    // but the primary classification is Character, with the assigned base char.
                    var isMixed = isCardMod;
                    result.Add(new DetectedSkinMod(pckId, modDir, pck, SkinModKind.Character,
                        new List<string> { assignedChar.ToLowerInvariant() }, previewPath, IsMixed: isMixed, DomainsLabel: domainsLabel));
                    continue;
                }
                else
                {
                    MainFile.Logger.Warn($"dll skin assignment '{pckId}' → '{assignedChar}' references unknown base character; ignoring.");
                }
            }

            if (chars.Count > 0)
            {
                // A mod that targets ONLY characters not in the base roster is adding a brand-new
                // character, not skinning an existing one. Skip so its pck stays auto-mountable —
                // otherwise our LoadResourcePack intercept would strand the character mod.
                // Skip only when base whitelist is non-empty (empty = base pck couldn't be read; fall through).
                var baseHits = baseCharacters.Count == 0
                    ? chars
                    : chars.Where(c => baseCharacters.Contains(c)).ToHashSet();
                if (baseHits.Count == 0)
                {
                    skippedCustomCharacterMods.Add(new SkippedCustomCharacterMod(pckId, chars.ToList(), domainsLabel));
                    continue;
                }
                // A mod that ships BOTH a base-character spine AND card_art/card_portraits is a "mixed"
                // mod (e.g. AncientWaifus). It registers as a Character variant (selectable from the
                // dropdown as main spine) but is also flagged IsMixed so the mixed-addon panel can
                // toggle it independently as a non-main mount.
                var isMixed = isCardMod;
                result.Add(new DetectedSkinMod(pckId, modDir, pck, SkinModKind.Character, baseHits.ToList(), previewPath, isMixed, domainsLabel));
            }
            else if (scan.IsCustomCharacterMod || CustomCharacterFrameworkDetector.IsCustomCharacterMod(modDir, pckId))
            {
                // BaseLib-style custom-character mod that packs spine under a non-standard path
                // (no `animations/characters/{X}/`) but ships C# code under `Code/Character/`,
                // references `CustomCharacterModel`, or carries a `characters.json` registration
                // manifest. The Watcher STS1→STS2 port is the canonical case — its 184 portraits
                // under `Watcher/images/card_portraits/` would otherwise misclassify it as a
                // base-card portrait redirect (RegentFemPortraits-style) and surface it in the
                // card-skin panel, where the user could accidentally disable it.
                //
                // CustomCharacterFrameworkDetector covers the second failure mode: mods like
                // MzmChar (Wakaba Mutsumi) that pack assets under `res://{ModId}/characters/`
                // and ship no `Code/Character/` source inside the pck — so the PCK-path regex
                // misses them entirely. We catch those via the manifest's BaseLib dependency
                // declaration or a direct DLL reference to `CustomCharacterModel`. Without this,
                // CharacterIdSuggester scored MzmChar.dll's legitimate Ironclad-template
                // references as 34/0 dominance and auto-blocked the whole custom character
                // whenever another Ironclad skin was active.
                //
                // Skip from classification so the game's normal mod loader keeps it mounted.
                skippedCustomCharacterMods.Add(new SkippedCustomCharacterMod(pckId, new List<string>(), domainsLabel));
            }
            else if (isCardMod)
            {
                // A card mod that ALSO touches character-select assets (e.g. TheDefectCardArtMod
                // restyles `char_select_bg_defect.tscn` alongside its card portraits) is flagged
                // mixed for visibility — same kind=Cards mount routing (mod_list toggle with
                // priority), but surfaces in the [mixed] log section so the user knows the mod
                // may visually conflict with a character skin targeting the same base character.
                var isMixed = scan.HasCharSelectAsset;
                result.Add(new DetectedSkinMod(pckId, modDir, pck, SkinModKind.Cards, new List<string>(), previewPath, isMixed, domainsLabel));
            }
            else if (scan.IsEventArtMod)
            {
                // Event background / Ancient NPC retexture mod (AncientRetexture pattern).
                // No character spine, no card art, no custom-character framework — just event
                // scene backgrounds or Neow-equivalent portraits. Surfaced so the user can
                // toggle it from the All Mods panel; no character variant dropdown applies and
                // mount routing stays with STS2's default loader (DLL-driven asset redirect
                // handles the actual override).
                result.Add(new DetectedSkinMod(pckId, modDir, pck, SkinModKind.EventArt, new List<string>(), previewPath, IsMixed: false, DomainsLabel: domainsLabel));
            }
        }
        return result;
    }

    // Walks modsDir recursively, yielding *.pck files at any depth >= 1. Pcks sitting directly in
    // modsDir/ are intentionally ignored to preserve the "one folder per mod" convention — that's
    // also where the game itself looks for its own bundles, and we don't want to misclaim them.
    private static IEnumerable<string> EnumeratePckFilesRecursive(string modsDir)
    {
        var stack = new Stack<string>();
        foreach (var topDir in SafeEnumerateDirectories(modsDir))
        {
            if (ShouldSkipDir(Path.GetFileName(topDir))) continue;
            stack.Push(topDir);
        }
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var pck in SafeEnumerateFiles(dir, "*.pck")) yield return pck;
            foreach (var sub in SafeEnumerateDirectories(dir))
            {
                if (ShouldSkipDir(Path.GetFileName(sub))) continue;
                stack.Push(sub);
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
        catch { return Array.Empty<string>(); }
    }

    private static string? FindPreview(string modDir)
    {
        foreach (var name in PreviewCandidateNames)
        {
            var candidate = Path.Combine(modDir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
