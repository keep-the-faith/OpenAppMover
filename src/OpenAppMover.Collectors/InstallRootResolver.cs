using System.Text.RegularExpressions;
using OpenAppMover.Core.Models;
using OpenAppMover.Core.Msi;
using OpenAppMover.Core.Platform;
using OpenAppMover.Core.Text;

namespace OpenAppMover.Collectors;

/// <summary>Finds the install directories of an application from its Uninstall entry and installer data.</summary>
public static partial class InstallRootResolver
{
    public static IReadOnlyList<InstallRoot> Resolve(CollectionContext context)
    {
        var app = context.App;
        var owner = context.Session.FindUser(app.UserSid);
        var candidates = new List<(string Path, Confidence Confidence, string Reason)>();

        void Add(string? path, Confidence confidence, string reason)
        {
            if (!string.IsNullOrWhiteSpace(path))
                candidates.Add((WinPath.Normalize(context.Expand(path, owner)), confidence, reason));
        }

        Add(app.InstallLocation, Confidence.Certain, "InstallLocation of the Uninstall entry");
        if (app.Installer?.Properties.GetValueOrDefault(InstallerProperties.InstallerAppPath) is { } innoPath)
            Add(innoPath, Confidence.Certain, "Install path declared by Inno Setup");
        foreach (var msi in context.MsiProducts)
            Add(msi.Registration.InstallLocation, Confidence.Certain, "InstallLocation registered by Windows Installer");
        if (app.DisplayIcon is { } icon && !IsInstallerCache(icon))
            Add(WinPath.GetDirectoryName(StripIconIndex(icon)) ?? "", Confidence.Probable, "Folder of the DisplayIcon");
        if (app.Installer?.UninstallerPath is { } uninstaller && !IsInstallerCache(uninstaller))
            Add(WinPath.GetDirectoryName(uninstaller), Confidence.Probable, "Folder of the uninstaller");
        foreach (var msi in context.MsiProducts)
        {
            foreach (var folder in ComponentFolders(context, msi))
                Add(folder, Confidence.Probable, "Common folder of the Windows Installer components");
        }

        var accepted = candidates
            .Where(c => IsAcceptable(c.Path, context))
            .ToList();
        accepted.AddRange(OtherSystemFolders(accepted, context));

        // Merge nested folders into their parent, keeping the best confidence. The first candidate is the primary one.
        var roots = new List<(string Path, Confidence Confidence, string Reason, bool Primary)>();
        foreach (var (candidate, index) in accepted.Select((c, i) => (c, i)).OrderBy(x => x.c.Path.Length))
        {
            var parent = roots.FindIndex(r => WinPath.IsUnderOrEqual(candidate.Path, r.Path));
            if (parent < 0)
            {
                roots.Add((candidate.Path, candidate.Confidence, candidate.Reason, index == 0));
                continue;
            }
            var r = roots[parent];
            roots[parent] = (r.Path, (Confidence)Math.Max((int)r.Confidence, (int)candidate.Confidence), r.Reason, r.Primary || index == 0);
        }

        // Registry values often have another case than the disk ("c:\gestcime" for C:\GESTCIME): the package and the
        // deployment use the folder name as it is on the disk.
        return roots.Select(r => new InstallRoot(context.Files.GetActualDirectoryCase(r.Path), r.Confidence, r.Reason, r.Primary)).ToList();
    }

    /// <summary>
    /// Folders holding the components of an MSI product: per known folder (Program Files, AppData…), the common folder
    /// of its components, or each component folder when the common folder is the known folder itself (nested folders are
    /// merged afterwards). Components in the Windows folder or shared with other products never define application folders.
    /// </summary>
    private static IEnumerable<string> ComponentFolders(CollectionContext context, MsiProduct msi)
    {
        var machine = context.Session.Machine;
        var known = machine.All().Select(f => f.Path)
            .Concat(context.Session.Users.SelectMany(u => u.Folders.All().Select(f => f.Path)))
            .ToList();

        var folders = msi.Components
            .Where(c => !c.Shared)
            .Select(c => c.KeyPath.Kind switch
            {
                MsiKeyPathKind.File => WinPath.GetDirectoryName(c.KeyPath.Path!),
                MsiKeyPathKind.Directory => c.KeyPath.Path,
                _ => null,
            })
            .OfType<string>()
            .Select(WinPath.Normalize)
            // Only absolute drive paths ("C:\…"): they are grouped by their drive below.
            .Where(f => f.Length >= 3 && f[1] == ':' && !WinPath.IsUnderOrEqual(f, machine.Windows))
            .Distinct(WinPath.Comparer);

        // Group by the deepest known folder containing them (or the drive).
        var groups = folders.GroupBy(f =>
            known.Where(k => WinPath.IsUnderOrEqual(f, k)).OrderByDescending(k => k.Length).FirstOrDefault() ?? f[..3], WinPath.Comparer);

        foreach (var group in groups)
        {
            string? common = group.First();
            foreach (var folder in group.Skip(1))
                common = CommonFolder(common, folder);
            if (common is not null && !WinPath.IsUnderOrEqual(group.Key, common))
            {
                yield return common;
                continue;
            }
            foreach (var folder in group.Where(f => !f.Equals(group.Key, StringComparison.OrdinalIgnoreCase)))
                yield return folder;
        }
    }

    private static string? CommonFolder(string? a, string b)
    {
        if (a is null)
            return null;
        var left = a.Split('\\');
        var right = b.Split('\\');
        var count = 0;
        while (count < left.Length && count < right.Length && left[count].Equals(right[count], StringComparison.OrdinalIgnoreCase))
            count++;
        return count == 0 ? null : WinPath.Normalize(string.Join("\\", left.Take(count)));
    }

    /// <summary>
    /// Rejects missing folders, folders that are too broad (drive root, Program Files, a user profile…) and the Windows
    /// folder, except an application's own folder in the system folders (<see cref="IsVendorSystemFolder"/>).
    /// </summary>
    private static bool IsAcceptable(string path, CollectionContext context) =>
        !context.IsTooBroad(path) && context.Files.DirectoryExists(path)
        && (!WinPath.IsUnderOrEqual(path, context.Session.Machine.Windows) || IsVendorSystemFolder(path, context.Session.Machine));

    /// <summary>Folders of Windows itself in System32 / SysWOW64, which can hold the uninstaller of a driver or a component.</summary>
    private static readonly HashSet<string> WindowsSystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "drivers", "DriverStore", "spool", "wbem", "WindowsPowerShell", "catroot", "catroot2", "config", "LogFiles", "Tasks",
        "Microsoft", "com", "inetsrv", "Sysprep", "oobe", "GroupPolicy", "GroupPolicyUsers", "migration", "wdi", "winevt",
        "CodeIntegrity", "Speech", "Speech_OneCore", "ias", "Dism", "MUI", "IME", "SMI", "Boot", "Recovery", "restore",
        "NetworkList", "AdvancedInstallers", "appmgmt", "downlevel", "lxss", "WinMetadata", "Printing_Admin_Scripts", "Temp",
    };

    /// <summary>
    /// "vendor\product" folder of a component installed in System32 or SysWOW64 (Adobe Flash Player ActiveX:
    /// SysWOW64\Macromed\Flash), outside the folders of Windows itself and the language folders (en-US…).
    /// </summary>
    private static bool IsVendorSystemFolder(string path, MachineFolders machine)
    {
        foreach (var system in new[] { machine.System32, machine.SysWow64 })
        {
            if (!WinPath.IsUnderOrEqual(path, system) || WinPath.Normalize(path).Length <= WinPath.Normalize(system).Length)
                continue;
            var relative = WinPath.Normalize(path)[(WinPath.Normalize(system).Length + 1)..].Split('\\');
            return relative.Length >= 2 && !WindowsSystemFolders.Contains(relative[0]) && !LanguageFolder().IsMatch(relative[0]);
        }
        return false;
    }

    /// <summary>
    /// A component installed in SysWOW64\Vendor\Product usually has its 64-bit part in System32\Vendor\Product (and the
    /// other way round), outside the paths of its Uninstall entry.
    /// </summary>
    private static IEnumerable<(string Path, Confidence Confidence, string Reason)> OtherSystemFolders(
        List<(string Path, Confidence Confidence, string Reason)> accepted, CollectionContext context)
    {
        var machine = context.Session.Machine;
        foreach (var (path, _, _) in accepted.Where(c => IsVendorSystemFolder(c.Path, machine)).ToList())
        {
            var (from, to, part) = WinPath.IsUnderOrEqual(path, machine.SysWow64)
                ? (machine.SysWow64, machine.System32, "64-bit")
                : (machine.System32, machine.SysWow64, "32-bit");
            var other = WinPath.Combine(to, WinPath.Normalize(path)[(WinPath.Normalize(from).Length + 1)..]);
            if (IsAcceptable(other, context) && !accepted.Any(c => WinPath.IsUnderOrEqual(other, c.Path)))
                yield return (other, Confidence.Probable, $"Same folder in {WinPath.GetFileName(to)} ({part} part)");
        }
    }

    /// <summary>Installer caches hold a copy of the setup, not the application.</summary>
    private static bool IsInstallerCache(string path) =>
        path.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\InstallShield Installation Information\", StringComparison.OrdinalIgnoreCase);

    /// <summary>"C:\App\app.exe,0" → "C:\App\app.exe".</summary>
    public static string StripIconIndex(string icon) => IconIndex().Replace(icon.Trim().Trim('"'), "").Trim('"');

#if NET7_0_OR_GREATER
    [GeneratedRegex(@"^[a-z]{2,3}(-[a-z0-9]+)+$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageFolder();
#else
    private static Regex? _languageFolder;
    private static Regex LanguageFolder() => _languageFolder ??= new(@"^[a-z]{2,3}(-[a-z0-9]+)+$", RegexOptions.IgnoreCase);
#endif

#if NET7_0_OR_GREATER
    [GeneratedRegex(@",\s*-?\d+\s*$")]
    private static partial Regex IconIndex();
#else
    private static Regex? _iconIndex;
    private static Regex IconIndex() => _iconIndex ??= new(@",\s*-?\d+\s*$");
#endif
}
