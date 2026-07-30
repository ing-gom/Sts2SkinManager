using System;
using Sts2SkinManager.Runtime;
using MkBridge = Sts2.ModKit.Config.ModConfigBridge;

namespace Sts2SkinManager;

/// <summary>
/// In-game settings registration, delegated to the shared <see cref="Sts2.ModKit.Config.ModConfigBridge"/>.
/// That bridge integrates with EITHER the RitsuLib or the ModConfig framework (RitsuLib preferred),
/// and no-ops with defaults when neither is installed — so this mod keeps zero hard dependency on
/// either. Exposes a single dropdown for the character-select overlay anchor (Top Left / Top Right).
/// The local class name is kept so <c>MainFile</c>'s call site is unchanged.
/// </summary>
internal static class ModConfigBridge
{
    private const string EntryKey = "overlayAnchor";
    private static readonly string[] AnchorOptions = { "Top Left", "Top Right" };
    private const string AnchorDefault = "Top Right";

    private static bool _attempted;

    public static void TryRegister()
    {
        if (_attempted) return;
        _attempted = true;

        MkBridge.For(MainFile.ModId, "Skin Manager", MainFile.Logger)
            .Dropdown(EntryKey, "Overlay position (character select)", defaultValue: AnchorDefault, options: AnchorOptions,
                onChanged: v => SkinSelectorOverlay.SetAnchor(v))
                .Description("Where to dock the skin manager overlay on the character select screen. Change applies immediately.")
            .Register();

        // Apply the persisted value so the live overlay honors the saved setting on first attach.
        var saved = MkBridge.GetValue<string>(MainFile.ModId, EntryKey, AnchorDefault);
        if (!string.IsNullOrEmpty(saved)) SkinSelectorOverlay.SetAnchor(saved);
    }
}
