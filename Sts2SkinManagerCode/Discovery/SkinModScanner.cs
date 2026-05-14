using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sts2SkinManager.Discovery;

public enum SkinModKind { Character, Cards }

public record DetectedSkinMod(
    string ModId,
    string ModFolder,
    string PckPath,
    SkinModKind Kind,
    IReadOnlyList<string> Characters,
    string? PreviewPath,
    bool IsMixed = false
);

public static class SkinModScanner
{
    private static readonly Regex CharacterPathRegex = new(
        @"animations/characters/([a-z_][a-z0-9_]*)/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex CardArtBaseOverrideRegex = new(
        @"card_art/MegaCrit\.Sts2\.Core\.Models\.Cards\.",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex CardPortraitsNamespaceRegex = new(
        @"/card_portraits/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // Reads `animations/characters/{char}/` from the base game pck so we can tell
    // "skin for an existing character" apart from "mod that adds a brand-new character".
    // Touching only the raw bytes here keeps us clear of ModelDb.AllCharacters caching/Harmony timing.
    public static HashSet<string> ScanBaseCharacters(string gameDir)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var basePck = Path.Combine(gameDir, "SlayTheSpire2.pck");
        if (!File.Exists(basePck)) return result;
        foreach (var p in PckPathReader.ReadAsciiRuns(basePck))
        {
            var m = CharacterPathRegex.Match(p);
            if (m.Success) result.Add(m.Groups[1].Value.ToLowerInvariant());
        }
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

    public static List<DetectedSkinMod> Scan(string modsDir, IReadOnlySet<string> baseCharacters, out List<string> skippedCustomCharacterMods)
    {
        var result = new List<DetectedSkinMod>();
        skippedCustomCharacterMods = new List<string>();
        if (!Directory.Exists(modsDir)) return result;

        // Recursive walk so users can group pcks under category folders (e.g. mods/캐릭터/, mods/아트워크/).
        // Each pck's immediate parent directory is treated as its modDir for preview lookup.
        var seenModIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pck in EnumeratePckFilesRecursive(modsDir))
        {
            var modDir = Path.GetDirectoryName(pck)!;
            var previewPath = FindPreview(modDir);

            var paths = PckPathReader.ReadAsciiRuns(pck);
            var chars = new HashSet<string>();
            var isCardMod = false;
            foreach (var p in paths)
            {
                var m = CharacterPathRegex.Match(p);
                if (m.Success) chars.Add(m.Groups[1].Value.ToLowerInvariant());
                if (!isCardMod && (CardArtBaseOverrideRegex.IsMatch(p) || CardPortraitsNamespaceRegex.IsMatch(p)))
                {
                    isCardMod = true;
                }
            }

            var pckId = Path.GetFileNameWithoutExtension(pck);
            // ModId is the pck filename — same name in different subfolders would collide silently.
            // Keep the first occurrence, log + skip duplicates so the user can rename or dedupe.
            if (seenModIds.TryGetValue(pckId, out var firstPath))
            {
                MainFile.Logger.Warn($"duplicate pck name '{pckId}' at {pck} (first seen at {firstPath}) — skipping duplicate.");
                continue;
            }
            seenModIds[pckId] = pck;

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
                    skippedCustomCharacterMods.Add($"{pckId} → [{string.Join(",", chars)}]");
                    continue;
                }
                // A mod that ships BOTH a base-character spine AND card_art/card_portraits is a "mixed"
                // mod (e.g. AncientWaifus). It registers as a Character variant (selectable from the
                // dropdown as main spine) but is also flagged IsMixed so the mixed-addon panel can
                // toggle it independently as a non-main mount.
                var isMixed = isCardMod;
                result.Add(new DetectedSkinMod(pckId, modDir, pck, SkinModKind.Character, baseHits.ToList(), previewPath, isMixed));
            }
            else if (isCardMod)
            {
                result.Add(new DetectedSkinMod(pckId, modDir, pck, SkinModKind.Cards, new List<string>(), previewPath));
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
