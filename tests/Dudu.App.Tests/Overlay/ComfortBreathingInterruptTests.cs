using System.Reflection;
using Dudu.App.Hosting;
using Dudu.App.Overlay;
using Dudu.App.ViewModels;
using Dudu.Core.Assets;
using Dudu.Core.Abstractions;
using Dudu.Core.CheckIns;
using Dudu.Core.Focus;
using Dudu.Core.Models;
using Dudu.Core.Notes;
using Dudu.Core.Pet;
using Dudu.Core.Tasks;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.App.Tests.Overlay;

/// <summary>
/// Overlay dispatch is strictly serial and "breathe with me" occupies it for
/// a full minute; any other comfort choice clicked meanwhile used to sit
/// behind it, looking dead, and then fire up to a minute later.
/// </summary>
public sealed class ComfortBreathingInterruptTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_comfort_choice_clicked_mid_breath_interrupts_the_exercise_and_runs_now()
    {
        var presented = new List<PetEvent>();
        var breathingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new OverlayCommandRouter(
            CreateContext(presented),
            (_, _) => Task.CompletedTask,
            async (_, cancellationToken) =>
            {
                breathingEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        using var surface = await OpenComfortPanelAsync(router);
        var reported = new List<Exception>();
        using var queue = new OverlayActionDispatchQueue(reported.Add);

        queue.Enqueue(surface, ComfortChoice(surface, Dudu.App.Overlay.ComfortAction.BreatheWithMe));
        await breathingEntered.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.True(surface.IsBreathing);

        queue.Enqueue(surface, ComfortChoice(surface, Dudu.App.Overlay.ComfortAction.TinyHug));
        await queue.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.False(router.IsBreathing);
        Assert.Contains(presented, petEvent => petEvent is PetEvent.ComfortRequested);
        Assert.Equal(OverlayActionSurfaceKind.Comfort, surface.Kind);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task Choosing_breathe_with_me_again_restarts_the_exercise()
    {
        var entries = 0;
        var secondEntry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new OverlayCommandRouter(
            CreateContext([]),
            (_, _) => Task.CompletedTask,
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref entries) == 1) firstEntry.TrySetResult();
                else secondEntry.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        using var surface = await OpenComfortPanelAsync(router);
        using var queue = new OverlayActionDispatchQueue(_ => { });
        var breathe = ComfortChoice(surface, Dudu.App.Overlay.ComfortAction.BreatheWithMe);

        queue.Enqueue(surface, breathe);
        await firstEntry.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        queue.Enqueue(surface, breathe);
        await secondEntry.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(router.IsBreathing);
        surface.CancelBreathing();
        await queue.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_comfort_choice_while_not_breathing_leaves_the_panel_text_alone()
    {
        var router = new OverlayCommandRouter(CreateContext([]), (_, _) => Task.CompletedTask);
        using var surface = await OpenComfortPanelAsync(router);
        using var queue = new OverlayActionDispatchQueue(_ => { });
        var before = router.ComfortPanel.Instruction;

        queue.Enqueue(surface, ComfortChoice(surface, Dudu.App.Overlay.ComfortAction.TinyHug));
        await queue.Completion.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(before, router.ComfortPanel.Instruction);
        Assert.DoesNotContain("cancelled", router.ComfortPanel.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void Breathing_detail_line_shows_the_instruction_without_debug_prefixes()
    {
        var inhale = new ComfortPanelState(true, true, BreathVisualPhase.Inhale, "breathe in for 4");
        var reduced = new ComfortPanelState(true, false, BreathVisualPhase.Static, "breathe slowly in for 4, out for 6");

        Assert.Equal("breathe in for 4", Dudu.App.Animation.OverlaySurfaceText.ComfortDetail(inhale));
        Assert.Equal("breathe slowly in for 4, out for 6", Dudu.App.Animation.OverlaySurfaceText.ComfortDetail(reduced));
    }

    private static async Task<OverlayActionSurfaceController> OpenComfortPanelAsync(OverlayCommandRouter router)
    {
        var surface = new OverlayActionSurfaceController();
        surface.Bind(router);
        surface.Open(new PixelRect(0, 0, 640, 480), new PixelPoint(320, 400));
        var comfortMe = surface.CreateRenderSnapshot().Actions
            .Single(action => action.PrimaryAction == OverlayAction.ComfortMe);
        await surface.HandlePresentedActionAsync(comfortMe, TestContext.Current.CancellationToken);
        Assert.Equal(OverlayActionSurfaceKind.Comfort, surface.Kind);
        return surface;
    }

    private static Dudu.App.Animation.OverlaySurfaceAction ComfortChoice(
        OverlayActionSurfaceController surface,
        ComfortAction action) =>
        surface.CreateRenderSnapshot().Actions.Single(item => item.ComfortAction == action);

    private static CompanionFeatureContext CreateContext(List<PetEvent> presented)
    {
        var clock = Stub<IClock>();
        var preferences = Preferences.Default;
        var tasks = Stub<ITaskRepository>();
        var pet = PetStateMachine.CreateIdle();
        return new CompanionFeatureContext(
            clock,
            new PreferenceMutationCoordinator(preferences, Stub<IPreferencesRepository>()),
            Stub<IProfileRepository>(),
            Stub<IPetPlacementRepository>(),
            Stub<IReminderRepository>(),
            Stub<IReminderWriter>(),
            tasks,
            Stub<IFocusSessionRepository>(),
            Stub<ILocalNoteRepository>(),
            Stub<IRemoteEnvelopeRepository>(),
            Stub<ICountdownRepository>(),
            Stub<ICheckInRepository>(),
            new CheckInService(Stub<ICheckInRepository>(), clock),
            new TaskService(tasks, clock),
            new FocusService(Stub<IFocusSessionRepository>(), clock, tasks),
            new LocalNoteSelector(Stub<ILocalNoteRepository>(), clock, Stub<IRandomSource>(), preferences),
            Stub<IPairingService>(),
            Stub<ICompanionFeatureTransactions>(),
            pet,
            presentOneShotPetAsync: (petEvent, _, _) =>
            {
                lock (presented) presented.Add(petEvent);
                return Task.CompletedTask;
            });
    }

    private static T Stub<T>() where T : class => DispatchProxy.Create<T, InertProxy>();

    /// <summary>Answers every interface call with a completed/default result;
    /// only the breathing path of the router is exercised here.</summary>
    public class InertProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType;
            if (returnType is null || returnType == typeof(void)) return null;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var result = returnType.GenericTypeArguments[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(result)
                    .Invoke(null, [result.IsValueType ? Activator.CreateInstance(result) : null]);
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }
}
