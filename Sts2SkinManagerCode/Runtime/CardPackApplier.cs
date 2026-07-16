using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Modding;
using Sts2SkinManager.Config;
using Sts2SkinManager.Discovery;

namespace Sts2SkinManager.Runtime;

public static class CardPackApplier
{
    public static bool ApplyToSettings(Sts2SettingsFile settings, CardPacksConfig packs, List<DetectedSkinMod> detectedCardMods)
    {
        var modList = settings.Root["mod_settings"]?["mod_list"]?.AsArray();
        if (modList == null) return false;

        var changed = false;
        var byModId = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<JsonNode>();
        foreach (var entry in modList)
        {
            if (entry == null) continue;
            entries.Add(entry);
            var id = entry["id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id)) byModId[id] = entry;
        }

        foreach (var packModId in packs.Enabled.Keys)
        {
            if (!byModId.TryGetValue(packModId, out var entry)) continue;
            var want = packs.Enabled[packModId];
            var current = entry["is_enabled"]?.GetValue<bool>() ?? true;
            if (current != want)
            {
                entry["is_enabled"] = want;
                changed = true;
            }
        }

        var orderedIds = packs.Ordering;
        var orderedSet = new HashSet<string>(orderedIds, StringComparer.OrdinalIgnoreCase);
        var nonCardEntries = entries.Where(e => !orderedSet.Contains(e["id"]?.GetValue<string>() ?? "")).ToList();
        // Top of UI list = highest priority = mounted LAST = goes to END of mod_list.
        // mod_list iterates in order, so reverse the UI ordering when writing.
        //
        // A pack the game has never written into mod_list (freshly installed — the game only
        // rebuilds mod_list at the END of ModManager.Initialize, one boot behind) must be
        // MATERIALIZED here, not skipped. SortModList scores anything absent from mod_list at
        // 999999999, which sorts it past every listed mod → mounts last → wins every conflict.
        // Skipping it therefore inverted the panel: SyncCardPacks appends new packs at the UI
        // bottom labelled lowest priority, while they silently outranked everything on disk.
        var orderedCardEntries = orderedIds
            .AsEnumerable()
            .Reverse()
            .Select(id => byModId.TryGetValue(id, out var existing) ? existing : CreateModListEntry(id, packs, detectedCardMods))
            .Where(e => e != null)
            .Select(e => e!)
            .ToList();

        var newList = new List<JsonNode>(nonCardEntries);
        newList.AddRange(orderedCardEntries);

        var orderDiffers = false;
        if (newList.Count != entries.Count) orderDiffers = true;
        else
        {
            for (var i = 0; i < newList.Count; i++)
            {
                if (!ReferenceEquals(newList[i], entries[i])) { orderDiffers = true; break; }
            }
        }

        if (orderDiffers)
        {
            for (var i = modList.Count - 1; i >= 0; i--) modList.RemoveAt(i);
            foreach (var n in newList)
            {
                var serialized = n.ToJsonString();
                var clone = JsonNode.Parse(serialized);
                if (clone != null) modList.Add(clone);
            }
            changed = true;
        }

        return changed;
    }

    // Builds the mod_list entry the game itself would have written for a pack it hasn't listed yet:
    // {id, source, is_enabled}. Returns null for an id with no installed pck — SyncCardPacks already
    // drops those from Ordering, so reaching here means there's nothing real to order.
    //
    // `source` is load-bearing, not cosmetic: ModSettings.IsModDisabled matches on
    // (Id, Source, IsEnabled), so an entry carrying the wrong source makes the row's checkbox
    // silently do nothing. Take it from ModManager.Mods, which is authoritative for this session.
    private static JsonNode? CreateModListEntry(string modId, CardPacksConfig packs, List<DetectedSkinMod> detectedCardMods)
    {
        var detected = detectedCardMods.FirstOrDefault(m => string.Equals(m.ModId, modId, StringComparison.OrdinalIgnoreCase));
        if (detected == null) return null;

        return new JsonObject
        {
            ["id"] = modId,
            ["source"] = ResolveModSource(modId, detected),
            ["is_enabled"] = packs.Enabled.TryGetValue(modId, out var want) ? want : true,
        };
    }

    private static string ResolveModSource(string modId, DetectedSkinMod detected)
    {
        try
        {
            foreach (var mod in ModManager.Mods)
            {
                if (mod?.manifest?.id == null) continue;
                if (!string.Equals(mod.manifest.id, modId, StringComparison.OrdinalIgnoreCase)) continue;
                return mod.modSource == ModSource.SteamWorkshop ? "steam_workshop" : "mods_directory";
            }
        }
        catch { }
        // Mod isn't loaded this session (or ModManager isn't up yet): fall back to where the pck sits.
        return detected.PckPath.Replace('\\', '/').Contains("/workshop/content/", StringComparison.OrdinalIgnoreCase)
            ? "steam_workshop"
            : "mods_directory";
    }

    public static bool ApplyToMemoryModList(CardPacksConfig packs)
    {
        var settings = ModManager._settings;
        if (settings == null) return false;
        var changed = false;
        foreach (var entry in settings.ModList)
        {
            if (string.IsNullOrEmpty(entry.Id)) continue;
            if (packs.Enabled.TryGetValue(entry.Id, out var want) && entry.IsEnabled != want)
            {
                entry.IsEnabled = want;
                changed = true;
            }
        }
        return changed;
    }
}
