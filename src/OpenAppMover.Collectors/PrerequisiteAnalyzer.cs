using System.Text.Json;
using OpenAppMover.Core.Models;
using OpenAppMover.Core.Pe;
using OpenAppMover.Core.Platform;
using OpenAppMover.Core.Registry;
using OpenAppMover.Core.Text;

namespace OpenAppMover.Collectors;

/// <summary>A COM class registered on the source machine: its name and the file serving it (environment expanded).</summary>
public sealed record ComClass(string? Name, string? Server);

/// <summary>
/// Finds the runtimes an application needs from the DLLs its executables import, the ActiveX controls of their dialogs
/// and its .NET configuration.
/// </summary>
public static class PrerequisiteAnalyzer
{
    public const string FlashClsId = "{D27CDB6E-AE6D-11CF-96B8-444553540000}";

    private const int MaxBinaries = 3000;
    private const long MaxBinarySize = 256L * 1024 * 1024;

    private static readonly HashSet<string> VcRuntime140 = new(StringComparer.OrdinalIgnoreCase)
    {
        "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "msvcp140_atomic_wait.dll",
        "msvcp140_codecvt_ids.dll", "concrt140.dll", "vccorlib140.dll", "mfc140.dll", "mfc140u.dll", "vcomp140.dll",
    };

    /// <summary>DLL prefix → Visual C++ version (year).</summary>
    private static readonly (string Prefix, string Year)[] VcRuntimeLegacy =
    [
        ("msvcr120", "2013"), ("msvcp120", "2013"), ("mfc120", "2013"),
        ("msvcr110", "2012"), ("msvcp110", "2012"), ("mfc110", "2012"),
        ("msvcr100", "2010"), ("msvcp100", "2010"), ("mfc100", "2010"),
        ("msvcr90", "2008"), ("msvcp90", "2008"),
        ("msvcr80", "2005"), ("msvcp80", "2005"),
    ];

    private static readonly string[] DirectXPrefixes = ["d3dx9_", "d3dx10_", "d3dx11_", "xinput1_3", "xaudio2_7", "x3daudio1_7", "d3dcompiler_43"];

    private static readonly Dictionary<string, string> DotNetFrameworkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.NETCore.App"] = ".NET Runtime",
        ["Microsoft.WindowsDesktop.App"] = ".NET Desktop Runtime",
        ["Microsoft.AspNetCore.App"] = "ASP.NET Core Runtime",
    };

    /// <param name="findClass">
    /// COM class registered on the source machine (CLSID, registry view), null when it is not registered; without it, ActiveX
    /// controls are not analyzed.
    /// </param>
    public static IReadOnlyList<Prerequisite> Analyze(IFileSystem files, IEnumerable<string> roots, Func<string, RegView, ComClass?>? findClass = null)
    {
        var rootList = roots.ToList();
        var allFiles = rootList.SelectMany(r => files.EnumerateFiles(r, recursive: true)).ToList();
        var localNames = allFiles.Select(WinPath.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<Prerequisite>();

        var binaries = allFiles
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Take(MaxBinaries);
        var architectures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Highest minimum Windows version, with the reason.
        (Version Version, string Reason)? windows = null;
        void RequireWindows(Version version, string reason)
        {
            if (version > Windows7 && (windows is null || version > windows.Value.Version))
                windows = (version, reason);
        }

        foreach (var binary in binaries)
        {
            if (Read(files, binary) is not { } pe)
                continue;
            var name = WinPath.GetFileName(binary);
            var arch = pe.Architecture ?? "x64";
            architectures[binary] = arch;

            foreach (var import in pe.Imports.Where(i => !localNames.Contains(i)))
            {
                if (VcRuntime140.Contains(import))
                    found.Add(new Prerequisite(PrerequisiteKind.VcRuntime, $"Microsoft Visual C++ 2015-2022 Redistributable ({arch})", $"{name} imports {import}")
                        { Architecture = arch });
                else if (VcRuntimeLegacy.FirstOrDefault(v => import.StartsWith(v.Prefix, StringComparison.OrdinalIgnoreCase)) is { Year: not null } legacy)
                    found.Add(new Prerequisite(PrerequisiteKind.VcRuntimeLegacy, $"Microsoft Visual C++ {legacy.Year} Redistributable ({arch})",
                        $"{name} imports {import}") { Architecture = arch, Version = legacy.Year });
                else if (DirectXPrefixes.Any(p => import.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    found.Add(new Prerequisite(PrerequisiteKind.DirectX, "DirectX End-User Runtime (June 2010)", $"{name} imports {import}")
                        { Architecture = arch, Version = import });

                if (import.StartsWith("api-ms-win-crt-", StringComparison.OrdinalIgnoreCase) || import.Equals("ucrtbase.dll", StringComparison.OrdinalIgnoreCase))
                    found.Add(new Prerequisite(PrerequisiteKind.UniversalCrt, $"Universal C Runtime ({arch})", $"{name} imports {import}")
                        { Architecture = arch });
            }

            if (findClass is not null)
                found.AddRange(ActiveXControls(pe, name, arch, rootList, findClass));

            // Windows does not start an executable made for a later version than its own.
            if (pe.SubsystemVersion is { } subsystem && binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                RequireWindows(subsystem, $"{name} is made for {WindowsName(subsystem)} and later");

            // A managed .exe without runtimeconfig.json runs on the .NET Framework.
            if (pe.IsManaged && binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !files.FileExists(WinPath.ChangeExtension(binary, ".runtimeconfig.json")))
            {
                var v2 = pe.ClrVersion!.StartsWith("v2", StringComparison.OrdinalIgnoreCase);
                found.Add(new Prerequisite(PrerequisiteKind.NetFramework, v2 ? ".NET Framework 3.5" : ".NET Framework 4.x",
                    $"{name} is a .NET Framework application ({pe.ClrVersion})") { Version = v2 ? "v2.0.50727" : "v4.0.30319" });
            }
        }

        if (localNames.Contains("WebView2Loader.dll"))
            found.Add(new Prerequisite(PrerequisiteKind.WebView2, "Microsoft Edge WebView2 Runtime", "The application ships WebView2Loader.dll"));

        foreach (var config in allFiles.Where(f => f.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var prerequisite in ReadRuntimeConfig(files, config, architectures))
            {
                found.Add(prerequisite);
                // .NET 7 and later: Windows 10 and later (.NET 5 and 6 still run on Windows 7 SP1).
                if (System.Version.TryParse(prerequisite.Version, out var dotnet) && dotnet.Major >= 7)
                    RequireWindows(Windows10, $"{prerequisite.Name} needs {WindowsName(Windows10)} or later");
            }
        }

        if (windows is { } minimum)
            found.Add(new Prerequisite(PrerequisiteKind.Windows, $"{WindowsName(minimum.Version)} or later", minimum.Reason)
                { Version = minimum.Version.ToString(2) });

        return found
            .GroupBy(p => (p.Kind, p.Architecture, p.Version, p.Framework))
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// ActiveX controls of the dialogs that another product registered on the source machine. Controls that are not
    /// registered (the dialog is not used there) or that the application serves itself (moved with it) are left out.
    /// </summary>
    private static IEnumerable<Prerequisite> ActiveXControls(
        PeInfo pe, string binary, string arch, List<string> roots, Func<string, RegView, ComClass?> findClass)
    {
        var view = arch == "x86" ? RegView.Registry32 : RegView.Registry64;
        foreach (var clsid in pe.ActiveXControls)
        {
            if (findClass(clsid, view) is not { } com || (com.Server is { } server && roots.Any(r => WinPath.IsUnderOrEqual(server, r))))
                continue;
            var flash = clsid.Equals(FlashClsId, StringComparison.OrdinalIgnoreCase);
            var name = flash ? "Adobe Flash Player (ActiveX)" : $"ActiveX control '{com.Name ?? clsid}'";
            yield return new Prerequisite(PrerequisiteKind.ActiveX, $"{name} ({arch})",
                $"{binary} hosts the ActiveX control {clsid}" + (com.Server is null ? "" : $", served on the source PC by {com.Server}"))
            {
                Architecture = arch,
                Version = clsid,
                Note = flash ? FlashNote(arch) : null,
            };
        }
    }

    private static string FlashNote(string arch) =>
        "Flash Player reached its end of life in 2020: Windows 10 and 11 no longer include it (KB4577586) and its last versions "
        + "refuse to play any content. Copy the Flash .ocx file of the source PC to the same folder here and register it from an "
        + $@"elevated command prompt with %SystemRoot%\{(arch == "x86" ? "SysWOW64" : "System32")}\regsvr32.exe.";

    private static readonly Version Windows7 = new(6, 1);
    private static readonly Version Windows10 = new(10, 0);

    /// <summary>Marketing name of a Windows version (client editions).</summary>
    public static string WindowsName(Version version) => (version.Major, version.Minor) switch
    {
        (6, 0) => "Windows Vista",
        (6, 1) => "Windows 7",
        (6, 2) => "Windows 8",
        (6, 3) => "Windows 8.1",
        (10, _) => "Windows 10",
        _ => $"Windows {version.Major}.{version.Minor}",
    };

    private static PeInfo? Read(IFileSystem files, string path)
    {
        if (files.GetFileMeta(path) is not { Length: > 0 and <= MaxBinarySize })
            return null;
        try
        {
            using var stream = files.OpenRead(path);
            return PeReader.Read(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Framework-dependent .NET apps list their shared frameworks; self-contained ones list "includedFrameworks".</summary>
    private static IEnumerable<Prerequisite> ReadRuntimeConfig(IFileSystem files, string path, Dictionary<string, string> architectures)
    {
        JsonDocument document;
        try
        {
            using var stream = files.OpenRead(path);
            document = JsonDocument.Parse(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("runtimeOptions", out var options))
                yield break;
            var frameworks = new List<JsonElement>();
            if (options.TryGetProperty("framework", out var single))
                frameworks.Add(single);
            if (options.TryGetProperty("frameworks", out var many) && many.ValueKind == JsonValueKind.Array)
                frameworks.AddRange(many.EnumerateArray());

            // "App.runtimeconfig.json" → architecture of the "App.exe" host.
            var host = path[..^".runtimeconfig.json".Length] + ".exe";
            var arch = architectures.GetValueOrDefault(host, "x64");
            foreach (var framework in frameworks)
            {
                var name = framework.TryGetProperty("name", out var n) ? n.GetString() : null;
                var version = framework.TryGetProperty("version", out var v) ? v.GetString() : null;
                if (name is null || version is null)
                    continue;
                var majorMinor = string.Join(".", version.Split('.').Take(2));
                yield return new Prerequisite(PrerequisiteKind.DotNet,
                    $"{DotNetFrameworkNames.GetValueOrDefault(name, name)} {majorMinor} ({arch})",
                    $"{WinPath.GetFileName(path)} requires {name} {version}")
                {
                    Architecture = arch, Version = majorMinor, Framework = name,
                };
            }
        }
    }
}
