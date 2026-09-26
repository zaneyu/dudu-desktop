using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.Overlay;

public sealed class OverlayCommandRouter
{
    private readonly object _gate = new();
    private readonly CompanionFeatureContext _context;
    private readonly Func<string, CancellationToken, Task>? _navigateSettings;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private CancellationTokenSource? _breathingCancellation;
    private bool _closePanelAfterBreathingCancellation;
    private bool _isBreathing;
    private string _breathingInstruction = "breathe in for 4, out for 6";
    private ComfortPanelState _comfortPanel = ComfortPanelState.Closed;
    private string? _mealId;
    /// <summary>Serializes eat-together toggles end to end, so a quick
    /// start-then-end reaches the pet in that order.</summary>
    private readonly SemaphoreSlim _mealToggle = new(1, 1);
    private CancellationTokenSource? _mealTimer;

    public OverlayCommandRouter(
        CompanionFeatureContext context,
        Func<string, CancellationToken, Task>? navigateSettings = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _navigateSettings = navigateSettings;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public static IReadOnlyList<OverlayAction> PrimaryActions { get; } =
    [
        OverlayAction.Pet,
        OverlayAction.DrinkWater,
        OverlayAction.StartFocus,
        OverlayAction.Tasks,
        OverlayAction.LoveNote,
        OverlayAction.ComfortMe,
        OverlayAction.EatTogether,
    ];

    /// <summary>How long an eat-together meal lasts unless ended early.</summary>
    public static readonly TimeSpan EatingDuration = TimeSpan.FromMinutes(20);

    public static IReadOnlyList<ComfortAction> ComfortActions => ActionBubbleLayout.ComfortActions;

    public static IReadOnlyList<OverlayActionAccessibility> AccessiblePrimaryActions { get; } =
        PrimaryActions
            .Select(action => new OverlayActionAccessibility(
                action,
                ActionBubbleLayout.Label(action),
                ActionBubbleLayout.AutomationId(action),
                EquivalentSettingsDestination(action)))
            .ToArray();

    public static IReadOnlyList<ComfortActionAccessibility> AccessibleComfortActions { get; } =
        ComfortActions
            .Select(action => new ComfortActionAccessibility(
                action,
                ActionBubbleLayout.ComfortLabel(action),
                ActionBubbleLayout.ComfortAutomationId(action),
                EquivalentSettingsDestination(action)))
            .ToArray();

    public bool IsReducedMotion => _context.CurrentPreferences.ReducedMotion;
    public AppTheme Theme => _context.CurrentPreferences.Theme;
    public bool IsBreathing { get { lock (_gate) return _isBreathing; } }
    public string BreathingInstruction { get { lock (_gate) return _breathingInstruction; } }
    public ComfortPanelState ComfortPanel { get { lock (_gate) return _comfortPanel; } }
    public bool IsEating { get { lock (_gate) return _mealId is not null; } }

    /// <summary>Painted label, reflecting live toggle state (eat together
    /// becomes "done eating" while a meal runs).</summary>
    public string LabelFor(OverlayAction action) =>
        action == OverlayAction.EatTogether && IsEating
            ? "done eating"
            : ActionBubbleLayout.Label(action);

    public event EventHandler? ComfortPanelChanged;

    public Task ExecuteAsync(
        OverlayAction action,
        CancellationToken cancellationToken = default) => action switch
        {
            OverlayAction.Pet => ExecutePetAsync(cancellationToken),
            OverlayAction.DrinkWater => ExecuteDrinkWaterAsync(cancellationToken),
            OverlayAction.StartFocus => ExecuteStartFocusAsync(cancellationToken),
            OverlayAction.Tasks => NavigateAsync("tasks", cancellationToken),
            OverlayAction.LoveNote => NavigateAsync("notes", cancellationToken),
            OverlayAction.ComfortMe => ExecuteComfortAsync(cancellationToken),
            OverlayAction.EatTogether => ToggleEatTogetherAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "alala unknown overlay action"),
        };

    public Task ExecuteComfortAsync(
        ComfortAction action,
        CancellationToken cancellationToken = default) => action switch
        {
            ComfortAction.BreatheWithMe => BreatheWithMeAsync(cancellationToken),
            ComfortAction.TinyHug => PresentTinyHugAsync(cancellationToken),
            ComfortAction.ReadALoveNote => NavigateAsync("notes", cancellationToken),
            ComfortAction.TakeAFiveMinuteBreak => TakeFiveMinuteBreakAsync(cancellationToken),
            ComfortAction.Close => CloseComfortAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "wait unknown comfort action"),
        };

    /// <summary>Runs the same operation exposed by the pointer-only overlay
    /// from an ordinary keyboard/UIA Settings control. The destination is a
    /// real navigation callback, not just descriptive text.</summary>
    public async Task ExecuteAccessibleAsync(
        OverlayAction action,
        CancellationToken cancellationToken = default)
    {
        switch (action)
        {
            case OverlayAction.Pet:
                await ExecutePetAsync(cancellationToken);
                break;
            case OverlayAction.DrinkWater:
                await ExecuteDrinkWaterAsync(cancellationToken);
                break;
            case OverlayAction.StartFocus:
                await ExecuteStartFocusAsync(cancellationToken);
                return;
            case OverlayAction.Tasks:
                await NavigateAsync("tasks", cancellationToken);
                return;
            case OverlayAction.LoveNote:
                await NavigateAsync("notes", cancellationToken);
                return;
            case OverlayAction.ComfortMe:
                await ExecuteComfortAsync(cancellationToken);
                break;
            case OverlayAction.EatTogether:
                await ToggleEatTogetherAsync(cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "hmm unknown overlay action");
        }

        await NavigateAsync(EquivalentSettingsDestination(action), cancellationToken);
    }

    /// <summary>Keyboard/UIA execution path for every comfort-panel choice.</summary>
    public async Task ExecuteComfortAccessibleAsync(
        ComfortAction action,
        CancellationToken cancellationToken = default)
    {
        if (action == ComfortAction.ReadALoveNote)
        {
            await NavigateAsync("notes", cancellationToken);
            return;
        }

        await ExecuteComfortAsync(action, cancellationToken);
        await NavigateAsync(EquivalentSettingsDestination(action), cancellationToken);
    }

    public static string EquivalentSettingsDestination(OverlayAction action) => action switch
    {
        OverlayAction.Pet => "home",
        OverlayAction.DrinkWater => "reminders",
        OverlayAction.StartFocus or OverlayAction.Tasks => "tasks",
        OverlayAction.LoveNote => "notes",
        OverlayAction.ComfortMe or OverlayAction.EatTogether => "home",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "oh no unknown overlay action"),
    };

    public static string EquivalentSettingsDestination(ComfortAction action) => action switch
    {
        ComfortAction.BreatheWithMe or ComfortAction.TinyHug
            or ComfortAction.TakeAFiveMinuteBreak or ComfortAction.Close => "home",
        ComfortAction.ReadALoveNote => "notes",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "alala unknown comfort action"),
    };

    private Task ExecutePetAsync(CancellationToken cancellationToken) =>
        _context.PetAsync(cancellationToken);

    private Task ExecuteDrinkWaterAsync(CancellationToken cancellationToken) =>
        _context.DrinkAsync(cancellationToken);

    /// <summary>Starts an in-memory eat-together meal (eat loop, notes and
    /// reminders held back like focus) or, when one is running, ends it.
    /// A running meal ends by itself after <see cref="EatingDuration"/>.</summary>
    private async Task ToggleEatTogetherAsync(CancellationToken cancellationToken)
    {
        await _mealToggle.WaitAsync(cancellationToken);
        try
        {
            await ToggleEatTogetherCoreAsync(cancellationToken);
        }
        finally
        {
            _mealToggle.Release();
        }
    }

    private async Task ToggleEatTogetherCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? endingMeal;
        string? startingMeal = null;
        CancellationTokenSource? timer = null;
        lock (_gate)
        {
            endingMeal = _mealId;
            _mealTimer?.Cancel();
            _mealTimer = null;
            _mealId = null;
            if (endingMeal is null)
            {
                startingMeal = Guid.NewGuid().ToString("N");
                timer = new CancellationTokenSource();
                _mealId = startingMeal;
                _mealTimer = timer;
            }
        }

        if (endingMeal is not null)
        {
            try
            {
                await PresentAsync(new PetEvent.EatingEnded(endingMeal), cancellationToken);
            }
            catch
            {
                // The router already reads "no meal"; never leave the pet
                // latched in one nothing will end.
                _context.Pet.Handle(new PetEvent.EatingEnded(endingMeal));
                throw;
            }
            return;
        }

        try
        {
            await PresentAsync(new PetEvent.EatingStarted(startingMeal!), cancellationToken);
        }
        catch
        {
            // Never leave the pet latched in a meal nothing can end.
            lock (_gate)
            {
                if (_mealId == startingMeal)
                {
                    _mealId = null;
                    _mealTimer = null;
                }
            }
            timer!.Cancel();
            _context.Pet.Handle(new PetEvent.EatingEnded(startingMeal!));
            throw;
        }

        _ = EndMealWhenDueAsync(startingMeal!, timer!.Token);
    }

    private async Task EndMealWhenDueAsync(string mealId, CancellationToken timerToken)
    {
        try
        {
            await _delayAsync(EatingDuration, timerToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (_mealId != mealId) return;
            _mealId = null;
            _mealTimer = null;
        }

        try
        {
            await PresentAsync(new PetEvent.EatingEnded(mealId), CancellationToken.None);
        }
        catch (Exception exception)
        {
            _context.Pet.Handle(new PetEvent.EatingEnded(mealId));
            global::System.Diagnostics.Trace.TraceError(
                "Dudu eat-together end failed: {0} (0x{1:X8})",
                exception.GetType().FullName,
                exception.HResult);
        }
    }

    private async Task ExecuteStartFocusAsync(CancellationToken cancellationToken)
    {
        var vm = new TasksFocusViewModel(_context);
        await vm.StartFocusOrThrowAsync(cancellationToken);
        await NavigateAsync("tasks", cancellationToken);
    }

    private Task ExecuteComfortAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenComfortPanel();
        return Task.CompletedTask;
    }

    private Task NavigateAsync(string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_navigateSettings is null)
        {
            return Task.FromException(new InvalidOperationException(
                "aiyo dudu couldnt open that settings page"));
        }

        return _navigateSettings(destination, cancellationToken);
    }

    private Task PresentTinyHugAsync(CancellationToken cancellationToken) =>
        _context.PresentOneShotPetAsync(
            new PetEvent.ComfortRequested(),
            "comfort",
            cancellationToken);

    private async Task TakeFiveMinuteBreakAsync(CancellationToken cancellationToken)
    {
        // Present the hug first: applying the pause hides the overlay
        // (indirectly, via the lifecycle coordinator's pause gate), so
        // doing that before the hug animation played it into a window that
        // was about to disappear underneath it.
        //
        // Finding 5: the pause must not depend on the hug succeeding -- a
        // faulting hug animation must not mean no break at all. Swallow and
        // trace it the same way SetComfortPanel's listener failures are
        // handled just below, then apply the pause regardless.
        try
        {
            await PresentTinyHugAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            global::System.Diagnostics.Trace.TraceError("Dudu tiny-hug presentation failed: {0}", exception);
        }

        await _context.ApplyPauseAsync(
            PausePolicy.ForFiveMinutes(_context.Clock.UtcNow.ToUniversalTime()),
            cancellationToken);
    }

    private async Task CloseComfortAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelBreathing(closePanel: true);
        await PresentAsync(new PetEvent.Dismissed("comfort"), cancellationToken);
    }

    private async Task BreatheWithMeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsReducedMotion)
        {
            const string instruction = "breathe slowly in for 4, out for 6";
            SetComfortPanel(new ComfortPanelState(true, false, BreathVisualPhase.Static, instruction));
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _breathingCancellation?.Cancel();
            _breathingCancellation = linked;
            _closePanelAfterBreathingCancellation = false;
            _isBreathing = true;
        }
        try
        {
            for (var cycle = 0; cycle < 6; cycle++)
            {
                SetComfortPanel(new ComfortPanelState(true, true, BreathVisualPhase.Inhale, "breathe in for 4"));
                await _delayAsync(TimeSpan.FromSeconds(4), linked.Token);
                SetComfortPanel(new ComfortPanelState(true, true, BreathVisualPhase.Exhale, "breathe out for 6"));
                await _delayAsync(TimeSpan.FromSeconds(6), linked.Token);
            }
        }
        finally
        {
            bool closePanel;
            lock (_gate)
            {
                _isBreathing = false;
                closePanel = _closePanelAfterBreathingCancellation;
                if (ReferenceEquals(_breathingCancellation, linked))
                {
                    _breathingCancellation = null;
                    _closePanelAfterBreathingCancellation = false;
                }
            }
            SetComfortPanel(closePanel
                ? ComfortPanelState.Closed
                : linked.IsCancellationRequested
                    ? new ComfortPanelState(true, false, BreathVisualPhase.Idle, "breathing exercise cancelled")
                    : new ComfortPanelState(true, false, BreathVisualPhase.Complete, "good job lihai breathe done"));
        }
    }

    public void OpenComfortPanel() => SetComfortPanel(new ComfortPanelState(true, false, BreathVisualPhase.Idle, "choose a gentle next step"));

    public void CancelBreathing(bool closePanel = false)
    {
        lock (_gate)
        {
            _closePanelAfterBreathingCancellation = closePanel;
            _breathingCancellation?.Cancel();
            _breathingCancellation = null;
            _isBreathing = false;
        }
        SetComfortPanel(closePanel
            ? ComfortPanelState.Closed
            : new ComfortPanelState(true, false, BreathVisualPhase.Idle, "breathing exercise cancelled"));
    }

    private void SetComfortPanel(ComfortPanelState state)
    {
        lock (_gate)
        {
            _comfortPanel = state;
            _breathingInstruction = state.Instruction;
        }
        var handlers = ComfortPanelChanged;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().OfType<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                global::System.Diagnostics.Trace.TraceError("Dudu comfort panel listener failed: {0}", exception);
            }
        }
    }

    private Task PresentAsync(PetEvent petEvent, CancellationToken cancellationToken) =>
        _context.PresentPetAsync(petEvent, cancellationToken);
}

public enum BreathVisualPhase { Idle, Inhale, Exhale, Static, Complete }

/// <summary>Application contract for slice 3's comfort surface. It contains no
/// mood deduction or recording; all transitions are user initiated.</summary>
public sealed record ComfortPanelState(bool IsOpen, bool IsBreathing, BreathVisualPhase Phase, string Instruction)
{
    public static ComfortPanelState Closed { get; } = new(false, false, BreathVisualPhase.Idle, string.Empty);
}

public sealed record OverlayActionAccessibility(
    OverlayAction Action,
    string Label,
    string AutomationId,
    string SettingsDestination);

public sealed record ComfortActionAccessibility(
    ComfortAction Action,
    string Label,
    string AutomationId,
    string SettingsDestination);
