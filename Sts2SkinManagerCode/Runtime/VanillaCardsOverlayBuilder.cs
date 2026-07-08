using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Sts2SkinManager.Discovery;

namespace Sts2SkinManager.Runtime;

// Mirror image of VanillaBodyOverlayBuilder for the OTHER axis of a mixed (skin + cards) mod such as
// raye: keep the mod's character body/spine but revert its CARD art back to the base game. Where the
// body builder redirects character scene/image paths and leaves card art alone, this one targets
// exactly the card-domain paths (card_portraits / card_art / lance_cards / card atlases) and reverts
// them to the vanilla base-game assets, leaving everything else (spine, char_select, icons, relics)
// untouched. Mounting it AFTER the mod's own pck therefore yields the mod's look with stock card art.
//
// IMPORTANT — why we override the target resource, not just the .import:
//   Godot names an imported texture `.godot/imported/<name>-<md5-of-res-path>.ctex`. That md5 is over
//   the RESOURCE PATH, not the file bytes, so a mod that reskins e.g.
//   images/packed/card_portraits/ironclad/break.png ships its new pixels at the SAME ctex path the
//   base game uses (…break.png-5c648….ctex) — it just overrides the bytes. Overlaying the mod's
//   `.png.import` with the base one is a no-op (both point at the same ctex path, whose bytes are
//   still the mod's). To actually revert we must overlay the CTEX file itself with the base bytes.
//   For that we read the mod's `.import`, follow its `path="res://…ctex"` target, and — when the base
//   game ships the same target path (true for direct-override mixed skins like raye) — overlay that
//   target with the base bytes.
//
// Card ATLAS sprites are `.tres.remap` files pointing at the mod's own exported `.res` (a
// content-hashed path the base game never has). The base game instead ships the plain `break.tres`.
// So for `.remap` entries we rewrite the remap to point back at the base source path — the exact
// mechanism VanillaBodyOverlayBuilder uses for character `.tscn.remap`s (proven in-game).
public static class VanillaCardsOverlayBuilder
{
    private static readonly Regex ResPathRx = new(@"res://([^\s""']+)", RegexOptions.Compiled);

    // Returns the written overlay pck path, or null if there is nothing to redirect.
    public static string? Build(string modPckPath, string baseGamePckPath, string outDir)
    {
        var modIdx = PckFileExtractor.TryReadIndex(modPckPath);
        if (modIdx == null) { MainFile.Logger.Warn($"vanilla-cards: cannot read mod pck {modPckPath}"); return null; }
        var baseIdx = PckFileExtractor.TryReadIndex(baseGamePckPath);
        if (baseIdx == null) { MainFile.Logger.Warn("vanilla-cards: cannot read base game pck"); return null; }

        var baseByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in baseIdx.Keys)
        {
            var name = p.Substring(p.LastIndexOf('/') + 1);
            if (!baseByName.ContainsKey(name)) baseByName[name] = p;
        }

        var overlay = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var modPath in modIdx.Keys)
        {
            if (!IsCardDomain(modPath)) continue;

            if (modPath.EndsWith(".remap", StringComparison.OrdinalIgnoreCase))
            {
                var inner = modPath.Substring(0, modPath.Length - ".remap".Length); // X.tres / X.tscn
                var basePath = ResolveBasePath(inner, baseIdx, baseByName);
                if (basePath != null)
                {
                    var content = $"[remap]\n\npath=\"res://{basePath}\"\n";
                    overlay[modPath] = Encoding.UTF8.GetBytes(content);
                }
                continue;
            }

            if (modPath.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
            {
                var importBytes = PckFileExtractor.TryRead(modPckPath, modIdx[modPath]);
                if (importBytes == null) continue;
                var content = Encoding.UTF8.GetString(importBytes);

                // Follow every res:// the .import references (the imported .ctex). Revert exactly the
                // ones the mod itself overrides AND the base game also ships at that path — i.e. the
                // reskinned texture bytes. Direct-override mixed skins (raye) share the base ctex path
                // so this reverts the portrait; namespace-remapped card mods won't match and are left
                // to the mod's DLL (best-effort — such a mixed mod simply can't be reverted this way).
                foreach (Match m in ResPathRx.Matches(content))
                {
                    var target = m.Groups[1].Value;
                    if (!modIdx.ContainsKey(target) || !baseIdx.ContainsKey(target)) continue;
                    if (overlay.ContainsKey(target)) continue;
                    var baseBytes = PckFileExtractor.TryRead(baseGamePckPath, baseIdx[target]);
                    if (baseBytes != null) overlay[target] = baseBytes;
                }

                // Also restore the .import metadata itself when the base ships the same path, so its
                // import parameters match the base ctex we just put back.
                if (baseIdx.TryGetValue(modPath, out var baseImportEntry) && !overlay.ContainsKey(modPath))
                {
                    var baseImport = PckFileExtractor.TryRead(baseGamePckPath, baseImportEntry);
                    if (baseImport != null) overlay[modPath] = baseImport;
                }
            }
        }

        if (overlay.Count == 0)
        {
            MainFile.Logger.Info($"vanilla-cards: no revertible card paths found in {Path.GetFileName(modPckPath)}");
            return null;
        }

        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(modPckPath) + "_vanillacards.pck");
        PckFileWriter.Write(outPath, overlay);
        MainFile.Logger.Info($"vanilla-cards overlay: {Path.GetFileName(outPath)} ({overlay.Count} entries from {Path.GetFileName(modPckPath)})");
        return outPath;
    }

    // True if this mod's pck has any card path the overlay can revert. Used to hide the
    // "Vanilla cards / Mod cards" toggle for mixed mods that bundle no card art. Index-only (cheap);
    // does not verify base-game resolvability (the overlay build is best-effort per path).
    public static bool HasRevertibleCards(string modPckPath)
    {
        var idx = PckFileExtractor.TryReadIndex(modPckPath);
        if (idx == null) return false;
        foreach (var p in idx.Keys)
        {
            if (!IsCardDomain(p)) continue;
            if (p.EndsWith(".remap", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // "{pool}/{entry}" keys (e.g. "ironclad/break") for cards this mod reskins via a direct packed-
    // portrait override — i.e. it ships images/packed/card_portraits/{pool}/{entry}.png(.import) whose
    // imported .ctex lives at the SAME path the base game uses (a direct byte-override, like the
    // character body). Such cards render reliably when the card's PortraitPath is redirected to that
    // packed png path, unlike the mod's card-atlas ".tres.remap" which Godot ignores for a
    // late-mounted pack. Beta portraits ({pool}/beta/{entry}) are skipped. Index-only (cheap).
    public static HashSet<string> CollectPackedPortraitCardKeys(string modPckPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var idx = PckFileExtractor.TryReadIndex(modPckPath);
        if (idx == null) return result;
        const string marker = "packed/card_portraits/";
        foreach (var p in idx.Keys)
        {
            var pl = p.Replace('\\', '/').ToLowerInvariant();
            var i = pl.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) continue;
            var rest = pl.Substring(i + marker.Length); // {pool}/{entry}.png[.import]
            if (rest.EndsWith(".import", StringComparison.Ordinal)) rest = rest.Substring(0, rest.Length - ".import".Length);
            if (!rest.EndsWith(".png", StringComparison.Ordinal)) continue;
            rest = rest.Substring(0, rest.Length - ".png".Length); // {pool}/{entry} (or {pool}/beta/{entry})
            if (rest.Split('/').Length != 2) continue;             // skip beta subfolder / malformed
            result.Add(rest);
        }
        return result;
    }

    // Full path first (direct base-path override — the common mixed-card case), then basename.
    private static string? ResolveBasePath(string wanted, Dictionary<string, PckEntry> baseIdx, Dictionary<string, string> baseByName)
    {
        if (baseIdx.ContainsKey(wanted)) return wanted;
        var name = wanted.Substring(wanted.LastIndexOf('/') + 1);
        return baseByName.TryGetValue(name, out var basePath) ? basePath : null;
    }

    // Same card-domain test the body builder uses to EXCLUDE cards — here it INCLUDES them.
    private static bool IsCardDomain(string p)
    {
        var pl = p.ToLowerInvariant();
        return pl.Contains("lance_cards") || pl.Contains("card_art") || pl.Contains("card_portrait")
               || (pl.Contains("/atlases/") && pl.Contains("card"));
    }
}
