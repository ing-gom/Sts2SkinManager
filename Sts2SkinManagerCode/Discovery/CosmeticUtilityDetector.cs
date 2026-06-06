using System;
using System.IO;
using System.Text;

namespace Sts2SkinManager.Discovery;

// Detects "global cosmetic utility" mods — DLLs that retexture the base game's content models
// (cards, relics, powers, potions) wholesale rather than skinning a single character.
//
// Why this exists: these mods trip the DLL-skin suspect heuristic because they Harmony-patch a
// whitelisted character-select type. The canonical case is CustomCardTextureLoaderSG, which patches
// NCharacterSelectButton.Init (and LockForAnimation / AnimateUnlock / …) to swap the select-screen
// button textures alongside its card/relic/power/potion texture loaders. HarmonyPatchInspector sees
// the NCharacterSelectButton patch and flags it as a character-skin suspect, yet the mod has zero
// per-character signal — CharacterIdSuggester returns null (its DLL/pck contain no character id),
// so it surfaces as an ambiguous suspect. If a stale _dll_skin_assignments entry exists for it
// (e.g. a hand-edited or pre-v0.12 "necrobinder"), the scanner would treat it as that character's
// skin and DLL-block it whenever a real skin for that character is active — the texture loader
// silently stops working.
//
// Signal B (companion to EntityBasedRescue's Signal A, "defines new content-entity subclasses"):
// the DLL references two or more of the base content-model types RelicModel / PowerModel /
// PotionModel. A genuine character skin only ever touches CharacterModel / spine / character-select
// nodes — it has no reason to reference relics, powers, or potions. Requiring >= 2 of the three
// makes a coincidental match effectively impossible while still firing on any real texture replacer.
public static class CosmeticUtilityDetector
{
    // Base-game content-model class names. Short PascalCase identifiers that appear in the DLL's
    // metadata whenever the mod does `[HarmonyPatch(typeof(RelicModel))]` etc. — a character skin
    // never references these.
    private static readonly string[] ContentModelTokens = { "RelicModel", "PowerModel", "PotionModel" };
    private const int MinDistinctTokens = 2;

    // True if the DLL at dllPath references >= MinDistinctTokens of the base content-model types,
    // i.e. it's a wholesale content/texture utility rather than a per-character skin.
    public static bool IsGlobalCosmeticMod(string dllPath)
    {
        if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath)) return false;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(dllPath); }
        catch { return false; }

        var distinct = 0;
        foreach (var token in ContentModelTokens)
        {
            // Type names live in the metadata #Strings heap (ASCII/UTF-8); AssemblyQualifiedName
            // string literals can also appear as UTF-16 in the user-string heap. Check both.
            if (ContainsSequence(bytes, Encoding.ASCII.GetBytes(token))
                || ContainsSequence(bytes, Encoding.Unicode.GetBytes(token)))
            {
                distinct++;
                if (distinct >= MinDistinctTokens) return true;
            }
        }
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
