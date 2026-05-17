using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Sts2SkinManager.Config;
using Sts2SkinManager.Discovery;
using Sts2SkinManager.Localization;

namespace Sts2SkinManager.Runtime;

// Runs once per session, ~2 seconds after MainFile.Initialize, by which time STS2's mod loader
// has finished walking the mod list and every mod's DLL has applied its Harmony patches. We ask
// HarmonyPatchInspector for any mod that touched a character-spine type, filter out ones already
// known to SkinManager (assigned / skipped / pck-detected), then for the remaining suspects we
// auto-suggest a base character via CharacterIdSuggester. High-confidence suggestions get written
// to skin_choices.json and surfaced in a single restart modal.
public static class DllSkinDetectionService
{
    private const float DetectionDelaySeconds = 2.0f;

    public static void ScheduleAfter(
        SceneTree tree,
        string modsDir,
        string choicesPath,
        string managerDataDir,
        IReadOnlySet<string> baseCharacters,
        IReadOnlyCollection<string> alreadyDetectedModIds,
        IReadOnlyCollection<string> customCharacterModIds)
    {
        // alreadyDetectedModIds = pck-based scanner already classified these (character or card),
        // so skip them entirely from the Harmony inspection pass.
        var alreadyDetected = new HashSet<string>(alreadyDetectedModIds, StringComparer.OrdinalIgnoreCase);
        // customCharacterModIds = pck has animations/characters/{non_base}/ — this is a mod that
        // adds a brand-new character. Its DLL may reference base-game audio/asset paths (e.g.
        // STS1→STS2 ports reusing Necrobinder SFX events), which would otherwise trick the
        // byte-frequency suggester into mis-classifying the mod as that base character's skin.
        var customCharacterMods = new HashSet<string>(customCharacterModIds, StringComparer.OrdinalIgnoreCase);

        try
        {
            var timer = tree.CreateTimer(DetectionDelaySeconds);
            timer.Timeout += () =>
            {
                try { Run(modsDir, choicesPath, managerDataDir, baseCharacters, alreadyDetected, customCharacterMods); }
                catch (Exception ex) { MainFile.Logger.Warn($"dll skin detection failed: {ex.Message}"); }
            };
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"could not schedule dll skin detection: {ex.Message}");
        }
    }

    private static void Run(
        string modsDir,
        string choicesPath,
        string managerDataDir,
        IReadOnlySet<string> baseCharacters,
        HashSet<string> alreadyDetected,
        HashSet<string> customCharacterMods)
    {
        var assemblyToModId = HarmonyPatchInspector.BuildAssemblyToModIdMap(modsDir);
        var suspects = HarmonyPatchInspector.Inspect(assemblyToModId);

        // Reload from disk to capture any UI-driven edits made between MainFile.Initialize and
        // this deferred run (defensive — most paths just modify in-memory choices).
        var choices = SkinChoicesConfig.LoadOrEmpty(choicesPath);

        // Visibility log every boot: surface what's already known so the user can see at a
        // glance which DLL-skin assignments are active without grepping skin_choices.json.
        if (choices.DllSkinAssignments.Count > 0)
        {
            MainFile.Logger.Info($"dll-skin: {choices.DllSkinAssignments.Count} existing assignment(s):");
            foreach (var kv in choices.DllSkinAssignments.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                MainFile.Logger.Info($"  [assigned] {kv.Key} → {kv.Value}");
        }
        if (choices.DllSkinSkipped.Count > 0)
        {
            MainFile.Logger.Info($"dll-skin: {choices.DllSkinSkipped.Count} skipped mod(s) (silenced from prompts): [{string.Join(", ", choices.DllSkinSkipped.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}]");
        }

        var newAutoAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new List<SuspectDllSkin>();

        foreach (var suspect in suspects)
        {
            if (alreadyDetected.Contains(suspect.ModId)) continue;
            if (choices.DllSkinAssignments.ContainsKey(suspect.ModId)) continue;
            if (choices.DllSkinSkipped.Contains(suspect.ModId)) continue;
            if (customCharacterMods.Contains(suspect.ModId))
            {
                // Custom-character mod whose DLL incidentally patches CharacterModel (for the new
                // character's spine setup) and references base-game asset paths. Refuse to suggest
                // — assigning it as a base skin would DLL-block it and the custom character would
                // disappear whenever the user picks "default" or any other skin for that base.
                MainFile.Logger.Info($"dll-skin: suppressing auto-suggest for custom-character mod '{suspect.ModId}' (patched [{string.Join(", ", suspect.PatchedTargets)}]). Its pck adds a non-base character; the byte-frequency suggester would mis-attribute references to a base character.");
                continue;
            }

            // Find the mod folder. assemblyToModId maps name→folderId but we still need the path
            // to read pck/dll bytes. Walk the mods tree once to find the matching folder.
            var modFolder = LocateModFolder(modsDir, suspect.ModId);
            if (modFolder == null)
            {
                MainFile.Logger.Warn($"dll-skin suspect '{suspect.ModId}' patched [{string.Join(", ", suspect.PatchedTargets)}] but mod folder not found — skipping suggestion.");
                continue;
            }

            // Strongest signal: a patch on one of the concrete character subclasses
            // (CharacterModel.Defect / Ironclad / etc.) directly names the target character
            // — no need to count string occurrences in pck/dll bytes.
            string? concreteHit = null;
            foreach (var target in suspect.PatchedTargets)
            {
                if (HarmonyPatchInspector.ConcreteCharacterTypeToId.TryGetValue(target, out var concreteId))
                {
                    concreteHit = concreteId;
                    break;
                }
            }

            string? via = null;
            string? suggested = null;
            if (concreteHit != null) { suggested = concreteHit; via = "concrete-type patch"; }
            if (suggested == null)
            {
                suggested = CharacterIdSuggester.Suggest(modFolder, baseCharacters);
                if (suggested != null) via = "byte-frequency suggester";
            }
            if (suggested == null)
            {
                // Last fallback: scan the mod's manifest (name + description) for localized
                // character keywords. Catches mods where the target character is identified
                // only in non-English text (e.g. Chinese 静默猎手 = Silent).
                suggested = ManifestCharacterHinter.Suggest(modFolder, baseCharacters);
                if (suggested != null) via = "manifest keyword";
            }

            if (suggested != null)
            {
                newAutoAssignments[suspect.ModId] = suggested;
                MainFile.Logger.Info($"dll-skin auto-detected: {suspect.ModId} patches [{string.Join(", ", suspect.PatchedTargets)}] → suggested '{suggested}' (via {via})");
            }
            else
            {
                ambiguous.Add(suspect);
                var nameDesc = ManifestCharacterHinter.TryReadNameDescription(modFolder);
                var manifestExcerpt = nameDesc.HasValue
                    ? $" — manifest name='{nameDesc.Value.name}' description='{Truncate(nameDesc.Value.description, 120)}'"
                    : "";
                MainFile.Logger.Info($"dll-skin candidate (ambiguous): {suspect.ModId} patches [{string.Join(", ", suspect.PatchedTargets)}]{manifestExcerpt}. No clear character suggestion; manually add '\"{suspect.ModId}\": \"<character>\"' under _dll_skin_assignments in skin_choices.json (or '\"{suspect.ModId}\"' under _dll_skin_skipped to silence).");
            }
        }

        // Forensic pass — surface mods that have BOTH .dll and .pck but were classified by
        // neither pck-scan (no animations/characters or card_art paths) nor Harmony inspection
        // (didn't patch any whitelisted spine type). These are the "we should detect this but
        // can't" cases the user needs to know about. Logged every boot so the trail is durable.
        var harmonySuspectIds = suspects.Select(s => s.ModId).ToList();
        var unclassified = UnclassifiedModInventory.Build(
            modsDir, baseCharacters, alreadyDetected, harmonySuspectIds,
            choices.DllSkinAssignments.Keys, choices.DllSkinSkipped, customCharacterMods);
        if (unclassified.Count > 0)
        {
            MainFile.Logger.Info($"dll-skin forensic: {unclassified.Count} mod(s) with DLL+pck but no recognized skin signal:");
            foreach (var u in unclassified)
            {
                var hint = u.SuggestedCharacter != null
                    ? $" — possible target '{u.SuggestedCharacter}' (byte-frequency hint)"
                    : " — no character-id hint found";
                MainFile.Logger.Info($"  [unclassified] {u.ModId}{hint}. To manage, add '\"{u.ModId}\": \"<character>\"' under _dll_skin_assignments in skin_choices.json. To silence, add '\"{u.ModId}\"' to _dll_skin_skipped.");
            }
        }

        if (newAutoAssignments.Count == 0)
        {
            if (ambiguous.Count > 0)
            {
                MainFile.Logger.Info($"{ambiguous.Count} dll-skin candidate(s) need manual assignment — see _dll_skin_assignments / _dll_skin_skipped in skin_choices.json.");
            }
            return;
        }

        foreach (var (modId, charId) in newAutoAssignments)
        {
            choices.DllSkinAssignments[modId] = charId;
        }
        choices.Save(choicesPath);
        MainFile.Logger.Info($"wrote {newAutoAssignments.Count} new dll-skin assignment(s) to {choicesPath}");

        // Single combined restart modal — all auto-assignments take effect on next boot.
        var summary = string.Join(", ", newAutoAssignments.Select(kv => $"{kv.Key}→{kv.Value}"));
        MainFile.Logger.Info($"showing dll-skin restart modal: {summary}");

        // Snapshot the new assignment keys so cancel can revert exactly what we wrote (no clobber
        // of pre-existing assignments from earlier sessions).
        var newlyAddedModIds = newAutoAssignments.Keys.ToList();
        Action onCancel = () =>
        {
            // User clicked "Restart later" — without restart the new assignments would still take
            // effect on the next boot, possibly hijacking the DLL of a non-skin mod. Revert by
            // re-loading the on-disk choices, removing only the keys we just added, and
            // re-saving.
            try
            {
                var current = SkinChoicesConfig.LoadOrEmpty(choicesPath);
                var removed = 0;
                foreach (var modId in newlyAddedModIds)
                {
                    if (current.DllSkinAssignments.Remove(modId)) removed++;
                }
                if (removed > 0)
                {
                    current.Save(choicesPath);
                    MainFile.Logger.Info($"dll-skin: user cancelled — reverted {removed} pending assignment(s) from {choicesPath}.");
                }
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"dll-skin: cancel-revert failed: {ex.Message}");
            }
        };

        RestartCountdownModal.ShowOrReset(
            managerDataDir,
            10,
            "dll_skin_modal_title",
            "dll_skin_modal_body_summary",
            onCancel);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    private static string? LocateModFolder(string modsDir, string modId)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(modsDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(".") || string.Equals(name, "__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
                // Match by the presence of "{modId}.pck" or "{modId}.dll" in this folder.
                if (File.Exists(Path.Combine(dir, modId + ".pck")) || File.Exists(Path.Combine(dir, modId + ".dll")))
                    return dir;
            }
        }
        catch { }
        return null;
    }
}
