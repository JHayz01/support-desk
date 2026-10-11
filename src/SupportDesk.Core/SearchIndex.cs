using System.Text;
using System.Text.RegularExpressions;
namespace SupportDesk.Core;

public enum SearchMatch { AllWords, AnyWord, Phrase, WholeWords }
public enum SearchScope { Everywhere, Title, Body, Commands }
public sealed class SearchIndex
{
    sealed record Entry(Answer Answer, string Title, string Body, string Commands, string All);
    readonly Entry[] entries;
    public SearchIndex(IEnumerable<Answer> answers, IReadOnlyDictionary<string, string>? commands = null) => entries = answers.Select(a =>
    {
        var command = commands?.GetValueOrDefault(a.Id) ?? "";
        var raw = string.Join('\u0001', a.Title, a.SearchBody, a.Category, a.Notes, command);
        return new Entry(a, Normalize(a.Title), Normalize(a.SearchBody), Normalize(command), Normalize(raw + "\u0001" + Regex.Replace(raw, @"(?<=[a-z])(?=[A-Z])", " ")));
    }).ToArray();
    public static string Normalize(string s) => Regex.Replace(s.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('ё', 'е').Replace('ъ', 'ь'), @"\s+", " ");
    public List<Answer> Find(string query, string category = "", string platform = "Все", ISet<string>? favorites = null, CancellationToken token = default, SearchMatch match = SearchMatch.AllWords, SearchScope scope = SearchScope.Everywhere)
    {
        var terms = Regex.Matches(query, "\"([^\"]+)\"|(\\S+)").Select(m => (Text: Normalize(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value), Exact: m.Groups[1].Success)).ToArray();
        var phrase = Normalize(query.Trim().Trim('"'));
        var hits = new List<(Answer A, int Score)>();
        foreach (var e in entries)
        {
            token.ThrowIfCancellationRequested();
            if (category.Length > 0 && e.Answer.Category != category || platform != "Все" && e.Answer.Platform != "Все" && e.Answer.Platform != platform || favorites is not null && !favorites.Contains(e.Answer.Id)) continue;
            string text = scope switch { SearchScope.Title => e.Title, SearchScope.Body => e.Body, SearchScope.Commands => e.Commands, _ => e.All };
            bool Matches((string Text, bool Exact) t) => Contains(text, t.Text, t.Exact, match == SearchMatch.WholeWords);
            bool found = terms.Length == 0 || (match switch { SearchMatch.Phrase => Contains(text, phrase, true, true), SearchMatch.AnyWord => terms.Any(Matches), _ => terms.All(Matches) });
            if (!found) continue;
            int score = terms.Sum(t => Contains(e.Title, t.Text, t.Exact, false) ? 60 : Contains(e.Body, t.Text, t.Exact, false) ? 15 : 2);
            if (phrase.Length > 0) score += e.Title == phrase ? 600 : e.Title.StartsWith(phrase, StringComparison.Ordinal) ? 350 : e.Title.Contains(phrase, StringComparison.Ordinal) ? 200 : 0;
            hits.Add((e.Answer, score));
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.A.Title, StringComparer.OrdinalIgnoreCase).Select(h => h.A).ToList();
    }
    static bool Contains(string text, string term, bool exact, bool whole)
    {
        if (!whole && (exact || term.Any(c => !char.IsLetterOrDigit(c)))) return text.Contains(term, StringComparison.Ordinal);
        for (int at = text.IndexOf(term, StringComparison.Ordinal); at >= 0; at = text.IndexOf(term, at + 1, StringComparison.Ordinal))
            if ((at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (!whole || at + term.Length == text.Length || !char.IsLetterOrDigit(text[at + term.Length]))) return true;
        return false;
    }
}
