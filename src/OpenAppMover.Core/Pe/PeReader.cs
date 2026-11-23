using System.Text;

namespace OpenAppMover.Core.Pe;

/// <summary>What OpenAppMover needs to know about an executable or a DLL.</summary>
/// <param name="Machine">IMAGE_FILE_MACHINE_* (0x8664 x64, 0x14C x86, 0xAA64 ARM64).</param>
/// <param name="ClrVersion">Runtime version of a .NET assembly ("v4.0.30319"), null for native code.</param>
/// <param name="Imports">Imported and delay-loaded DLLs, lower case.</param>
public sealed record PeInfo(ushort Machine, string? ClrVersion, IReadOnlyList<string> Imports)
{
    public const ushort MachineX86 = 0x14C;
    public const ushort MachineX64 = 0x8664;
    public const ushort MachineArm64 = 0xAA64;

    public bool IsManaged => ClrVersion is not null;

    /// <summary>File version of the version resource (VS_FIXEDFILEINFO), null when the file has none.</summary>
    public Version? FileVersion { get; init; }

    /// <summary>
    /// Minimum Windows version of the image (MajorSubsystemVersion.MinorSubsystemVersion): Windows refuses to load an
    /// image whose version is above its own ("6.2": Windows 8 and later).
    /// </summary>
    public Version? SubsystemVersion { get; init; }

    /// <summary>
    /// CLSIDs ("{D27CDB6E-…}", upper case) of the ActiveX controls placed in the dialogs of the image (MFC and ATL dialog
    /// templates name a control by its CLSID).
    /// </summary>
    public IReadOnlyList<string> ActiveXControls { get; init; } = [];

    /// <summary>"x64", "x86", "arm64", or null for other machines.</summary>
    public string? Architecture => Machine switch
    {
        MachineX64 => "x64",
        MachineX86 => "x86",
        MachineArm64 => "arm64",
        _ => null,
    };
}

/// <summary>Minimal Portable Executable reader: machine, imports, delay imports, CLR header, version and dialog resources.</summary>
public static class PeReader
{
    private const int ImportDirectory = 1;
    private const int ResourceDirectory = 2;
    private const int VersionResourceType = 16; // RT_VERSION
    private const int DialogResourceType = 5;   // RT_DIALOG
    private const int MaxDialogs = 4096;
    private const int MaxDialogSize = 64 * 1024;
    private const uint FixedFileInfoSignature = 0xFEEF04BD;
    private const int DelayImportDirectory = 13;
    private const int ClrDirectory = 14;
    private const int MaxImports = 2048;

    private sealed record Section(uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawPointer);

    /// <summary>Reads a PE file from a seekable stream; returns null when it is not a valid PE file.</summary>
    public static PeInfo? Read(Stream stream)
    {
        try
        {
            return ReadCore(new BinaryReader(stream, Encoding.ASCII, leaveOpen: true));
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static PeInfo? ReadCore(BinaryReader reader)
    {
        var stream = reader.BaseStream;
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) // "MZ"
            return null;
        stream.Position = 0x3C;
        var peOffset = reader.ReadUInt32();
        if (peOffset > stream.Length - 24)
            return null;
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
            return null;

        var machine = reader.ReadUInt16();
        var sectionCount = reader.ReadUInt16();
        stream.Position += 12;
        var optionalHeaderSize = reader.ReadUInt16();
        stream.Position += 2;

        var optionalHeader = stream.Position;
        var magic = reader.ReadUInt16();
        var pe32Plus = magic == 0x20B;
        if (magic != 0x10B && !pe32Plus)
            return null;
        stream.Position = optionalHeader + 48;
        var subsystemVersion = new Version(reader.ReadUInt16(), reader.ReadUInt16());
        stream.Position = optionalHeader + (pe32Plus ? 108 : 92);
        var directoryCount = reader.ReadUInt32();
        var directories = new (uint Rva, uint Size)[Math.Min(directoryCount, 16)];
        for (var i = 0; i < directories.Length; i++)
            directories[i] = (reader.ReadUInt32(), reader.ReadUInt32());

        stream.Position = optionalHeader + optionalHeaderSize;
        var sections = new List<Section>(sectionCount);
        for (var i = 0; i < sectionCount; i++)
        {
            stream.Position += 8; // name
            var virtualSize = reader.ReadUInt32();
            var virtualAddress = reader.ReadUInt32();
            var rawSize = reader.ReadUInt32();
            var rawPointer = reader.ReadUInt32();
            stream.Position += 16;
            sections.Add(new Section(virtualAddress, virtualSize, rawSize, rawPointer));
        }

        long? Offset(uint rva)
        {
            var section = sections.FirstOrDefault(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
            return section is null ? null : rva - section.VirtualAddress + section.RawPointer;
        }

        string? ReadString(uint rva)
        {
            if (Offset(rva) is not { } offset || offset >= stream.Length)
                return null;
            stream.Position = offset;
            var bytes = new List<byte>();
            for (int b; bytes.Count < 256 && (b = stream.ReadByte()) > 0;)
                bytes.Add((byte)b);
            return Encoding.ASCII.GetString(bytes.ToArray()).ToLowerInvariant();
        }

        var imports = new List<string>();
        void ReadDescriptors(int directory, int descriptorSize, int nameOffset)
        {
            if (directory >= directories.Length || directories[directory].Rva == 0 || Offset(directories[directory].Rva) is not { } start)
                return;
            for (var i = 0; i < MaxImports; i++)
            {
                stream.Position = start + i * descriptorSize + nameOffset;
                var nameRva = reader.ReadUInt32();
                if (nameRva == 0)
                    break;
                var position = stream.Position;
                if (ReadString(nameRva) is { Length: > 0 } name)
                    imports.Add(name);
                stream.Position = position;
            }
        }

        // IMAGE_IMPORT_DESCRIPTOR (20 bytes, Name at +12), IMAGE_DELAYLOAD_DESCRIPTOR (32 bytes, DllNameRVA at +4).
        ReadDescriptors(ImportDirectory, 20, 12);
        ReadDescriptors(DelayImportDirectory, 32, 4);

        return new PeInfo(machine, ReadClrVersion(reader, directories, Offset), imports.Distinct().ToList())
        {
            FileVersion = ReadFileVersion(reader, directories, Offset),
            SubsystemVersion = subsystemVersion,
            ActiveXControls = ReadActiveXControls(reader, directories, Offset),
        };
    }

    /// <summary>
    /// Data (file offset, size) of the resources of one type: resource tree type → every name → every language.
    /// </summary>
    private static IEnumerable<(long Offset, uint Size)> Resources(
        BinaryReader reader, (uint Rva, uint Size)[] directories, Func<uint, long?> offset, int type, int max)
    {
        if (directories.Length <= ResourceDirectory || directories[ResourceDirectory].Rva == 0
            || offset(directories[ResourceDirectory].Rva) is not { } root)
            yield break;
        var stream = reader.BaseStream;
        const uint subdirectory = 0x80000000;

        // (name or id, offset field) of the entries of a resource directory.
        List<(uint Name, uint Target)> Entries(long directory)
        {
            stream.Position = directory + 12;
            var count = reader.ReadUInt16() + reader.ReadUInt16(); // named + id entries
            var entries = new List<(uint, uint)>();
            for (var i = 0; i < Math.Min(count, max); i++)
                entries.Add((reader.ReadUInt32(), reader.ReadUInt32()));
            return entries;
        }

        var types = Entries(root).Where(e => e.Name == type && (e.Target & subdirectory) != 0).Select(e => e.Target).ToList();
        if (types.Count == 0)
            yield break;
        var found = 0;
        foreach (var (_, names) in Entries(root + (types[0] & ~subdirectory)).Where(e => (e.Target & subdirectory) != 0).ToList())
        {
            foreach (var (_, language) in Entries(root + (names & ~subdirectory)).Where(e => (e.Target & subdirectory) == 0).ToList())
            {
                stream.Position = root + language;
                var dataRva = reader.ReadUInt32();
                var size = reader.ReadUInt32();
                if (offset(dataRva) is { } data)
                    yield return (data, size);
                if (++found >= max)
                    yield break;
            }
        }
    }

    /// <summary>First RT_VERSION resource, then VS_FIXEDFILEINFO.dwFileVersionMS/LS.</summary>
    private static Version? ReadFileVersion(BinaryReader reader, (uint Rva, uint Size)[] directories, Func<uint, long?> offset)
    {
        foreach (var (data, size) in Resources(reader, directories, offset, VersionResourceType, max: 1))
        {
            reader.BaseStream.Position = data;
            var bytes = reader.ReadBytes((int)Math.Min(size, 1024));
            for (var i = 0; i + 16 <= bytes.Length; i += 4)
            {
                if (BitConverter.ToUInt32(bytes, i) != FixedFileInfoSignature)
                    continue;
                var high = BitConverter.ToUInt32(bytes, i + 8);
                var low = BitConverter.ToUInt32(bytes, i + 12);
                return new Version((int)(high >> 16), (int)(high & 0xFFFF), (int)(low >> 16), (int)(low & 0xFFFF));
            }
        }
        return null;
    }

    /// <summary>
    /// CLSIDs in the dialog templates: the class of an ActiveX control (MFC), or the text of an "AtlAxWin" window (ATL),
    /// is "{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}" in UTF-16.
    /// </summary>
    private static IReadOnlyList<string> ReadActiveXControls(BinaryReader reader, (uint Rva, uint Size)[] directories, Func<uint, long?> offset)
    {
        var controls = new List<string>();
        foreach (var (data, size) in Resources(reader, directories, offset, DialogResourceType, MaxDialogs))
        {
            reader.BaseStream.Position = data;
            var bytes = reader.ReadBytes((int)Math.Min(size, MaxDialogSize));
            var text = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
            for (var start = text.IndexOf('{'); start >= 0 && start + 38 <= text.Length; start = text.IndexOf('{', start + 1))
            {
                if (Guid.TryParseExact(text.Substring(start, 38), "B", out var clsid))
                    controls.Add(clsid.ToString("B").ToUpperInvariant());
            }
        }
        return controls.Distinct().ToList();
    }

    /// <summary>CLR header → metadata root → version string ("v4.0.30319").</summary>
    private static string? ReadClrVersion(BinaryReader reader, (uint Rva, uint Size)[] directories, Func<uint, long?> offset)
    {
        if (directories.Length <= ClrDirectory || directories[ClrDirectory].Rva == 0 || offset(directories[ClrDirectory].Rva) is not { } clr)
            return null;
        var stream = reader.BaseStream;
        stream.Position = clr + 8; // cb, MajorRuntimeVersion, MinorRuntimeVersion
        var metadataRva = reader.ReadUInt32();
        if (offset(metadataRva) is not { } metadata)
            return "";
        stream.Position = metadata;
        if (reader.ReadUInt32() != 0x424A5342) // "BSJB"
            return "";
        stream.Position += 8; // major, minor, reserved
        var length = reader.ReadUInt32();
        var version = reader.ReadBytes((int)Math.Min(length, 255));
        var end = Array.IndexOf(version, (byte)0);
        return Encoding.ASCII.GetString(version, 0, end < 0 ? version.Length : end);
    }
}
