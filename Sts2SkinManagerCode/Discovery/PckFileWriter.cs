using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Sts2SkinManager.Discovery;

// Writes a Godot 4.5 PCK (pack format version 3) — the mirror of PckFileExtractor's reader.
// Layout: 112-byte header (magic, format=3, godot ver, flags=PACK_REL_FILEBASE, file_base=112,
// dir_offset, zero-padded reserved), then 16-byte-aligned file data, then the directory at the
// tail (file_count + per-entry: plen, NUL-padded path, file_base-relative offset, size, md5,
// flags). Validated in tools/_pcklib.py against ATA_IronClad.pck (283-entry byte-identical
// round-trip) before porting here.
public static class PckFileWriter
{
    private const uint MagicGdpc = 0x43504447; // "GDPC"
    private const int FileBase = 112;
    private const uint PackRelFilebase = 2; // offsets are file_base-relative (matches STS2 pcks)

    public static void Write(string outPath, IReadOnlyDictionary<string, byte[]> files)
    {
        // Lay out file data 16-byte aligned, recording each entry's offset relative to file_base.
        using var body = new MemoryStream();
        var entries = new List<(string path, long relOff, long size, byte[] md5)>();
        using (var md5 = MD5.Create())
        {
            foreach (var kv in files)
            {
                while (body.Length % 16 != 0) body.WriteByte(0);
                var relOff = body.Length;
                body.Write(kv.Value, 0, kv.Value.Length);
                entries.Add((kv.Key, relOff, kv.Value.Length, md5.ComputeHash(kv.Value)));
            }
        }
        var bodyBytes = body.ToArray();
        long dirOffset = FileBase + bodyBytes.Length;

        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // Header (112 bytes). BinaryWriter is little-endian, matching Godot's PCK encoding.
        bw.Write(MagicGdpc);            // 0
        bw.Write((uint)3);              // 4  pack format version
        bw.Write((uint)4);              // 8  godot major
        bw.Write((uint)5);              // 12 godot minor
        bw.Write((uint)1);              // 16 godot patch
        bw.Write(PackRelFilebase);      // 20 pack flags
        bw.Write((long)FileBase);       // 24 file_base (abs offset where file data begins)
        bw.Write(dirOffset);            // 32 directory offset (abs, at tail)
        for (var i = 40; i < FileBase; i++) bw.Write((byte)0); // 40..112 reserved

        bw.Write(bodyBytes);

        // Directory at the tail.
        bw.Write((uint)entries.Count);
        foreach (var (path, relOff, size, md5) in entries)
        {
            var pb = Encoding.UTF8.GetBytes(path);
            var pad = (4 - (pb.Length % 4)) % 4;
            bw.Write((uint)(pb.Length + pad));
            bw.Write(pb);
            for (var i = 0; i < pad; i++) bw.Write((byte)0);
            bw.Write(relOff);           // file_base-relative
            bw.Write(size);
            bw.Write(md5);              // 16 bytes
            bw.Write((uint)0);          // per-entry flags
        }
    }
}
