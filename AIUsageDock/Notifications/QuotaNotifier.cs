using System.Security;
using AIUsageDock.Models;
using AIUsageDock.Pages;
using Windows.ApplicationModel;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AIUsageDock.Notifications;

internal interface IToastSink
{
    /// <param name="link">A URI to open when the notification is clicked; null opens nothing in particular.</param>
    void Show(string title, string body, string? link = null);
}

/// <summary>
/// Command Palette's external command links: <c>x-cmdpal://commands/{provider}/{command}</c>
/// opens a top-level command of an extension. Command Palette names an extension's
/// provider "{package family name}!{application id}!{app extension id}".
/// </summary>
internal static class CommandLinks
{
    // Application Id and uap3:AppExtension Id in Package.appxmanifest.
    private const string ApplicationId = "App";
    private const string ExtensionId = "AIUsageDock";

    private static readonly Lazy<string?> FamilyName = new(() =>
    {
        try
        {
            return Package.Current.Id.FamilyName;
        }
        catch
        {
            // No package identity: tests, or an unpackaged debug run.
            return null;
        }
    });

    /// <summary>A link to one of this extension's top-level commands, or null without package identity.</summary>
    public static string? For(string commandId) =>
        FamilyName.Value is { } family ? For(family, commandId) : null;

    internal static string For(string packageFamilyName, string commandId) =>
        $"x-cmdpal://commands/{Uri.EscapeDataString($"{packageFamilyName}!{ApplicationId}!{ExtensionId}")}/{Uri.EscapeDataString(commandId)}";
}

/// <summary>Windows toast notifications, sent under the extension's package identity.</summary>
internal sealed class WindowsToastSink : IToastSink
{
    public void Show(string title, string body, string? link = null)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(Xml(title, body, link));
            ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
        }
        catch
        {
            // Notifications can be off, or the process may run without package identity
            // (tests, an unpackaged debug run). A missed toast must not break a refresh.
        }
    }

    /// <summary>
    /// With a link the toast is a protocol activation, so a click goes to that URI.
    /// Without one Windows starts this package's executable, which only brings up
    /// Command Palette's home page.
    /// </summary>
    internal static string Xml(string title, string body, string? link)
    {
        var activation = link is null
            ? string.Empty
            : $" activationType=\"protocol\" launch=\"{SecurityElement.Escape(link)}\"";
        return $"<toast{activation}><visual><binding template=\"ToastGeneric\">" +
               $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(body)}</text>" +
               "</binding></visual></toast>";
    }
}

/// <summary>
/// Sends a notification when a usage window crosses the high or critical threshold, or
/// runs out. Each window notifies once per level and again only after it has dropped
/// back (its reset) and climbed once more. Clicking one opens the provider's usage page.
/// </summary>
/// <param name="linkFor">The link a click on a provider's notification opens, by provider id.</param>
internal sealed class QuotaNotifier(IToastSink sink, Func<string, string?>? linkFor = null)
{
    private readonly Func<string, string?> _linkFor = linkFor ?? (providerId => CommandLinks.For(UsagePage.IdFor(providerId)));
    private readonly Dictionary<string, int> _levels = [];
    private readonly object _gate = new();

    public void Observe(string providerName, string providerId, UsageSnapshot snapshot, DisplayOptions options, DateTimeOffset now)
    {
        // A stale reading describes an earlier moment; warning about it again would mislead.
        if (snapshot.IsStale)
        {
            return;
        }

        foreach (var window in snapshot.Windows)
        {
            if (window.IsLocked)
            {
                continue;
            }

            var level = Level(window, options);
            var key = $"{providerId}|{window.Kind}|{window.Label}";
            bool rose;
            lock (_gate)
            {
                rose = level > _levels.GetValueOrDefault(key);
                _levels[key] = level;
            }

            if (rose)
            {
                var (title, body) = Describe(providerName, window, level, options, now);
                sink.Show(title, body, _linkFor(providerId));
            }
        }
    }

    /// <summary>0 healthy, 1 high, 2 critical, 3 exhausted.</summary>
    internal static int Level(UsageWindow window, DisplayOptions options) =>
        (int)Math.Round(window.RemainingPercent) <= 0 ? 3 : UsageCard.Tier(window, options);

    internal static (string Title, string Body) Describe(string providerName, UsageWindow window, int level, DisplayOptions options, DateTimeOffset now)
    {
        var state = level switch
        {
            3 => "is used up",
            2 => "is almost out",
            _ => "is running low",
        };
        var parts = new List<string> { PanelText.Percent(window, options.ShowUsed) };
        if (PanelText.Reset(window, now, options.RelativeResets) is { } reset)
        {
            parts.Add(reset);
        }

        return ($"{providerName} · {window.Label} {state}", string.Join(" · ", parts));
    }
}
