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

    [Fact]
    public async Task Glide_walks_step_by_step_to_the_target_and_saves_the_placement()
    {
        var glide = new GlideRecorder();

        var completed = await glide.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(completed);
        Assert.Equal(GlideRecorder.Plan.Start, glide.Steps[0].From);
        Assert.Equal(GlideRecorder.Plan.Target, glide.Steps[^1].To);
        for (var index = 1; index < glide.Steps.Count; index++)
        {
            Assert.Equal(glide.Steps[index - 1].To, glide.Steps[index].From);
        }

        Assert.Equal(1, glide.Commits);
        Assert.Empty(glide.Failures);
    }

    [Fact]
    public async Task Glide_stops_where_it_is_when_something_else_moves_the_pet()
    {
        var glide = new GlideRecorder { StepsAllowed = 2 };

        var completed = await glide.RunAsync(TestContext.Current.CancellationToken);

        Assert.False(completed);
        Assert.Equal(3, glide.Steps.Count);
        Assert.Equal(1, glide.Commits);
        Assert.Empty(glide.Failures);
    }

    [Fact]
    public async Task Glide_failure_is_reported_and_the_placement_is_still_saved()
    {
        var glide = new GlideRecorder { StepFailure = new InvalidOperationException("move") };

        var completed = await glide.RunAsync(TestContext.Current.CancellationToken);

        Assert.False(completed);
        Assert.IsType<InvalidOperationException>(Assert.Single(glide.Failures));
        Assert.Equal(1, glide.Commits);
    }

    [Fact]
    public async Task Cancelled_glide_still_saves_the_placement()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var glide = new GlideRecorder { OnStep = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => glide.RunAsync(cancellation.Token));

        Assert.Equal(1, glide.Commits);
        Assert.Empty(glide.Failures);
    }

    [Fact]
    public async Task Glide_save_failure_goes_to_the_commit_reporter_only()
    {
        var glide = new GlideRecorder { CommitFailure = new InvalidOperationException("save") };

        var completed = await glide.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(completed);
        Assert.Empty(glide.Failures);
        Assert.IsType<InvalidOperationException>(Assert.Single(glide.CommitFailures));
    }

    private sealed class GlideRecorder
    {
        public static readonly OverlayWanderPlan Plan = new(
            new PixelRect(800, 600, 384, 384),
            new PixelRect(950, 600, 384, 384));

        public List<(PixelRect From, PixelRect To)> Steps { get; } = [];

        public List<Exception> Failures { get; } = [];

        public List<Exception> CommitFailures { get; } = [];

        public int Commits { get; private set; }

        public int StepsAllowed { get; init; } = int.MaxValue;

        public Exception? StepFailure { get; init; }

        public Exception? CommitFailure { get; init; }

        public Action? OnStep { get; init; }

        public Task<bool> RunAsync(CancellationToken cancellationToken) =>
            PetWanderPath.GlideAsync(
                Plan,
                // Long enough that even ~16 ms Windows timer steps give the
                // interruption test its three steps before the walk ends.
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(5),
                (from, to, _) =>
                {
                    Steps.Add((from, to));
                    OnStep?.Invoke();
                    if (StepFailure is not null) throw StepFailure;
                    return Task.FromResult(Steps.Count <= StepsAllowed);
                },
                () =>
                {
                    Commits++;
                    return CommitFailure is null ? Task.CompletedTask : Task.FromException(CommitFailure);
                },
                Failures.Add,
                CommitFailures.Add,
                (delay, token) => Task.Delay(delay, token),
                cancellationToken);
    }
}
