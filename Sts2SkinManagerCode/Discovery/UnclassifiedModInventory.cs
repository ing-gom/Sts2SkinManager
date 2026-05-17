using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sts2SkinManager.Discovery;

public record UnclassifiedMod(
    string ModId,
    string ModFolder,
    bool HasDll,
    bool HasPck,
    int PckCharacterPathHits,
    int PckCardPathHits,
    string? SuggestedCharacter
);

// Forensic logger: every boot, walk mods/ and surface mods that look skin-shaped (DLL + pck)
// but were classified by NEITHER the pck-based scanner (no animations/characters paths, no
// card_art paths) NOR the Harmony inspector (didn't patch any of our spine whitelist types).
//
// These are mods the user may need to manually triage — either:
//   (a) a true DLL-driven character skin that uses a Harmony patch target we don't whitelist,
//   (b) a utility / gameplay mod that ships a pck for non-skin reasons (events, sfx, etc.), or
//   (c) a card / portrait / cosmetic mod whose pck uses a non-standard layout.
//
// We log every match plus the CharacterIdSuggester's best guess. User can then add an entry
// to _dll_skin_assignments manually (or _dll_skin_skipped to silence).
public static class UnclassifiedModInventory
{
    public static List<UnclassifiedMod> Build(
        string modsDir,
        IReadOnlySet<string> baseCharacters,
        IReadOnlyCollection<string> alreadyDetectedModIds,
        IReadOnlyCollection<string> harmonySuspectModIds,
        IReadOnlyCollection<string> assignedModIds,
        IReadOnlyCollection<string> skippedModIds,
        IReadOnlyCollection<string> customCharacterModIds)
    {
        var detected = new HashSet<string>(alreadyDetectedModIds, StringComparer.OrdinalIgnoreCase);
        var suspects = new HashSet<string>(harmonySuspectModIds, StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(assignedModIds, StringComparer.OrdinalIgnoreCase);
        var skipped = new HashSet<string>(skippedModIds, StringComparer.OrdinalIgnoreCase);
        // customCharacterMods = mods the pck scanner skipped because they add a brand-new
        // character (either `animations/characters/{non_base}/` or CustomCharacter indicator
        // strings). Their auto-mount is intentionally left intact; surfacing them again here
        // would be a duplicate report (e.g. BaseLib, Watcher).
        var customCharacters = new HashSet<string>(customCharacterModIds, StringComparer.OrdinalIgnoreCase);

        var result = new List<UnclassifiedMod>();
        if (!Directory.Exists(modsDir)) return result;

        foreach (var modFolder in EnumerateModFolders(modsDir))
        {
            var dllPath = FindFirstFileByExtension(modFolder, ".dll");
            var pckPath = FindFirstFileByExtension(modFolder, ".pck");
            if (dllPath == null && pckPath == null) continue;

            // Use the .pck filename as the canonical mod id (matches SkinModScanner's convention).
            // Falls back to the .dll filename when there's no pck (rare for skin-shaped mods).
            var primaryFile = pckPath ?? dllPath!;
            var modId = Path.GetFileNameWithoutExtension(primaryFile);

            if (detected.Contains(modId)) continue;
            if (assigned.Contains(modId)) continue;
            if (skipped.Contains(modId)) continue;
            if (customCharacters.Contains(modId)) continue;
            // If Harmony inspector already flagged it, it'll be handled by DllSkinDetectionService.
            // Avoid double-reporting.
            if (suspects.Contains(modId)) continue;

            int pckChars = 0, pckCards = 0;
            if (pckPath != null)
            {
                try
                {
                    var scan = AssetDomainCatalog.ScanPaths(PckPathReader.ReadAsciiRuns(pckPath));
                    pckChars = scan.CharacterSpineHits;
                    pckCards = scan.CardArtHits + scan.CardPortraitsHits;
                }
                catch { }
            }

            // Skin-shaped only — has both files. Pure-DLL utility mods aren't suspicious.
            // Pure-pck mods are already covered by SkinModScanner, so they shouldn't reach here
            // unless their pck is genuinely unrecognizable.
            if (dllPath == null || pckPath == null) continue;

            // If pck already had standard skin signals, SkinModScanner should have detected it
            // (and we'd have filtered above). Belt-and-suspenders: skip if so.
            if (pckChars > 0 || pckCards > 0) continue;

            string? suggested = null;
            try { suggested = CharacterIdSuggester.Suggest(modFolder, baseCharacters); }
            catch { }

            result.Add(new UnclassifiedMod(modId, modFolder, HasDll: true, HasPck: true, pckChars, pckCards, suggested));
        }
        return result;
    }

    private static IEnumerable<string> EnumerateModFolders(string modsDir)
    {
        var stack = new Stack<string>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(modsDir))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith(".") || string.Equals(name, "__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(d);
            }
        }
        catch { yield break; }

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            yield return dir;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); }
            catch { subs = Array.Empty<string>(); }
            foreach (var s in subs)
            {
                var name = Path.GetFileName(s);
                if (name.StartsWith(".") || string.Equals(name, "__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(s);
            }
        }
    }

    private static string? FindFirstFileByExtension(string dir, string extension)
    {
        try
        {
            var f = Directory.EnumerateFiles(dir, "*" + extension, SearchOption.TopDirectoryOnly).FirstOrDefault();
            return f;
        }
        catch { return null; }
    }
}
