using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sts2SkinManager.Config;

public class CharacterSkinChoice
{
    [JsonPropertyName("active")]
    public string Active { get; set; } = "default";

    [JsonPropertyName("available_variants")]
    public List<string> AvailableVariants { get; set; } = new();
}

public class CardPacksConfig
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("ordering")]
    public List<string> Ordering { get; set; } = new();

    [JsonPropertyName("enabled")]
    public Dictionary<string, bool> Enabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class SkinChoicesConfig
{
    public Dictionary<string, CharacterSkinChoice> Characters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public CardPacksConfig CardPacks { get; set; } = new();

    // Mixed mods (character spine + card art bundled in one .pck, e.g. AncientWaifus). They can be
    // toggled independently of the main-spine dropdown selection. Same schema as CardPacksConfig:
    // ordering[0] = highest priority (mounted last, wins overlapping paths). Mixed mods are still
    // overridden by whichever Character variant the dropdown ultimately mounts.
    public CardPacksConfig MixedAddons { get; set; } = new();

    // modId → user-chosen alias. modId stays the unique key; alias is display-only.
    // Stale entries (mods no longer detected) get pruned in SyncAliases.
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // DLL-driven character skins (Hcxmmx_King_Skin pattern). modId → base character id.
    // SkinModScanner injects these as Character variants even though their pck has no
    // animations/characters/{base}/ paths. Populated by HarmonyPatchInspector + user confirmation.
    public Dictionary<string, string> DllSkinAssignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Mods the user explicitly marked as "not a character skin" — silenced from future detection
    // prompts so the modal doesn't re-pop on every boot.
    public HashSet<string> DllSkinSkipped { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Mixed mods (skin + cards in one DLL/pck, e.g. ATA_IronClad) the user set to "vanilla body,
    // keep cards". For these, MainFile keeps the mod's DLL loaded (so its custom card art stays)
    // and mounts a generated overlay (VanillaBodyOverlayBuilder) that re-points the mod's character
    // scene/image paths to vanilla base assets. Persisted as _vanilla_body_mods.
    public HashSet<string> VanillaBodyMods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // When set (by MainFile at boot), every Save() also writes the same JSON to this path —
    // letting a curator zip their `mods/` folder and have their preset travel with it. The
    // receiving end auto-imports from this same path when user_data has no choices yet.
    // Mirror failures are logged but never propagate; preset write must not block user actions.
    public static string? PresetMirrorPath { get; set; }

    public static SkinChoicesConfig LoadOrEmpty(string path)
    {
        if (!File.Exists(path)) return new SkinChoicesConfig();
        try
        {
            var json = File.ReadAllText(path);
            if (JsonNode.Parse(json) is not JsonObject root) return new SkinChoicesConfig();

            var cfg = new SkinChoicesConfig();

            if (root.TryGetPropertyValue("_card_packs", out var cardPacksNode) && cardPacksNode != null)
            {
                cfg.CardPacks = JsonSerializer.Deserialize<CardPacksConfig>(cardPacksNode.ToJsonString(), JsonOpts) ?? new CardPacksConfig();
                root.Remove("_card_packs");

                if (cfg.CardPacks.Schema < 2)
                {
                    cfg.CardPacks.Ordering.Reverse();
                    cfg.CardPacks.Schema = 2;
                }
            }
            else
            {
                cfg.CardPacks.Schema = 2;
            }

            if (root.TryGetPropertyValue("_mixed_addons", out var mixedNode) && mixedNode != null)
            {
                cfg.MixedAddons = JsonSerializer.Deserialize<CardPacksConfig>(mixedNode.ToJsonString(), JsonOpts) ?? new CardPacksConfig();
                cfg.MixedAddons.Schema = 2; // Mixed addons are introduced post-migration; always treat ordering as top-wins.
                root.Remove("_mixed_addons");
            }

            if (root.TryGetPropertyValue("_aliases", out var aliasesNode) && aliasesNode != null)
            {
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(aliasesNode.ToJsonString(), JsonOpts);
                if (deserialized != null) cfg.Aliases = new Dictionary<string, string>(deserialized, StringComparer.OrdinalIgnoreCase);
                root.Remove("_aliases");
            }

            if (root.TryGetPropertyValue("_dll_skin_assignments", out var dllAssignNode) && dllAssignNode != null)
            {
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(dllAssignNode.ToJsonString(), JsonOpts);
                if (deserialized != null) cfg.DllSkinAssignments = new Dictionary<string, string>(deserialized, StringComparer.OrdinalIgnoreCase);
                root.Remove("_dll_skin_assignments");
            }

            if (root.TryGetPropertyValue("_dll_skin_skipped", out var dllSkipNode) && dllSkipNode != null)
            {
                var deserialized = JsonSerializer.Deserialize<List<string>>(dllSkipNode.ToJsonString(), JsonOpts);
                if (deserialized != null) cfg.DllSkinSkipped = new HashSet<string>(deserialized, StringComparer.OrdinalIgnoreCase);
                root.Remove("_dll_skin_skipped");
            }

            if (root.TryGetPropertyValue("_vanilla_body_mods", out var vbNode) && vbNode != null)
            {
                var deserialized = JsonSerializer.Deserialize<List<string>>(vbNode.ToJsonString(), JsonOpts);
                if (deserialized != null) cfg.VanillaBodyMods = new HashSet<string>(deserialized, StringComparer.OrdinalIgnoreCase);
                root.Remove("_vanilla_body_mods");
            }

            root.Remove("_preview_visible"); // legacy v0.4.0-dev key, ignored

            cfg.Characters = JsonSerializer.Deserialize<Dictionary<string, CharacterSkinChoice>>(root.ToJsonString(), JsonOpts) ?? new(StringComparer.OrdinalIgnoreCase);
            return cfg;
        }
        catch
        {
            return new SkinChoicesConfig();
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var root = new JsonObject();
        foreach (var (key, value) in Characters)
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(value, JsonOpts));
            if (node != null) root[key] = node;
        }
        var cardPacksNode = JsonNode.Parse(JsonSerializer.Serialize(CardPacks, JsonOpts));
        if (cardPacksNode != null) root["_card_packs"] = cardPacksNode;

        if (MixedAddons.Ordering.Count > 0 || MixedAddons.Enabled.Count > 0)
        {
            var mixedNode = JsonNode.Parse(JsonSerializer.Serialize(MixedAddons, JsonOpts));
            if (mixedNode != null) root["_mixed_addons"] = mixedNode;
        }

        if (Aliases.Count > 0)
        {
            var aliasNode = JsonNode.Parse(JsonSerializer.Serialize(Aliases, JsonOpts));
            if (aliasNode != null) root["_aliases"] = aliasNode;
        }

        if (DllSkinAssignments.Count > 0)
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(DllSkinAssignments, JsonOpts));
            if (node != null) root["_dll_skin_assignments"] = node;
        }

        if (DllSkinSkipped.Count > 0)
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(DllSkinSkipped.ToList(), JsonOpts));
            if (node != null) root["_dll_skin_skipped"] = node;
        }

        if (VanillaBodyMods.Count > 0)
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(VanillaBodyMods.ToList(), JsonOpts));
            if (node != null) root["_vanilla_body_mods"] = node;
        }

        var json = root.ToJsonString(JsonOpts);
        File.WriteAllText(path, json);

        var mirror = PresetMirrorPath;
        if (!string.IsNullOrEmpty(mirror) && !string.Equals(mirror, path, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var mirrorDir = Path.GetDirectoryName(mirror);
                if (!string.IsNullOrEmpty(mirrorDir)) Directory.CreateDirectory(mirrorDir);
                File.WriteAllText(mirror, json);
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"preset mirror write failed ({mirror}): {ex.Message}");
            }
        }
    }

    // Same algorithm as SyncCardPacks but operates on MixedAddons.
    public void SyncMixedAddons(IEnumerable<string> detectedModIds)
    {
        var detected = detectedModIds.ToList();
        var detectedSet = new HashSet<string>(detected, StringComparer.OrdinalIgnoreCase);

        var newOrdering = new List<string>();
        foreach (var modId in MixedAddons.Ordering)
        {
            if (detectedSet.Contains(modId) && !newOrdering.Contains(modId, StringComparer.OrdinalIgnoreCase))
                newOrdering.Add(modId);
        }
        foreach (var modId in detected.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            if (!newOrdering.Contains(modId, StringComparer.OrdinalIgnoreCase)) newOrdering.Add(modId);
        }
        MixedAddons.Ordering = newOrdering;

        // Default mixed addons OFF — they're additive on top of the dropdown choice, so the user opts in.
        foreach (var modId in detected)
        {
            if (!MixedAddons.Enabled.ContainsKey(modId)) MixedAddons.Enabled[modId] = false;
        }
        var stale = MixedAddons.Enabled.Keys.Where(k => !detectedSet.Contains(k)).ToList();
        foreach (var s in stale) MixedAddons.Enabled.Remove(s);
    }

    // Drops alias entries whose modId is no longer present in the detected set.
    public void SyncAliases(IEnumerable<string> detectedModIds)
    {
        var keep = new HashSet<string>(detectedModIds, StringComparer.OrdinalIgnoreCase);
        var stale = Aliases.Keys.Where(k => !keep.Contains(k)).ToList();
        foreach (var k in stale) Aliases.Remove(k);
    }

    public void SyncAvailableVariants(string character, IEnumerable<string> variants)
    {
        if (!Characters.TryGetValue(character, out var choice))
        {
            choice = new CharacterSkinChoice { Active = "default" };
            Characters[character] = choice;
        }
        var list = new List<string> { "default" };
        list.AddRange(variants.Where(v => v != "default").OrderBy(v => v));
        choice.AvailableVariants = list.Distinct().ToList();
        if (!choice.AvailableVariants.Contains(choice.Active))
        {
            choice.Active = "default";
        }
    }

    public void SyncCardPacks(IEnumerable<string> detectedModIds)
    {
        var detected = detectedModIds.ToList();
        var detectedSet = new HashSet<string>(detected, StringComparer.OrdinalIgnoreCase);

        var newOrdering = new List<string>();
        foreach (var modId in CardPacks.Ordering)
        {
            if (detectedSet.Contains(modId) && !newOrdering.Contains(modId, StringComparer.OrdinalIgnoreCase))
                newOrdering.Add(modId);
        }
        foreach (var modId in detected.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            if (!newOrdering.Contains(modId, StringComparer.OrdinalIgnoreCase)) newOrdering.Add(modId);
        }
        CardPacks.Ordering = newOrdering;

        foreach (var modId in detected)
        {
            if (!CardPacks.Enabled.ContainsKey(modId)) CardPacks.Enabled[modId] = true;
        }
        var stale = CardPacks.Enabled.Keys.Where(k => !detectedSet.Contains(k)).ToList();
        foreach (var s in stale) CardPacks.Enabled.Remove(s);
    }
}
