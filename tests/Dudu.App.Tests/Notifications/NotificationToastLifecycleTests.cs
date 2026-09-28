using Dudu.App.Notifications;
using Xunit;

namespace Dudu.App.Tests.Notifications;

public sealed class NotificationToastLifecycleTests
{
    [Theory]
    [InlineData("a&action=open-note")]
    [InlineData("a;b=c")]
    [InlineData("100%")]
    public void Argument_values_are_escaped_and_round_trip_without_forging_pairs(string value)
    {
        var fragment = "action=open-note&" + NotificationArguments.Pair("messageId", value);

        var pairs = NotificationArguments.Parse(fragment).ToArray();

        Assert.Equal([("action", "open-note"), ("messageId", value)], pairs);
    }
}
