using Dudu.Core.Assets;

namespace Dudu.App.Overlay;

public enum OverlayAction { Pet, DrinkWater, StartFocus, Tasks, LoveNote, ComfortMe, EatTogether }

public enum ComfortAction { BreatheWithMe, TinyHug, ReadALoveNote, TakeAFiveMinuteBreak, Close }

public enum OverlayActionSurfaceKind { Closed, Primary, Comfort, Status }

public sealed record OverlayActionPlacement(OverlayAction Action, PixelRect HitRegion);

public sealed record ComfortActionPlacement(ComfortAction Action, PixelRect HitRegion);

public sealed record ActionBubbleArrangement(
    PixelRect Bounds,
    IReadOnlyList<OverlayActionPlacement> PrimaryActions)
{
    public PixelRect DetailRegion { get; init; }
}

public sealed record ComfortBubbleArrangement(
    PixelRect Bounds,
    IReadOnlyList<ComfortActionPlacement> Actions)
{
    public PixelRect DetailRegion { get; init; }
}

public static class ActionBubbleLayout
{
    private const int BubbleWidth = 280;
    private const int PreferredActionHeight = 44;
    private const int MinimumActionHeight = 16;
    private const int PreferredPadding = 12;
    private const int PreferredGap = 8;
    private const int DetailGap = 4;
    private const int DetailHeight = 28;
    private const int MinimumSurfaceWidth = 16;

    /// <summary>Upper bound on painted primary rows. A surface too short
    /// for all of them (the 128 px fallback pack fits six) keeps the first
    /// rows in <see cref="OverlayCommandRouter.PrimaryActions"/> order, so
    /// eat-together, listed last, is the one that drops.</summary>
    public const int MaximumPrimaryActions = 7;
    private static readonly OverlayAction[] AllowedActions = Enum.GetValues<OverlayAction>();

    public static IReadOnlyList<ComfortAction> ComfortActions { get; } =
    [
        ComfortAction.BreatheWithMe,
        ComfortAction.TinyHug,
        ComfortAction.ReadALoveNote,
        ComfortAction.TakeAFiveMinuteBreak,
        ComfortAction.Close,
    ];

    public static ActionBubbleArrangement Arrange(
        IEnumerable<OverlayAction> actions,
        PixelRect workArea,
        PixelPoint petAnchor) =>
        TryArrange(actions, workArea, petAnchor)
        ?? throw new InvalidOperationException("hmm work area too small for an action surface");

    public static ActionBubbleArrangement? TryArrange(
        IEnumerable<OverlayAction> actions,
        PixelRect workArea,
        PixelPoint petAnchor)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ValidateWorkArea(workArea);
        var selected = actions
            .Where(action => AllowedActions.Contains(action))
            .Distinct()
            .Take(MaximumPrimaryActions)
            .ToArray();
        if (selected.Length == 0) selected = [OverlayAction.Pet];

        var rows = ArrangeRows(selected.Length, workArea, petAnchor);
        if (rows is null) return null;
        return new ActionBubbleArrangement(
            rows.Value.Bounds,
            selected.Take(rows.Value.Regions.Count)
                .Select((action, index) => new OverlayActionPlacement(action, rows.Value.Regions[index]))
                .ToArray())
        {
            DetailRegion = rows.Value.DetailRegion,
        };
    }

    public static ComfortBubbleArrangement? ArrangeComfort(PixelRect workArea, PixelPoint petAnchor)
    {
        ValidateWorkArea(workArea);
        var rows = ArrangeRows(ComfortActions.Count, workArea, petAnchor, requireAllRows: true);
        if (rows is null || rows.Value.Regions.Count != ComfortActions.Count) return null;
        return new ComfortBubbleArrangement(
            rows.Value.Bounds,
            ComfortActions
                .Select((action, index) => new ComfortActionPlacement(action, rows.Value.Regions[index]))
                .ToArray())
        {
            DetailRegion = rows.Value.DetailRegion,
        };
    }

    public static string Label(OverlayAction action) => action switch
    {
        OverlayAction.Pet => "pet",
        OverlayAction.DrinkWater => "drink water",
        OverlayAction.StartFocus => "start focus",
        OverlayAction.Tasks => "tasks",
        OverlayAction.LoveNote => "love note",
        OverlayAction.ComfortMe => "comfort me",
        // No emoji here: the overlay paints labels with Segoe UI, which has
        // no emoji glyphs.
        OverlayAction.EatTogether => "eat together",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "oh no unknown overlay action"),
    };

    /// <summary>Stable UI Automation identifier shared by the painted action
    /// and its keyboard-accessible Settings equivalent.</summary>
    public static string AutomationId(OverlayAction action) => action switch
    {
        OverlayAction.Pet => "OverlayActionPet",
        OverlayAction.DrinkWater => "OverlayActionDrinkWater",
        OverlayAction.StartFocus => "OverlayActionStartFocus",
        OverlayAction.Tasks => "OverlayActionTasks",
        OverlayAction.LoveNote => "OverlayActionLoveNote",
        OverlayAction.ComfortMe => "OverlayActionComfortMe",
        OverlayAction.EatTogether => "OverlayActionEatTogether",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "alala unknown overlay action"),
    };

    public static string ComfortLabel(ComfortAction action) => action switch
    {
        ComfortAction.BreatheWithMe => "breathe with me",
        ComfortAction.TinyHug => "tiny hug",
        ComfortAction.ReadALoveNote => "read a love note",
        ComfortAction.TakeAFiveMinuteBreak => "take a five-minute break",
        ComfortAction.Close => "close",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "wait unknown comfort action"),
    };

    /// <summary>Stable UI Automation identifier for the normal-control route
    /// to a comfort action. The native overlay remains pointer-only.</summary>
    public static string ComfortAutomationId(ComfortAction action) => action switch
    {
        ComfortAction.BreatheWithMe => "OverlayComfortActionBreatheWithMe",
        ComfortAction.TinyHug => "OverlayComfortActionTinyHug",
        ComfortAction.ReadALoveNote => "OverlayComfortActionReadALoveNote",
        ComfortAction.TakeAFiveMinuteBreak => "OverlayComfortActionTakeAFiveMinuteBreak",
        ComfortAction.Close => "OverlayComfortActionClose",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "hmm unknown comfort action"),
    };

    private static (PixelRect Bounds, IReadOnlyList<PixelRect> Regions, PixelRect DetailRegion)? ArrangeRows(
        int requestedCount,
        PixelRect workArea,
        PixelPoint petAnchor,
        bool requireAllRows = false)
    {
        var minimumHeight = MinimumActionHeight + DetailGap + DetailHeight;
        if (workArea.Width < MinimumSurfaceWidth || workArea.Height < minimumHeight) return null;

        var preferredHeight = requestedCount * PreferredActionHeight
            + (requestedCount - 1) * PreferredGap
            + PreferredPadding * 2
            + DetailGap
            + DetailHeight;
        var usePreferredSpacing = workArea.Height >= preferredHeight;
        var padding = usePreferredSpacing ? PreferredPadding : 0;
        var gap = usePreferredSpacing ? PreferredGap : 0;
        var available = workArea.Height - padding * 2 - DetailGap - DetailHeight;
        var maximumRows = Math.Max(1, (available + gap) / (MinimumActionHeight + gap));
        if (requireAllRows && maximumRows < requestedCount) return null;
        var count = Math.Min(requestedCount, maximumRows);
        var rowHeight = Math.Min(PreferredActionHeight, (available - gap * (count - 1)) / count);
        if (rowHeight < MinimumActionHeight) return null;

        var actionHeight = rowHeight * count + gap * (count - 1);
        var height = padding * 2 + actionHeight + DetailGap + DetailHeight;
        var width = Math.Min(BubbleWidth, workArea.Width);
        var x = Math.Clamp(petAnchor.X - width / 2, workArea.X, workArea.Right - width);
        var y = Math.Clamp(petAnchor.Y - height - PreferredGap, workArea.Y, workArea.Bottom - height);
        var bounds = new PixelRect(x, y, width, height);
        var horizontalPadding = width > PreferredPadding * 2 ? PreferredPadding : 0;
        var regions = Enumerable.Range(0, count)
            .Select(index => new PixelRect(
                bounds.X + horizontalPadding,
                bounds.Y + padding + index * (rowHeight + gap),
                bounds.Width - horizontalPadding * 2,
                rowHeight))
            .ToArray();
        var detailRegion = new PixelRect(
            bounds.X + horizontalPadding,
            bounds.Y + padding + actionHeight + DetailGap,
            bounds.Width - horizontalPadding * 2,
            DetailHeight);
        return (bounds, regions, detailRegion);
    }

    private static void ValidateWorkArea(PixelRect workArea)
    {
        if (!workArea.IsValid) throw new ArgumentException("oh no work area must be valid", nameof(workArea));
    }
}
