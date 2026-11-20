using System.Windows.Media;

namespace UnzipUnlinkGUI.ViewModels;

/// <summary>
/// The small "link group" marker shown per series: series that share a Frame of Reference UID get the same
/// letter and colour; a series that is alone in its frame gets a muted dash.
/// </summary>
public sealed record LinkBadge(string Text, Brush Background, bool IsMuted)
{
    public static readonly LinkBadge None = new("–", Brushes.Transparent, true);

    public string Description => IsMuted ? "Not linked to any other series here" : $"Link group {Text}";
}

public static class LinkGroups
{
    // Deliberately different from the modality chip colours (blue / violet / amber / slate).
    private static readonly Brush[] Palette =
    [
        Frozen("#0F766E"), Frozen("#BE123C"), Frozen("#4D7C0F"), Frozen("#0E7490"),
        Frozen("#A21CAF"), Frozen("#C2410C"), Frozen("#4338CA"), Frozen("#57534E"),
    ];

    /// <summary>
    /// Works out both badges for every series. <paramref name="frames"/> holds, per series and in display
    /// order, the frame of reference it has first ("before") and second ("after"). A group that survives
    /// keeps its letter, so the eye can follow it from one column to the next.
    /// </summary>
    public static (LinkBadge[] Before, LinkBadge[] After) Assign(IReadOnlyList<(string Before, string After)> frames)
    {
        HashSet<string> sharedBefore = Shared(frames.Select(f => f.Before));
        HashSet<string> sharedAfter = Shared(frames.Select(f => f.After));

        var beforeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string before, _) in frames)
        {
            if (sharedBefore.Contains(before) && !beforeIndex.ContainsKey(before))
            {
                beforeIndex.Add(before, beforeIndex.Count);
            }
        }

        var afterIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var used = new HashSet<int>();
        // A frame that is literally unchanged has first claim on its own letter...
        foreach ((_, string after) in frames)
        {
            if (sharedAfter.Contains(after) && !afterIndex.ContainsKey(after)
                && beforeIndex.TryGetValue(after, out int same) && used.Add(same))
            {
                afterIndex.Add(after, same);
            }
        }
        // ...then a re-framed group inherits the letter its members had, when that is still free.
        int next = beforeIndex.Count;
        foreach ((string before, string after) in frames)
        {
            if (!sharedAfter.Contains(after) || afterIndex.ContainsKey(after))
            {
                continue;
            }
            if (beforeIndex.TryGetValue(before, out int inherited) && used.Add(inherited))
            {
                afterIndex.Add(after, inherited);
            }
            else
            {
                used.Add(next);
                afterIndex.Add(after, next++);
            }
        }

        var beforeBadges = new LinkBadge[frames.Count];
        var afterBadges = new LinkBadge[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            beforeBadges[i] = beforeIndex.TryGetValue(frames[i].Before, out int b) ? Badge(b) : LinkBadge.None;
            afterBadges[i] = afterIndex.TryGetValue(frames[i].After, out int a) ? Badge(a) : LinkBadge.None;
        }
        return (beforeBadges, afterBadges);
    }

    /// <summary>Frames used by two or more series (an empty frame never links anything).</summary>
    private static HashSet<string> Shared(IEnumerable<string> frames) =>
        frames.Where(f => f.Length > 0)
              .GroupBy(f => f, StringComparer.Ordinal)
              .Where(g => g.Count() > 1)
              .Select(g => g.Key)
              .ToHashSet(StringComparer.Ordinal);

    private static LinkBadge Badge(int index) => new(Letters(index), Palette[index % Palette.Length], false);

    /// <summary>0 → A, 25 → Z, 26 → AA ...</summary>
    internal static string Letters(int index)
    {
        string text = "";
        for (int n = index; n >= 0; n = n / 26 - 1)
        {
            text = (char)('A' + n % 26) + text;
        }
        return text;
    }

    internal static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
