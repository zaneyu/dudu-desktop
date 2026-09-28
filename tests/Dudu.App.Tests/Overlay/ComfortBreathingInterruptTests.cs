using System.Reflection;
using Dudu.App.Hosting;
using Dudu.App.Overlay;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Pet;
using Dudu.Core.Time;
using Xunit;

namespace Dudu.App.Tests.Overlay;

/// <summary>
/// "Breathe with me" runs on Home for a full minute; choosing it again
/// restarts it, and Stop (<see cref="ComfortAction.Close"/>) ends it at once.
/// </summary>
public sealed class ComfortBreathingInterruptTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

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

        var first = router.ExecuteAsync(OverlayAction.BreatheWithMe, TestContext.Current.CancellationToken);
        await firstEntry.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        var second = router.ExecuteAsync(OverlayAction.BreatheWithMe, TestContext.Current.CancellationToken);
        await secondEntry.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

        // The first run ends as a normal cancellation; the second one runs.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.True(router.IsBreathing);

        await router.ExecuteComfortAsync(ComfortAction.Close, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.False(router.IsBreathing);
        Assert.False(router.ComfortPanel.IsOpen);
    }

    [Fact]
    public async Task A_tiny_hug_while_not_breathing_leaves_the_panel_text_alone()
    {
        var presented = new List<PetEvent>();
        var router = new OverlayCommandRouter(CreateContext(presented), (_, _) => Task.CompletedTask);
        var before = router.ComfortPanel;

        await router.ExecuteAsync(OverlayAction.TinyHug, TestContext.Current.CancellationToken);

        Assert.Equal(before, router.ComfortPanel);
        Assert.Contains(presented, petEvent => petEvent is PetEvent.ComfortRequested);
    }

    private static CompanionFeatureContext CreateContext(List<PetEvent> presented)
    {
        var clock = Stub<IClock>();
        var preferences = Preferences.Default;
        var pet = PetStateMachine.CreateIdle();
        return new CompanionFeatureContext(
            clock,
            new PreferenceMutationCoordinator(preferences, Stub<IPreferencesRepository>()),
            Stub<IProfileRepository>(),
            Stub<IPetPlacementRepository>(),
            Stub<ILocalNoteRepository>(),
            Stub<IRemoteEnvelopeRepository>(),
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
