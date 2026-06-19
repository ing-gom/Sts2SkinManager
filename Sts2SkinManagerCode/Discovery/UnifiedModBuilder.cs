using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Sts2SkinManager.Config;

namespace Sts2SkinManager.Discovery;

// Category as displayed in the "All Mods" panel. Spans every state SkinManager can be in for
// a given mod — pck-detected categories, user overrides, and "we haven't decided".
public enum UnifiedModCategory
{
    CharacterSkin,     // SkinModKind.Character variant (auto-detected or user-forced via _dll_skin_assignments)
    CardSkin,          // SkinModKind.Cards (auto-detected from pck card_art / card_portraits)
    Mixed,             // SkinModKind.Character with IsMixed=true (spine + card art in one pck)
    EventArt,          // SkinModKind.EventArt (auto-detected from images/ancients/ or images/events/)
    NotManaged,        // User opted out via _dll_skin_skipped; SkinManager leaves it alone
    Pending,           // DLL+pck mod that SkinManager noticed but the user hasn't decided about
}

public record UnifiedModItem(
    string ModId,
    string ManifestName,
    string ManifestDescription,
    UnifiedModCategory Category,
    string? Character,              // populated for CharacterSkin / Mixed (assigned base char) and Pending (auto-suggested hint)
    bool DefinesContentEntities,    // signal A — populated for any item with a locatable DLL
    string DomainsLabel             // diagnostic string like "spine:42 card_art:10" from AssetDomainCatalog
);

// Single source of truth for the "Other Mods" panel — DLL-shipping mods that aren't already
// surfaced in the Character dropdown, Card tab, or Mixed tab. Contents:
//   - Pck-detected event-art mods (AncientRetexture pattern) — no dedicated panel yet, so
//     they surface here purely as a toggle surface (mod_list is_enabled)
//   - User skip list (_dll_skin_skipped — content mods like Act4FinalAscent)
//   - Pending DLL+pck mods that SkinManager noticed but haven't been routed yet
//
// Excludes:
//   - Pck-detected character variants (animeDefect, Hcxmmx_King_Skin) — those live in the
//     character dropdown; managing them from a separate tab would just duplicate the dropdown
//   - SkinModKind.Cards results — those have their own Card Skins tab
//   - IsMixed results — those have their own Mixed tab
//   - SkinManager itself, BaseLib, Sts2* sister mods
//   - Custom-character mods (auto-mounted by STS2; toggling them as "skin for X" would
//     DLL-block the new character)
public static class UnifiedModBuilder
{
    public static List<UnifiedModItem> Build(
        IReadOnlyList<string> modsDirs,
        List<DetectedSkinMod> scannerDetected,
        SkinChoicesConfig choices,
        IReadOnlyCollection<string> customCharacterModIds,
        IReadOnlySet<string> baseCharacters)
    {
        var result = new List<UnifiedModItem>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var customChars = new HashSet<string>(customCharacterModIds, System.StringComparer.OrdinalIgnoreCase);

        // (0) Event-art mods classified by the scanner. Unlike character variants (dropdown) and
        //     card packs (Card Skins tab), event-art mods have no dedicated panel yet — surface
        //     them in the All Mods list so the user can toggle is_enabled. Their pck mount stays
        //     with STS2's default loader; this entry is purely a UI/toggle surface.
        foreach (var d in scannerDetected)
        {
            if (d.Kind != SkinModKind.EventArt) continue;
            if (seen.Contains(d.ModId)) continue;
            seen.Add(d.ModId);

            var manifestPath = TryFindManifest(d.ModFolder);
            var (name, desc) = manifestPath != null ? ReadManifest(manifestPath) : ("", "");

            var dllPath = HarmonyPatchInspector.FindModDllPath(modsDirs, d.ModId);
            var definesEntities = dllPath != null && EntityDefinitionDetector.InspectFile(d.ModId, dllPath) != null;

            result.Add(new UnifiedModItem(
                ModId: d.ModId,
                ManifestName: name,
                ManifestDescription: desc,
                Category: UnifiedModCategory.EventArt,
                Character: null,
                DefinesContentEntities: definesEntities,
                DomainsLabel: d.DomainsLabel ?? ""));
        }

        // Track every modId that the scanner classified — these are already represented in the
        // Character dropdown, Card tab, Mixed tab, or (for EventArt above) the All Mods list,
        // and must not appear again in the Pending pass.
        foreach (var d in scannerDetected)
        {
            seen.Add(d.ModId);
        }

        // (1) _dll_skin_skipped entries — mods the user opted out, including auto-rescued
        //     content mods. Surfaced so the user can change their mind.
        foreach (var modId in choices.DllSkinSkipped)
        {
            if (seen.Contains(modId)) continue;
            if (IsKnownNonSkin(modId)) continue;
            if (customChars.Contains(modId)) continue;
            seen.Add(modId);

            var modFolder = LocateModFolder(modsDirs, modId);
            var manifestPath = modFolder != null ? TryFindManifest(modFolder) : null;
            var (name, desc) = manifestPath != null ? ReadManifest(manifestPath) : ("", "");

            var dllPath = HarmonyPatchInspector.FindModDllPath(modsDirs, modId);
            var definesEntities = dllPath != null && EntityDefinitionDetector.InspectFile(modId, dllPath) != null;

            result.Add(new UnifiedModItem(
                ModId: modId,
                ManifestName: name,
                ManifestDescription: desc,
                Category: UnifiedModCategory.NotManaged,
                Character: null,
                DefinesContentEntities: definesEntities,
                DomainsLabel: ""));
        }

        // (2) Pending DLL mods — DLL+pck mods not detected by pck scan, not on the skip list,
        //     and not assigned. Surface so the user can route them. (HarmonyPatchInspector
        //     suspect filtering happens elsewhere; this pass uses the cheaper pck+dll presence
        //     check as the user-facing trigger.)
        foreach (var modFolder in EnumerateModFolders(modsDirs))
        {
            var dllPath = FindFirstFileByExtension(modFolder, ".dll");
            if (dllPath == null) continue;
            var modId = Path.GetFileNameWithoutExtension(dllPath);
            if (string.IsNullOrEmpty(modId)) continue;
            if (seen.Contains(modId)) continue;
            if (string.Equals(modId, MainFile.ModId, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (IsKnownNonSkin(modId)) continue;
            if (customChars.Contains(modId)) continue;
            seen.Add(modId);

            var manifestPath = TryFindManifest(modFolder);
            var (name, desc) = manifestPath != null ? ReadManifest(manifestPath) : ("", "");

            var definesEntities = EntityDefinitionDetector.InspectFile(modId, dllPath) != null;

            string? suggested = null;
            try { suggested = CharacterIdSuggester.Suggest(modFolder, baseCharacters); }
            catch { }

            result.Add(new UnifiedModItem(
                ModId: modId,
                ManifestName: name,
                ManifestDescription: desc,
                Category: UnifiedModCategory.Pending,
                Character: suggested,
                DefinesContentEntities: definesEntities,
                DomainsLabel: ""));
        }

        return result
            .OrderBy(r => (int)r.Category)
            .ThenBy(r => r.ModId, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Same filter list as HarmonyPatchInspector.IsKnownNonSkin so the All Mods panel doesn't
    // surface utility/sister mods. Match is case-insensitive. Public so the Applied summary can
    // reuse it (e.g. to keep the BaseLib framework out of the custom-characters list — BaseLib
    // trips the custom-character signal because it *defines* CustomCharacterModel).
    public static bool IsKnownNonSkin(string modId)
    {
        if (string.Equals(modId, "BaseLib", System.StringComparison.OrdinalIgnoreCase)) return true;
        if (modId.StartsWith("Sts2", System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? TryFindManifest(string modFolder)
    {
        var canonical = Path.Combine(modFolder, "mod_manifest.json");
        if (File.Exists(canonical)) return canonical;
        try
        {
            foreach (var json in Directory.EnumerateFiles(modFolder, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var text = File.ReadAllText(json);
                    if (JsonNode.Parse(text) is JsonObject root && root.ContainsKey("id")) return json;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static (string name, string description) ReadManifest(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            if (JsonNode.Parse(text) is JsonObject root)
            {
                return (root["name"]?.ToString() ?? "", root["description"]?.ToString() ?? "");
            }
        }
        catch { }
        return ("", "");
    }

    // Searches roots in order; a local mods/ folder is returned before a Workshop duplicate.
    private static string? LocateModFolder(IReadOnlyList<string> modsDirs, string modId)
    {
        foreach (var modsDir in modsDirs)
        {
            var hit = LocateModFolder(modsDir, modId);
            if (hit != null) return hit;
        }
        return null;
    }

    private static string? LocateModFolder(string modsDir, string modId)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(modsDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(".") || string.Equals(name, "__MACOSX", System.StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(Path.Combine(dir, modId + ".pck")) || File.Exists(Path.Combine(dir, modId + ".dll")))
                    return dir;
            }
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> EnumerateModFolders(IReadOnlyList<string> modsDirs)
    {
        foreach (var modsDir in modsDirs)
            foreach (var folder in EnumerateModFolders(modsDir))
                yield return folder;
    }

    private static IEnumerable<string> EnumerateModFolders(string modsDir)
    {
        var stack = new Stack<string>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(modsDir))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith(".") || string.Equals(name, "__MACOSX", System.StringComparison.OrdinalIgnoreCase)) continue;
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
            catch { subs = System.Array.Empty<string>(); }
            foreach (var s in subs)
            {
                var name = Path.GetFileName(s);
                if (name.StartsWith(".") || string.Equals(name, "__MACOSX", System.StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(s);
            }
        }
    }

    private static string? FindFirstFileByExtension(string dir, string extension)
    {
        try { return Directory.EnumerateFiles(dir, "*" + extension, SearchOption.TopDirectoryOnly).FirstOrDefault(); }
        catch { return null; }
    }
}
