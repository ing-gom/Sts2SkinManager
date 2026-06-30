using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sts2SkinManager.Config;

namespace Sts2SkinManager.Discovery;

// Rescue pass for false-positive DLL skin assignments. Runs at MainFile.Initialize, BEFORE
// SkinModScanner reads _dll_skin_assignments — so any entry demoted here doesn't get DLL-blocked
// in the current session.
//
// Why this exists: prior versions auto-assigned via CharacterIdSuggester (byte-frequency over
// raw DLL/pck bytes). Content mods like Act4FinalAscent (Nexus #37, "Adds The Architect as a
// full Act 4 boss encounter") reuse Defect's spine/sprite paths inside their pck and pass the
// suggester's dominance ratio. Once written to _dll_skin_assignments, the scanner treats them
// as a defect skin variant and DLL-blocks them whenever the user picks another skin — the
// Architect boss silently disappears from the game.
//
// Two complementary false-positive signals, either of which demotes a bogus assignment:
//   Signal A (EntityDefinitionDetector): the DLL DEFINES new MonsterModel/EventModel/
//     EncounterModel/CardModel/PowerModel/RelicModel/PotionModel subclasses — a content mod
//     (Act4FinalAscent). Read via System.Reflection.Metadata, no assembly load.
//   Signal B (CosmeticUtilityDetector): the DLL REFERENCES >= 2 base content-model types
//     (relic/power/potion) AND the mod has NO per-character identity — a wholesale texture/
//     cosmetic utility (CustomCardTextureLoaderSG) that patches a whitelisted character-select
//     type for retexturing, not skinning. The "no per-character identity" guard (HasCharacterSignal)
//     is what protects a *themed* skin that also retextures relics/powers/potions: such a mod still
//     names its target character (via concrete patch / byte-frequency / manifest keyword), so it
//     keeps a character signal and is NOT demoted — only a content-model-referencing mod with zero
//     character signal is treated as a utility.
// Skin mods never trip either signal. Any positive signal moves the assignment from
// _dll_skin_assignments to _dll_skin_skipped, saves the config, and logs the rescue. On next
// boot, the cleaned config causes the scanner to leave the mod alone.
public static class EntityBasedRescue
{
    public record RescueResult(IReadOnlyList<string> RescuedModIds);

    // Returns the list of modIds demoted. Caller decides whether to show a restart toast — the
    // current session's dll-block already happened for these mods before this method ran, so
    // a restart is needed to actually re-mount the content. (Or the user just plays through;
    // they get the content back on next launch.)
    public static RescueResult RunPreScan(IReadOnlyList<string> modsDirs, string choicesPath, IReadOnlySet<string> baseCharacters)
    {
        var choices = SkinChoicesConfig.LoadOrEmpty(choicesPath);
        if (choices.DllSkinAssignments.Count == 0)
        {
            MainFile.Logger.Info($"dll-skin rescue: no existing assignments to check.");
            return new RescueResult(System.Array.Empty<string>());
        }

        MainFile.Logger.Info($"dll-skin rescue: checking {choices.DllSkinAssignments.Count} existing assignment(s) for content-mod signal.");

        var rescued = new List<string>();
        foreach (var modId in choices.DllSkinAssignments.Keys.ToList())
        {
            var assignedChar = choices.DllSkinAssignments[modId];
            var dllPath = HarmonyPatchInspector.FindModDllPath(modsDirs, modId);
            if (dllPath == null)
            {
                MainFile.Logger.Info($"dll-skin rescue: '{modId}' (assigned '{assignedChar}') — DLL not found in mods tree, leaving as-is.");
                continue;
            }

            var report = EntityDefinitionDetector.InspectFile(modId, dllPath);
            // Signal B only fires when the mod has NO character identity — a themed skin that also
            // retextures relics/powers/potions still names its character, so HasCharacterSignal keeps
            // it managed. Only a content-model-referencing mod with zero character signal is a utility.
            var modFolder = Path.GetDirectoryName(dllPath)!;
            var isCosmetic = report == null
                && !HasCharacterSignal(modFolder, baseCharacters)
                && CosmeticUtilityDetector.IsGlobalCosmeticMod(dllPath);
            // Signal C (custom character): the mod adds a brand-new character via ANY framework —
            // BaseLib dependency / CustomCharacterModel base class, OR (framework-agnostic) it ships
            // a char-select asset for a non-base id. A byte-frequency assignment to a base character
            // would DLL-block the whole custom character; demote it. Catches RitsuLib characters
            // (HornetMod → assigned 'silent') that trip neither signal A nor B.
            var isCustomChar = CustomCharacterFrameworkDetector.IsCustomCharacterMod(modFolder, modId)
                || CustomCharacterFrameworkDetector.IntroducesNonBaseCharacterByPck(modFolder, modId, baseCharacters);
            if (report == null && !isCosmetic && !isCustomChar)
            {
                MainFile.Logger.Info($"dll-skin rescue: '{modId}' (assigned '{assignedChar}') — no content-entity subclasses (signal A), no content-model texture patches without character signal (signal B), and no custom-character signal (signal C), keeping assignment. DLL: {dllPath}");
                continue;
            }

            choices.DllSkinAssignments.Remove(modId);
            choices.DllSkinSkipped.Add(modId);
            rescued.Add(modId);

            if (report == null && isCustomChar)
            {
                MainFile.Logger.Warn(
                    $"dll-skin rescue: '{modId}' was assigned as '{assignedChar}' skin but adds a " +
                    $"brand-new character (custom-character framework signal — BaseLib/CustomCharacterModel " +
                    $"or a char-select asset for a non-base id). Assigning it as a base skin would DLL-block " +
                    $"the new character whenever the base uses 'default' or another skin. " +
                    $"Treating as custom-character mod, moving to _dll_skin_skipped. Restart STS2 to restore the mod's DLL.");
            }
            else if (report != null)
            {
                MainFile.Logger.Warn(
                    $"dll-skin rescue: '{modId}' was assigned as '{assignedChar}' skin but defines " +
                    $"{report.DefinedEntities.Count} content entit{(report.DefinedEntities.Count == 1 ? "y" : "ies")} " +
                    $"(first base: {report.FirstEntityBaseName}). " +
                    $"Treating as content mod, moving to _dll_skin_skipped. " +
                    $"Restart STS2 to restore the mod's DLL. " +
                    $"Entities: [{string.Join(", ", report.DefinedEntities.Take(5))}{(report.DefinedEntities.Count > 5 ? ", …" : "")}]");
            }
            else
            {
                MainFile.Logger.Warn(
                    $"dll-skin rescue: '{modId}' was assigned as '{assignedChar}' skin but references >= 2 " +
                    $"base content-model types (relic/power/potion — signal B). A character skin never " +
                    $"patches relics/powers/potions; this is a global cosmetic/texture utility. " +
                    $"Treating as utility mod, moving to _dll_skin_skipped. Restart STS2 to restore the mod's DLL.");
            }
        }

        if (rescued.Count > 0)
        {
            choices.Save(choicesPath);
            MainFile.Logger.Info($"dll-skin rescue: demoted {rescued.Count} false-positive assignment(s) to _dll_skin_skipped — saved to {choicesPath}.");
        }
        else
        {
            MainFile.Logger.Info($"dll-skin rescue: no assignments demoted (all {choices.DllSkinAssignments.Count} pass signal A/B check).");
        }

        return new RescueResult(rescued);
    }

    // Same shape as RunPreScan but for the deferred path inside DllSkinDetectionService: given a
    // candidate modId discovered via Harmony patch inspection, decide whether to auto-skip
    // entirely (no suggestion modal). Returns true if the mod was demoted.
    public static bool TryGateSuspect(string modId, IReadOnlyList<string> modsDirs, SkinChoicesConfig choices)
    {
        var dllPath = HarmonyPatchInspector.FindModDllPath(modsDirs, modId);
        if (dllPath == null) return false;
        var report = EntityDefinitionDetector.InspectFile(modId, dllPath);
        if (report == null) return false;

        choices.DllSkinSkipped.Add(modId);
        MainFile.Logger.Info(
            $"dll-skin: auto-skip '{modId}' — defines {report.DefinedEntities.Count} content " +
            $"entit{(report.DefinedEntities.Count == 1 ? "y" : "ies")} (first base: {report.FirstEntityBaseName}). " +
            $"Likely a content mod, not a skin. Adding to _dll_skin_skipped.");
        return true;
    }

    // A genuine character skin always references its target character somewhere — via a concrete
    // CharacterModel.<X> patch, the byte-frequency suggester, or a localized manifest keyword. If
    // none of these resolve a character, the mod has no per-character identity, so Signal B can
    // safely treat a content-model reference as "global utility, not a skin". This guard is what
    // protects a themed skin that also retextures relics/powers/potions from being demoted.
    // (Signal B in the deferred DllSkinDetectionService path uses the already-computed `suggested`
    // — which includes the concrete-patch hit — and so needs no separate call here.)
    private static bool HasCharacterSignal(string modFolder, IReadOnlySet<string> baseCharacters)
    {
        if (CharacterIdSuggester.Suggest(modFolder, baseCharacters) != null) return true;
        if (ManifestCharacterHinter.Suggest(modFolder, baseCharacters) != null) return true;
        return false;
    }
}
