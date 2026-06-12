using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sts2SkinManager.Discovery;

// Detects ClymandSaru-framework skin mods and resolves the base character they reskin.
//
// These mods (e.g. ClymandSaru_jasmine_q → Ironclad, ClymandSaru_zhubao_q → Defect) reskin an
// EXISTING character but do NOT ship the STS2-standard `animations/characters/{char}/` spine
// paths in their pck. Instead the pck carries flat `res://img/...` / `res://scenes/...` assets and
// a sibling `{pckId}_config.cfg` whose `template_replacements` map names the base character whose
// template the mod's DLL overwrites at runtime (via a `CharacterModel.CreateVisuals` patch):
//
//   { "template_replacements": { "ironclad": true, "silent": false, "defect": false, ... }, ... }
//
// Two failure modes this reader fixes:
//   1. The pck-path scanner (AssetDomainCatalog) sees 0 base characters, so without this reader
//      the mod falls through every classification branch and is invisible to Skin Manager — no
//      dropdown variant, no DLL-block coordination with other skins for the same character.
//   2. CharacterIdSuggester's byte-frequency heuristic mis-fires: every ClymandSaru DLL embeds the
//      literal "ironclad" (the framework's default template) regardless of the real target, so a
//      Defect skin (zhubao_q) would be wrongly suggested as Ironclad and DLL-blocked under the
//      wrong character. The `template_replacements` map is the author's explicit declaration, so
//      it is authoritative and overrides the heuristic.
//
// A ClymandSaru mod whose `template_replacements` has no `true` entry (e.g. reven_q reskins only a
// `byrdpip` creature scene via `custom_scene_replacements`, not a player character) is NOT a base-
// character skin — ResolveTargetCharacter returns null and the mod is left to the game's normal
// auto-mount.
public static class ClymandSaruSkinReader
{
    // STS2 mod config files use JSON; allow trailing commas / comments defensively since these
    // are hand-authored by the mod author.
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // True when `{pckId}_config.cfg` exists in modDir and declares a `template_replacements` block.
    // The paired filename + that key together are specific to the ClymandSaru framework — standard
    // pck/DLL skins ship no such config.
    public static bool IsClymandSaruSkin(string modDir, string pckId)
        => TryLoadTemplateReplacements(modDir, pckId, out _);

    // Returns the lowercased base character id this mod reskins (the first `template_replacements`
    // entry set to true), or null if the folder isn't a ClymandSaru skin or declares no character
    // target.
    public static string? ResolveTargetCharacter(string modDir, string pckId)
    {
        if (!TryLoadTemplateReplacements(modDir, pckId, out var replacements)) return null;
        foreach (var kv in replacements)
        {
            if (kv.Value is JsonValue v && v.TryGetValue<bool>(out var on) && on)
                return kv.Key.ToLowerInvariant();
        }
        return null;
    }

    private static bool TryLoadTemplateReplacements(string modDir, string pckId, out JsonObject replacements)
    {
        replacements = new JsonObject();
        if (string.IsNullOrEmpty(modDir) || string.IsNullOrEmpty(pckId)) return false;

        var cfgPath = Path.Combine(modDir, pckId + "_config.cfg");
        if (!File.Exists(cfgPath)) return false;

        string text;
        try { text = File.ReadAllText(cfgPath); }
        catch { return false; }

        JsonNode? root;
        try { root = JsonNode.Parse(text, documentOptions: DocOptions); }
        catch { return false; }

        if (root is not JsonObject obj) return false;
        if (obj["template_replacements"] is not JsonObject tr) return false;

        replacements = tr;
        return true;
    }
}
