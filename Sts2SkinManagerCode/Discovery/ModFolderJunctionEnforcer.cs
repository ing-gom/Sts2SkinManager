using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Sts2SkinManager.Discovery;

public enum JunctionAction
{
    Created,
    AlreadyCorrect,
    SkippedConflict,
    SkippedDuplicate,
    Failed,
    OrphanRemoved,
}

public record JunctionResult(string ModFolder, string JunctionPath, JunctionAction Action, string? Detail);

// When a user moves a DLL-bundled mod folder out of mods/<modId>/ and into a category subfolder
// (e.g. mods/캐릭터/Booba-Necrobinder-Mod/), the game's framework — which only scans the direct
// children of mods/ — loses the manifest and stops loading the DLL. This enforcer detects such
// "deep" mod folders and creates a directory junction at mods/<folderName>/ pointing back at them,
// so the framework finds the manifest on the *next* boot. The mod is not loaded the same session
// the junction is created (framework's scan already finished). Caller prompts a restart.
//
// On Windows we shell out to `mklink /J` because Directory.CreateSymbolicLink creates a true
// symbolic link, which on Windows requires admin or Developer Mode. Junctions need neither.
// On non-Windows hosts we fall back to Directory.CreateSymbolicLink.
public static class ModFolderJunctionEnforcer
{
    public static List<JunctionResult> Enforce(string modsDir)
    {
        var results = new List<JunctionResult>();
        if (!Directory.Exists(modsDir)) return results;

        RemoveOrphanedJunctions(modsDir, results);

        var deepMods = FindDeepDllBundledMods(modsDir);
        var seenFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var modDir in deepMods)
        {
            var folderName = Path.GetFileName(modDir);
            var junctionPath = Path.Combine(modsDir, folderName);

            if (!seenFolderNames.Add(folderName))
            {
                results.Add(new(modDir, junctionPath, JunctionAction.SkippedDuplicate,
                    $"another deep mod already claimed '{folderName}'"));
                continue;
            }

            if (Directory.Exists(junctionPath))
            {
                var info = new DirectoryInfo(junctionPath);
                if (IsReparsePoint(info))
                {
                    if (PathsEqual(info.LinkTarget, modDir))
                    {
                        results.Add(new(modDir, junctionPath, JunctionAction.AlreadyCorrect, null));
                        continue;
                    }
                    results.Add(new(modDir, junctionPath, JunctionAction.SkippedConflict,
                        $"junction points elsewhere: {info.LinkTarget}"));
                    continue;
                }
                results.Add(new(modDir, junctionPath, JunctionAction.SkippedConflict,
                    "a real folder already occupies the junction path"));
                continue;
            }

            try
            {
                CreateJunction(junctionPath, modDir);
                HideJunction(junctionPath);
                results.Add(new(modDir, junctionPath, JunctionAction.Created, null));
            }
            catch (Exception ex)
            {
                results.Add(new(modDir, junctionPath, JunctionAction.Failed, ex.Message));
            }
        }

        return results;
    }

    // Depth=1 junctions whose target no longer exists are leftovers from a previous boot's
    // reorganization. Remove them so they don't accumulate.
    private static void RemoveOrphanedJunctions(string modsDir, List<JunctionResult> results)
    {
        foreach (var sub in SafeDirs(modsDir))
        {
            try
            {
                var info = new DirectoryInfo(sub);
                if (!IsReparsePoint(info)) continue;
                var target = info.LinkTarget;
                if (string.IsNullOrEmpty(target)) continue;
                var absolute = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(modsDir, target));
                if (Directory.Exists(absolute)) continue;
                Directory.Delete(sub);
                results.Add(new(absolute, sub, JunctionAction.OrphanRemoved, "target missing"));
            }
            catch { }
        }
    }

    private static List<string> FindDeepDllBundledMods(string modsDir)
    {
        var found = new List<string>();
        foreach (var topDir in SafeDirs(modsDir))
        {
            if (ShouldSkipDir(Path.GetFileName(topDir))) continue;
            // Top-level dirs are the conventional mod home; don't junction them onto themselves.
            // We only care about depth >= 2.
            foreach (var child in SafeDirs(topDir))
            {
                if (ShouldSkipDir(Path.GetFileName(child))) continue;
                CollectDeep(child, found);
            }
        }
        return found;
    }

    private static void CollectDeep(string dir, List<string> result)
    {
        if (DirIsDllBundledMod(dir))
        {
            result.Add(dir);
            return; // don't descend — inner files belong to this mod
        }
        foreach (var sub in SafeDirs(dir))
        {
            if (ShouldSkipDir(Path.GetFileName(sub))) continue;
            CollectDeep(sub, result);
        }
    }

    // A directory looks like a DLL-bundled STS2 mod when it has at least one .dll *and* a sibling
    // .json that declares "has_dll": true (manifest signature).
    private static bool DirIsDllBundledMod(string dir)
    {
        try
        {
            if (!Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly).Any()) return false;
            foreach (var json in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                string content;
                try { content = File.ReadAllText(json); }
                catch { continue; }
                if (LooksLikeManifestWithDll(content)) return true;
            }
        }
        catch { }
        return false;
    }

    // Loose textual check — STS2 manifests are tiny and we want to avoid a JSON parser dependency.
    // Accepts "has_dll": true with optional whitespace between key and value.
    private static bool LooksLikeManifestWithDll(string content)
    {
        var key = "\"has_dll\"";
        var idx = content.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var colon = content.IndexOf(':', idx + key.Length);
        if (colon < 0) return false;
        var after = content.AsSpan(colon + 1).TrimStart();
        if (!after.StartsWith("true", StringComparison.OrdinalIgnoreCase)) return false;
        return content.Contains("\"id\"", StringComparison.OrdinalIgnoreCase)
            || content.Contains("\"name\"", StringComparison.OrdinalIgnoreCase)
            || content.Contains("\"has_pck\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipDir(string dirName) =>
        dirName.StartsWith(".") || string.Equals(dirName, "__MACOSX", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    private static bool IsReparsePoint(FileSystemInfo info)
    {
        try { return (info.Attributes & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    private static bool PathsEqual(string? a, string b)
    {
        if (string.IsNullOrEmpty(a)) return false;
        try
        {
            var na = Path.GetFullPath(a).TrimEnd('\\', '/');
            var nb = Path.GetFullPath(b).TrimEnd('\\', '/');
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // Hide the junction so Explorer (with default "show hidden = off") keeps mods/ visually clean.
    // The game framework's Directory.EnumerateDirectories(path) overload still returns hidden
    // entries — only the EnumerationOptions-bearing overload's defaults skip them.
    private static void HideJunction(string junctionPath)
    {
        try
        {
            var attrs = File.GetAttributes(junctionPath);
            File.SetAttributes(junctionPath, attrs | FileAttributes.Hidden);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"could not hide junction {junctionPath}: {ex.Message}");
        }
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi)
                ?? throw new IOException("failed to spawn cmd.exe for mklink");
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd().Trim();
                throw new IOException($"mklink /J failed (exit {proc.ExitCode}): {err}");
            }
        }
        else
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
    }
}
