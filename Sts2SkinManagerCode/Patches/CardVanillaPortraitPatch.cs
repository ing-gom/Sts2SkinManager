using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace Sts2SkinManager.Patches;

// Forces base-game ("vanilla") card art for the characters the user flagged "Vanilla cards" in the
// mixed panel — even when a card-art framework (RitsuLib/ATA) or a direct-override skin is supplying
// the art.
//
// Why a Harmony patch and NOT a pck overlay:
//   Frameworks like RitsuLib intercept CardModel.PortraitPath with their OWN Harmony prefix and
//   redirect the portrait to their registered CharacterAssetProfile. That resolution happens ABOVE
//   the resource filesystem, so mounting a base-game overlay pck can never win — the game never even
//   loads the path we overrode. To revert reliably we must intervene at the SAME layer: a
//   higher-priority prefix on the same getter that recomputes the stock path and short-circuits
//   (returns false), which skips both the framework's prefix and the original getter.
//
// The stock path formula mirrors CardModel.PortraitPath / BetaPortraitPath exactly:
//   res://images/atlases/card_atlas.sprites/{pool}/[beta/]{entry}.tres
// Because CardModel.Portrait loads that path via ResourceLoader, forcing the path is enough to show
// stock art; no pixel copying required. Keyed on Pool.Title (== the character/pool folder in the
// path), so it also covers card-reward/library previews that have no Owner.
[HarmonyPatch]
public static class CardVanillaPortraitPatch
{
    // Lowercased card-pool titles (== character entries, e.g. "ironclad") to force to vanilla art.
    // Replaced wholesale once at boot from choices.VanillaCardsMods → each flagged mixed mod's
    // characters. volatile so the boot-time write is visible to the getter threads without locking.
    private static volatile HashSet<string> _vanillaPools = new(StringComparer.OrdinalIgnoreCase);

    // "{pool}/{entry}" keys (e.g. "ironclad/break") for cards of an active, non-reverted mixed mod
    // that ships a direct packed-portrait override. For these we redirect the card face from the
    // atlas sprite to the packed portrait png, so the mod's ctex byte-override wins (its card-atlas
    // ".tres.remap" does not, because Godot ignores late-mounted .remap files — see MainFile wiring).
    private static volatile HashSet<string> _modCardKeys = new(StringComparer.OrdinalIgnoreCase);

    public static void SetVanillaPools(IEnumerable<string> pools) =>
        _vanillaPools = new HashSet<string>(pools, StringComparer.OrdinalIgnoreCase);

    public static void SetModCardKeys(IEnumerable<string> keys) =>
        _modCardKeys = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);

    // subfolder is "" for the normal portrait, "beta/" for the beta portrait — matching the getters.
    private static bool Resolve(CardModel card, string subfolder, bool beta, ref string result)
    {
        string? pool = card?.Pool?.Title?.ToLowerInvariant();
        if (pool == null) return true;
        string entry = card!.Id.Entry.ToLowerInvariant();

        // 1. Vanilla revert (highest intent): force the stock atlas path, beating any framework.
        if (_vanillaPools.Contains(pool))
        {
            result = $"res://images/atlases/card_atlas.sprites/{pool}/{subfolder}{entry}.tres";
            return false;
        }

        // 2. Surface a direct-override mixed mod's card (raye-style) whose atlas .remap Godot ignores:
        //    point the card face at the packed portrait png, where the mod's ctex override does win.
        //    Normal portrait only — beta cards keep their atlas path.
        if (!beta && _modCardKeys.Contains($"{pool}/{entry}"))
        {
            result = $"res://images/packed/card_portraits/{pool}/{entry}.png";
            return false;
        }

        return true; // not ours → run the framework prefix + original getter unchanged
    }

    [HarmonyPatch(typeof(CardModel), "PortraitPath", MethodType.Getter)]
    [HarmonyPriority(Priority.First)]
    [HarmonyPrefix]
    public static bool PortraitPath(CardModel __instance, ref string __result) =>
        Resolve(__instance, "", false, ref __result);

    [HarmonyPatch(typeof(CardModel), "BetaPortraitPath", MethodType.Getter)]
    [HarmonyPriority(Priority.First)]
    [HarmonyPrefix]
    public static bool BetaPortraitPath(CardModel __instance, ref string __result) =>
        Resolve(__instance, "beta/", true, ref __result);
}
