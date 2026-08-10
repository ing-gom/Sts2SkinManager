using System;
using System.Collections.Generic;
using System.IO;

namespace Sts2SkinManager.Discovery;

// "Which DLLs make up this mod?" — every *.dll under the mod's folder, not just `{modId}.dll`.
//
// Why this exists: a mod's root `{modId}.dll` is no longer guaranteed to be the mod. Multi-version
// packaging puts a thin *loader* stub at the root and the real implementation under `lib/<gameVersion>/`,
// with the loader picking a variant at boot and associating it with the mod:
//
//   mods/HextechRunes/
//     HextechRunes.dll            <- loader stub (HextechRunes.Loader.dll, renamed)
//     lib/0.107.1/HextechRunes.dll  <- the actual mod (300+ relics, cards, powers)
//     lib/0.110.0/HextechRunes.dll  <- ditto, built against the other game branch
//
// Every detector that judged a mod by `FindModDllPath` was reading the stub — which defines no
// content entities and references no content models — so Signal A (EntityDefinitionDetector) and
// Signal B (CosmeticUtilityDetector) both came up empty on a 19 MB content mod. Meanwhile
// CharacterIdSuggester walks the folder recursively and *does* read the variants, so the two halves
// of the decision disagreed about what the mod even was: the guards saw a stub, the heuristic saw
// the payload. ARAM: Mayhem (HextechRunes, Workshop 3747501308) was auto-assigned as a Regent skin
// on the strength of one `event:/sfx/characters/regent/…` literal, DLL-blocked whenever another
// Regent skin was active, and took its dependent expansion pack (HextechRunesSponsorPack, which
// hard-references the base assembly) down with it — a boot failure, not just a missing mod.
//
// Reading every assembly in the folder puts the guards back on the same footing as the heuristic.
public static class ModAssemblySet
{
    // All *.dll under modFolder, recursively. Files sitting directly in the folder come first, so
    // a caller that stops at the first hit still sees the conventional `{modId}.dll` before any
    // bundled variant or third-party library.
    public static IReadOnlyList<string> ForFolder(string? modFolder)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(modFolder) || !Directory.Exists(modFolder)) return result;

        var queue = new Queue<string>();
        queue.Enqueue(modFolder!);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                    result.Add(f);
            }
            catch { }
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith(".") || string.Equals(name, "__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
                    queue.Enqueue(sub);
                }
            }
            catch { }
        }
        return result;
    }

    // Resolves the mod folder from `{modId}.dll` anywhere in the scan roots, then returns every
    // assembly in it. Empty when the mod has no DLL at all (pck-only skin).
    public static IReadOnlyList<string> ForMod(IReadOnlyList<string> modsDirs, string modId)
    {
        var primary = HarmonyPatchInspector.FindModDllPath(modsDirs, modId);
        if (primary == null) return Array.Empty<string>();
        return ForFolder(Path.GetDirectoryName(primary));
    }
}
