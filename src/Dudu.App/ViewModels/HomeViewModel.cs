using CommunityToolkit.Mvvm.Input;
using Dudu.Core.Models;
using Dudu.Core.Time;

namespace Dudu.App.ViewModels;

/// <summary>Home is the cute page: a greeting that uses the recipient name, the UK
/// partner clock, and a pet button. The other cute buttons (drink, eat together,
/// study together, sing for me, tiny hug, breathe with me) are routed by HomePage through the overlay command
/// router, so this view model carries no practical state at all.</summary>
public sealed class HomeViewModel : FeatureViewModelBase
{
    private readonly CompanionFeatureContext _context;
    private string _recipientName = string.Empty;
    private readonly PartnerClock _partnerClock;
    private PartnerClockReading _partnerReading;

    public HomeViewModel(CompanionFeatureContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RefreshCommand = new AsyncRelayCommand((CancellationToken ct) => RefreshAsync(ct));
        PetCommand = new AsyncRelayCommand((CancellationToken ct) => PetAsync(ct));
        _partnerClock = new PartnerClock(_context.Clock);
        _partnerReading = _partnerClock.Now();
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand PetCommand { get; }

    /// <summary>", name" when a recipient name is on file, or empty so
    /// copy built from it still reads naturally.</summary>
    private string RecipientClause => string.IsNullOrWhiteSpace(_recipientName) ? string.Empty : $", {_recipientName}";

    public string GreetingText => $"hi hi{RecipientClause}! dudu missed u";

    // Display-only UK partner clock for the Home card. HomePage ticks
    // RefreshPartnerClock from a DispatcherTimer while the page is loaded.
    public string PartnerTimeText => _partnerReading.TimeText;
    public string PartnerSecondsText => _partnerReading.SecondsText;
    public string PartnerDayPeriodEmoji => _partnerReading.DayPeriodEmoji;
    public string PartnerDateLine => $"{_partnerReading.DateText} · {_partnerReading.DayText}";
    public string PartnerOffsetLine => $"{_partnerReading.OffsetLabel} · {_partnerReading.DifferenceText}";
    public string PartnerTimeOfDayText => _partnerReading.MoodText;
    public string PartnerClockSpokenText => _partnerReading.SpokenText;

    /// <summary>Re-reads the partner clock and raises change notifications only
    /// for strings that actually changed, so a sub-second tick is cheap.</summary>
    public void RefreshPartnerClock()
    {
        var previous = _partnerReading;
        _partnerReading = _partnerClock.Now();
        if (previous.SecondsText != _partnerReading.SecondsText) OnPropertyChanged(nameof(PartnerSecondsText));
        if (previous.TimeText == _partnerReading.TimeText
            && previous.OffsetLabel == _partnerReading.OffsetLabel
            && previous.DifferenceText == _partnerReading.DifferenceText
            && previous.DateText == _partnerReading.DateText
            && previous.DayText == _partnerReading.DayText)
        {
            return;
        }

        OnPropertyChanged(nameof(PartnerTimeText));
        OnPropertyChanged(nameof(PartnerDayPeriodEmoji));
        OnPropertyChanged(nameof(PartnerDateLine));
        OnPropertyChanged(nameof(PartnerOffsetLine));
        OnPropertyChanged(nameof(PartnerTimeOfDayText));
        OnPropertyChanged(nameof(PartnerClockSpokenText));
    }

    /// <summary>Plain-language pet state. The raw enum name lower-cased read as
    /// "dudu is remotenote" / "welcomeback"; the settings shell's
    /// companion panel uses this wording.</summary>
    public static string DescribePetState(PetState state) => state switch
    {
        PetState.Comfort => "comforting u",
        PetState.RemoteNote => "holding a note for u",
        PetState.WelcomeBack => "saying welcome back",
        PetState.Ambient => "having a little moment",
        PetState.Interaction => "enjoying the attention",
        PetState.Dragging => "being carried around",
        PetState.Eating => "eating with u",
        PetState.Studying => "studying with u",
        PetState.Singing => "singing for u",
        _ => "idle",
    };

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunRefreshAsync(async ct =>
        {
            var profile = await _context.Profiles.GetAsync(ct);
            await MutateAsync(() =>
            {
                // Trimmed once here so a name saved with stray whitespace never
                // leaks into the greeting as an extra space.
                _recipientName = profile?.RecipientName?.Trim() ?? string.Empty;
                OnPropertyChanged(nameof(GreetingText));
            }, ct);
        }, cancellationToken);
    }

    public Task PetAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _context.PetAsync(cancellationToken));

    /// <summary>Shares FeatureViewModelBase's exception-to-copy mapping with
    /// HomePage's code-behind click handlers, which run outside any
    /// RunAsync call and would otherwise surface raw exception.Message.</summary>
    public static string DescribeError(Exception exception) => ToUserMessage(exception);
}
