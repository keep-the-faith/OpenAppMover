using OpenAppMover.Core.Models;
using OpenAppMover.Core.Platform;
using OpenAppMover.Core.Registry;
using OpenAppMover.Core.Text;

namespace OpenAppMover.Deployment;

/// <summary>Checks whether the runtimes needed by an application are installed on this machine.</summary>
public sealed class PrerequisiteChecker(IRegistrySource registry, IFileSystem files, MachineFolders machine)
{
    private const string WebView2ClientKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static readonly Dictionary<string, string> LegacyVcDlls = new()
    {
        ["2013"] = "msvcr120.dll", ["2012"] = "msvcr110.dll", ["2010"] = "msvcr100.dll",
    };

    /// <summary>True / false, or null when it cannot be checked (e.g. side-by-side Visual C++ 2005/2008).</summary>
    public bool? IsInstalled(Prerequisite prerequisite) => prerequisite.Kind switch
    {
        PrerequisiteKind.VcRuntime => SystemFileExists("vcruntime140.dll", prerequisite.Architecture),
        PrerequisiteKind.VcRuntimeLegacy => LegacyVcDlls.TryGetValue(prerequisite.Version ?? "", out var dll)
            ? SystemFileExists(dll, prerequisite.Architecture)
            : null,
        PrerequisiteKind.DirectX => prerequisite.Version is { } file ? SystemFileExists(file, prerequisite.Architecture) : null,
        PrerequisiteKind.NetFramework => prerequisite.Version?.StartsWith("v2", StringComparison.OrdinalIgnoreCase) == true
            ? NumberValue(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5", "Install") == 1
            : NumberValue(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release") is not null,
        PrerequisiteKind.DotNet => DotNetInstalled(prerequisite),
        PrerequisiteKind.WebView2 => WebView2Installed(),
        PrerequisiteKind.Windows => Version.TryParse(prerequisite.Version, out var minimum) && WindowsVersion() is { } windows
            ? windows >= minimum
            : null,
        PrerequisiteKind.UniversalCrt => SystemFileExists("ucrtbase.dll", prerequisite.Architecture),
        PrerequisiteKind.ActiveX => prerequisite.Version is { } clsid ? ComClassInstalled(clsid, prerequisite.Architecture) : null,
        _ => null,
    };

    /// <summary>
    /// Version of this Windows (major.minor). CurrentVersion stays "6.3" on Windows 10 and later, which have
    /// CurrentMajorVersionNumber.
    /// </summary>
    private Version? WindowsVersion()
    {
        using var key = registry.OpenKey(RegHive.LocalMachine, RegView.Registry64, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (key?.GetValue("CurrentMajorVersionNumber")?.AsNumber() is { } major)
            return new Version((int)major, (int)(key.GetValue("CurrentMinorVersionNumber")?.AsNumber() ?? 0));
        return Version.TryParse(key?.GetString("CurrentVersion"), out var version) ? version : null;
    }

    /// <summary>32-bit DLLs live in SysWOW64 on 64-bit Windows.</summary>
    private bool SystemFileExists(string fileName, string? architecture) =>
        files.FileExists(WinPath.Combine(architecture == "x86" ? machine.SysWow64 : machine.System32, fileName));

    /// <summary>
    /// The class is registered for the machine in the view of the application, and its server file exists (null when its
    /// path cannot be resolved here).
    /// </summary>
    private bool? ComClassInstalled(string clsid, string? architecture)
    {
        var view = architecture == "x86" ? RegView.Registry32 : RegView.Registry64;
        using var key = registry.OpenKey(RegHive.LocalMachine, view, $@"SOFTWARE\Classes\CLSID\{clsid}");
        if (key is null)
            return false;
        using var inproc = key.OpenSubKey("InprocServer32");
        using var local = key.OpenSubKey("LocalServer32");
        var server = inproc?.GetString("") is { Length: > 0 } dll ? dll.Trim('"') : CommandLine.ExtractExecutable(local?.GetString(""));
        if (server is null)
            return false;
        var path = ExpandSystemVariables(server, view);
        var absolute = path.Length > 2 && path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal);
        return path.Contains('%') || !absolute ? null : files.FileExists(path);
    }

    /// <summary>The variables found in COM registrations, for this machine's folders.</summary>
    private string ExpandSystemVariables(string path, RegView view)
    {
        string Replace(string text, string variable, string value) => text.Replace($"%{variable}%", value, StringComparison.OrdinalIgnoreCase);
        var programFiles = view == RegView.Registry32 ? machine.ProgramFilesX86 : machine.ProgramFiles;
        path = Replace(path, "SystemRoot", machine.Windows);
        path = Replace(path, "windir", machine.Windows);
        path = Replace(path, "SystemDrive", machine.Windows[..2]);
        path = Replace(path, "ProgramFiles(x86)", machine.ProgramFilesX86);
        path = Replace(path, "ProgramW6432", machine.ProgramFiles);
        path = Replace(path, "ProgramFiles", programFiles);
        path = Replace(path, "CommonProgramFiles(x86)", WinPath.Combine(machine.ProgramFilesX86, "Common Files"));
        path = Replace(path, "CommonProgramFiles", WinPath.Combine(programFiles, "Common Files"));
        return path;
    }

    /// <summary>%ProgramFiles%\dotnet\shared\&lt;framework&gt;\&lt;major.minor&gt;.x</summary>
    private bool? DotNetInstalled(Prerequisite prerequisite)
    {
        if (prerequisite.Framework is null || prerequisite.Version is null)
            return null;
        var root = prerequisite.Architecture == "x86" ? machine.ProgramFilesX86 : machine.ProgramFiles;
        var shared = WinPath.Combine(root, $@"dotnet\shared\{prerequisite.Framework}");
        return files.EnumerateDirectories(shared)
            .Any(d => WinPath.GetFileName(d).StartsWith(prerequisite.Version + ".", StringComparison.OrdinalIgnoreCase));
    }

    private bool WebView2Installed()
    {
        foreach (var view in new[] { RegView.Registry32, RegView.Registry64 })
        {
            using var key = registry.OpenKey(RegHive.LocalMachine, view, WebView2ClientKey);
            if (key?.GetString("pv") is { Length: > 0 } version && version != "0.0.0.0")
                return true;
        }
        return false;
    }

    private long? NumberValue(string path, string name)
    {
        using var key = registry.OpenKey(RegHive.LocalMachine, RegView.Registry64, path);
        return key?.GetValue(name)?.AsNumber();
    }
}
