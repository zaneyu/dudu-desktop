using Dudu.App.Audio;
using Dudu.Core.Pet;
using Xunit;

namespace Dudu.App.Tests.Audio;

public sealed class AudioCueSelectionTests
{
    public static TheoryData<PetEvent> EndingEvents => new()
    {
        new PetEvent.InteractionDismissed("drink"),
        new PetEvent.DragEnded(),
        new PetEvent.EatingEnded("meal-1"),
    };

    [Theory]
    [MemberData(nameof(EndingEvents))]
    public void Ending_an_interaction_drag_or_meal_does_not_replay_the_state_underneath(PetEvent petEvent)
    {
        // e.g. comfort -> drag -> release returns the comfort presentation;
        // the hug sound already played and must not sound again.
        Assert.True(AudioCueSelection.IsSettlingEvent(petEvent));
    }

    [Fact]
    public void Starting_an_interaction_drag_or_meal_still_sounds()
    {
        Assert.False(AudioCueSelection.IsSettlingEvent(new PetEvent.InteractionRequested("petted")));
        Assert.False(AudioCueSelection.IsSettlingEvent(new PetEvent.DragStarted()));
        Assert.False(AudioCueSelection.IsSettlingEvent(new PetEvent.EatingStarted("meal-1")));
    }
}
