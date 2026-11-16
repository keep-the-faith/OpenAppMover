using System.Text;
using OpenAppMover.Collectors;
using OpenAppMover.Core.Models;
using OpenAppMover.Core.Pe;
using OpenAppMover.Core.Platform;
using OpenAppMover.Core.Registry;
using OpenAppMover.Deployment;
using OpenAppMover.Tests.Fakes;

namespace OpenAppMover.Tests;

public class PeReaderTests
{
    [Fact]
    public void ReadsNativeImports()
    {
        var pe = PeReader.Read(new MemoryStream(PeBuilder.Build(PeInfo.MachineX64, ["KERNEL32.dll", "VCRUNTIME140.dll"])));

        Assert.NotNull(pe);
        Assert.Equal("x64", pe.Architecture);
        Assert.False(pe.IsManaged);
        Assert.Equal(["kernel32.dll", "vcruntime140.dll"], pe.Imports);
    }

    [Fact]
    public void ReadsPe32AndClrVersion()
    {
        var pe = PeReader.Read(new MemoryStream(PeBuilder.Build(PeInfo.MachineX86, ["mscoree.dll"], "v4.0.30319")));

        Assert.Equal("x86", pe!.Architecture);
        Assert.Equal("v4.0.30319", pe.ClrVersion);
    }

    [Fact]
    public void ReadsTheFileVersion()
    {
        Assert.Equal(new Version(2, 1, 30, 4), PeReader.Read(new MemoryStream(PeBuilder.Build(PeInfo.MachineX64, ["kernel32.dll"], version: new Version(2, 1, 30, 4))))!.FileVersion);
        Assert.Null(PeReader.Read(new MemoryStream(PeBuilder.Build(PeInfo.MachineX64, ["kernel32.dll"])))!.FileVersion);
    }

    [Fact]
    public void ReadsTheSubsystemVersion()
    {
        Assert.Equal(new Version(6, 2), PeReader.Read(new MemoryStream(PeBuilder.Build(PeInfo.MachineX64, ["kernel32.dll"], subsystem: new Version(6, 2))))!.SubsystemVersion);
        Assert.Equal(new Version(5, 1), PeReader.Read(new MemoryStream(PeBuilder.Build(PeInfo.MachineX86, ["kernel32.dll"], subsystem: new Version(5, 1))))!.SubsystemVersion);
    }

    [Fact]
    public void RejectsOtherFiles()
    {
        Assert.Null(PeReader.Read(new MemoryStream(Encoding.ASCII.GetBytes("MZ but nothing else"))));
        Assert.Null(PeReader.Read(new MemoryStream(new byte[4096])));
    }
}

public class PrerequisiteAnalyzerTests
{
    private const string App = @"C:\Program Files\App";
    private readonly FakeFileSystem _files = new();

    private IReadOnlyList<Prerequisite> Analyze() => PrerequisiteAnalyzer.Analyze(_files, [App]);

    [Fact]
    public void VisualCppAndDirectX()
    {
        _files
            .Add($@"{App}\app.exe", PeBuilder.Build(PeInfo.MachineX64, ["kernel32.dll", "vcruntime140.dll", "msvcp140.dll"]))
            .Add($@"{App}\tools\helper32.exe", PeBuilder.Build(PeInfo.MachineX86, ["msvcr120.dll"]))
            .Add($@"{App}\render.dll", PeBuilder.Build(PeInfo.MachineX64, ["d3dx9_43.dll"]))
            .Add($@"{App}\readme.txt", "not a binary");

        var prerequisites = Analyze();

        Assert.Equal(3, prerequisites.Count);
        Assert.Contains(prerequisites, p => p is { Kind: PrerequisiteKind.VcRuntime, Architecture: "x64" });
        Assert.Contains(prerequisites, p => p is { Kind: PrerequisiteKind.VcRuntimeLegacy, Architecture: "x86", Version: "2013" });
        Assert.Contains(prerequisites, p => p is { Kind: PrerequisiteKind.DirectX, Version: "d3dx9_43.dll" });
    }

    [Fact]
    public void AppLocalRuntimeIsNotAPrerequisite()
    {
        _files
            .Add($@"{App}\app.exe", PeBuilder.Build(PeInfo.MachineX64, ["vcruntime140.dll"]))
            .Add($@"{App}\vcruntime140.dll", PeBuilder.Build(PeInfo.MachineX64, ["kernel32.dll"]));

        Assert.Empty(Analyze());
    }

    [Fact]
    public void DotNetFrameworkAndDotNet()
    {
        _files
            .Add($@"{App}\legacy.exe", PeBuilder.Build(PeInfo.MachineX86, ["mscoree.dll"], "v4.0.30319"))
            .Add($@"{App}\Modern.exe", PeBuilder.Build(PeInfo.MachineX64, ["kernel32.dll"]))
            .Add($@"{App}\Modern.dll", PeBuilder.Build(PeInfo.MachineX86, ["mscoree.dll"], "v4.0.30319"))
            .Add($@"{App}\Modern.runtimeconfig.json",
                """{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.WindowsDesktop.App", "version": "8.0.0" } } }""")
            .Add($@"{App}\Standalone.runtimeconfig.json",
                """{ "runtimeOptions": { "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "8.0.11" } ] } }""");

        var prerequisites = Analyze();

        Assert.Equal(3, prerequisites.Count);
        Assert.Contains(prerequisites, p => p is { Kind: PrerequisiteKind.NetFramework, Name: ".NET Framework 4.x" });
        var dotnet = Assert.Single(prerequisites, p => p.Kind == PrerequisiteKind.DotNet);
        Assert.Equal(("Microsoft.WindowsDesktop.App", "8.0", "x64"), (dotnet.Framework, dotnet.Version, dotnet.Architecture));
        Assert.Equal(".NET Desktop Runtime 8.0 (x64)", dotnet.Name);
        // .NET 7 and later do not run on Windows 7.
        var windows = Assert.Single(prerequisites, p => p.Kind == PrerequisiteKind.Windows);
        Assert.Equal(("10.0", "Windows 10 or later"), (windows.Version, windows.Name));
    }

    [Fact]
    public void DotNet6StillRunsOnWindows7()
    {
        _files.Add($@"{App}\App.runtimeconfig.json",
            """{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "6.0.36" } } }""");

        Assert.Equal(PrerequisiteKind.DotNet, Assert.Single(Analyze()).Kind);
    }

    [Fact]
    public void MinimumWindowsOfTheExecutables()
    {
        _files
            .Add($@"{App}\old.exe", PeBuilder.Build(PeInfo.MachineX64, ["kernel32.dll"], subsystem: new Version(6, 0)))
            .Add($@"{App}\new.exe", PeBuilder.Build(PeInfo.MachineX64, ["kernel32.dll"], subsystem: new Version(6, 2)))
            .Add($@"{App}\newer.dll", PeBuilder.Build(PeInfo.MachineX64, ["kernel32.dll"], subsystem: new Version(10, 0)));

        var windows = Assert.Single(Analyze());
        Assert.Equal((PrerequisiteKind.Windows, "6.2", "Windows 8 or later"), (windows.Kind, windows.Version, windows.Name));
        Assert.Equal("new.exe is made for Windows 8 and later", windows.Reason);
    }

    [Fact]
    public void UniversalCrt()
    {
        _files
            .Add($@"{App}\app.exe", PeBuilder.Build(PeInfo.MachineX64, ["kernel32.dll", "api-ms-win-crt-runtime-l1-1-0.dll", "api-ms-win-crt-heap-l1-1-0.dll"]))
            .Add($@"{App}\x86\tool.exe", PeBuilder.Build(PeInfo.MachineX86, ["ucrtbase.dll"]));

        var crts = Analyze();
        Assert.All(crts, p => Assert.Equal(PrerequisiteKind.UniversalCrt, p.Kind));
        Assert.Equal(["x64", "x86"], crts.Select(p => p.Architecture).Order());
    }

    [Fact]
    public void WebView2()
    {
        _files.Add($@"{App}\WebView2Loader.dll", PeBuilder.Build(PeInfo.MachineX64, ["kernel32.dll"]));

        Assert.Equal(PrerequisiteKind.WebView2, Assert.Single(Analyze()).Kind);
    }
}

public class PrerequisiteCheckerTests
{
    private readonly FakeRegistry _registry = new();
    private readonly FakeFileSystem _files = new();
    private readonly PrerequisiteChecker _checker;

    public PrerequisiteCheckerTests()
    {
        var machine = new MachineFolders
        {
            Windows = @"C:\Windows", System32 = @"C:\Windows\System32", SysWow64 = @"C:\Windows\SysWOW64",
            ProgramFiles = @"C:\Program Files", ProgramFilesX86 = @"C:\Program Files (x86)", ProgramData = @"C:\ProgramData",
            Public = @"C:\Users\Public", CommonDesktop = @"C:\Users\Public\Desktop", CommonStartMenu = @"C:\ProgramData\Microsoft\Windows\Start Menu",
        };
        _checker = new PrerequisiteChecker(_registry, _files, machine);
    }

    private static Prerequisite Of(PrerequisiteKind kind, string? arch = null, string? version = null, string? framework = null) =>
        new(kind, kind.ToString(), "test") { Architecture = arch, Version = version, Framework = framework };

    [Fact]
    public void VisualCpp()
    {
        _files.Add(@"C:\Windows\System32\vcruntime140.dll").Add(@"C:\Windows\SysWOW64\msvcr120.dll");

        Assert.True(_checker.IsInstalled(Of(PrerequisiteKind.VcRuntime, "x64")));
        Assert.False(_checker.IsInstalled(Of(PrerequisiteKind.VcRuntime, "x86")));
        Assert.True(_checker.IsInstalled(Of(PrerequisiteKind.VcRuntimeLegacy, "x86", "2013")));
        Assert.Null(_checker.IsInstalled(Of(PrerequisiteKind.VcRuntimeLegacy, "x64", "2008")));
    }

    [Fact]
    public void DotNet()
    {
        _files.AddDirectory(@"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.11");

        Assert.True(_checker.IsInstalled(Of(PrerequisiteKind.DotNet, "x64", "8.0", "Microsoft.WindowsDesktop.App")));
        Assert.False(_checker.IsInstalled(Of(PrerequisiteKind.DotNet, "x64", "6.0", "Microsoft.WindowsDesktop.App")));
        Assert.False(_checker.IsInstalled(Of(PrerequisiteKind.DotNet, "x86", "8.0", "Microsoft.WindowsDesktop.App")));
    }

    [Fact]
    public void NetFrameworkAndWebView2()
    {
        Assert.False(_checker.IsInstalled(Of(PrerequisiteKind.NetFramework, version: "v4.0.30319")));
        Assert.False(_checker.IsInstalled(Of(PrerequisiteKind.WebView2)));

        _registry
            .Set(RegHive.LocalMachine, @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", RegValue.DWord("Release", 533320))
            .Set(RegHive.LocalMachine, RegView.Registry32, @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                RegValue.String("pv", "131.0.2903.70"));

        Assert.True(_checker.IsInstalled(Of(PrerequisiteKind.NetFramework, version: "v4.0.30319")));
        Assert.False(_checker.IsInstalled(Of(PrerequisiteKind.NetFramework, version: "v2.0.50727")));
        Assert.True(_checker.IsInstalled(Of(PrerequisiteKind.WebView2)));
    }

    [Fact]
    public void WindowsVersionOnWindows7()
    {
        Assert.Null(_checker.IsInstalled(Of(PrerequisiteKind.Windows, version: "6.2")));

        _registry.Set(RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", RegValue.String("CurrentVersion", "6.1"));

        Assert.True(_checker.IsInstalled(Of(PrerequisiteKind.Windows, version: "6.1")));
        Assert.False(_checker.IsInstalled(Of(PrerequisiteKind.Windows, version: "6.2")));
        Assert.False(_checker.IsInstalled(Of(PrerequisiteKind.Windows, version: "10.0")));
    }

    [Fact]
    public void WindowsVersionOnWindows10()
    {
        // CurrentVersion stays "6.3" on Windows 10 and 11.
        _registry
            .Set(RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", RegValue.String("CurrentVersion", "6.3"))
            .Set(RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", RegValue.DWord("CurrentMajorVersionNumber", 10))
            .Set(RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", RegValue.DWord("CurrentMinorVersionNumber", 0));

        Assert.True(_checker.IsInstalled(Of(PrerequisiteKind.Windows, version: "10.0")));
    }

    [Fact]
    public void UniversalCrt()
    {
        _files.Add(@"C:\Windows\System32\ucrtbase.dll");

        Assert.True(_checker.IsInstalled(Of(PrerequisiteKind.UniversalCrt, "x64")));
        Assert.False(_checker.IsInstalled(Of(PrerequisiteKind.UniversalCrt, "x86")));
    }
}
