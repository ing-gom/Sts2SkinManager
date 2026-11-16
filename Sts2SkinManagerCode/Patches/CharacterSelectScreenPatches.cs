using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using Sts2SkinManager.Runtime;

namespace Sts2SkinManager.Patches;

[HarmonyPatch(typeof(NCharacterSelectScreen), "_Ready")]
public static class CharacterSelectScreenReadyPatch
{
    public static void Postfix(NCharacterSelectScreen __instance)
    {
        try
        {
            SkinSelectorOverlay.Attach(__instance);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"_Ready postfix error: {ex.Message}");
        }
    }
}

// Bind to the button, not the model: v0.113 dropped the `CharacterModel characterModel` parameter
// (SelectCharacter(btn, model) → SelectCharacter(btn)). Harmony resolves postfix args by NAME, so
// asking for `characterModel` threw inside PatchAll on the new branch and took the whole mod down.
// Every build passes the button's own `Character` as the model, so reading it here is equivalent.
[HarmonyPatch(typeof(NCharacterSelectScreen), "SelectCharacter")]
public static class CharacterSelectScreenSelectPatch
{
    public static void Postfix(NCharacterSelectButton charSelectButton)
    {
        try
        {
            CharacterModel? characterModel = charSelectButton?.Character;
            var id = characterModel?.Id?.Entry;
            if (string.IsNullOrEmpty(id)) return;
            SkinSelectorOverlay.OnCharacterSelected(id);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"SelectCharacter postfix error: {ex.Message}");
        }
    }
}
