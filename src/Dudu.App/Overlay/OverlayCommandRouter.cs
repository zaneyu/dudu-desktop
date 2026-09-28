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

    /// <summary>The cute buttons Home shows, in order.</summary>
    public static IReadOnlyList<OverlayAction> PrimaryActions { get; } =
    [
        OverlayAction.Pet,
        OverlayAction.DrinkWater,
        OverlayAction.EatTogether,
        OverlayAction.TinyHug,
        OverlayAction.BreatheWithMe,
    ];

    /// <summary>How long an eat-together meal lasts unless ended early.</summary>
    public static readonly TimeSpan EatingDuration = TimeSpan.FromMinutes(20);

    /// <summary>Label + UI Automation id + settings destination of every
    /// Home button; each id must appear in HomePage.xaml.</summary>
    public static IReadOnlyList<OverlayActionAccessibility> AccessiblePrimaryActions { get; } =
        PrimaryActions
            .Select(action => new OverlayActionAccessibility(
                action,
                Label(action),
                AutomationId(action),
                EquivalentSettingsDestination(action)))
            .ToArray();

    /// <summary>Short status label for an action ("tiny hug ready le").</summary>
    public static string Label(OverlayAction action) => action switch
    {
        OverlayAction.Pet => "pet",
        OverlayAction.DrinkWater => "drink water",
        OverlayAction.EatTogether => "eat together",
        OverlayAction.TinyHug => "tiny hug",
        OverlayAction.BreatheWithMe => "breathe with me",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "oh no unknown overlay action"),
    };

    /// <summary>Stable UI Automation identifier of the Home button for an
    /// action. Tiny hug and breathe keep the ids they had as comfort-panel
    /// buttons, so UI tests and assistive-tech users find them unchanged.</summary>
    public static string AutomationId(OverlayAction action) => action switch
    {
        OverlayAction.Pet => "OverlayActionPet",
        OverlayAction.DrinkWater => "OverlayActionDrinkWater",
        OverlayAction.EatTogether => "OverlayActionEatTogether",
        OverlayAction.TinyHug => "OverlayComfortActionTinyHug",
        OverlayAction.BreatheWithMe => "OverlayComfortActionBreatheWithMe",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "alala unknown overlay action"),
    };

    public static string ComfortLabel(ComfortAction action) => action switch
    {
        ComfortAction.BreatheWithMe => "breathe with me",
        ComfortAction.Close => "stop",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "wait unknown comfort action"),
    };

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
            : Label(action);

    public event EventHandler? ComfortPanelChanged;

    public Task ExecuteAsync(
        OverlayAction action,
        CancellationToken cancellationToken = default) => action switch
        {
            OverlayAction.Pet => ExecutePetAsync(cancellationToken),
            OverlayAction.DrinkWater => ExecuteDrinkWaterAsync(cancellationToken),
            OverlayAction.EatTogether => ToggleEatTogetherAsync(cancellationToken),
            OverlayAction.TinyHug => PresentTinyHugAsync(cancellationToken),
            OverlayAction.BreatheWithMe => BreatheWithMeAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "alala unknown overlay action"),
        };

    public Task ExecuteComfortAsync(
        ComfortAction action,
        CancellationToken cancellationToken = default) => action switch
        {
            ComfortAction.BreatheWithMe => BreatheWithMeAsync(cancellationToken),
            ComfortAction.Close => CloseComfortAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "wait unknown comfort action"),
        };

    /// <summary>Runs an action from an ordinary keyboard/UIA control (the
    /// Home buttons), then navigates to its settings destination. The
    /// destination is a real navigation callback, not just descriptive
    /// text.</summary>
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
            case OverlayAction.EatTogether:
                await ToggleEatTogetherAsync(cancellationToken);
                break;
            case OverlayAction.TinyHug:
                await PresentTinyHugAsync(cancellationToken);
                break;
            case OverlayAction.BreatheWithMe:
                await BreatheWithMeAsync(cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "hmm unknown overlay action");
        }

        await NavigateAsync(EquivalentSettingsDestination(action), cancellationToken);
    }

    public static string EquivalentSettingsDestination(OverlayAction action) => action switch
    {
        OverlayAction.Pet => "home",
        OverlayAction.DrinkWater => "home",
        OverlayAction.EatTogether => "home",
        OverlayAction.TinyHug or OverlayAction.BreatheWithMe => "home",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "oh no unknown overlay action"),
    };

    public static string EquivalentSettingsDestination(ComfortAction action) => action switch
    {
        ComfortAction.BreatheWithMe or ComfortAction.Close => "home",
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
            // Only the run that still owns the breathing state may reset it. A run
            // superseded by a restart must not clear IsBreathing or overwrite the
            // newer run's panel, and CancelBreathing already set both itself.
            bool owner;
            lock (_gate)
            {
                owner = ReferenceEquals(_breathingCancellation, linked);
                if (owner)
                {
                    _isBreathing = false;
                    _breathingCancellation = null;
                }
            }
            if (owner)
            {
                SetComfortPanel(linked.IsCancellationRequested
                    ? new ComfortPanelState(true, false, BreathVisualPhase.Idle, "breathing exercise cancelled")
                    : new ComfortPanelState(true, false, BreathVisualPhase.Complete, "good job lihai breathe done"));
            }
        }
    }

    public void CancelBreathing(bool closePanel = false)
    {
        lock (_gate)
        {
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

/// <summary>State of the "breathe with me" exercise shown on Home. It contains
/// no mood deduction or recording; all transitions are user initiated.</summary>
public sealed record ComfortPanelState(bool IsOpen, bool IsBreathing, BreathVisualPhase Phase, string Instruction)
{
    public static ComfortPanelState Closed { get; } = new(false, false, BreathVisualPhase.Idle, string.Empty);
}

public sealed record OverlayActionAccessibility(
    OverlayAction Action,
    string Label,
    string AutomationId,
    string SettingsDestination);
