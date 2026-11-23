using System.Text;
using OpenAppMover.Core.Msi;
using OpenAppMover.Core.Pe;

namespace OpenAppMover.Tests.Fakes;

/// <summary>
/// Builds minimal PE files: one section holding the import table, the CLR header for managed code, a version resource
/// (RT_VERSION → VS_FIXEDFILEINFO) when a version is given and a dialog (RT_DIALOG) per ActiveX control.
/// </summary>
public static class PeBuilder
{
    private const int SectionRva = 0x1000;
    private const int SectionOffset = 0x200;
    private const int SectionSize = 0x1000;

    /// <param name="subsystem">MajorSubsystemVersion.MinorSubsystemVersion (minimum Windows version), 0.0 when null.</param>
    /// <param name="activeX">CLSIDs of ActiveX controls, each one in its own dialog template.</param>
    public static byte[] Build(ushort machine, string[] imports, string? clrVersion = null, Version? version = null, Version? subsystem = null,
        string[]? activeX = null)
    {
        var resources = new List<(uint Type, byte[] Data)>();
        if (version is not null)
            resources.Add((16, VersionInfo(version)));
        foreach (var clsid in activeX ?? [])
            resources.Add((5, Dialog(clsid)));

        var pe32Plus = machine != PeInfo.MachineX86;
        var optionalSize = pe32Plus ? 240 : 224;
        var file = new byte[SectionOffset + SectionSize];
        using var writer = new BinaryWriter(new MemoryStream(file));

        writer.Write((ushort)0x5A4D);
        writer.Seek(0x3C, SeekOrigin.Begin);
        writer.Write(0x40);

        writer.Seek(0x40, SeekOrigin.Begin);
        writer.Write(0x00004550);
        writer.Write(machine);
        writer.Write((ushort)1);         // sections
        writer.Write(new byte[12]);
        writer.Write((ushort)optionalSize);
        writer.Write((ushort)0x22);

        var optional = 0x40 + 24;
        writer.Write((ushort)(pe32Plus ? 0x20B : 0x10B));
        if (subsystem is not null)
        {
            writer.Seek(optional + 48, SeekOrigin.Begin);
            writer.Write((ushort)subsystem.Major);
            writer.Write((ushort)subsystem.Minor);
        }
        writer.Seek(optional + (pe32Plus ? 108 : 92), SeekOrigin.Begin);
        writer.Write(16);                // NumberOfRvaAndSizes
        var directories = optional + (pe32Plus ? 112 : 96);
        writer.Seek(directories + 1 * 8, SeekOrigin.Begin);
        writer.Write(SectionRva);        // import table
        writer.Write((imports.Length + 1) * 20);
        if (resources.Count > 0)
        {
            writer.Seek(directories + 2 * 8, SeekOrigin.Begin);
            writer.Write(SectionRva + ResourceOffset);
            writer.Write(SectionSize - ResourceOffset);
        }
        if (clrVersion is not null)
        {
            writer.Seek(directories + 14 * 8, SeekOrigin.Begin);
            writer.Write(SectionRva + 0x800);
            writer.Write(72);
        }

        writer.Seek(optional + optionalSize, SeekOrigin.Begin);
        writer.Write(Encoding.ASCII.GetBytes(".rdata\0\0"));
        writer.Write(SectionSize);       // virtual size
        writer.Write(SectionRva);
        writer.Write(SectionSize);       // raw size
        writer.Write(SectionOffset);

        // Import descriptors, then the DLL names.
        var nameRva = SectionRva + 0x100;
        for (var i = 0; i < imports.Length; i++)
        {
            writer.Seek(SectionOffset + i * 20 + 12, SeekOrigin.Begin);
            writer.Write(nameRva);
            writer.Seek(SectionOffset + nameRva - SectionRva, SeekOrigin.Begin);
            writer.Write(Encoding.ASCII.GetBytes(imports[i] + "\0"));
            nameRva += imports[i].Length + 1;
        }

        if (clrVersion is not null)
        {
            writer.Seek(SectionOffset + 0x800 + 8, SeekOrigin.Begin);
            writer.Write(SectionRva + 0x900); // metadata
            writer.Seek(SectionOffset + 0x900, SeekOrigin.Begin);
            writer.Write(0x424A5342);
            writer.Write(new byte[8]);
            var runtime = Encoding.ASCII.GetBytes(clrVersion + "\0\0\0\0");
            writer.Write(runtime.Length);
            writer.Write(runtime);
        }
        WriteResources(writer, resources);
        return file;
    }

    private const int ResourceOffset = 0xA00;

    /// <summary>Resource tree at section offset 0xA00: root → type → name (100, 101…) → language 0x409 → data.</summary>
    private static void WriteResources(BinaryWriter writer, List<(uint Type, byte[] Data)> resources)
    {
        if (resources.Count == 0)
            return;
        const int root = SectionOffset + ResourceOffset;
        const uint subdirectory = 0x80000000;
        var types = resources.GroupBy(r => r.Type).ToList();
        var next = 16 + 8 * types.Count; // after the root directory
        int Allocate(int size)
        {
            var at = next;
            next += (size + 3) & ~3;
            return at;
        }
        void DirectoryHeader(int at, int count)
        {
            writer.Seek(root + at + 14, SeekOrigin.Begin);
            writer.Write((ushort)count);
        }
        void Entry(int directory, int index, uint id, uint target)
        {
            writer.Seek(root + directory + 16 + 8 * index, SeekOrigin.Begin);
            writer.Write(id);
            writer.Write(target);
        }

        DirectoryHeader(0, types.Count);
        for (var t = 0; t < types.Count; t++)
        {
            var items = types[t].ToList();
            var names = Allocate(16 + 8 * items.Count);
            DirectoryHeader(names, items.Count);
            Entry(0, t, types[t].Key, subdirectory | (uint)names);
            for (var i = 0; i < items.Count; i++)
            {
                var languages = Allocate(24);
                DirectoryHeader(languages, 1);
                Entry(names, i, (uint)(100 + i), subdirectory | (uint)languages);
                var dataEntry = Allocate(16);
                Entry(languages, 0, 0x409, (uint)dataEntry);
                var data = Allocate(items[i].Data.Length);
                writer.Seek(root + dataEntry, SeekOrigin.Begin);
                writer.Write(SectionRva + ResourceOffset + data);
                writer.Write(items[i].Data.Length);
                writer.Seek(root + data, SeekOrigin.Begin);
                writer.Write(items[i].Data);
            }
        }
    }

    /// <summary>VS_VERSIONINFO: header + "VS_VERSION_INFO", then VS_FIXEDFILEINFO (aligned) at +0x28.</summary>
    private static byte[] VersionInfo(Version version)
    {
        var data = new byte[0x60];
        using var writer = new BinaryWriter(new MemoryStream(data));
        writer.Seek(0x28, SeekOrigin.Begin);
        writer.Write(0xFEEF04BD);
        writer.Write(0x00010000);
        writer.Write((uint)((version.Major << 16) | version.Minor));
        writer.Write((uint)((version.Build << 16) | Math.Max(0, version.Revision)));
        return data;
    }

    /// <summary>A DLGTEMPLATE with one control whose class is the CLSID (as MFC stores ActiveX controls).</summary>
    private static byte[] Dialog(string clsid)
    {
        var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        writer.Write(0x80C800C0u);              // style
        writer.Write(0u);                       // extended style
        writer.Write((ushort)1);                // controls
        writer.Write(new byte[8]);              // x, y, cx, cy
        writer.Write(new byte[6]);              // no menu, default class, no title
        writer.Write(new byte[2]);              // align on DWORD
        writer.Write(0x50000000u);              // control style
        writer.Write(0u);
        writer.Write(new byte[8]);
        writer.Write((ushort)1000);             // id
        writer.Write(Encoding.Unicode.GetBytes(clsid + "\0"));
        writer.Write(new byte[4]);              // no title, no creation data
        writer.Flush();
        return data.ToArray();
    }
}

/// <summary>Writes Inno Setup uninstall logs (format of Setup.UninstallLog.pas).</summary>
public sealed class InnoLogBuilder
{
    private readonly List<(ushort Type, uint Extra, string[] Data)> _records = [];

    public InnoLogBuilder Add(ushort type, uint extra, params string[] data)
    {
        _records.Add((type, extra, data));
        return this;
    }

    public byte[] Build(string appName = "Acme Inno", bool is64Bit = true, int flags = 1)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.Unicode, leaveOpen: true))
        {
            foreach (var (type, extra, data) in _records)
            {
                var bytes = new MemoryStream();
                foreach (var text in data)
                {
                    var encoded = Encoding.Unicode.GetBytes(text);
                    bytes.WriteByte(0xFE);
                    bytes.Write(BitConverter.GetBytes(-encoded.Length));
                    bytes.Write(encoded);
                }
                bytes.WriteByte(0xFF);
                writer.Write(type);
                writer.Write(extra);
                writer.Write((uint)bytes.Length);
                writer.Write(bytes.ToArray());
            }
        }

        using var file = new MemoryStream();
        using var output = new BinaryWriter(file);
        var header = new byte[448];
        Encoding.ASCII.GetBytes(is64Bit ? "Inno Setup Uninstall Log (b) 64-bit" : "Inno Setup Uninstall Log (b)").CopyTo(header, 0);
        Encoding.ASCII.GetBytes("AcmeInno_is1").CopyTo(header, 64);
        Encoding.ASCII.GetBytes(appName).CopyTo(header, 192);
        output.Write(header);

        // Blocks of 4096 bytes at most: Size, ~Size, CRC (not checked by OpenAppMover), payload.
        var stream = payload.ToArray();
        for (var offset = 0; offset < stream.Length; offset += 4096)
        {
            var size = Math.Min(4096, stream.Length - offset);
            output.Write((uint)size);
            output.Write(~(uint)size);
            output.Write(0);
            output.Write(stream, offset, size);
        }

        var bytesOut = file.ToArray();
        BitConverter.GetBytes(1048).CopyTo(bytesOut, 320);              // version
        BitConverter.GetBytes(_records.Count).CopyTo(bytesOut, 324);    // records
        BitConverter.GetBytes((uint)bytesOut.Length).CopyTo(bytesOut, 328); // end offset
        BitConverter.GetBytes(flags).CopyTo(bytesOut, 332);
        return bytesOut;
    }
}

/// <summary>Tables of a fake .msi database.</summary>
public sealed class FakeMsiDatabase : IMsiDatabaseReader
{
    public Dictionary<string, List<string?[]>> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeMsiDatabase Table(string name, params string?[][] rows)
    {
        Tables[name] = rows.ToList();
        return this;
    }

    public IReadOnlyList<string?[]>? Query(string databasePath, string sql)
    {
        var table = sql[(sql.IndexOf("FROM `", StringComparison.Ordinal) + 6)..].TrimEnd('`');
        return Tables.GetValueOrDefault(table);
    }
}
