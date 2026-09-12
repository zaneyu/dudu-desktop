using Dudu.Core.Assets;

namespace Dudu.App.Overlay;

public enum OverlayAction
{
    Pet,
    DrinkWater,
    StartFocus,
    Tasks,
    LoveNote,
    ComfortMe,
}

public enum ComfortAction
{
    BreatheWithMe,
    TinyHug,
    ReadALoveNote,
    TakeAFiveMinuteBreak,
    Close,
}

public sealed record OverlayActionPlacement(OverlayAction Action, PixelRect HitRegion);

public sealed record ActionBubbleArrangement(
    PixelRect Bounds,
    IReadOnlyList<OverlayActionPlacement> PrimaryActions)
{
    public static IReadOnlyList<ComfortAction> ComfortActions { get; } =
    [
        ComfortAction.BreatheWithMe,
        ComfortAction.TinyHug,
        ComfortAction.ReadALoveNote,
        ComfortAction.TakeAFiveMinuteBreak,
        ComfortAction.Close,
    ];
}

public static class ActionBubbleLayout
{
    private const int BubbleWidth = 280;
    private const int ActionHeight = 44;
    private const int BubblePadding = 12;
    private const int BubbleGap = 8;
    private static readonly OverlayAction[] AllowedActions = Enum.GetValues<OverlayAction>();

    public static ActionBubbleArrangement Arrange(
        IEnumerable<OverlayAction> actions,
        PixelRect workArea,
        PixelPoint petAnchor)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (!workArea.IsValid) throw new ArgumentException("The work area must be valid.", nameof(workArea));

        var selected = actions
            .Where(action => AllowedActions.Contains(action))
            .Distinct()
            .Take(6)
            .ToArray();
        if (selected.Length == 0) selected = [OverlayAction.Pet];

        var height = BubblePadding * 2 + selected.Length * ActionHeight + (selected.Length - 1) * BubbleGap;
        var x = petAnchor.X - BubbleWidth / 2;
        var y = petAnchor.Y - height - BubbleGap;
        var width = Math.Min(BubbleWidth, workArea.Width);
        var visibleHeight = Math.Min(height, workArea.Height);
        x = Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.Right - width));
        y = Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - visibleHeight));

        var bounds = new PixelRect(x, y, width, visibleHeight);
        var placements = new List<OverlayActionPlacement>(selected.Length);
        var horizontalPadding = Math.Min(BubblePadding, Math.Max(0, bounds.Width / 2));
        var verticalPadding = Math.Min(BubblePadding, Math.Max(0, bounds.Height / 2));
        var availableHeight = Math.Max(1, bounds.Height - verticalPadding * 2 - BubbleGap * (selected.Length - 1));
        var rowHeight = Math.Max(1, availableHeight / selected.Length);
        var currentY = bounds.Y + verticalPadding;
        foreach (var action in selected)
        {
            var actionHeight = Math.Min(rowHeight, Math.Max(1, bounds.Bottom - currentY - verticalPadding));
            var actionWidth = Math.Max(1, bounds.Width - horizontalPadding * 2);
            placements.Add(new OverlayActionPlacement(action, new PixelRect(bounds.X + horizontalPadding, currentY, actionWidth, actionHeight)));
            currentY += rowHeight + BubbleGap;
        }

        return new ActionBubbleArrangement(bounds, placements);
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

    public static string AutomationId(OverlayAction action) => $"OverlayAction{Label(action).Replace(" ", string.Empty, StringComparison.Ordinal)}";

    public static string ComfortLabel(ComfortAction action) => action switch
    {
        ComfortAction.BreatheWithMe => "Breathe with me",
        ComfortAction.TinyHug => "Tiny hug",
        ComfortAction.ReadALoveNote => "Read a love note",
        ComfortAction.TakeAFiveMinuteBreak => "Take a five-minute break",
        ComfortAction.Close => "Close",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown comfort action."),
    };
}
