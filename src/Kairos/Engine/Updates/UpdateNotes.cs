using System.Text.RegularExpressions;

namespace Kairos.Engine.Updates;

/// <summary>
/// What a release's notes (docs\release-notes\&lt;version&gt;.md, carried in the update feed) say to Kairos, in comments
/// GitHub's page does not show: <c>&lt;!-- important --&gt;</c> marks an important update, and
/// <c>&lt;!-- summary.ar: line | line --&gt;</c> gives the Status page card's lines in a language.
/// </summary>
public static partial class UpdateNotes
{
    [GeneratedRegex(@"<!--\s*important\s*-->", RegexOptions.IgnoreCase)]
    private static partial Regex Important();

    [GeneratedRegex(@"<!--\s*summary\.([A-Za-z-]+)\s*:(.*?)-->", RegexOptions.Singleline)]
    private static partial Regex SummaryLine();

    public static bool IsImportant(string? notes) => notes != null && Important().IsMatch(notes);

    /// <summary>
    /// The card's lines in <paramref name="language"/>: the release's own, else the notes' first points up to their colon
    /// ("- Black screen fix: some games…" gives "Black screen fix").
    /// </summary>
    public static IReadOnlyList<string> Summary(string? notes, string language, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return [];
        foreach (Match m in SummaryLine().Matches(notes))
            if (m.Groups[1].Value.Equals(language, StringComparison.OrdinalIgnoreCase))
                return [.. m.Groups[2].Value.Split('|').Select(l => l.Trim()).Where(l => l.Length > 0).Take(max)];
        return [.. notes.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l[2..].Split(": ")[0].TrimEnd('.').Trim())
            .Where(l => l.Length > 0)
            .Take(max)];
    }
}
