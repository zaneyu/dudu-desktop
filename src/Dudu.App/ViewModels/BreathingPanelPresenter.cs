using CommunityToolkit.Mvvm.ComponentModel;
using Dudu.App.Overlay;

namespace Dudu.App.ViewModels;

/// <summary>
/// Shows the "breathe with me" exercise on Home. The Home button runs the
/// router's 60 s breathing loop; this mirrors the router's
/// <see cref="OverlayCommandRouter.ComfortPanelChanged"/> state as an
/// instruction plus a short phase line, and its <see cref="StopAsync"/> is the
/// panel's Stop button (<see cref="ComfortAction.Close"/>).
/// </summary>
public sealed class BreathingPanelPresenter : ObservableObject, IDisposable
{
    private readonly OverlayCommandRouter? _router;
    private readonly Action<Action> _dispatch;
    private bool _attached;
    private bool _isVisible;
    private string _instruction = string.Empty;
    private string _phaseText = string.Empty;

    /// <param name="router">The page's command router; without one (it is not
    /// composed yet) the panel simply never shows.</param>
    /// <param name="dispatch">Runs a state update on the UI thread; the
    /// router may raise its change event from a timer continuation. Inline
    /// when omitted.</param>
    public BreathingPanelPresenter(OverlayCommandRouter? router, Action<Action>? dispatch = null)
    {
        _router = router;
        _dispatch = dispatch ?? (action => action());
        Attach();
    }

    public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }

    /// <summary>What to do right now ("breathe in for 4").</summary>
    public string Instruction { get => _instruction; private set => SetProperty(ref _instruction, value); }

    /// <summary>A short, friendly name for the current phase.</summary>
    public string PhaseText { get => _phaseText; private set => SetProperty(ref _phaseText, value); }

    /// <summary>True while the timed exercise is running (not in reduced
    /// motion, where the instruction is static).</summary>
    public bool IsBreathing => _router?.IsBreathing == true;

    /// <summary>Starts listening again (the page was shown again) and picks up
    /// whatever the router shows right now.</summary>
    public void Attach()
    {
        if (_router is null || _attached) return;
        _router.ComfortPanelChanged += OnComfortPanelChanged;
        _attached = true;
        Apply(_router.ComfortPanel);
    }

    /// <summary>Stops listening (the page was hidden).</summary>
    public void Detach()
    {
        if (_router is null || !_attached) return;
        _router.ComfortPanelChanged -= OnComfortPanelChanged;
        _attached = false;
    }

    /// <summary>The Stop button: ends the exercise and hides the panel. The
    /// running breathe click then ends with a cancellation, which is the
    /// normal "stopped" outcome, not an error.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_router is null) return Task.CompletedTask;
        return _router.ExecuteComfortAsync(ComfortAction.Close, cancellationToken);
    }

    /// <summary>Leaving Home stops a running exercise, and only a running
    /// one: a blanket Close would also cut short a tiny hug, which shares
    /// the "comfort" dismissal id.</summary>
    public Task StopIfBreathingAsync(CancellationToken cancellationToken = default) =>
        IsBreathing ? StopAsync(cancellationToken) : Task.CompletedTask;

    public void Dispose() => Detach();

    public static string PhaseTextFor(BreathVisualPhase phase) => phase switch
    {
        BreathVisualPhase.Inhale => "in… 🌬",
        BreathVisualPhase.Exhale => "and out… 🍃",
        BreathVisualPhase.Static => "slow and steady",
        BreathVisualPhase.Complete => "all done, good job",
        _ => "stopped",
    };

    private void OnComfortPanelChanged(object? sender, EventArgs args)
    {
        var router = _router;
        if (router is null) return;
        _dispatch(() => Apply(router.ComfortPanel));
    }

    private void Apply(ComfortPanelState panel)
    {
        Instruction = panel.Instruction;
        PhaseText = panel.IsOpen ? PhaseTextFor(panel.Phase) : string.Empty;
        IsVisible = panel.IsOpen;
    }
}
