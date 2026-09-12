using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.Overlay;

public sealed class OverlayCommandRouter
{
    private readonly CompanionFeatureContext _context;
    private readonly Func<string, CancellationToken, Task>? _navigateSettings;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private CancellationTokenSource? _breathingCancellation;
    private bool _closePanelAfterBreathingCancellation;

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

    public bool IsReducedMotion => _context.CurrentPreferences.ReducedMotion;
    public bool IsBreathing { get; private set; }
    public string BreathingInstruction { get; private set; } = "Breathe in for 4, out for 6.";
    public ComfortPanelState ComfortPanel { get; private set; } = ComfortPanelState.Closed;
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
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown overlay action."),
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
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown comfort action."),
        };

    public static string EquivalentSettingsDestination(OverlayAction action) => action switch
    {
        OverlayAction.Pet => "home",
        OverlayAction.DrinkWater => "reminders",
        OverlayAction.StartFocus or OverlayAction.Tasks => "tasks",
        OverlayAction.LoveNote => "notes",
        OverlayAction.ComfortMe => "home",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown overlay action."),
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
        await vm.StartFocusAsync(cancellationToken);
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
                "Dudu could not open the requested settings destination."));
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
        await _context.ApplyPauseAsync(
            PausePolicy.ForFiveMinutes(_context.Clock.UtcNow.ToUniversalTime()),
            cancellationToken);
        await PresentTinyHugAsync(cancellationToken);
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
            BreathingInstruction = "Breathe slowly: in for 4, out for 6.";
            SetComfortPanel(new ComfortPanelState(true, false, BreathVisualPhase.Static, BreathingInstruction));
            return;
        }

        _breathingCancellation?.Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _breathingCancellation = linked;
        _closePanelAfterBreathingCancellation = false;
        IsBreathing = true;
        try
        {
            for (var cycle = 0; cycle < 6; cycle++)
            {
                SetComfortPanel(new ComfortPanelState(true, true, BreathVisualPhase.Inhale, "Breathe in for 4."));
                await _delayAsync(TimeSpan.FromSeconds(4), linked.Token);
                SetComfortPanel(new ComfortPanelState(true, true, BreathVisualPhase.Exhale, "Breathe out for 6."));
                await _delayAsync(TimeSpan.FromSeconds(6), linked.Token);
            }
        }
        finally
        {
            IsBreathing = false;
            var closePanel = _closePanelAfterBreathingCancellation;
            if (ReferenceEquals(_breathingCancellation, linked))
            {
                _breathingCancellation = null;
                _closePanelAfterBreathingCancellation = false;
            }
            SetComfortPanel(closePanel
                ? ComfortPanelState.Closed
                : linked.IsCancellationRequested
                    ? new ComfortPanelState(true, false, BreathVisualPhase.Idle, "Breathing exercise cancelled.")
                    : new ComfortPanelState(true, false, BreathVisualPhase.Complete, "Nice job. You took a minute for yourself."));
        }
    }

    public void OpenComfortPanel() => SetComfortPanel(new ComfortPanelState(true, false, BreathVisualPhase.Idle, "Choose a gentle next step."));

    public void CancelBreathing(bool closePanel = false)
    {
        _closePanelAfterBreathingCancellation = closePanel;
        _breathingCancellation?.Cancel();
        _breathingCancellation = null;
        IsBreathing = false;
        SetComfortPanel(closePanel
            ? ComfortPanelState.Closed
            : new ComfortPanelState(true, false, BreathVisualPhase.Idle, "Breathing exercise cancelled."));
    }

    private void SetComfortPanel(ComfortPanelState state)
    {
        ComfortPanel = state;
        BreathingInstruction = state.Instruction;
        ComfortPanelChanged?.Invoke(this, EventArgs.Empty);
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
