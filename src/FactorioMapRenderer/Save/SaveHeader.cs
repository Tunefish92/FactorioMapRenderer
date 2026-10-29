using System.IO.Compression;
using System.Text;

namespace FactorioMapRenderer.Save;

/// <summary>A mod as a save lists it: its name and version.</summary>
public sealed record SaveMod(string Name, string Version);

/// <summary>
/// The start of a Factorio save's level data: the game version that wrote it, the scenario and the mods it was
/// played with. That is all the renderer reads from the save itself; the map comes from the game (see
/// <see cref="Game.FactorioRunner"/>).
/// </summary>
/// <remarks>
/// A save is a zip with one folder. The level data is "level.dat0", "level.dat1", … (zlib, since 1.1) or one
/// "level.dat" (older saves). It begins with: version (4 × u16), a byte, the campaign, level and base mod names
/// (strings with a space-optimized length), a few flags, the version it was loaded from, then the mod list:
/// a space-optimized count and per mod its name, version (3 × space-optimized u16) and CRC (u32). The flags in
/// between have changed between versions, so the mod list is found by trying where it can begin.
/// </remarks>
public sealed record SaveHeader(string Name, Version GameVersion, string Scenario, IReadOnlyList<SaveMod> Mods)
{
    public string GameVersionText => $"{GameVersion.Major}.{GameVersion.Minor}.{GameVersion.Build}";

    public static SaveHeader Read(string zipPath)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException)
        {
            throw new SaveFormatException("The file is not a Factorio save (not a zip archive).");
        }
        using (zip)
        {
            var level = zip.Entries.FirstOrDefault(e => e.Name == "level.dat0") ?? zip.Entries.FirstOrDefault(e => e.Name == "level.dat")
                ?? throw new SaveFormatException("The zip has no level.dat: it is not a Factorio save.");
            string folder = level.FullName.Contains('/') ? level.FullName[..level.FullName.IndexOf('/')] : Path.GetFileNameWithoutExtension(zipPath);
            byte[] head = ReadStart(level, 1 << 16);
            return Parse(head, folder);
        }
    }

    private static byte[] ReadStart(ZipArchiveEntry entry, int length)
    {
        using var raw = entry.Open();
        var buffer = new byte[length];
        int read = raw.ReadAtLeast(buffer, 2, throwOnEndOfStream: false);
        bool zlib = read >= 2 && buffer[0] == 0x78 && (buffer[0] << 8 | buffer[1]) % 31 == 0;
        if (!zlib)
            return buffer[..read];
        using var again = entry.Open();
        using var inflate = new ZLibStream(again, CompressionMode.Decompress);
        try
        {
            read = inflate.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch (InvalidDataException ex)
        {
            throw new SaveFormatException($"The level data cannot be decompressed: {ex.Message}");
        }
        return buffer[..read];
    }

    internal static SaveHeader Parse(byte[] data, string name)
    {
        var r = new Reader(data);
        var version = new Version(r.U16(), r.U16(), r.U16(), r.U16());
        if (version.Major is < 0 or > 10)
            throw new SaveFormatException($"Unknown save version {version}.");
        r.Skip(1);
        string campaign = r.String(), level = r.String();
        r.String(); // base mod
        int afterNames = r.Position;
        // The flags and the "loaded from" version take 10 to 30 bytes, depending on the version.
        for (int offset = 0; offset < 64 && afterNames + offset < data.Length; offset++)
        {
            if (TryMods(data, afterNames + offset) is { } mods)
                return new SaveHeader(name, version, campaign.Length > 0 ? $"{campaign}/{level}" : level, mods);
        }
        throw new SaveFormatException("The mod list of the save could not be found.");
    }

    /// <summary>A complete, plausible mod list at this position that contains "base", or null.</summary>
    private static List<SaveMod>? TryMods(byte[] data, int position)
    {
        try
        {
            var r = new Reader(data) { Position = position };
            uint count = r.OptimizedU32();
            if (count is 0 or > 2000)
                return null;
            var mods = new List<SaveMod>((int)count);
            for (int i = 0; i < count; i++)
            {
                string modName = r.String();
                if (modName.Length is 0 or > 100 || modName.Any(c => c < 0x20 || c is '/' or '\\'))
                    return null;
                mods.Add(new SaveMod(modName, $"{r.OptimizedU16()}.{r.OptimizedU16()}.{r.OptimizedU16()}"));
                r.Skip(4); // CRC
            }
            return mods.Any(m => m.Name == "base") ? mods : null;
        }
        catch (SaveFormatException)
        {
            return null;
        }
    }

    private sealed class Reader(byte[] data)
    {
        public int Position { get; set; }

        private void Need(int count)
        {
            if (Position + count > data.Length)
                throw new SaveFormatException("The level data ends too early.");
        }

        public void Skip(int count)
        {
            Need(count);
            Position += count;
        }

        public byte U8()
        {
            Need(1);
            return data[Position++];
        }

        public ushort U16()
        {
            Need(2);
            ushort v = BitConverter.ToUInt16(data, Position);
            Position += 2;
            return v;
        }

        public uint U32()
        {
            Need(4);
            uint v = BitConverter.ToUInt32(data, Position);
            Position += 4;
            return v;
        }

        /// <summary>One byte, or 0xFF followed by the full value.</summary>
        public uint OptimizedU32()
        {
            byte b = U8();
            return b == 0xFF ? U32() : b;
        }

        public ushort OptimizedU16()
        {
            byte b = U8();
            return b == 0xFF ? U16() : b;
        }

        public string String()
        {
            uint length = OptimizedU32();
            if (length > 10_000)
                throw new SaveFormatException("Implausible string length in the level data.");
            Need((int)length);
            string s = Encoding.UTF8.GetString(data, Position, (int)length);
            Position += (int)length;
            return s;
        }
    }
}

/// <summary>The file is not a save this renderer can read; its message is shown to the user.</summary>
public sealed class SaveFormatException(string message) : Exception(message);
