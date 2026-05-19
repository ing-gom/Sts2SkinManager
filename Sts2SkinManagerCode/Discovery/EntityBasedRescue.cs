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
// EntityDefinitionDetector reads each mod's DLL via System.Reflection.Metadata (no assembly
// load) and checks whether it defines new MonsterModel/EventModel/EncounterModel/CardModel/
// PowerModel/RelicModel/PotionModel subclasses. Skin mods never do this; content mods almost
// always do. Any positive signal moves the assignment from _dll_skin_assignments to
// _dll_skin_skipped, saves the config, and logs the rescue. On next boot, the cleaned config
// causes the scanner to leave the mod alone.
public static class EntityBasedRescue
{
    public record RescueResult(IReadOnlyList<string> RescuedModIds);

    // Returns the list of modIds demoted. Caller decides whether to show a restart toast — the
    // current session's dll-block already happened for these mods before this method ran, so
    // a restart is needed to actually re-mount the content. (Or the user just plays through;
    // they get the content back on next launch.)
    public static RescueResult RunPreScan(string modsDir, string choicesPath)
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
            var dllPath = HarmonyPatchInspector.FindModDllPath(modsDir, modId);
            if (dllPath == null)
            {
                MainFile.Logger.Info($"dll-skin rescue: '{modId}' (assigned '{assignedChar}') — DLL not found in mods tree, leaving as-is.");
                continue;
            }

            var report = EntityDefinitionDetector.InspectFile(modId, dllPath);
            if (report == null)
            {
                MainFile.Logger.Info($"dll-skin rescue: '{modId}' (assigned '{assignedChar}') — no content-entity subclasses found (signal A negative), keeping assignment. DLL: {dllPath}");
                continue;
            }

            choices.DllSkinAssignments.Remove(modId);
            choices.DllSkinSkipped.Add(modId);
            rescued.Add(modId);

            MainFile.Logger.Warn(
                $"dll-skin rescue: '{modId}' was assigned as '{assignedChar}' skin but defines " +
                $"{report.DefinedEntities.Count} content entit{(report.DefinedEntities.Count == 1 ? "y" : "ies")} " +
                $"(first base: {report.FirstEntityBaseName}). " +
                $"Treating as content mod, moving to _dll_skin_skipped. " +
                $"Restart STS2 to restore the mod's DLL. " +
                $"Entities: [{string.Join(", ", report.DefinedEntities.Take(5))}{(report.DefinedEntities.Count > 5 ? ", …" : "")}]");
        }

        if (rescued.Count > 0)
        {
            choices.Save(choicesPath);
            MainFile.Logger.Info($"dll-skin rescue: demoted {rescued.Count} false-positive assignment(s) to _dll_skin_skipped — saved to {choicesPath}.");
        }
        else
        {
            MainFile.Logger.Info($"dll-skin rescue: no assignments demoted (all {choices.DllSkinAssignments.Count} pass signal A check).");
        }

        return new RescueResult(rescued);
    }

    // Same shape as RunPreScan but for the deferred path inside DllSkinDetectionService: given a
    // candidate modId discovered via Harmony patch inspection, decide whether to auto-skip
    // entirely (no suggestion modal). Returns true if the mod was demoted.
    public static bool TryGateSuspect(string modId, string modsDir, SkinChoicesConfig choices)
    {
        var dllPath = HarmonyPatchInspector.FindModDllPath(modsDir, modId);
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
}
