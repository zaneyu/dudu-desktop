using Dudu.App.System;
using Dudu.App.ViewModels;
using Dudu.Core.Models;
using Dudu.Core.Pet;

namespace Dudu.App.Overlay;

public sealed class OverlayCommandRouter
{
    private readonly CompanionFeatureContext _context;
    private readonly Action<string>? _navigateSettings;

    public OverlayCommandRouter(
        CompanionFeatureContext context,
        Action<string>? navigateSettings = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _navigateSettings = navigateSettings;
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

    public static IReadOnlyList<ComfortAction> ComfortActions => ActionBubbleArrangement.ComfortActions;

    public bool IsReducedMotion => _context.InitialPreferences.ReducedMotion;
    public bool IsBreathing { get; private set; }
    public string BreathingInstruction { get; private set; } = "Breathe in for 4, out for 6.";

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
            ComfortAction.TinyHug => PresentComfortAsync(cancellationToken),
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
        PresentAsync(new PetEvent.AmbientRequested("wave"), cancellationToken);

    private Task ExecuteDrinkWaterAsync(CancellationToken cancellationToken) =>
        PresentAsync(new PetEvent.AmbientRequested("blink"), cancellationToken);

    private async Task ExecuteStartFocusAsync(CancellationToken cancellationToken)
    {
        var vm = new TasksFocusViewModel(_context);
        await vm.StartFocusAsync(cancellationToken);
        _navigateSettings?.Invoke("tasks");
    }

    private Task ExecuteComfortAsync(CancellationToken cancellationToken) =>
        PresentComfortAsync(cancellationToken);

    private Task NavigateAsync(string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _navigateSettings?.Invoke(destination);
        return Task.CompletedTask;
    }

    private Task PresentComfortAsync(CancellationToken cancellationToken) =>
        PresentAsync(new PetEvent.ComfortRequested(), cancellationToken);

    private async Task TakeFiveMinuteBreakAsync(CancellationToken cancellationToken)
    {
        await _context.ApplyPauseAsync(
            new PauseState(PauseMode.OneHour, _context.Clock.UtcNow.ToUniversalTime().AddMinutes(5)),
            cancellationToken);
        await PresentComfortAsync(cancellationToken);
    }

    private async Task CloseComfortAsync(CancellationToken cancellationToken)
    {
        await PresentAsync(new PetEvent.Dismissed("comfort"), cancellationToken);
        _navigateSettings?.Invoke("home");
    }

    private async Task BreatheWithMeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsReducedMotion)
        {
            BreathingInstruction = "Breathe slowly: in for 4, out for 6.";
            return;
        }

        IsBreathing = true;
        try
        {
            for (var cycle = 0; cycle < 6; cycle++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
        finally
        {
            IsBreathing = false;
        }
    }

    private Task PresentAsync(PetEvent petEvent, CancellationToken cancellationToken) =>
        _context.PresentPetAsync(petEvent, cancellationToken);
}
