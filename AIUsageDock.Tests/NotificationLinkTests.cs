using AIUsageDock.Models;
using AIUsageDock.Notifications;
using AIUsageDock.Pages;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class NotificationLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LinkNamesTheProviderAsCommandPaletteDoes()
    {
        // Command Palette parses this back to provider "Family_abc!App!AIUsageDock"
        // and command "aiusagedock.page.claude".
        Assert.Equal(
            "x-cmdpal://commands/Family_abc%21App%21AIUsageDock/aiusagedock.page.claude",
            CommandLinks.For("Family_abc", UsagePage.IdFor("claude")));
    }

    [Fact]
    public void WarningLinksToTheProvidersOwnPage()
    {
        var sink = new RecordingSink();
        var notifier = new QuotaNotifier(sink, providerId => $"link:{UsagePage.IdFor(providerId)}");

        notifier.Observe(
            "Claude",
            "claude",
            new UsageSnapshot("Claude", null, [new UsageWindow(UsageWindowKind.Session, "Session", 95, Now.AddHours(1), 300)], Now, "test"),
            DisplayOptions.Default,
            Now);

        Assert.Equal("link:aiusagedock.page.claude", Assert.Single(sink.Links));
    }

    [Fact]
    public void ToastWithALinkIsAProtocolActivation()
    {
        var linked = WindowsToastSink.Xml("Claude · Session is almost out", "5% left & falling", "x-cmdpal://commands/a%21b/c?query=x&filter=y");
        var plain = WindowsToastSink.Xml("Title", "Body", null);

        Assert.StartsWith("<toast activationType=\"protocol\" launch=\"x-cmdpal://commands/a%21b/c?query=x&amp;filter=y\">", linked);
        Assert.Contains("<text>5% left &amp; falling</text>", linked);
        Assert.StartsWith("<toast><visual>", plain);
    }

    private sealed class RecordingSink : IToastSink
    {
        public List<string?> Links { get; } = [];

        public void Show(string title, string body, string? link = null) => Links.Add(link);
    }
}
