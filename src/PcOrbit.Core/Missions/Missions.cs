using System.Globalization;
using System.Text;
using PcOrbit.Core.Navigation;

namespace PcOrbit.Core.Missions;

/// <summary>Whether finishing this mission restarts the PC.</summary>
public enum MissionRestart
{
    None = 0,

    /// <summary>Depends on what the machine turns out to need.</summary>
    Possible,

    Required,
}

/// <summary>
/// A thing a person wants done, in their words, and where in the app that starts.
/// </summary>
/// <remarks>
/// <para>
/// The rail is organised by subsystem and problems are not. Someone whose PC got slow after
/// yesterday's update does not know that the answer is Changes → Timeline → the 48 hours before
/// the symptom; they know "máy chậm sau update". A mission is that sentence, in both languages,
/// with the route already chosen and the cost already stated — how long, whether it restarts, and
/// what the way back is.
/// </para>
/// <para>
/// Shipped as data, like outcomes and guides, so the list can grow and be reviewed without a build.
/// A mission carries no script and no action: it points at a page or an outcome, and the outcome
/// is where a change is planned, compiled per machine and verified like any other.
/// </para>
/// </remarks>
/// <param name="Aliases">
/// What people type, in Vietnamese and English, with and without accents. Matching folds accents,
/// so "may cham" finds "máy chậm" — but listing the plain form too costs nothing and documents it.
/// </param>
/// <param name="Inspects">Capability ids the mission will look at, shown so the card says what it reads.</param>
/// <param name="RecoveryKey">One sentence on how this is undone if it disappoints.</param>
public sealed record Mission(
    string Id,
    string TitleKey,
    string DescriptionKey,
    IReadOnlyList<string> Aliases,
    Route Route,
    IReadOnlyList<string> Inspects,
    int EstimatedMinutes,
    MissionRestart Restart,
    string RecoveryKey,
    string Glyph);

/// <param name="Score">Higher is a better match. Deterministic for a given query and catalogue.</param>
public sealed record MissionMatch(Mission Mission, int Score);

/// <summary>
/// Folds text to a form where "Máy Chậm", "may cham" and "MÁY CHẬM" are the same string.
/// </summary>
/// <remarks>
/// Lower-case, decomposed, combining marks dropped, and the one Vietnamese letter that is not a
/// base letter plus a mark — đ — mapped by hand. Pure string work, so it lives in Core and is
/// what the app uses for every search box, not only missions.
/// </remarks>
public static class TextFold
{
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sb = new StringBuilder(text.Length);

        foreach (char c in text.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);

            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(c switch
            {
                'đ' => 'd',
                _ => c,
            });
        }

        return sb.ToString();
    }

    /// <summary>The query as folded words, empties dropped.</summary>
    public static IReadOnlyList<string> Words(string query) =>
        [.. Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

/// <summary>
/// The missions this build ships, and how a typed sentence finds one.
/// </summary>
public sealed class MissionCatalog(IEnumerable<Mission> missions)
{
    private readonly IReadOnlyList<Mission> _missions = [.. missions];

    public static MissionCatalog Empty { get; } = new([]);

    public IReadOnlyList<Mission> All => _missions;

    public Mission? Find(string id) =>
        _missions.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Missions that match what was typed, best first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every word of the query has to land somewhere in a mission for it to count — an alias, or
    /// the translated title — so "máy chậm" does not return every mission with "máy" in it. Within
    /// that, a word that is a whole alias outscores one that begins an alias, which outscores one
    /// merely inside it. Ties break on catalogue order, so the same query gives the same list
    /// every time; a search that reorders itself between keystrokes is one nobody trusts.
    /// </para>
    /// <para>
    /// No fuzzy distance, deliberately. A typo tolerance that turns "docker" into "locker" is the
    /// kind of helpfulness that sends someone to the wrong page with confidence.
    /// </para>
    /// </remarks>
    /// <param name="title">Resolves a mission's title key in the current language, so titles match too.</param>
    public IReadOnlyList<MissionMatch> Match(string query, Func<string, string> title)
    {
        ArgumentNullException.ThrowIfNull(title);

        IReadOnlyList<string> words = TextFold.Words(query ?? string.Empty);

        if (words.Count == 0)
        {
            return [];
        }

        List<MissionMatch> matches = [];

        foreach (Mission mission in _missions)
        {
            string[] aliases = [.. mission.Aliases.Select(TextFold.Fold)];
            string foldedTitle = TextFold.Fold(title(mission.TitleKey));
            int total = 0;

            foreach (string word in words)
            {
                int best = 0;

                foreach (string alias in aliases)
                {
                    int score =
                        alias == word ? 4
                        : alias.StartsWith(word, StringComparison.Ordinal) ? 3
                        : alias.Contains(word, StringComparison.Ordinal) ? 2
                        : 0;

                    best = Math.Max(best, score);
                }

                if (best == 0 && foldedTitle.Contains(word, StringComparison.Ordinal))
                {
                    best = 1;
                }

                if (best == 0)
                {
                    total = 0;
                    break;
                }

                total += best;
            }

            if (total > 0)
            {
                matches.Add(new MissionMatch(mission, total));
            }
        }

        return [.. matches.OrderByDescending(m => m.Score)];
    }
}
