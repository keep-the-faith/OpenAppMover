using OpenAppMover.Core.Models;
using OpenAppMover.Core.Registry;
using static OpenAppMover.Collectors.CollectionContext;

namespace OpenAppMover.Collectors.Collectors;

/// <summary>
/// Explorer shell extensions whose COM class was collected: the handlers that hook the class into Explorer
/// (<c>*\shellex\ContextMenuHandlers\name</c>, <c>Directory\Background\shellex\…</c>, <c>shellex\{IID}</c> for
/// icon / preview / thumbnail handlers…), icon overlays and the "Approved" list. Without them, the class is restored
/// but the context menu entries are not.
/// </summary>
public sealed class ShellExtensionCollector : ICollector
{
    private const string Approved = @"Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";
    private const string IconOverlays = @"Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";

    public string Name => "Shell extensions";

    public CollectionPhase Phase => CollectionPhase.Dependent;

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        // Handler keys are shared between the registry views: 64-bit view only, like the other non-COM classes.
        var scopes = new List<RegistryScope> { new(RegistryLocation.Machine(RegView.Registry64, @"SOFTWARE\Classes"), null) };
        scopes.AddRange(context.Session.Users.Select(u => new RegistryScope(RegistryLocation.User(u.Sid, @"Software\Classes"), u)));

        foreach (var scope in scopes)
        {
            using var classes = context.Open(scope.Location);
            if (classes is null)
                continue;
            foreach (var name in classes.GetSubKeyNames())
            {
                foreach (var item in CollectHandlers(context, classes, scope.Location, name))
                    yield return item;
            }
        }

        foreach (var scope in context.SoftwareScopes(Approved))
        {
            using var key = context.Open(scope.Location);
            foreach (var clsid in key?.GetValueNames() ?? [])
            {
                if (context.FindComClass(clsid) is { } confidence)
                    yield return Item(Name, ItemKind.RegistryValue, ItemRole.Com, confidence,
                        $"Shell extension {clsid} approved for Explorer", registry: scope.Location, valueName: clsid);
            }
        }

        foreach (var scope in context.SoftwareScopes(IconOverlays))
        {
            using var overlays = context.Open(scope.Location);
            foreach (var name in overlays?.GetSubKeyNames() ?? [])
            {
                using var overlay = overlays!.OpenSubKey(name);
                if (ClassOf(overlay, name) is { } clsid && context.FindComClass(clsid) is { } confidence)
                    yield return Item(Name, ItemKind.RegistryKey, ItemRole.Com, confidence, $"Icon overlay handler {clsid}",
                        registry: scope.Location with { Path = $@"{scope.Location.Path}\{name}" });
            }
        }
    }

    /// <summary>Handlers of a class (<c>*</c>, <c>Directory</c>, <c>.zip</c>…), its background and its system associations.</summary>
    private IEnumerable<CollectedItem> CollectHandlers(CollectionContext context, IRegistryKey classes, RegistryLocation root, string className)
    {
        var classPaths = new List<string> { className, $@"{className}\Background" };
        if (className.Equals("SystemFileAssociations", StringComparison.OrdinalIgnoreCase))
        {
            using var associations = classes.OpenSubKey(className);
            classPaths.AddRange(associations?.GetSubKeyNames().Select(n => $@"{className}\{n}") ?? []);
        }

        foreach (var classPath in classPaths)
        {
            using var shellex = classes.OpenSubKey($@"{classPath}\shellex");
            if (shellex is null)
                continue;

            foreach (var group in shellex.GetSubKeyNames())
            {
                using var groupKey = shellex.OpenSubKey(group);
                var groupPath = $@"{root.Path}\{classPath}\shellex\{group}";

                // shellex\{IID} (icon, preview, thumbnail handlers): the default value is the class.
                if (groupKey?.GetString("") is { Length: > 0 } direct && context.FindComClass(direct) is { } directConfidence)
                {
                    yield return Item(Name, ItemKind.RegistryKey, ItemRole.Com, directConfidence,
                        $"Shell handler {group} of '{classPath}' ({direct})", registry: root with { Path = groupPath });
                    continue;
                }

                // shellex\ContextMenuHandlers\<name>: the default value is the class, or the name is.
                foreach (var handler in groupKey?.GetSubKeyNames() ?? [])
                {
                    using var handlerKey = groupKey!.OpenSubKey(handler);
                    if (ClassOf(handlerKey, handler) is { } clsid && context.FindComClass(clsid) is { } confidence)
                        yield return Item(Name, ItemKind.RegistryKey, ItemRole.Com, confidence,
                            $"{group} entry '{handler}' of '{classPath}' ({clsid})", registry: root with { Path = $@"{groupPath}\{handler}" });
                }
            }
        }
    }

    /// <summary>The CLSID in the default value, or the key name when it is a CLSID itself.</summary>
    private static string? ClassOf(IRegistryKey? key, string name) =>
        key?.GetString("") is { Length: > 0 } value ? value
        : name.StartsWith('{') ? name
        : null;
}
