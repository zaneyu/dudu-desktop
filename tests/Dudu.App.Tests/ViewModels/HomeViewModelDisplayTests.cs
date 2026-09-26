using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>Home page display helpers: plain-language pet state, local-time
/// check-in history rows, and the countdown-target box re-sync rule.</summary>
public sealed class HomeViewModelDisplayTests
{
    [Theory]
    [InlineData(PetState.RemoteNote)]
    [InlineData(PetState.FocusTransition)]
    [InlineData(PetState.WelcomeBack)]
    [InlineData(PetState.Comfort)]
    [InlineData(PetState.Reminder)]
    [InlineData(PetState.Ambient)]
    [InlineData(PetState.Focus)]
    [InlineData(PetState.Idle)]
    public void Pet_state_reads_as_plain_words_not_a_squashed_enum_name(PetState state)
    {
        var text = HomeViewModel.DescribePetState(state);

        Assert.False(string.IsNullOrWhiteSpace(text));
        if (state != PetState.Idle)
        {
            Assert.NotEqual(state.ToString().ToLowerInvariant(), text);
        }

        Assert.DoesNotContain("remotenote", text);
        Assert.DoesNotContain("focustransition", text);
        Assert.DoesNotContain("welcomeback", text);
    }

    [Fact]
    public void Every_pet_state_has_its_own_description()
    {
        var descriptions = Enum.GetValues<PetState>().Select(HomeViewModel.DescribePetState).ToArray();

        Assert.Equal(descriptions.Length, descriptions.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Check_in_history_time_is_local_not_raw_utc_with_an_offset()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("dudu-test+8", TimeSpan.FromHours(8), "test +8", "test +8");
        var createdUtc = new DateTimeOffset(2026, 9, 12, 20, 30, 0, TimeSpan.Zero);

        var text = HomeViewModel.FormatCheckInTimeIn(createdUtc, zone);

        Assert.Equal(new DateTime(2026, 9, 13, 4, 30, 0).ToString("g"), text);
        Assert.DoesNotContain("+00:00", text);
        Assert.DoesNotContain("+08:00", text);
        Assert.DoesNotContain("+00:00", HomeViewModel.FormatCheckInTime(createdUtc));
    }

    [Theory]
    [InlineData(MoodChoice.Great, "great")]
    [InlineData(MoodChoice.Okay, "okay")]
    [InlineData(MoodChoice.Tired, "tired")]
    [InlineData(MoodChoice.Rough, "rough")]
    public void Check_in_history_choice_matches_the_mood_picker_wording(MoodChoice choice, string expected)
    {
        Assert.Equal(expected, HomeViewModel.FormatCheckInChoice(choice));
    }

    [Fact]
    public void Home_focus_line_uses_plain_words_and_the_tasks_page_time_format()
    {
        var id = Guid.NewGuid();

        Assert.Equal("no focus running", HomeViewModel.DescribeFocus(null));
        Assert.Equal(
            "focus is running with 25 min left",
            HomeViewModel.DescribeFocus(new FocusSnapshot(id, null, FocusStatus.Running, TimeSpan.FromMinutes(24.2))));
        Assert.Equal(
            "focus is paused with 1 hr 30 min left",
            HomeViewModel.DescribeFocus(new FocusSnapshot(id, null, FocusStatus.Paused, TimeSpan.FromMinutes(90))));
        Assert.Equal(
            "focus is running with 1 hr left",
            HomeViewModel.DescribeFocus(new FocusSnapshot(id, null, FocusStatus.Running, TimeSpan.FromHours(1))));
        Assert.Equal(
            "last focus session completed le",
            HomeViewModel.DescribeFocus(new FocusSnapshot(id, null, FocusStatus.Completed, TimeSpan.Zero)));
        var ended = HomeViewModel.DescribeFocus(new FocusSnapshot(id, null, FocusStatus.EndedEarly, TimeSpan.Zero));
        Assert.Equal("last focus session ended early", ended);
        Assert.DoesNotContain("endedearly", ended);
    }

    [Fact]
    public void Countdown_target_box_is_resynced_when_the_view_model_clears_the_target()
    {
        var target = new DateTimeOffset(2026, 12, 31, 17, 0, 0, TimeSpan.Zero);
        var shown = target.ToLocalTime().ToString("g");

        // After a save the view model clears the target; the old date left in the box
        // no longer matches, so HomePage must rewrite the box.
        Assert.False(HomeViewModel.CountdownTargetTextMatches(shown, null));
        // A box that already shows the target (the user just typed it) is left alone.
        Assert.True(HomeViewModel.CountdownTargetTextMatches(shown, target));
        Assert.True(HomeViewModel.CountdownTargetTextMatches("", null));
        Assert.True(HomeViewModel.CountdownTargetTextMatches("   ", null));
        Assert.False(HomeViewModel.CountdownTargetTextMatches("", target));
        Assert.False(HomeViewModel.CountdownTargetTextMatches("not a date", target));
    }
}
