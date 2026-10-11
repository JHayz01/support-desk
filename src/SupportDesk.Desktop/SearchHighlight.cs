using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;
namespace SupportDesk.Desktop;

public static class SearchHighlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(SearchHighlight), new PropertyMetadata("", Update));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached("Query", typeof(string), typeof(SearchHighlight), new PropertyMetadata("", Update));
    public static void SetText(DependencyObject d, string v) => d.SetValue(TextProperty, v);
    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetQuery(DependencyObject d, string v) => d.SetValue(QueryProperty, v);
    public static string GetQuery(DependencyObject d) => (string)d.GetValue(QueryProperty);
    static string Normalize(string s) => s.ToLowerInvariant().Replace('ё', 'е').Replace('ъ', 'ь');
    static void Update(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
            return;
        string text = GetText(d) ?? "", normalized = Normalize(text);
        var ranges = new List<(int Start, int End)>();
        foreach (Match m in Regex.Matches(GetQuery(d) ?? "", "\"([^\"]+)\"|(\\S+)"))
        {
            var term = Normalize(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            if (term.Length == 0)
                continue;
            for (int at = normalized.IndexOf(term, StringComparison.Ordinal); at >= 0; at = normalized.IndexOf(term, at + term.Length, StringComparison.Ordinal))
                ranges.Add((at, at + term.Length));
        }
        var marked = new bool[text.Length];
        foreach (var (start, end) in ranges)
            for (int i = start; i < end; i++)
                marked[i] = true;
        block.Inlines.Clear();
        for (int i = 0; i < text.Length;)
        {
            int next = i + 1;
            while (next < text.Length && marked[next] == marked[i])
                next++;
            var run = new Run(text[i..next]);
            if (marked[i])
            {
                run.Background = new SolidColorBrush(Color.FromRgb(80, 69, 108));
                run.Foreground = new SolidColorBrush(Color.FromRgb(255, 234, 178));
            }
            block.Inlines.Add(run);
            i = next;
        }
    }
}
