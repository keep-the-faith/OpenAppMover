using System.Text;
using OpenAppMover.Collectors;
using OpenAppMover.Core.Models;
using OpenAppMover.Core.Pe;
using OpenAppMover.Core.Registry;
using OpenAppMover.Deployment;
using OpenAppMover.Packaging;
using OpenAppMover.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static OpenAppMover.Tests.Fakes.TestMachine;

namespace OpenAppMover.Tests;

public class BinaryStringsTests
{
    private static IReadOnlyList<string> Find(byte[] data) => BinaryStrings.FindIniFileNames(data);

    [Fact]
    public void FindsBareIniNamesInAnsiAndUtf16()
    {
        var data = new List<byte> { 0x4D, 0x5A, 0 };
        data.AddRange(Encoding.ASCII.GetBytes("Param\0budget.ini\0"));
        data.AddRange(Encoding.Unicode.GetBytes("\0Votre Budget.INI\0"));
        data.AddRange(Encoding.ASCII.GetBytes("\0%s.ini\0C:\\App\\app.ini\0.ini\0abc.ini.bak\0win.ini"));

        // "%s.ini" (format), a path, a bare extension, another extension and an unterminated string are left out.
        Assert.Equal(["budget.ini", "votre budget.ini"], Find(data.ToArray()));
    }

    [Fact]
    public void NothingInAnEmptyFile() => Assert.Empty(Find([]));
}

/// <summary>
/// A legacy application: an ActiveX control in a dialog (Flash, registered by another product), settings in
/// %WINDIR%\acme.ini (and its UAC VirtualStore copy).
/// </summary>
public sealed class LegacyApplicationTests : IDisposable
{
    private const string Alice = "S-1-5-21-9-9-9-1001";
    private const string AppLocalClsId = "{AAAAAAAA-0000-0000-0000-000000000001}";
    private const string UnregisteredClsId = "{AAAAAAAA-0000-0000-0000-000000000002}";
    private const string FlashOcx = @"C:\Windows\SysWOW64\Macromed\Flash\Flash32_11_2_202_235.ocx";
    private const string Classes32 = @"SOFTWARE\Classes\CLSID";

    private readonly TestMachine _source = new TestMachine().WithAcmeEditor();
    private readonly TestMachine _target = new(Alice, "alice");
    private readonly string _packagePath = Path.Combine(AppContext.BaseDirectory, "test-output", $"{Guid.NewGuid():N}{PackageManifest.Extension}");

    public LegacyApplicationTests()
    {
        var legacy = PeBuilder.Build(PeInfo.MachineX86, ["kernel32.dll"], activeX: [PrerequisiteAnalyzer.FlashClsId, AppLocalClsId, UnregisteredClsId])
            .Concat(Encoding.ASCII.GetBytes("\0acme.ini\0win.ini\0missing.ini\0")).ToArray();
        _source.Files
            .Add($@"{InstallDir}\legacy.exe", legacy)
            .Add($@"{InstallDir}\control.ocx", "OCX")
            .Add(FlashOcx, "FLASH")
            .Add(@"C:\Windows\acme.ini", "[Options]\r\nSound=1\r\n")
            .Add(@"C:\Windows\win.ini", "[fonts]\r\n")
            .Add(@"C:\Users\bob\AppData\Local\VirtualStore\Windows\acme.ini", "[Options]\r\nSound=0\r\n");
        _source.Registry
            .Set(RegHive.LocalMachine, RegView.Registry32, $@"{Classes32}\{PrerequisiteAnalyzer.FlashClsId}", RegValue.String("", "Shockwave Flash Object"))
            .Set(RegHive.LocalMachine, RegView.Registry32, $@"{Classes32}\{PrerequisiteAnalyzer.FlashClsId}\InprocServer32", RegValue.String("", FlashOcx))
            .Set(RegHive.LocalMachine, RegView.Registry32, $@"{Classes32}\{AppLocalClsId}\InprocServer32", RegValue.String("", $@"{InstallDir}\control.ocx"));
    }

    public void Dispose() => File.Delete(_packagePath);

    private CollectionResult Collect()
    {
        using var session = _source.OpenSession();
        return _source.CollectionService().Collect(_source.App, session);
    }

    [Fact]
    public void ActiveXControlOfAnotherProductIsAPrerequisite()
    {
        var activeX = Assert.Single(Collect().Prerequisites, p => p.Kind == PrerequisiteKind.ActiveX);

        Assert.Equal(("Adobe Flash Player (ActiveX) (x86)", "x86", PrerequisiteAnalyzer.FlashClsId), (activeX.Name, activeX.Architecture, activeX.Version));
        Assert.Contains("legacy.exe", activeX.Reason);
        Assert.True(activeX.Reason.Contains(FlashOcx), activeX.Reason);
        Assert.Contains(@"SysWOW64\regsvr32.exe", activeX.Note);
    }

    [Fact]
    public void IniFilesOfTheWindowsFolderAreCollected()
    {
        var items = Collect().Items.Where(i => i.Collector == "Legacy .ini").ToList();

        Assert.Equal([@"C:\Users\bob\AppData\Local\VirtualStore\Windows\acme.ini", @"C:\Windows\acme.ini"], items.Select(i => i.Path).Order());
        Assert.All(items, i => Assert.True(i is { Kind: ItemKind.File, Role: ItemRole.Settings, Selected: true }));
        Assert.Equal(Bob, items.Single(i => i.Path!.Contains("VirtualStore")).UserSid);
        Assert.Contains("legacy.exe", items[0].Reason);
    }

    [Fact]
    public void DeploymentRestoresTheIniFilesAndReportsTheMissingControl()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_packagePath)!);
        using (var session = _source.OpenSession())
        {
            var collection = _source.CollectionService().Collect(_source.App, session);
            new PackageWriter(_source.Registry, _source.Files, NullLogger<PackageWriter>.Instance)
                .Write(_packagePath, _source.App, collection, collection.Items, session);
        }

        using var package = PackageReader.Open(_packagePath);
        using var target = _target.OpenSession();
        var plan = new DeploymentPlanner(_target.Registry, _target.Files, new FakeSystem(_target).Value).Plan(package, target,
            new DeployOptions { UserMap = new Dictionary<int, string> { [0] = Alice }, UninstallCommand = "uninstall" });

        Assert.Contains(plan.Operations, o => o is ExtractFileOperation { Target: @"C:\Windows\acme.ini" });
        Assert.Contains(plan.Operations, o => o is ExtractFileOperation { Target: @"C:\Users\alice\AppData\Local\VirtualStore\Windows\acme.ini" });
        Assert.True(plan.Issues.Any(i => i.Message.StartsWith("Missing prerequisite: Adobe Flash Player (ActiveX) (x86).") && i.Message.Contains("regsvr32")), string.Join("\n", plan.Issues.Select(i => i.Message)));
    }
}

/// <summary>Adobe Flash Player ActiveX itself, installed in the system folders, exported as an application.</summary>
public class FlashPlayerExportTests
{
    private const string Flash32 = @"C:\Windows\SysWOW64\Macromed\Flash";
    private const string Flash64 = @"C:\Windows\System32\Macromed\Flash";
    private const string Clsid = @"SOFTWARE\Classes\CLSID\" + PrerequisiteAnalyzer.FlashClsId;

    [Fact]
    public void CollectsTheFilesAndTheComClassesOfBothParts()
    {
        var machine = new TestMachine();
        machine.Files
            .Add($@"{Flash32}\FlashUtil32_32_0_0_330_ActiveX.exe", "UTIL")
            .Add($@"{Flash32}\Flash32_32_0_0_330.ocx", "OCX32")
            .Add($@"{Flash64}\Flash64_32_0_0_330.ocx", "OCX64");
        machine.Registry
            .Set(RegHive.LocalMachine, RegView.Registry32, $@"{Clsid}\InprocServer32", RegValue.String("", $@"{Flash32}\Flash32_32_0_0_330.ocx"))
            .Set(RegHive.LocalMachine, RegView.Registry64, $@"{Clsid}\InprocServer32", RegValue.String("", $@"{Flash64}\Flash64_32_0_0_330.ocx"));
        var app = machine.App;
        var flash = new InstalledApp
        {
            Id = @"HKLM32\Adobe Flash Player ActiveX", KeyName = "Adobe Flash Player ActiveX", RegistryPath = app.RegistryPath,
            Scope = AppScope.Machine, View = RegView.Registry32, RawValues = app.RawValues, DisplayName = "Adobe Flash Player 32 ActiveX",
            Publisher = "Adobe", DisplayIcon = $@"{Flash32}\FlashUtil32_32_0_0_330_ActiveX.exe",
            Installer = new InstallerInfo
            {
                Type = InstallerType.Unknown, Confidence = 0, Evidence = [], UninstallerPath = $@"{Flash32}\FlashUtil32_32_0_0_330_ActiveX.exe",
            },
        };

        using var session = machine.OpenSession();
        var result = machine.CollectionService().Collect(flash, session);

        Assert.Equal([Flash32, Flash64], result.InstallRoots.Select(r => r.Path));
        var com = result.Items.Where(i => i is { Role: ItemRole.Com, Kind: ItemKind.RegistryKey } && i.Registry!.Path.EndsWith(PrerequisiteAnalyzer.FlashClsId)).ToList();
        Assert.Equal([RegView.Registry64, RegView.Registry32], com.Select(i => i.Registry!.View).Order());
        // Served from its own folders: not a prerequisite of itself.
        Assert.DoesNotContain(result.Prerequisites, p => p.Kind == PrerequisiteKind.ActiveX);
    }
}

public class ActiveXCheckerTests
{
    private const string ClsId = "{AAAAAAAA-0000-0000-0000-000000000001}";
    private readonly FakeRegistry _registry = new();
    private readonly FakeFileSystem _files = new();
    private readonly PrerequisiteChecker _checker;

    public ActiveXCheckerTests()
    {
        var machine = new Core.Platform.MachineFolders
        {
            Windows = @"C:\Windows", System32 = @"C:\Windows\System32", SysWow64 = @"C:\Windows\SysWOW64",
            ProgramFiles = @"C:\Program Files", ProgramFilesX86 = @"C:\Program Files (x86)", ProgramData = @"C:\ProgramData",
            Public = @"C:\Users\Public", CommonDesktop = @"C:\Users\Public\Desktop", CommonStartMenu = @"C:\ProgramData\Microsoft\Windows\Start Menu",
        };
        _checker = new PrerequisiteChecker(_registry, _files, machine);
    }

    private bool? Installed(string arch = "x86") =>
        _checker.IsInstalled(new Prerequisite(PrerequisiteKind.ActiveX, "control", "test") { Architecture = arch, Version = ClsId });

    private void Register(string server, string key = "InprocServer32") =>
        _registry.Set(RegHive.LocalMachine, RegView.Registry32, $@"SOFTWARE\Classes\CLSID\{ClsId}\{key}", RegValue.ExpandString("", server));

    [Fact]
    public void NotRegistered() => Assert.False(Installed());

    [Fact]
    public void RegisteredWithoutItsFile()
    {
        Register(@"%SystemRoot%\SysWOW64\control.ocx");

        Assert.False(Installed());
        _files.Add(@"C:\Windows\SysWOW64\control.ocx");
        Assert.True(Installed());
        // 64-bit applications use the other registry view.
        Assert.False(Installed("x64"));
    }

    [Fact]
    public void LocalServerAndUnresolvedPaths()
    {
        Register("\"%ProgramFiles%\\Vendor\\server.exe\" /automation", "LocalServer32");
        _files.Add(@"C:\Program Files (x86)\Vendor\server.exe");

        Assert.True(Installed());
        Register(@"%VENDOR_HOME%\control.ocx");
        Assert.Null(Installed());
    }
}
