namespace SupportDesk.Core;
// IDF weights distinguish rare errors and problem terms from repeated boilerplate.
public sealed class HybridKeywords
{
    readonly (string Id, Dictionary<string,double> Weights)[] documents;
    readonly Dictionary<string,int> frequency = [];
    public HybridKeywords(IEnumerable<Answer> answers)
    {
        documents = answers.Select(a => {
            var weights = new Dictionary<string,double>();
            foreach (var text in new[] { (a.SearchBody, 1d), (a.Category, 2d), (a.Title, 4d) })
                foreach (var term in SemanticKeywords.Terms(text.Item1)) weights[term] = Math.Max(weights.GetValueOrDefault(term), text.Item2);
            foreach (var term in weights.Keys) frequency[term] = frequency.GetValueOrDefault(term) + 1;
            return (a.Id, weights);
        }).ToArray();
    }
    public Dictionary<string,double> Score(string query)
    {
        var terms = SemanticKeywords.Terms(query);
        var scores = new Dictionary<string,double>();
        foreach (var d in documents)
        {
            double score = 0;
            foreach (var term in terms)
                if (d.Weights.TryGetValue(term, out var weight))
                    score += Math.Log(1 + (documents.Length + .5) / (frequency[term] + .5)) * weight;
            if (score > 0) scores[d.Id] = score;
        }
        double max = scores.Values.DefaultIfEmpty(1).Max();
        return scores.ToDictionary(p => p.Key, p => p.Value / max);
    }
}
