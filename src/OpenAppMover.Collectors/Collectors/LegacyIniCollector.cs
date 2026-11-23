using OpenAppMover.Core.Models;
using OpenAppMover.Core.Pe;
using OpenAppMover.Core.Text;
using static OpenAppMover.Collectors.CollectionContext;

namespace OpenAppMover.Collectors.Collectors;

/// <summary>
/// Settings of legacy applications in the Windows folder: an .ini file given without a path to GetPrivateProfileString /
/// WritePrivateProfileString is %WINDIR%\name.ini, redirected by UAC to %LOCALAPPDATA%\VirtualStore\Windows when the
/// application writes it without admin rights.
/// </summary>
public sealed class LegacyIniCollector : ICollector
{
    private const int MaxBinaries = 500;
    private const long MaxBinarySize = 64L * 1024 * 1024;

    /// <summary>Files of Windows itself, that applications also read or write.</summary>
    private static readonly HashSet<string> WindowsIniFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "win.ini", "system.ini", "control.ini", "desktop.ini", "progman.ini", "winfile.ini", "protocol.ini", "odbc.ini",
        "odbcinst.ini", "msdfmap.ini", "mapisvc.ini", "dosapp.ini", "wininit.ini", "setup.ini", "boot.ini",
    };

    public string Name => "Legacy .ini";
    public CollectionPhase Phase => CollectionPhase.Main;

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // .ini → binary naming it
        var binaries = context.InstallRoots
            .SelectMany(r => context.Files.EnumerateFiles(r.Path, recursive: true))
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Take(MaxBinaries);
        foreach (var binary in binaries)
        {
            foreach (var name in ReadIniNames(context, binary).Where(n => !WindowsIniFiles.Contains(n)))
                names.TryAdd(name, WinPath.GetFileName(binary));
        }

        var windows = context.Session.Machine.Windows;
        foreach (var (name, binary) in names)
        {
            var reason = $"{name} named without a folder in {binary}: Windows keeps it in its own folder";
            var path = WinPath.Combine(windows, name);
            if (context.Files.FileExists(path))
                yield return Item(Name, ItemKind.File, ItemRole.Settings, Confidence.Probable, reason, path: path);

            // C:\Windows\name.ini → %LOCALAPPDATA%\VirtualStore\Windows\name.ini
            foreach (var user in context.Session.Users)
            {
                var virtualPath = WinPath.Combine(WinPath.Combine(user.Folders.LocalAppData, "VirtualStore"), WinPath.Combine(windows[3..], name));
                if (context.Files.FileExists(virtualPath))
                    yield return Item(Name, ItemKind.File, ItemRole.Settings, Confidence.Probable, reason + " (redirected by UAC)",
                        path: virtualPath, user: user);
            }
        }
    }

    private static IReadOnlyList<string> ReadIniNames(CollectionContext context, string path)
    {
        if (context.Files.GetFileMeta(path) is not { Length: > 0 and <= MaxBinarySize })
            return [];
        try
        {
            using var stream = context.Files.OpenRead(path);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return BinaryStrings.FindIniFileNames(memory.ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
