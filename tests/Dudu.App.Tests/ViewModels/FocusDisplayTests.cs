using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>The focus status line is shared by Home and the Tasks and Focus page so the
/// two surfaces can't drift apart again (Home used to print the raw enum,
/// "focus is endedearly with 0 min left").</summary>
public sealed class FocusDisplayTests
{
    [Fact]
    public void Describe_words_every_status_plainly()
    {
        var id = Guid.NewGuid();

        Assert.Equal("no focus running", FocusDisplay.Describe(null, TimeSpan.Zero));
        Assert.Equal(
            "focus is running with 25 min left",
            FocusDisplay.Describe(new FocusSnapshot(id, null, FocusStatus.Running, TimeSpan.FromMinutes(30)), TimeSpan.FromMinutes(24.1)));
        Assert.Equal(
            "focus is paused with 1 hr 30 min left",
            FocusDisplay.Describe(new FocusSnapshot(id, null, FocusStatus.Paused, TimeSpan.FromMinutes(90)), TimeSpan.FromMinutes(90)));
        Assert.Equal(
            "last focus session completed le",
            FocusDisplay.Describe(new FocusSnapshot(id, null, FocusStatus.Completed, TimeSpan.Zero), TimeSpan.Zero));
        Assert.Equal(
            "last focus session ended early",
            FocusDisplay.Describe(new FocusSnapshot(id, null, FocusStatus.EndedEarly, TimeSpan.Zero), TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0, "0 min")]
    [InlineData(-5, "0 min")]
    [InlineData(0.2, "1 min")]
    [InlineData(59.5, "1 hr")]
    [InlineData(60, "1 hr")]
    [InlineData(125, "2 hr 5 min")]
    public void FormatRemaining_rounds_up_and_switches_to_hours(double minutes, string expected) =>
        Assert.Equal(expected, FocusDisplay.FormatRemaining(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData(FocusStatus.Running, 24.2)]
    [InlineData(FocusStatus.Paused, 90)]
    [InlineData(FocusStatus.Completed, 0)]
    [InlineData(FocusStatus.EndedEarly, 0)]
    public void Home_uses_the_shared_focus_line(FocusStatus status, double remainingMinutes)
    {
        var snapshot = new FocusSnapshot(Guid.NewGuid(), null, status, TimeSpan.FromMinutes(remainingMinutes));

        Assert.Equal(
            FocusDisplay.Describe(snapshot, snapshot.Remaining),
            HomeViewModel.DescribeFocus(snapshot));
        Assert.Equal(FocusDisplay.Describe(null, TimeSpan.Zero), HomeViewModel.DescribeFocus(null));
    }
}
