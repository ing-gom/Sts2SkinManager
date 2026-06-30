using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Modding;
using Sts2SkinManager.Config;

namespace Sts2SkinManager.Runtime;

// SkinManager must load BEFORE the skin/character mods it manages, so its TryLoadMod /
// LoadResourcePack prefixes are live before those mods load and mount. It does NOT need to be
// absolute-first in the list — only ahead of its targets.
//
// Targeting "just before my managed skin mods" (instead of index 0) is what keeps us out of a
// reorder war with unrelated mods that also force themselves to the front: a core-patch or UI mod
// can sit at index 0 forever and we won't touch the list, because we're still ahead of every skin
// mod we care about. The game appends newly-installed mods to the END of the list (SortModList
// gives un-listed mods priority 999999999), so once we're ahead of the current skin mods, later
// additions land behind us automatically — no re-trigger.
public static class LoadOrderEnforcer
{
    // Pure check (no mutation): does the persisted settings mod_list have SkinManager sitting AT or
    // AFTER the earliest of its target mods? True = a reorder is needed. Used to drive the
    // anti-thrash streak counter before we decide whether to actually reorder.
    public static bool NeedsReorderInModList(Sts2SettingsFile settings, string modId, IReadOnlyCollection<string> targetIds)
    {
        var modList = settings.Root["mod_settings"]?["mod_list"]?.AsArray();
        if (modList == null) return false;
        var (selfIndex, minTarget) = LocateInModList(modList, modId, targetIds);
        if (selfIndex < 0 || minTarget == int.MaxValue) return false;
        return selfIndex > minTarget;
    }

    // Moves SkinManager to just before the earliest target in the persisted mod_list. Returns true
    // if the list was changed (i.e. a restart is warranted to pick up the new persisted order).
    public static bool EnsureBeforeTargetsInModList(Sts2SettingsFile settings, string modId, IReadOnlyCollection<string> targetIds)
    {
        var modList = settings.Root["mod_settings"]?["mod_list"]?.AsArray();
        if (modList == null) return false;
        var (selfIndex, minTarget) = LocateInModList(modList, modId, targetIds);
        if (selfIndex < 0 || minTarget == int.MaxValue) return false;
        if (selfIndex <= minTarget) return false; // already ahead of every target — nothing to do.

        var node = modList[selfIndex];
        if (node == null) return false;
        var clone = JsonNode.Parse(node.ToJsonString());
        // selfIndex > minTarget, so removing at selfIndex leaves minTarget's position unchanged;
        // inserting there drops us right in front of the earliest target.
        modList.RemoveAt(selfIndex);
        modList.Insert(minTarget, clone);
        return true;
    }

    // Ids of target mods that sit BEFORE SkinManager in the persisted mod_list — i.e. the mods
    // actually blocking us. When a load-order war can't be won, these are the ones to disable.
    public static List<string> TargetsAheadOfSelf(Sts2SettingsFile settings, string modId, IReadOnlyCollection<string> targetIds)
    {
        var result = new List<string>();
        var modList = settings.Root["mod_settings"]?["mod_list"]?.AsArray();
        if (modList == null) return result;
        var targetSet = new HashSet<string>(targetIds, StringComparer.OrdinalIgnoreCase);

        var selfIndex = -1;
        for (var i = 0; i < modList.Count; i++)
        {
            if (string.Equals(modList[i]?["id"]?.GetValue<string>(), modId, StringComparison.OrdinalIgnoreCase))
            {
                selfIndex = i;
                break;
            }
        }
        if (selfIndex < 0) return result;

        for (var i = 0; i < selfIndex; i++)
        {
            var id = modList[i]?["id"]?.GetValue<string>();
            if (id != null && targetSet.Contains(id)) result.Add(id);
        }
        return result;
    }

    private static (int selfIndex, int minTarget) LocateInModList(JsonArray modList, string modId, IReadOnlyCollection<string> targetIds)
    {
        var targetSet = new HashSet<string>(targetIds, StringComparer.OrdinalIgnoreCase);
        var selfIndex = -1;
        var minTarget = int.MaxValue;
        for (var i = 0; i < modList.Count; i++)
        {
            var id = modList[i]?["id"]?.GetValue<string>();
            if (id == null) continue;
            // Real mod_lists can contain DUPLICATE entries for the same id (observed in the wild:
            // SkinManager appearing at both index 0 and 59). Use the FIRST occurrence of each id as
            // its effective position, so a stray late duplicate of ourselves doesn't read as "behind
            // the targets" and trigger a needless reorder.
            if (string.Equals(id, modId, StringComparison.OrdinalIgnoreCase))
            {
                if (selfIndex < 0) selfIndex = i;
            }
            else if (targetSet.Contains(id) && i < minTarget) minTarget = i;
        }
        return (selfIndex, minTarget);
    }

    // In-memory equivalent over ModManager._mods. As noted in MainFile, this has no effect on the
    // CURRENT boot (the game already iterated _mods calling TryLoadMod in the old order before our
    // Initialize ran) — it just keeps the live list tidy and prevents a misleading "reordered"
    // warning once we're already settled ahead of our targets.
    public static bool EnsureBeforeTargetsInMods(string modId, IReadOnlyCollection<string> targetIds)
    {
        var oldMods = ModManager._mods;
        if (oldMods == null || oldMods.Count == 0) return false;
        var targetSet = new HashSet<string>(targetIds, StringComparer.OrdinalIgnoreCase);

        var selfIndex = -1;
        var minTarget = int.MaxValue;
        for (var i = 0; i < oldMods.Count; i++)
        {
            var id = oldMods[i]?.manifest?.id;
            if (id == null) continue;
            if (string.Equals(id, modId, StringComparison.OrdinalIgnoreCase))
            {
                if (selfIndex < 0) selfIndex = i;
            }
            else if (targetSet.Contains(id) && i < minTarget) minTarget = i;
        }
        if (selfIndex < 0 || minTarget == int.MaxValue) return false;
        if (selfIndex <= minTarget) return false;

        var ourMod = oldMods[selfIndex];
        var newMods = new List<Mod>(oldMods.Count);
        for (var i = 0; i < oldMods.Count; i++)
        {
            if (i == selfIndex) continue;
            if (i == minTarget) newMods.Add(ourMod); // minTarget < selfIndex, so reached first.
            newMods.Add(oldMods[i]);
        }
        ModManager._mods = newMods;
        return true;
    }
}
