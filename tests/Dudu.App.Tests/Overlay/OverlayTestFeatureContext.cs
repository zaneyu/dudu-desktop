using System.Reflection;
using Dudu.App.Hosting;
using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Abstractions;
using Dudu.Core.CheckIns;
using Dudu.Core.Focus;
using Dudu.Core.Models;
using Dudu.Core.Notes;
using Dudu.Core.Pet;
using Dudu.Core.Tasks;
using Dudu.Core.Time;

namespace Dudu.App.Tests.Overlay;

/// <summary>
/// Minimal <see cref="CompanionFeatureContext"/> for overlay surface tests.
/// Repositories and services the overlay never touches are inert
/// <see cref="DispatchProxy"/> stand-ins; only the presentation, pause and
/// navigation seams the overlay actually drives are configurable.
/// </summary>
internal static class OverlayTestFeatureContext
{
    public static CompanionFeatureContext Create(
        Func<PetEvent, string, CancellationToken, Task>? presentOneShotPetAsync = null,
        Func<PauseState, CancellationToken, Task>? applyPauseAsync = null)
    {
        var clock = new FixedClock();
        var preferences = Preferences.Default;
        var preferenceRepository = Inert<IPreferencesRepository>();
        var tasks = Inert<ITaskRepository>();
        var localNotes = Inert<ILocalNoteRepository>();
        var checkIns = Inert<ICheckInRepository>();
        var focusSessions = Inert<IFocusSessionRepository>();
        var reminders = Inert<IReminderRepository>();
        var pet = PetStateMachine.CreateIdle();
        return new CompanionFeatureContext(
            clock,
            new PreferenceMutationCoordinator(preferences, preferenceRepository),
            Inert<IProfileRepository>(),
            Inert<IPetPlacementRepository>(),
            reminders,
            Inert<IReminderWriter>(),
            tasks,
            focusSessions,
            localNotes,
            Inert<IRemoteEnvelopeRepository>(),
            Inert<ICountdownRepository>(),
            checkIns,
            new CheckInService(checkIns, clock),
            new TaskService(tasks, clock),
            new FocusService(focusSessions, clock, tasks),
            new LocalNoteSelector(localNotes, clock, new FirstRandom(), preferences),
            Inert<IPairingService>(),
            Inert<ICompanionFeatureTransactions>(),
            pet,
            applyPauseAsync: applyPauseAsync,
            presentPetAsync: (petEvent, _) =>
            {
                pet.Handle(petEvent);
                return Task.CompletedTask;
            },
            presentOneShotPetAsync: presentOneShotPetAsync ?? ((_, _, _) => Task.CompletedTask));
    }

    private static T Inert<T>() where T : class
    {
        var proxy = DispatchProxy.Create<T, InertProxy>();
        return proxy;
    }

    public class InertProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType;
            if (returnType is null || returnType == typeof(void)) return null;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (returnType == typeof(ValueTask)) return ValueTask.CompletedTask;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                return typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, [DefaultOf(resultType)]);
            }

            return DefaultOf(returnType);
        }

        private static object? DefaultOf(Type type) =>
            type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.Parse("2026-09-12T10:00:00Z");

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FirstRandom : IRandomSource
    {
        public int Next(int exclusiveMax) => 0;
    }
}
