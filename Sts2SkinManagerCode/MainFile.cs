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

        // Read existing DLL-skin assignments BEFORE scanning, so the scanner can inject
        // assignments-only mods (e.g. Hcxmmx_King_Skin) as Character variants even though their
        // pck has no animations/characters/{base}/ paths.
        var preliminaryChoicesPath = Path.Combine(managerDataDir, "skin_choices.json");

        // Entity-based rescue: prior versions may have auto-assigned content mods (Act4FinalAscent
        // pattern) as Defect skins via CharacterIdSuggester false positives. Run a metadata-level
        // type-graph check on each assigned mod's DLL and demote any that defines new MonsterModel/
        // EventModel/EncounterModel/CardModel/PowerModel/RelicModel/PotionModel subclasses. This
        // must run BEFORE the scanner reads _dll_skin_assignments — otherwise the demoted mod still
        // gets dll-blocked for the current session.
        var rescue = EntityBasedRescue.RunPreScan(modsDir, preliminaryChoicesPath);

        var preliminaryChoices = SkinChoicesConfig.LoadOrEmpty(preliminaryChoicesPath);
        var preliminaryDllAssignments = preliminaryChoices.DllSkinAssignments;
        var preliminaryDllSkipped = preliminaryChoices.DllSkinSkipped;

        var detected = SkinModScanner.Scan(modsDir, baseCharacters, out var skippedCustom, preliminaryDllAssignments, preliminaryDllSkipped);
        var characterMods = detected.Where(d => d.Kind == SkinModKind.Character).ToList();
        var cardMods = detected.Where(d => d.Kind == SkinModKind.Cards).ToList();
        var eventArtMods = detected.Where(d => d.Kind == SkinModKind.EventArt).ToList();

        if (skippedCustom.Count > 0)
        {
            Logger.Info($"skipped {skippedCustom.Count} custom-character mod(s) — not in base roster, leaving auto-mount intact:");
            foreach (var s in skippedCustom) Logger.Info($"  [skip] {s.ModId} → [{string.Join(",", s.CharacterIds)}] {s.DomainsLabel}");
        }

        Logger.Info($"detected {characterMods.Count} character skin pck(s), {cardMods.Count} card pack pck(s), {eventArtMods.Count} event-art pck(s):");
        foreach (var d in characterMods)
        {
            ManagedPckRegistry.Manage(d.PckPath);
            var mixedTag = d.IsMixed ? " (mixed)" : "";
            Logger.Info($"  [char] {d.ModId} → [{string.Join(",", d.Characters)}]{mixedTag} {d.DomainsLabel}");
        }
        foreach (var d in cardMods)
        {
            var mixedTag = d.IsMixed ? " (mixed)" : "";
            Logger.Info($"  [cards] {d.ModId}{mixedTag} {d.DomainsLabel}");
        }
        foreach (var d in eventArtMods)
        {
            Logger.Info($"  [events] {d.ModId} {d.DomainsLabel}");
        }

        // Dedicated mixed-domain summary — collects every IsMixed mod across both Kinds so the
        // user can spot cross-domain mods at a glance. Mount routing is unchanged (each mod
        // still mounts per its Kind); this block is purely diagnostic visibility. A "mixed" mod
        // is one that touches both character-related (spine or char_select) AND card-related
        // (card_art or card_portraits) assets — these are most likely to visually conflict with
        // other mods targeting the same character.
        var mixedAll = detected.Where(d => d.IsMixed).ToList();
        if (mixedAll.Count > 0)
        {
            Logger.Info($"{mixedAll.Count} mixed mod(s) — touch both character and card asset domains:");
            foreach (var d in mixedAll)
            {
                var kind = d.Kind == SkinModKind.Character ? "char" : "cards";
                var charsTag = d.Characters.Count > 0 ? $" → [{string.Join(",", d.Characters)}]" : "";
                Logger.Info($"  [mixed] {d.ModId}{charsTag} {d.DomainsLabel} (mounted as {kind})");
            }
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

        var choicesPath = Path.Combine(managerDataDir, "skin_choices.json");

        // Modpack preset: a curator can ship `mods/Sts2SkinManager/modpack_preset.preset` alongside
        // their mod bundle. When a fresh install has no user-side choices yet, we seed from it so
        // the recipient just unzips and plays. After seeding, the user_data file becomes the truth
        // and every Save() mirrors back to the preset path — so re-zipping `mods/` always carries
        // the latest selection forward. Mod-update zips MUST NOT contain modpack_preset.preset or
        // they'll overwrite recipient selections.
        var selfDir = Path.GetDirectoryName(typeof(MainFile).Assembly.Location) ?? Path.Combine(modsDir, ModId);
        var presetPath = Path.Combine(selfDir, "modpack_preset.preset");
        // [NOTE] Migration block — remove from here...
        // v0.12.2: Renamed preset file from modpack_preset.json → modpack_preset.preset. STS2's
        // ModManager walks the entire mods/ tree and tries to deserialize every .json as a mod
        // manifest, so the old name produced a one-line error on every boot ("missing the 'id'
        // field"). The error was harmless — our DLL still loaded — but it spammed user logs.
        // The blocks below pick up any leftover .json copies and rename them, also covering the
        // pre-v0.11.6 hardcoded-path case (mods/Sts2SkinManager/modpack_preset.json) for users
        // whose DLL lives in a non-standard subdirectory. Safe to delete after a few releases.
        var legacyJsonCandidates = new[]
        {
            Path.Combine(selfDir, "modpack_preset.json"),
            Path.Combine(modsDir, ModId, "modpack_preset.json"),
        };
        foreach (var oldJson in legacyJsonCandidates)
        {
            if (!File.Exists(oldJson)) continue;
            if (string.Equals(oldJson, presetPath, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (!File.Exists(presetPath)) File.Copy(oldJson, presetPath);
                File.Delete(oldJson);
                Logger.Info($"migrated legacy preset {oldJson} → {presetPath}");
            }
            catch (Exception ex) { Logger.Warn($"preset migrate failed ({oldJson}): {ex.Message}"); }
        }
        // ...to here. [/NOTE]
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

        // Build the unified All Mods list. Spans every mod SkinManager is aware of: character
        // variants, card skins, mixed mods, user-skipped DLL mods, and pending DLL+pck mods.
        // Excludes SkinManager itself, BaseLib, Sts2* sister mods, and custom-character mods.
        var customCharacterIdsForPanel = skippedCustom.Select(s => s.ModId).ToList();
        var allMods = UnifiedModBuilder.Build(modsDir, detected, choices, customCharacterIdsForPanel, baseCharacters);
        if (allMods.Count > 0)
        {
            Logger.Info($"all mods: {allMods.Count} mod(s) tracked:");
            foreach (var m in allMods)
            {
                var categoryLabel = m.Category switch
                {
                    Discovery.UnifiedModCategory.CharacterSkin => $"char→{m.Character ?? "?"}",
                    Discovery.UnifiedModCategory.CardSkin => "card",
                    Discovery.UnifiedModCategory.Mixed => $"mixed→{m.Character ?? "?"}",
                    Discovery.UnifiedModCategory.EventArt => "events",
                    Discovery.UnifiedModCategory.NotManaged => "skipped",
                    _ => "pending",
                };
                var entityTag = m.DefinesContentEntities ? " [content-mod]" : "";
                Logger.Info($"  [all-mods] {m.ModId} → {categoryLabel}{entityTag}");
            }
        }

        // Read current mod_list enabled state so the "Other Mods" tab checkbox reflects the
        // game's actual per-mod is_enabled. Falls back to empty (defaults to true per row) if
        // settings.save is missing.
        var bootModEnabled = settings != null
            ? Sts2SettingsWriter.ReadModEnabledState(settings)
            : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        SkinSelectorOverlay.Configure(choicesPath, byCharacter, cardMods, mixedMods, allMods, baseCharacters, bootModEnabled);
        SkinSelectorOverlay.SetWatcher(_watcher);

        // Defer ModConfig registration so the framework's own Initialize can run first.
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.CreateTimer(0.0).Timeout += ModConfigBridge.TryRegister;

            // After all other mods finish their Harmony patches, sweep for DLL-driven character
            // skins (Hcxmmx_King_Skin pattern) — mods whose pck has no standard skin paths but
            // whose DLL patches CharacterModel / NCharacterSelectButton / spine types. Auto-suggests
            // a base character via byte-frequency, writes assignments to skin_choices.json, then
            // shows a single restart modal so v0.7.0 DLL block can take effect on next boot.
            var alreadyDetectedIds = detected.Select(d => d.ModId).ToList();
            // Pck-scanner found `animations/characters/{non_base}/` for these — they're custom-
            // character mods. Their DLLs may patch CharacterModel (abstract) for the new
            // character's spine setup, and may reference base-game audio/asset paths (e.g. STS1
            // ports that reuse Necrobinder SFX events). The byte-frequency suggester would
            // mis-attribute those references to a base character and auto-assign the mod as
            // that character's skin — which then strands the custom character via DLL-block
            // whenever the user picks "default" or another skin. Pass them through so the
            // detection service short-circuits before suggesting.
            var customCharacterIds = skippedCustom.Select(s => s.ModId).ToList();
            DllSkinDetectionService.ScheduleAfter(tree, modsDir, choicesPath, managerDataDir, baseCharacters, alreadyDetectedIds, customCharacterIds);
        }
    }

    private static ChoicesFileWatcher? _watcher;
}
