using System.Xml.Linq;

namespace OpenAppMover.Deployment;

/// <summary>Adapts a scheduled task definition to the target machine: paths and user accounts.</summary>
public static class TaskXmlRewriter
{
    private static readonly string[] PathElements = ["Command", "Arguments", "WorkingDirectory"];

    /// <param name="xml">Task XML exported from the source machine.</param>
    /// <param name="relocate">Converts source paths found in a text into target paths.</param>
    /// <param name="accounts">Source account (SID, NAME or COMPUTER\NAME) → target SID.</param>
    public static string Rewrite(string xml, Func<string, string> relocate, IReadOnlyDictionary<string, string> accounts)
    {
        var document = XDocument.Parse(xml);
        foreach (var element in document.Descendants().ToList())
        {
            var name = element.Name.LocalName;
            if (name == "UserId" && accounts.TryGetValue(element.Value.Trim(), out var target))
                element.Value = target;
            else if (PathElements.Contains(name))
                element.Value = relocate(element.Value);
        }
        return document.ToString();
    }

    /// <summary>Windows 7 Task Scheduler: schema 1.3 at most (Windows 8 and later export 1.4).</summary>
    private static readonly Version Windows7Schema = new(1, 3);

    /// <summary>Settings added after schema 1.3: unknown to Windows 7, which refuses the whole definition.</summary>
    private static readonly string[] PostWindows7Settings =
        ["UseUnifiedSchedulingEngine", "DisallowStartOnRemoteAppSession", "Volatile", "MaintenanceSettings"];

    /// <summary>
    /// Makes a task exported from a newer Windows registrable on Windows 7 (before 6.2): schema version 1.3, settings it
    /// does not know removed. Unchanged on later versions.
    /// </summary>
    public static string AdaptToWindows(string xml, Version windows)
    {
        if (windows >= new Version(6, 2))
            return xml;
        var document = XDocument.Parse(xml);
        var root = document.Root;
        if (root?.Attribute("version") is { } attribute && Version.TryParse(attribute.Value, out var version) && version > Windows7Schema)
            attribute.Value = Windows7Schema.ToString();
        document.Descendants()
            .Where(e => e.Parent?.Name.LocalName == "Settings" && PostWindows7Settings.Contains(e.Name.LocalName))
            .ToList()
            .ForEach(e => e.Remove());
        return document.ToString();
    }
}
