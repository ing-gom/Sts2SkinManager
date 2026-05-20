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
                    var name = dep?.ToString();
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
