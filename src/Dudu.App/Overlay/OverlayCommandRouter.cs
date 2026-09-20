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
    ];

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
        OverlayAction.ComfortMe => "home",
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
        _context.PresentOneShotPetAsync(
            new PetEvent.AmbientRequested("greeting"),
            "greeting",
            cancellationToken);

    private Task ExecuteDrinkWaterAsync(CancellationToken cancellationToken) =>
        _context.PresentOneShotPetAsync(
            new PetEvent.AmbientRequested("drink"),
            "drink",
            cancellationToken);

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
        await PresentTinyHugAsync(cancellationToken);
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
