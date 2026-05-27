using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sts2SkinManager.Discovery;

public static class PckPathReader
{
    // Returns the pck's asset paths for domain classification. Prefers the structured PCK
    // directory (PckFileExtractor.TryReadIndex), which seeks straight to the path table and reads
    // only a few MB — vs ReadAsciiRuns, which File.ReadAllBytes the WHOLE pck (SlayTheSpire2.pck
    // is ~1.77 GB) and walks every byte. Falls back to the byte-scan only when the pck can't be
    // parsed as a standalone GDPC (embedded-in-exe / unexpected trailer).
    public static IEnumerable<string> ReadAssetPaths(string pckPath)
    {
        var index = PckFileExtractor.TryReadIndex(pckPath);
        if (index != null) return index.Keys;
        return ReadAsciiRuns(pckPath);
    }

    public static List<string> ReadAsciiRuns(string pckPath, int minLength = 8)
    {
        var result = new List<string>();
        if (!File.Exists(pckPath)) return result;

        var bytes = File.ReadAllBytes(pckPath);
        var run = new StringBuilder();
        foreach (var b in bytes)
        {
            if (b >= 32 && b < 127)
            {
                run.Append((char)b);
            }
            else
            {
                if (run.Length >= minLength) result.Add(run.ToString());
                run.Clear();
            }
        }
        if (run.Length >= minLength) result.Add(run.ToString());
        return result;
    }
}
