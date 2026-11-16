using System.Diagnostics;
using OpenAppMover.Deployment;

namespace OpenAppMover.Tests;

/// <summary>Windows 7 compatibility: helpers shared by the .NET 8 and .NET Framework 4.8 builds.</summary>
public class CompatTests
{
    private const string Windows10Task = """
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
            <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
            <MaintenanceSettings><Period>P1D</Period></MaintenanceSettings>
            <Volatile>false</Volatile>
          </Settings>
          <Actions><Exec><Command>C:\Acme\update.exe</Command></Exec></Actions>
        </Task>
        """;

    [Fact]
    public void TaskIsAdaptedToTheWindows7Schema()
    {
        var xml = TaskXmlRewriter.AdaptToWindows(Windows10Task, new Version(6, 1, 7601));

        Assert.Contains("version=\"1.3\"", xml);
        Assert.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", xml);
        Assert.Contains(@"<Command>C:\Acme\update.exe</Command>", xml);
        foreach (var setting in new[] { "UseUnifiedSchedulingEngine", "DisallowStartOnRemoteAppSession", "MaintenanceSettings", "Volatile" })
            Assert.DoesNotContain(setting, xml);
    }

    [Fact]
    public void TaskIsUnchangedOnWindows8AndLater()
    {
        Assert.Same(Windows10Task, TaskXmlRewriter.AdaptToWindows(Windows10Task, new Version(10, 0, 19045)));
        Assert.Same(Windows10Task, TaskXmlRewriter.AdaptToWindows(Windows10Task, new Version(6, 2)));
    }

    [Fact]
    public void OlderTaskSchemaIsKept()
    {
        var xml = TaskXmlRewriter.AdaptToWindows(Windows10Task.Replace("version=\"1.4\"", "version=\"1.2\""), new Version(6, 1));
        Assert.Contains("version=\"1.2\"", xml);
    }

    [Theory]
    [InlineData("a;b ; ;c", false, new[] { "a", "b", "", "c" })]
    [InlineData("a;b ; ;c", true, new[] { "a", "b", "c" })]
    [InlineData(" x = y = z ", false, new[] { "x", "y = z" })]
    public void SplitTrimmed(string text, bool removeEmpty, string[] expected)
    {
        var separator = text.Contains(';') ? ';' : '=';
        var count = separator == '=' ? 2 : int.MaxValue;
        Assert.Equal(expected, Portable.SplitTrimmed(text, separator, count, removeEmpty));
    }

    [Fact]
    public void Hashing()
    {
        var data = "abc"u8.ToArray();
        const string expected = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
        Assert.Equal(expected, Portable.ToHexString(Portable.Sha256(data)));
        Assert.Equal(expected, Portable.ToHexString(Portable.Sha256(new MemoryStream(data))));
        Assert.True(Portable.FixedTimeEquals(data, "abc"u8));
        Assert.False(Portable.FixedTimeEquals(data, "abd"u8));
        Assert.False(Portable.FixedTimeEquals(data, "ab"u8));
    }

    [Fact]
    public void Pbkdf2HmacSha256()
    {
        // Known PBKDF2-HMAC-SHA256 vector with a salt of 8 bytes or more (required by .NET Framework).
        var key = Portable.Pbkdf2("passwordPASSWORDpassword", "saltSALTsaltSALTsaltSALTsaltSALTsalt"u8.ToArray(), 4096,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 40);
        Assert.Equal("348C89DBCBD32B2F32D814B8116E84CF2B17347EBC1800181C4E2A1FB8DD53E1C635518C7DAC47E9", Portable.ToHexString(key));
    }

    [Fact]
    public void ArgumentsAreQuoted()
    {
        var info = new ProcessStartInfo("x.exe");
        Portable.AddArguments(info, ["/TN", @"\Acme\My Task", "", "say \"hi\"", @"C:\dir\"]);
#if NETFRAMEWORK
        // No space: not quoted, its trailing backslash stays as is.
        Assert.Equal(@"/TN ""\Acme\My Task"" """" ""say \""hi\"""" C:\dir\", info.Arguments);
#else
        Assert.Equal(["/TN", @"\Acme\My Task", "", "say \"hi\"", @"C:\dir\"], info.ArgumentList);
#endif
    }

    [Fact]
    public void AsciiClasses()
    {
        Assert.True(Portable.IsAsciiDigit('7'));
        Assert.False(Portable.IsAsciiDigit('٣'));
        Assert.True(Portable.IsAsciiLetter('z') && Portable.IsAsciiLetter('A'));
        Assert.False(Portable.IsAsciiLetter('é') || Portable.IsAsciiLetter('[') || Portable.IsAsciiLetter('@'));
        Assert.True(Portable.IsAsciiLetterOrDigit('0'));
    }

    [Fact]
    public void Latin1()
    {
        Assert.Equal("é", Portable.Latin1.GetString([0xE9]));
        Assert.Equal(28591, Portable.Latin1.CodePage);
    }

    [Fact]
    public void LongPathsGetTheExtendedPrefix()
    {
        var deep = @"C:\Program Files\" + string.Join(@"\", Enumerable.Repeat("SubFolder", 30));
        Assert.Equal(@"\\?\" + deep, Portable.WithExtendedPrefix(deep));
        Assert.Equal(deep, Portable.WithoutExtendedPrefix(Portable.WithExtendedPrefix(deep)));

        var unc = @"\\server\share\" + string.Join(@"\", Enumerable.Repeat("SubFolder", 30));
        Assert.Equal(@"\\?\UNC\server\share\" + unc.Substring(15), Portable.WithExtendedPrefix(unc));
        Assert.Equal(unc, Portable.WithoutExtendedPrefix(Portable.WithExtendedPrefix(unc)));
    }

    [Theory]
    [InlineData(@"C:\Short\path.txt")]
    [InlineData(@"relative\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder")]
    [InlineData(@"C:\SubFolder\..\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder")]
    [InlineData(@"\\?\C:\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder\SubFolder")]
    public void OtherPathsAreUnchanged(string path)
    {
        Assert.Equal(path, Portable.WithExtendedPrefix(path));
    }
}
