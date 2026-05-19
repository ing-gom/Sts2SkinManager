using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Sts2SkinManager.Discovery;

public record SuspectDllSkin(
    string ModId,
    string AssemblyName,
    IReadOnlyList<string> PatchedTargets
);

// Scans currently-applied Harmony patches for ones that touch character-spine / character-select
// types. A mod whose DLL hits this whitelist is almost certainly a "DLL-driven character skin"
// (e.g. Hcxmmx_King_Skin) — its pck has no `animations/characters/{base}/` paths so the standard
// pck-only scanner missed it. We surface those mods so MainFile can prompt the user to assign
// them to a base character, after which the v0.7.0 TryLoadMod block treats them like any other
// skin variant.
public static class HarmonyPatchInspector
{
    // Fully-qualified type names whose patches strongly signal "this DLL skins a character".
    // Confirmed against the v1.x sts2.dll decompile + known-good mod patterns:
    //   - Booba-Necrobinder-Mod patches CharacterModel.CreateVisuals
    //   - Hcxmmx_King_Skin patches NCharacterSelectButton
    // The list includes (1) the abstract base CharacterModel, (2) each concrete character
    // subclass (Defect/Ironclad/Necrobinder/Regent/Silent — patching one of these is the
    // strongest possible signal of "this targets exactly that character"), (3) display nodes
    // mods commonly hook to swap visuals, and (4) the Spine binding that ultimately swaps
    // skeleton data.
    private static readonly HashSet<string> SpineTargetTypes = new(StringComparer.Ordinal)
    {
        "MegaCrit.Sts2.Core.Models.CharacterModel",
        "MegaCrit.Sts2.Core.Models.Characters.Defect",
        "MegaCrit.Sts2.Core.Models.Characters.Ironclad",
        "MegaCrit.Sts2.Core.Models.Characters.Necrobinder",
        "MegaCrit.Sts2.Core.Models.Characters.Regent",
        "MegaCrit.Sts2.Core.Models.Characters.Silent",
        "MegaCrit.Sts2.Core.Nodes.Combat.NCreatureVisuals",
        "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton",
        "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen",
        "MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaSprite",
        "MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteCharacter",
        "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantCharacter",
        "MegaCrit.sts2.Core.Nodes.TopBar.NTopBarPortrait",
        "MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.NVfxSpine",
    };

    // Direct mapping from the concrete-character subclass FQN to its base-character id.
    // When an inspector hit lands on one of these, we don't need CharacterIdSuggester — the
    // patched type IS the target character. Auto-suggestion can short-circuit straight to it.
    public static readonly Dictionary<string, string> ConcreteCharacterTypeToId = new(StringComparer.Ordinal)
    {
        ["MegaCrit.Sts2.Core.Models.Characters.Defect"] = "defect",
        ["MegaCrit.Sts2.Core.Models.Characters.Ironclad"] = "ironclad",
        ["MegaCrit.Sts2.Core.Models.Characters.Necrobinder"] = "necrobinder",
        ["MegaCrit.Sts2.Core.Models.Characters.Regent"] = "regent",
        ["MegaCrit.Sts2.Core.Models.Characters.Silent"] = "silent",
    };

    // Known non-skin mods that legitimately patch character-select / character-model types for
    // gameplay reasons (advisor overlays, save tools, modding utilities). Excluding here keeps
    // them out of the dll-skin suggestion path. Match is case-insensitive on the full mod id.
    // - "Sts2" prefix covers the inggom sister-mod fleet and other community sts2-* mods
    //   (CardAdvisor, HostObserver, MultiplayerSync, OrbLayout, SeedVerdict, etc.).
    // - "BaseLib" is Alchyr's modding helper.
    private static readonly HashSet<string> KnownNonSkinModIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "BaseLib",
    };

    private static bool IsKnownNonSkin(string modId)
    {
        if (KnownNonSkinModIds.Contains(modId)) return true;
        if (modId.StartsWith("Sts2", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Walks all currently-patched methods and groups owners that hit any spine target.
    // assemblyToModId: precomputed map from `AssemblyName.Name` (the .dll file's module name) to
    // its mod folder id. Built by MainFile from the `mods/` directory walk.
    public static List<SuspectDllSkin> Inspect(IReadOnlyDictionary<string, string> assemblyToModId)
    {
        var perMod = new Dictionary<string, (string asmName, HashSet<string> targets)>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<MethodBase> patched;
        try { patched = Harmony.GetAllPatchedMethods(); }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"HarmonyPatchInspector: GetAllPatchedMethods failed: {ex.Message}");
            return new List<SuspectDllSkin>();
        }

        foreach (var method in patched)
        {
            var declaringFullName = method.DeclaringType?.FullName;
            if (declaringFullName == null || !SpineTargetTypes.Contains(declaringFullName)) continue;

            HarmonyLib.Patches? info;
            try { info = Harmony.GetPatchInfo(method); }
            catch { continue; }
            if (info == null) continue;

            var allPatches = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers);
            foreach (var patch in allPatches)
            {
                var asm = patch.PatchMethod?.DeclaringType?.Assembly;
                var asmName = asm?.GetName().Name;
                if (string.IsNullOrEmpty(asmName)) continue;
                // Skip our own patches and HarmonyLib internals.
                if (string.Equals(asmName, MainFile.ModId, StringComparison.OrdinalIgnoreCase)) continue;
                if (asmName.StartsWith("0Harmony", StringComparison.Ordinal)) continue;

                if (!assemblyToModId.TryGetValue(asmName, out var modId))
                {
                    // Patch came from an assembly we couldn't map to a mod folder — likely
                    // a base-game DLL or a transitively-loaded helper. Skip.
                    continue;
                }

                // Filter known sister mods / utilities that patch character-select for legit
                // non-skin reasons. Without this, e.g. Sts2CardAdvisor's Init patch on
                // NCharacterSelectButton would be flagged and DLL-blocked on next boot.
                if (IsKnownNonSkin(modId)) continue;

                if (!perMod.TryGetValue(modId, out var entry))
                {
                    entry = (asmName, new HashSet<string>(StringComparer.Ordinal));
                    perMod[modId] = entry;
                }
                entry.targets.Add(declaringFullName);
            }
        }

        return perMod
            .Select(kv => new SuspectDllSkin(kv.Key, kv.Value.asmName, kv.Value.targets.OrderBy(x => x).ToList()))
            .OrderBy(s => s.ModId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Builds the assembly-name → mod-id map by walking the mods/ tree and reading each loaded
    // DLL's AssemblyName.Name. Only inspects DLLs that are in AppDomain.CurrentDomain — never
    // touches disk for already-loaded assemblies (avoids reload pitfalls).
    public static Dictionary<string, string> BuildAssemblyToModIdMap(string modsDir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(modsDir)) return result;

        // For each DLL on disk under mods/, treat its file-name-without-extension as the candidate
        // mod folder id (matching SkinModScanner's pck convention). Then check if that assembly
        // is loaded in the current AppDomain — if yes, register the mapping. We don't load DLLs
        // ourselves; STS2's ModManager does that at boot, so by the time we're called everything
        // a mod patches has already been loaded.
        var loadedByName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var n = asm.GetName().Name;
                if (!string.IsNullOrEmpty(n) && !loadedByName.ContainsKey(n)) loadedByName[n] = asm;
            }
            catch { }
        }

        foreach (var dllPath in EnumerateDllsRecursive(modsDir))
        {
            var folderId = Path.GetFileNameWithoutExtension(dllPath);
            if (string.IsNullOrEmpty(folderId)) continue;
            if (loadedByName.ContainsKey(folderId) && !result.ContainsKey(folderId))
            {
                result[folderId] = folderId;
            }
        }

        return result;
    }

    // Exposed so EntityDefinitionDetector callers can locate a mod's DLL on disk without
    // re-walking the whole mods/ tree themselves. ModId convention matches BuildAssemblyToModIdMap.
    public static string? FindModDllPath(string modsDir, string modId)
    {
        foreach (var dllPath in EnumerateDllsRecursive(modsDir))
        {
            var folderId = Path.GetFileNameWithoutExtension(dllPath);
            if (string.Equals(folderId, modId, StringComparison.OrdinalIgnoreCase))
                return dllPath;
        }
        return null;
    }

    private static IEnumerable<string> EnumerateDllsRecursive(string root)
    {
        var stack = new Stack<string>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root))
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
            IEnumerable<string> dlls;
            try { dlls = Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly); }
            catch { dlls = Array.Empty<string>(); }
            foreach (var f in dlls) yield return f;

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
}
