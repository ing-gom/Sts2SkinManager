using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Sts2SkinManager.Discovery;

// Reverse-dependency index — "which installed mods declare mod X as a dependency?"
//
// SkinManager suppresses a skin it doesn't want applied in two ways, and STS2's loader treats
// BOTH as "that mod did not load":
//   - Patches/TryLoadModPatch          → mod.state = Disabled, the DLL is never loaded
//   - MainFile.ApplyLoadOrderBootstrap → is_enabled=false in settings.save, stripped before load
//
// ModManager then validates every mod's declared dependencies with
// `dependency == null || dependency.state != ModLoadState.Loaded` → it adds a
// MOD_ERROR.MISSING_DEPENDENCY LocString, sets the DEPENDENT to ModLoadState.Failed, and the main
// menu shows the mod-error screen. So blocking a skin another mod builds on doesn't merely
// suppress a skin — it kills the dependent and greets the player with an error at boot. That is
// what a Workshop comment reported: "loading this mod alone errors and the game won't start, but
// together with another skin mod it starts fine" — a different active pick blocks a different set.
//
// Reproducible from the author's own install: STS2-OrchisNecrobinderSkinFix declares
// OrchisNecrobinderSkinMod (a necrobinder skin that gets dll-blocked whenever it isn't the active
// pick) as its dependency, and the boot log shows `[dll-block] OrchisNecrobinderSkinMod`.
//
// The pre-existing guard (MainFile.IsContentOrFrameworkMod) only asks whether the TARGET is a
// content/framework mod — nobody asked who depends on it. This index closes that gap.
//
// Read from disk rather than from ModManager.Mods on purpose: manifest files are stable across
// game branches, whereas binding to the loader's manifest/dependency types would add MemberRefs
// that can shift between public and public-beta (the silent-MissingMethod class).
public sealed class ModDependencyIndex
{
    private static readonly IReadOnlyCollection<string> NoDependents = Array.Empty<string>();

    // Manifests are tiny — the whole installed set here measured 70 KB across 109 files. Anything
    // this big is a data file that happens to sit beside a manifest, not a manifest; skip it
    // rather than parse it at boot.
    private const long MaxManifestBytes = 512 * 1024;

    // dependency id → ids of installed mods declaring it. Case-insensitive like every other id set
    // in this codebase; a mismatch here would silently drop the protection.
    private readonly Dictionary<string, SortedSet<string>> _dependents = new(StringComparer.OrdinalIgnoreCase);

    private ModDependencyIndex() { }

    // Number of distinct (dependency → dependent) edges found. Diagnostic only.
    public int EdgeCount { get; private set; }

    // Installed mods that declare modId as a dependency. Empty when nobody does — the common case,
    // so callers can treat a non-empty result as "do not block this".
    public IReadOnlyCollection<string> DependentsOf(string modId)
        => !string.IsNullOrEmpty(modId) && _dependents.TryGetValue(modId, out var set) ? set : NoDependents;

    public static ModDependencyIndex Build(IReadOnlyList<string> modRoots)
    {
        var index = new ModDependencyIndex();
        foreach (var root in modRoots)
            foreach (var folder in EnumerateModFolders(root))
                foreach (var json in SafeEnumerateJson(folder))
                    index.AddManifest(json);
        return index;
    }

    private void AddManifest(string path)
    {
        // JsonDocument, not JsonNode, and every access inside the try — both deliberate.
        // CustomCardTextureLoaderSG (installed here) declares "dependencies" TWICE in one manifest:
        // JsonNode.Parse accepts that file and then throws ArgumentException on the first indexer
        // access, when it builds its backing dictionary — an exception that would escape into
        // MainFile.Run's catch-all and take the whole mod down to "init failed". JsonDocument
        // tolerates the duplicate, and walking the properties (rather than a lookup, which would
        // silently pick one copy) lets us union BOTH declarations: for a guard, an edge we missed
        // is a broken boot, while an extra edge only leaves one skin's DLL loaded.
        var edges = new List<(string dependency, string dependent)>();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxManifestBytes) return;

            using var doc = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            string? dependentId = null;
            var declared = new List<JsonElement>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (dependentId == null && prop.NameEquals("id") && prop.Value.ValueKind == JsonValueKind.String)
                    dependentId = prop.Value.GetString();
                else if (prop.NameEquals("dependencies") && prop.Value.ValueKind == JsonValueKind.Array)
                    declared.Add(prop.Value);
            }

            if (string.IsNullOrWhiteSpace(dependentId)) return; // not a manifest: no id field.

            foreach (var deps in declared)
            {
                foreach (var dep in deps.EnumerateArray())
                {
                    // Two shapes occur in the wild: a bare string ("BaseLib") or an object
                    // ({ "id": "BaseLib", "min_version": "1.2.0" }).
                    var dependencyId = dep.ValueKind switch
                    {
                        JsonValueKind.String => dep.GetString(),
                        JsonValueKind.Object => dep.TryGetProperty("id", out var depId) && depId.ValueKind == JsonValueKind.String
                            ? depId.GetString()
                            : null,
                        _ => null,
                    };
                    if (string.IsNullOrWhiteSpace(dependencyId)) continue;
                    // A self-reference is meaningless to the loader and would make the mod
                    // permanently un-blockable here, so drop it.
                    if (string.Equals(dependencyId, dependentId, StringComparison.OrdinalIgnoreCase)) continue;

                    edges.Add((dependencyId!, dependentId!));
                }
            }
        }
        catch { return; } // unreadable / not JSON / malformed — the game gets nothing from it either.

        foreach (var (dependency, dependent) in edges)
        {
            if (!_dependents.TryGetValue(dependency, out var set))
            {
                set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                _dependents[dependency] = set;
            }
            // A mod installed both locally and via Workshop yields the same edge twice; the set
            // keeps it at one.
            if (set.Add(dependent)) EdgeCount++;
        }
    }

    // Same walk as UnifiedModBuilder / SkinModScanner: every directory under a scan root at any
    // depth (users group mods in category folders; workshop items nest their payload), minus VCS
    // metadata and macOS archive cruft. Manifests are read at folder top level only — which is
    // also where STS2's own loader finds them.
    private static IEnumerable<string> EnumerateModFolders(string root)
    {
        var stack = new Stack<string>();
        foreach (var dir in SafeEnumerateDirectories(root))
            stack.Push(dir);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            yield return dir;
            foreach (var sub in SafeEnumerateDirectories(dir))
                stack.Push(sub);
        }
    }

    // Materialised (not lazy): an IO error mid-enumeration would otherwise escape the try block
    // and take the whole scan down. A directory we can't read simply contributes nothing.
    private static List<string> SafeEnumerateDirectories(string dir)
    {
        var result = new List<string>();
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                if (string.Equals(name, "__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(sub);
            }
        }
        catch { }
        return result;
    }

    private static string[] SafeEnumerateJson(string dir)
    {
        try { return Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly); }
        catch { return Array.Empty<string>(); }
    }
}
