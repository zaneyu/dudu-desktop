using Dudu.App.Overlay;
using Xunit;

namespace Dudu.App.Tests.Overlay;

public sealed class PetWanderPathTests
{
    private static readonly PixelRect WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void Walk_goes_the_preferred_way_when_there_is_room()
    {
        var bounds = new PixelRect(800, 600, 384, 384);

        var plan = PetWanderPath.Plan(bounds, WorkArea, preferredDirection: 1, distance: 150);

        Assert.NotNull(plan);
        Assert.Equal(bounds, plan.Value.Start);
        Assert.Equal(bounds with { X = 950 }, plan.Value.Target);
        Assert.Equal(1, plan.Value.Direction);
    }

    [Fact]
    public void Walk_turns_around_at_the_screen_edge()
    {
        var bounds = new PixelRect(1920 - 384 - 10, 600, 384, 384);

        var plan = PetWanderPath.Plan(bounds, WorkArea, preferredDirection: 1, distance: 150);

        Assert.NotNull(plan);
        Assert.Equal(-1, plan.Value.Direction);
        Assert.Equal(bounds.X - 150, plan.Value.Target.X);
    }

    [Fact]
    public void Walk_is_shortened_so_the_pet_never_leaves_the_work_area()
    {
        var bounds = new PixelRect(60, 600, 384, 384);

        var plan = PetWanderPath.Plan(bounds, WorkArea, preferredDirection: -1, distance: 200);

        Assert.NotNull(plan);
        Assert.Equal(0, plan.Value.Target.X);
        Assert.True(WorkArea.Contains(plan.Value.Target));
    }

    [Fact]
    public void No_walk_when_the_work_area_is_barely_wider_than_the_pet()
    {
        var bounds = new PixelRect(10, 600, 384, 384);
        var narrow = new PixelRect(0, 0, 404, 1040);

        Assert.Null(PetWanderPath.Plan(bounds, narrow, preferredDirection: 1, distance: 150));
        Assert.Null(PetWanderPath.Plan(bounds, narrow, preferredDirection: -1, distance: 150));
    }

    [Fact]
    public void Walk_only_moves_sideways()
    {
        var bounds = new PixelRect(800, 600, 384, 384);

        var plan = PetWanderPath.Plan(bounds, WorkArea, preferredDirection: -1, distance: 100);

        Assert.NotNull(plan);
        Assert.Equal(bounds.Y, plan.Value.Target.Y);
        Assert.Equal(bounds.Width, plan.Value.Target.Width);
        Assert.Equal(bounds.Height, plan.Value.Target.Height);
    }

    [Theory]
    [InlineData(0d, 800)]
    [InlineData(0.5d, 900)]
    [InlineData(1d, 1000)]
    [InlineData(2d, 1000)]
    [InlineData(-1d, 800)]
    [InlineData(double.NaN, 1000)]
    public void Position_along_the_walk_is_eased_and_clamped(double progress, int expectedX)
    {
        var start = new PixelRect(800, 600, 384, 384);
        var plan = new OverlayWanderPlan(start, start with { X = 1000 });

        var position = PetWanderPath.At(plan, progress);

        Assert.Equal(expectedX, position.X);
        Assert.Equal(start.Y, position.Y);
    }

    [Fact]
    public void Easing_starts_and_ends_slowly()
    {
        var start = new PixelRect(0, 0, 100, 100);
        var plan = new OverlayWanderPlan(start, start with { X = 1000 });

        var early = PetWanderPath.At(plan, 0.1).X;
        var middle = PetWanderPath.At(plan, 0.55).X - PetWanderPath.At(plan, 0.45).X;

        Assert.True(early < 100, $"early step {early} should be slower than linear");
        Assert.True(middle > 100, $"middle step {middle} should be faster than linear");
    }
}
