using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using Sts2SkinManager.Config;
using Sts2SkinManager.Discovery;
using Sts2SkinManager.Runtime;

namespace Sts2SkinManager;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "Sts2SkinManager";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; }
        = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        try
        {
            ApplyHarmonyPatches();
            Run();
        }
        catch (Exception ex)
        {
            Logger.Warn($"init failed: {ex}");
        }
    }

    private static void ApplyHarmonyPatches()
    {
        var harmony = new Harmony(ModId);
        harmony.PatchAll(typeof(MainFile).Assembly);
        Logger.Info("Harmony patches applied.");
    }

    private static void Run()
    {
        var executablePath = OS.GetExecutablePath();
        var gameDir = Path.GetDirectoryName(executablePath)!;
        var modsDir = Path.Combine(gameDir, "mods");
        var userDataDir = OS.GetUserDataDir();
        var managerDataDir = Path.Combine(userDataDir, ModId);
        Directory.CreateDirectory(managerDataDir);

        var baseCharacters = SkinModScanner.ScanBaseCharacters(gameDir);
        Logger.Info($"base character roster ({baseCharacters.Count}): [{string.Join(", ", baseCharacters.OrderBy(x => x))}]");

        // Auto-junction: if the user moved a DLL-bundled mod folder into a category subfolder
        // (e.g. mods/캐릭터/Booba-Necrobinder-Mod/), the game's manifest scan misses it because
        // the framework only walks direct children of mods/. We create a junction at depth=1
        // pointing back. Effect lands on the *next* boot — the framework's scan has already run.
        var junctionResults = ModFolderJunctionEnforcer.Enforce(modsDir);
        var junctionsCreated = 0;
        foreach (var r in junctionResults)
        {
            switch (r.Action)
            {
                case JunctionAction.Created:
                    Logger.Info($"  [junction+] {r.JunctionPath} → {r.ModFolder}");
                    junctionsCreated++;
                    break;
                case JunctionAction.OrphanRemoved:
                    Logger.Info($"  [junction-] {r.JunctionPath} (target missing)");
                    break;
                case JunctionAction.SkippedConflict:
                    Logger.Warn($"  [junction!] {r.JunctionPath}: {r.Detail}");
                    break;
                case JunctionAction.SkippedDuplicate:
                    Logger.Warn($"  [junction!] {r.JunctionPath}: {r.Detail}");
                    break;
                case JunctionAction.Failed:
                    Logger.Warn($"  [junction✗] {r.JunctionPath}: {r.Detail}");
                    break;
            }
        }

        var detected = SkinModScanner.Scan(modsDir, baseCharacters, out var skippedCustom);
        var characterMods = detected.Where(d => d.Kind == SkinModKind.Character).ToList();
        var cardMods = detected.Where(d => d.Kind == SkinModKind.Cards).ToList();

        if (skippedCustom.Count > 0)
        {
            Logger.Info($"skipped {skippedCustom.Count} custom-character mod(s) — not in base roster, leaving auto-mount intact:");
            foreach (var s in skippedCustom) Logger.Info($"  [skip] {s}");
        }

        Logger.Info($"detected {characterMods.Count} character skin pck(s), {cardMods.Count} card pack pck(s):");
        foreach (var d in characterMods)
        {
            ManagedPckRegistry.Manage(d.PckPath);
            Logger.Info($"  [char] {d.ModId} → [{string.Join(",", d.Characters)}]");
        }
        foreach (var d in cardMods)
        {
            Logger.Info($"  [cards] {d.ModId}");
        }

        var byCharacter = new Dictionary<string, List<DetectedSkinMod>>();
        foreach (var d in characterMods)
        {
            foreach (var c in d.Characters)
            {
                if (!byCharacter.TryGetValue(c, out var list))
                {
                    list = new List<DetectedSkinMod>();
                    byCharacter[c] = list;
                }
                list.Add(d);
            }
        }

        var settings = Sts2SettingsWriter.FindAndLoad(userDataDir);
        var fileReordered = settings != null && LoadOrderEnforcer.EnsureFirstInModList(settings, ModId);
        if (fileReordered && settings != null) Sts2SettingsWriter.Save(settings);

        var memoryReordered = LoadOrderEnforcer.EnsureFirstInMods(ModId);
        if (fileReordered || memoryReordered)
        {
            Logger.Warn($"self-bootstrap: file_reorder={fileReordered} memory_reorder={memoryReordered}. " +
                        "*** RESTART STS2 ONCE *** for full activation.");
        }

        // Show restart modal only if the persisted settings.save was reordered. That means *next*
        // boot needs a restart to actually pick up the new load order — without restart, character
        // mods loaded before us this session still have their Harmony patches live (e.g. Booba's
        // scale override). In-memory reorder alone has no effect this boot since TryLoadMod calls
        // already happened in the original order.
        if (fileReordered)
        {
            RestartCountdownModal.ShowOrReset(managerDataDir, 10, "load_order_modal_title", "load_order_modal_body");
        }
        // Newly-created junctions miss the current boot's manifest scan. Prompt for restart so the
        // relocated mods come back online. Suppressed when fileReordered already triggered the modal.
        else if (junctionsCreated > 0)
        {
            RestartCountdownModal.ShowOrReset(managerDataDir, 10, "junction_modal_title", "junction_modal_body");
        }

        var choicesPath = Path.Combine(managerDataDir, "skin_choices.json");

        // Modpack preset: a curator can ship `mods/Sts2SkinManager/modpack_preset.json` alongside
        // their mod bundle. When a fresh install has no user-side choices yet, we seed from it so
        // the recipient just unzips and plays. After seeding, the user_data file becomes the truth
        // and every Save() mirrors back to the preset path — so re-zipping `mods/` always carries
        // the latest selection forward. Mod-update zips MUST NOT contain modpack_preset.json or
        // they'll overwrite recipient selections.
        var presetPath = Path.Combine(modsDir, ModId, "modpack_preset.json");
        if (!File.Exists(choicesPath) && File.Exists(presetPath))
        {
            try
            {
                File.Copy(presetPath, choicesPath);
                Logger.Info($"seeded skin_choices.json from modpack preset ({presetPath}).");
            }
            catch (Exception ex)
            {
                Logger.Warn($"preset seed failed: {ex.Message}");
            }
        }
        SkinChoicesConfig.PresetMirrorPath = presetPath;

        var choices = SkinChoicesConfig.LoadOrEmpty(choicesPath);
        foreach (var (character, variants) in byCharacter)
        {
            choices.SyncAvailableVariants(character, variants.Select(v => v.ModId));
        }
        choices.SyncCardPacks(cardMods.Select(c => c.ModId));

        // Mixed mods (character spine + card art bundled). Tracked separately from CardPacks so the
        // user can toggle them on top of the dropdown's main-spine choice with explicit priority.
        var mixedMods = characterMods.Where(m => m.IsMixed).ToList();
        choices.SyncMixedAddons(mixedMods.Select(m => m.ModId));

        choices.Save(choicesPath);
        Logger.Info($"skin_choices.json → {choicesPath}");
        Logger.Info($"card pack state: ordering=[{string.Join(", ", choices.CardPacks.Ordering)}], enabled={{ {string.Join(", ", choices.CardPacks.Enabled.Select(kv => $"{kv.Key}={kv.Value}"))} }}");
        if (mixedMods.Count > 0)
        {
            Logger.Info($"mixed addon state: ordering=[{string.Join(", ", choices.MixedAddons.Ordering)}], enabled={{ {string.Join(", ", choices.MixedAddons.Enabled.Select(kv => $"{kv.Key}={kv.Value}"))} }}");
        }

        // Mount order matters: ordering[0] is highest priority = mounted LAST = wins on overlapping
        // paths. (1) Mixed addons first (in reverse-ordering), then (2) the dropdown's main-spine
        // choice last — so the dropdown choice always overrides spine conflicts coming from mixed
        // mods, while non-conflicting paths from mixed mods (card art, events) stay applied.
        if (mixedMods.Count > 0)
        {
            var mixedById = mixedMods.ToDictionary(m => m.ModId, m => m, StringComparer.OrdinalIgnoreCase);
            foreach (var modId in choices.MixedAddons.Ordering.AsEnumerable().Reverse())
            {
                if (!choices.MixedAddons.Enabled.TryGetValue(modId, out var enabled) || !enabled) continue;
                if (!mixedById.TryGetValue(modId, out var mod)) continue;
                RuntimeMountService.MountVariantPck(mod.PckPath);
            }
        }

        foreach (var (character, variants) in byCharacter)
        {
            if (!choices.Characters.TryGetValue(character, out var choice)) continue;
            if (string.Equals(choice.Active, "default", StringComparison.OrdinalIgnoreCase)) continue;

            var variant = variants.FirstOrDefault(v => string.Equals(v.ModId, choice.Active, StringComparison.OrdinalIgnoreCase));
            if (variant == null)
            {
                Logger.Warn($"choices.json says character '{character}' active='{choice.Active}' but no such variant found.");
                continue;
            }
            RuntimeMountService.MountVariantPck(variant.PckPath);
        }

        // Block DLL loading for non-active character mods. Without this, mods like
        // Booba-Necrobinder-Mod register Harmony patches that force-scale every base
        // necrobinder instance regardless of which skin the user selected.
        // Only effective if SkinManager loaded first (i.e. fileReordered == false this boot).
        var keepDllModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (character, _) in byCharacter)
        {
            if (!choices.Characters.TryGetValue(character, out var choice)) continue;
            if (string.IsNullOrEmpty(choice.Active)) continue;
            if (string.Equals(choice.Active, "default", StringComparison.OrdinalIgnoreCase)) continue;
            keepDllModIds.Add(choice.Active);
        }
        foreach (var m in mixedMods)
        {
            if (choices.MixedAddons.Enabled.TryGetValue(m.ModId, out var en) && en)
                keepDllModIds.Add(m.ModId);
        }
        foreach (var d in characterMods)
        {
            if (!keepDllModIds.Contains(d.ModId))
            {
                ManagedDllRegistry.Manage(d.ModId);
                Logger.Info($"  [dll-block] {d.ModId}");
            }
        }

        if (settings != null && cardMods.Count > 0)
        {
            var settingsChanged = CardPackApplier.ApplyToSettings(settings, choices.CardPacks, cardMods);
            var memoryChanged = CardPackApplier.ApplyToMemoryModList(choices.CardPacks);
            if (settingsChanged)
            {
                Sts2SettingsWriter.Save(settings);
                Logger.Info($"card pack settings.save updated (mem={memoryChanged}); takes full effect on next restart");
            }
        }

        _watcher = new ChoicesFileWatcher(choicesPath, managerDataDir, byCharacter, cardMods, choices);
        _watcher.Start();

        SkinSelectorOverlay.Configure(choicesPath, byCharacter, cardMods, mixedMods);
        SkinSelectorOverlay.SetWatcher(_watcher);

        // Defer ModConfig registration so the framework's own Initialize can run first.
        if (Engine.GetMainLoop() is SceneTree tree)
            tree.CreateTimer(0.0).Timeout += ModConfigBridge.TryRegister;
    }

    private static ChoicesFileWatcher? _watcher;
}
