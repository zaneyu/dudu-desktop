using Dudu.Core.Assets;

namespace Dudu.App.Overlay;

public enum OverlayAction { Pet, DrinkWater, StartFocus, Tasks, LoveNote, ComfortMe }

public enum ComfortAction { BreatheWithMe, TinyHug, ReadALoveNote, TakeAFiveMinuteBreak, Close }

public enum OverlayActionSurfaceKind { Closed, Primary, Comfort }

public sealed record OverlayActionPlacement(OverlayAction Action, PixelRect HitRegion);

public sealed record ComfortActionPlacement(ComfortAction Action, PixelRect HitRegion);

public sealed record ActionBubbleArrangement(
    PixelRect Bounds,
    IReadOnlyList<OverlayActionPlacement> PrimaryActions);

public sealed record ComfortBubbleArrangement(
    PixelRect Bounds,
    IReadOnlyList<ComfortActionPlacement> Actions);

public static class ActionBubbleLayout
{
    private const int BubbleWidth = 280;
    private const int PreferredActionHeight = 44;
    private const int MinimumActionHeight = 16;
    private const int PreferredPadding = 12;
    private const int PreferredGap = 8;
    private const int MinimumSurfaceWidth = 16;
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
        ?? throw new InvalidOperationException("The work area is too small for an action surface.");

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
            .Take(6)
            .ToArray();
        if (selected.Length == 0) selected = [OverlayAction.Pet];

        var rows = ArrangeRows(selected.Length, workArea, petAnchor);
        if (rows is null) return null;
        return new ActionBubbleArrangement(
            rows.Value.Bounds,
            selected.Take(rows.Value.Regions.Count)
                .Select((action, index) => new OverlayActionPlacement(action, rows.Value.Regions[index]))
                .ToArray());
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
                .ToArray());
    }

    public static string Label(OverlayAction action) => action switch
    {
        OverlayAction.Pet => "Pet",
        OverlayAction.DrinkWater => "Drink water",
        OverlayAction.StartFocus => "Start focus",
        OverlayAction.Tasks => "Tasks",
        OverlayAction.LoveNote => "Love note",
        OverlayAction.ComfortMe => "Comfort me",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown overlay action."),
    };

    public static string AutomationId(OverlayAction action) =>
        $"OverlayAction{Label(action).Replace(" ", string.Empty, StringComparison.Ordinal)}";

    public static string ComfortLabel(ComfortAction action) => action switch
    {
        ComfortAction.BreatheWithMe => "Breathe with me",
        ComfortAction.TinyHug => "Tiny hug",
        ComfortAction.ReadALoveNote => "Read a love note",
        ComfortAction.TakeAFiveMinuteBreak => "Take a five-minute break",
        ComfortAction.Close => "Close",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown comfort action."),
    };

    private static (PixelRect Bounds, IReadOnlyList<PixelRect> Regions)? ArrangeRows(
        int requestedCount,
        PixelRect workArea,
        PixelPoint petAnchor,
        bool requireAllRows = false)
    {
        if (workArea.Width < MinimumSurfaceWidth || workArea.Height < MinimumActionHeight) return null;

        var preferredHeight = requestedCount * PreferredActionHeight
            + (requestedCount - 1) * PreferredGap
            + PreferredPadding * 2;
        var usePreferredSpacing = workArea.Height >= preferredHeight;
        var padding = usePreferredSpacing ? PreferredPadding : 0;
        var gap = usePreferredSpacing ? PreferredGap : 0;
        var available = workArea.Height - padding * 2;
        var maximumRows = Math.Max(1, (available + gap) / (MinimumActionHeight + gap));
        if (requireAllRows && maximumRows < requestedCount) return null;
        var count = Math.Min(requestedCount, maximumRows);
        var rowHeight = Math.Min(PreferredActionHeight, (available - gap * (count - 1)) / count);
        if (rowHeight < MinimumActionHeight) return null;

        var height = padding * 2 + rowHeight * count + gap * (count - 1);
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
        return (bounds, regions);
    }

    private static void ValidateWorkArea(PixelRect workArea)
    {
        if (!workArea.IsValid) throw new ArgumentException("The work area must be valid.", nameof(workArea));
    }
}
