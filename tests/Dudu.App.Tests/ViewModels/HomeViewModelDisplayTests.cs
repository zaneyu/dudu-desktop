using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Xunit;

namespace Dudu.App.Tests.ViewModels;

/// <summary>Plain-language pet state shown by the settings window's companion
/// panel.</summary>
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
}
