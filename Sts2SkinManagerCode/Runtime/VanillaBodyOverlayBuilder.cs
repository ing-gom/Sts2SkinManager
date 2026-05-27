using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Sts2SkinManager.Discovery;

namespace Sts2SkinManager.Runtime;

// Builds a non-destructive "vanilla body" overlay for a mixed (skin + cards) mod such as
// ATA_IronClad. Those mods' DLLs rewrite the character's scene/image getters into their own
// namespace (res://{Mod}/scenes/creature_visuals/{char}.tscn, .../char_select_bg_{char}.tscn, the
// top-panel icon + multiplayer-hand pngs, ...). This overlay re-points exactly those namespace
// paths back to the vanilla base-game assets and leaves card art (images/atlases/*cards*,
// card_portraits) alone. Mounting it AFTER the mod's own pck therefore yields a vanilla-looking
// character while the mod's DLL keeps injecting its custom card art.
//
// Two redirect kinds (proven in-game via tools/_make_vanillabody_v3.py):
//   * SCENE  X.tscn.remap  -> rewritten to point at the vanilla "res://.../X.tscn"
//   * IMAGE  X.png.import  -> replaced with the vanilla .import (which targets the vanilla ctex
//                            already present in the base game pck)
public static class VanillaBodyOverlayBuilder
{
    // Character SCENE remap folders to redirect. Card scenes (images/atlases/*) are NOT here.
    private static readonly string[] CharSceneFolders =
    {
        "/scenes/creature_visuals/",
        "/scenes/merchant/characters/",
        "/scenes/rest_site/characters/",
        "/scenes/ui/character_icons/",
        "/scenes/character_select/",
    };

    // Character IMAGE import markers to redirect.
    private static readonly string[] CharImageMarkers =
    {
        "/images/ui/top_panel/character_icon_",
        "/images/packed/character_select/char_select_",
        "/images/ui/hands/multiplayer_hand_",
    };

    // Returns the written overlay pck path, or null if there is nothing to redirect.
    public static string? Build(string modPckPath, string baseGamePckPath, string outDir)
    {
        var modIdx = PckFileExtractor.TryReadIndex(modPckPath);
        if (modIdx == null) { MainFile.Logger.Warn($"vanilla-body: cannot read mod pck {modPckPath}"); return null; }
        var baseIdx = PckFileExtractor.TryReadIndex(baseGamePckPath);
        if (baseIdx == null) { MainFile.Logger.Warn("vanilla-body: cannot read base game pck"); return null; }

        // basename -> base entry path, for matching the vanilla counterpart regardless of folder
        // (mods place char_select under scenes/character_select/, base under scenes/screens/char_select/).
        var baseByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in baseIdx.Keys)
        {
            var name = p.Substring(p.LastIndexOf('/') + 1);
            if (!baseByName.ContainsKey(name)) baseByName[name] = p;
        }

        var overlay = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var modPath in modIdx.Keys)
        {
            if (IsCardDomain(modPath)) continue;

            if (modPath.EndsWith(".tscn.remap", StringComparison.OrdinalIgnoreCase)
                && CharSceneFolders.Any(f => modPath.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                var sceneName = BaseName(modPath);                            // X.tscn.remap
                sceneName = sceneName.Substring(0, sceneName.Length - ".remap".Length); // X.tscn
                if (baseByName.TryGetValue(sceneName, out var basePath))
                {
                    var content = $"[remap]\n\npath=\"res://{basePath}\"\n";
                    overlay[modPath] = Encoding.UTF8.GetBytes(content);
                }
                continue;
            }

            if (modPath.EndsWith(".png.import", StringComparison.OrdinalIgnoreCase)
                && CharImageMarkers.Any(m => modPath.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                var impName = BaseName(modPath);                              // X.png.import
                if (baseByName.TryGetValue(impName, out var basePath))
                {
                    var bytes = PckFileExtractor.TryRead(baseGamePckPath, baseIdx[basePath]);
                    if (bytes != null) overlay[modPath] = bytes;
                }
            }
        }

        if (overlay.Count == 0)
        {
            MainFile.Logger.Info($"vanilla-body: no character paths found in {Path.GetFileName(modPckPath)}");
            return null;
        }

        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(modPckPath) + "_vanillabody.pck");
        PckFileWriter.Write(outPath, overlay);
        MainFile.Logger.Info($"vanilla-body overlay: {Path.GetFileName(outPath)} ({overlay.Count} redirects from {Path.GetFileName(modPckPath)})");
        return outPath;
    }

    // True if this mod's pck has any character body path the overlay can revert (a scene remap under
    // a character folder, or a character image import). Used to hide the "Selected look / Mod look"
    // toggle for mixed mods that bundle no revertible body (e.g. AncientWaifus) — the toggle would be
    // a no-op there. Index-only read (cheap); the overlay's targets are real packed entries.
    public static bool HasRevertibleBody(string modPckPath)
    {
        var idx = PckFileExtractor.TryReadIndex(modPckPath);
        if (idx == null) return false;
        foreach (var p in idx.Keys)
        {
            if (IsCardDomain(p)) continue;
            if (p.EndsWith(".tscn.remap", StringComparison.OrdinalIgnoreCase)
                && CharSceneFolders.Any(f => p.Contains(f, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (p.EndsWith(".png.import", StringComparison.OrdinalIgnoreCase)
                && CharImageMarkers.Any(m => p.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static string BaseName(string p) => p.Substring(p.LastIndexOf('/') + 1);

    private static bool IsCardDomain(string p)
    {
        var pl = p.ToLowerInvariant();
        return pl.Contains("lance_cards") || pl.Contains("card_art") || pl.Contains("card_portrait")
               || (pl.Contains("/atlases/") && pl.Contains("card"));
    }
}
