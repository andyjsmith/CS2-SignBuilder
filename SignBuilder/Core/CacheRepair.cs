using System;
using System.IO;

namespace SignBuilder.Core
{
    /// <summary>
    /// Fixes textures written by versions before 0.1 that saved sign faces as ARGB32 (GraphicsFormat 87), which
    /// the game cannot recreate on load. The pixel data is rewritten to RGBA32 in place, so existing cities keep
    /// their signs without a rebuild. Runs before the cache database is registered.
    /// </summary>
    public static class CacheRepair
    {
        // TextureAsset v5 header: version u16, width u16, height u16, depth u16, mips u8, format u8,
        // dimension u8, filter u8, wrap u8, aniso u8, then an i32 before the pixel data.
        private const int kFormatOffset = 9;
        private const int kDataOffset = 18;
        private const byte kArgb32 = 87;
        private const byte kRgba32Srgb = 4;

        public static int FixArgbTextures(string root)
        {
            if (!Directory.Exists(root)) return 0;
            var fixedCount = 0;
            foreach (var path in Directory.EnumerateFiles(root, "*.Texture", SearchOption.AllDirectories))
            {
                try
                {
                    if (FixFile(path)) fixedCount++;
                }
                catch (Exception e)
                {
                    Mod.log.Warn($"Could not repair texture {Path.GetFileName(path)}: {e.Message}");
                }
            }
            if (fixedCount > 0) Mod.log.Info($"Repaired {fixedCount} sign textures saved in an unloadable format");
            return fixedCount;
        }

        private static bool FixFile(string path)
        {
            byte[] header = new byte[kDataOffset];
            using (var s = File.OpenRead(path))
            {
                if (s.Read(header, 0, header.Length) < header.Length) return false;
            }
            var version = BitConverter.ToUInt16(header, 0);
            if (version != 5 || header[kFormatOffset] != kArgb32) return false;

            var bytes = File.ReadAllBytes(path);
            var width = BitConverter.ToUInt16(bytes, 2);
            var height = BitConverter.ToUInt16(bytes, 4);
            var depth = BitConverter.ToUInt16(bytes, 6);
            int mips = bytes[8];
            long size = 0;
            for (var i = 0; i < mips; i++) size += (long)Math.Max(1, width >> i) * Math.Max(1, height >> i) * 4;
            size *= Math.Max((ushort)1, depth);
            if (kDataOffset + size > bytes.Length) return false;

            // ARGB -> RGBA: rotate every pixel's bytes left by one.
            for (long p = kDataOffset; p < kDataOffset + size; p += 4)
            {
                var a = bytes[p];
                bytes[p] = bytes[p + 1];
                bytes[p + 1] = bytes[p + 2];
                bytes[p + 2] = bytes[p + 3];
                bytes[p + 3] = a;
            }
            bytes[kFormatOffset] = kRgba32Srgb;
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Copy(temp, path, overwrite: true);
            File.Delete(temp);
            return true;
        }
    }
}
