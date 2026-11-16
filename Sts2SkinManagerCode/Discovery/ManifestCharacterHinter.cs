using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Sts2SkinManager.Discovery;

// Fallback character suggester that reads the mod's manifest (mod_manifest.json or {modId}.json
// in the mod folder) and looks for localized character-name keywords in `name` + `description`.
//
// Why this matters: mods authored outside the English-speaking community routinely identify the
// target base character ONLY in the localized name/description (e.g. Hcxmmx_Touhou_Sakuya_Skin
// description says "替换静默猎手外观" with zero English "silent" mentions anywhere in the
// binary). The byte-frequency CharacterIdSuggester returns null for these → no auto-assign.
//
// Coverage: English + Simplified Chinese for the v1.x base roster. Japanese / Korean / etc.
// can be added incrementally as we encounter mods that need them. False positives are
// minimized by requiring whole-substring matches and by scoring per-character (the highest
// scorer must beat the runner-up).
public static class ManifestCharacterHinter
{
    // Per-character localized substrings to look for in manifest name + description.
    // Lowercase here, scan is case-insensitive. Keep keywords specific enough to avoid
    // collisions (e.g. don't add bare "王" for Regent — too generic; "储君" + "屑国王" are safer).
    private static readonly Dictionary<string, string[]> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["defect"] = new[] { "defect", "缺陷", "デフェクト", "ディフェクト", "디펙트" },
        ["ironclad"] = new[] { "ironclad", "铁甲", "鉄甲", "アイアンクラッド", "아이언클래드" },
        ["necrobinder"] = new[] { "necrobinder", "缚灵者", "縛霊者", "ネクロバインダー", "네크로바인더" },
        ["regent"] = new[] { "regent", "储君", "儲君", "屑国王", "リージェント", "리젠트" },
        ["silent"] = new[] { "silent", "静默猎手", "靜默獵手", "静默", "サイレント", "사일런트" },
        // v0.113 beta character. Korean title is "풀" — too generic a substring (grass, 풀다…), so omitted.
        ["fool"] = new[] { "fool", "愚者", "フール" },
    };

    public static string? Suggest(string modFolder, IReadOnlySet<string> baseCharacters)
    {
        var manifestPath = FindManifest(modFolder);
        if (manifestPath == null) return null;

        string text;
        try { text = File.ReadAllText(manifestPath); }
        catch { return null; }

        string name = "", description = "";
        try
        {
            if (JsonNode.Parse(text) is JsonObject root)
            {
                name = root["name"]?.ToString() ?? "";
                description = root["description"]?.ToString() ?? "";
            }
        }
        catch { return null; }

        var haystack = (name + " " + description).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(haystack)) return null;

        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (charId, words) in Keywords)
        {
            if (baseCharacters.Count > 0 && !baseCharacters.Contains(charId)) continue;
            int hits = 0;
            foreach (var w in words)
            {
                var needle = w.ToLowerInvariant();
                int idx = 0;
                while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
                {
                    hits++;
                    idx += needle.Length;
                }
            }
            if (hits > 0) scores[charId] = hits;
        }

        if (scores.Count == 0) return null;
        var ranked = scores.OrderByDescending(kv => kv.Value).ToList();
        // Strict tie-breaker: when two characters tie, refuse to suggest (safer than guessing
        // and silently mis-blocking the wrong DLL on next boot).
        if (ranked.Count >= 2 && ranked[0].Value == ranked[1].Value) return null;
        return ranked[0].Key;
    }

    // Reads the manifest payload directly (caller-provided string), used for forensic logging
    // when we want to print name + description without re-reading the file.
    public static (string name, string description)? TryReadNameDescription(string modFolder)
    {
        var path = FindManifest(modFolder);
        if (path == null) return null;
        try
        {
            var text = File.ReadAllText(path);
            if (JsonNode.Parse(text) is JsonObject root)
            {
                return (root["name"]?.ToString() ?? "", root["description"]?.ToString() ?? "");
            }
        }
        catch { }
        return null;
    }

    // STS2 mods may use either `mod_manifest.json` (newer convention) or `{modId}.json` (legacy
    // matching the pck filename). Prefer mod_manifest.json when both exist, then fall back to
    // any *.json in the folder that has a top-level `id` field.
    private static string? FindManifest(string modFolder)
    {
        if (!Directory.Exists(modFolder)) return null;
        var canonical = Path.Combine(modFolder, "mod_manifest.json");
        if (File.Exists(canonical)) return canonical;

        try
        {
            foreach (var json in Directory.EnumerateFiles(modFolder, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var text = File.ReadAllText(json);
                    if (JsonNode.Parse(text) is JsonObject root && root.ContainsKey("id"))
                        return json;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }
}
