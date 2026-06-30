using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Sts2SkinManager.Discovery;

// Detects custom-character mods that build on the BaseLib framework but pack their assets under
// a mod-namespaced directory layout (e.g. MzmChar.pck stores everything under
// `res://MzmChar/characters/...` instead of the STS2-standard `res://animations/characters/{id}/`).
//
// AssetDomainCatalog.CustomCharacterIndicatorRegex catches the standard layout by scanning PCK
// ASCII paths for `Code/Character/` / `CustomCharacterModel` / `characters.json`, but a mod that
// uses non-standard asset paths AND doesn't ship its own character.cs source inside the pck slips
// through every PCK-only signal. The MzmChar (Wakaba Mutsumi) mod fell into this gap and got
// auto-classified as an Ironclad skin because its DLL legitimately references the base Ironclad
// character data as a template — the byte-frequency suggester then assigned it to ironclad with
// score 34, which would DLL-block the entire custom character whenever another Ironclad skin was
// active.
//
// Two complementary signals here, both explicit declarations of the BaseLib framework:
//   1. Manifest `dependencies: ["BaseLib"]` — the canonical, author-declared signal. BaseLib is
//      the custom-character framework; only mods that add new characters depend on it.
//   2. DLL byte reference to `CustomCharacterModel` (ASCII or UTF-16) — the framework's base
//      class. A mod that inherits from this class is by definition adding a new character.
public static class CustomCharacterFrameworkDetector
{
    private const string BaseLibDependency = "BaseLib";
    private const string CustomCharacterModelToken = "CustomCharacterModel";

    // True if the mod folder shows any of the BaseLib custom-character framework signals.
    // Caller already determined this mod has no `animations/characters/{base}/` paths in its pck.
    public static bool IsCustomCharacterMod(string modDir, string modId)
    {
        if (DeclaresBaseLibDependency(modDir)) return true;
        if (DllReferencesCustomCharacterModel(modDir, modId)) return true;
        return false;
    }

    // Framework-agnostic "adds a brand-new character" signal: the mod ships a character-select
    // asset for an id that is NOT in the base roster. Skins reuse the base character's id; only a
    // new character mints a new char-select id. This catches RitsuLib-packed characters (HornetMod
    // → `hornet`) that carry none of the BaseLib signals above — no BaseLib dependency, no
    // CustomCharacterModel base class — and so would otherwise be byte-frequency mis-assigned to a
    // base character and DLL-blocked. `charSelectIds` comes from AssetDomainCatalog.ScanPaths.
    // Returns false when the base roster is empty (base pck unreadable) — we can't tell new from
    // base then, so we stay conservative and leave the mod to the existing signals.
    public static bool IntroducesNonBaseCharacter(IReadOnlySet<string> charSelectIds, IReadOnlySet<string> baseCharacters)
    {
        if (baseCharacters.Count == 0) return false;
        foreach (var id in charSelectIds)
            if (!baseCharacters.Contains(id)) return true;
        return false;
    }

    // Same signal as above but reads the mod's pck directly — for callers (EntityBasedRescue) that
    // only have the mod folder, not a pre-computed PathScan. Locates `{modId}.pck` in modDir.
    public static bool IntroducesNonBaseCharacterByPck(string modDir, string modId, IReadOnlySet<string> baseCharacters)
    {
        if (baseCharacters.Count == 0) return false;
        var pck = Path.Combine(modDir, modId + ".pck");
        if (!File.Exists(pck)) return false;
        try
        {
            var scan = AssetDomainCatalog.ScanPaths(PckPathReader.ReadAsciiRuns(pck));
            return IntroducesNonBaseCharacter(scan.CharSelectIds, baseCharacters);
        }
        catch { return false; }
    }

    private static bool DeclaresBaseLibDependency(string modDir)
    {
        if (!Directory.Exists(modDir)) return false;
        try
        {
            foreach (var json in Directory.EnumerateFiles(modDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                string text;
                try { text = File.ReadAllText(json); }
                catch { continue; }

                JsonNode? root;
                try { root = JsonNode.Parse(text); }
                catch { continue; }

                if (root is not JsonObject obj) continue;
                if (obj["dependencies"] is not JsonArray deps) continue;

                foreach (var dep in deps)
                {
                    // Manifests use two dependency shapes: a bare string ("BaseLib") or an object
                    // ({ "id": "BaseLib", "min_version": "..." }). HornetMod uses the object form for
                    // its RitsuLib dependency; reading dep.ToString() on an object yields the whole
                    // JSON, never matching — so resolve the id field explicitly when present.
                    var name = dep is JsonObject depObj ? depObj["id"]?.ToString() : dep?.ToString();
                    if (string.Equals(name, BaseLibDependency, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    // Scans the mod's primary DLL (matching the pck name) for the `CustomCharacterModel` token
    // in ASCII or UTF-16 LE. A single hit is enough — the token is specific to the BaseLib
    // framework's base class. We only scan the modId.dll to keep the work bounded; mods that
    // ship multiple DLLs but only reference the token from a sibling are vanishingly rare.
    private static bool DllReferencesCustomCharacterModel(string modDir, string modId)
    {
        var dllPath = Path.Combine(modDir, modId + ".dll");
        if (!File.Exists(dllPath)) return false;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(dllPath); }
        catch { return false; }

        if (ContainsSequence(bytes, Encoding.ASCII.GetBytes(CustomCharacterModelToken))) return true;
        if (ContainsSequence(bytes, Encoding.Unicode.GetBytes(CustomCharacterModelToken))) return true;
        return false;
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        var limit = haystack.Length - needle.Length;
        for (var i = 0; i <= limit; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}
