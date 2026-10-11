using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SupportDesk.Core;
namespace SupportDesk.Api;

public sealed record EmbeddingRequest(string[] Texts);
public sealed record EmbeddingResult(float[][] Vectors, string Model);
public sealed class Semantic(Store store, string url) : BackgroundService
{
    readonly HttpClient http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(45) };
    readonly SemaphoreSlim gate = new(1);
    readonly Dictionary<string, float[]> queries = new();
    public bool Indexing { get; private set; } = true;
    public string Model { get; private set; } = "";
    static string TitleText(Answer a) => a.Title + ". " + a.Category;
    static string IndexModel(string name) => name + "|title80-option20-v4";
    static float[] Blend(float[] title, float[] body)
    {
        var v = title.Zip(body, (x, y) => 0.8f * x + 0.2f * y).ToArray();
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        return v.Select(x => (float)(x / Math.Max(norm, 1e-12))).ToArray();
    }
    async Task<EmbeddingResult> Embed(string[] texts, CancellationToken ct)
    {
        for (int attempt=0;;attempt++)
        {
            try { return await EmbedOnce(texts,ct); }
            catch (HttpRequestException) when (attempt<3 && !ct.IsCancellationRequested) { await Task.Delay(300*(attempt+1),ct); }
        }
    }
    async Task<EmbeddingResult> EmbedOnce(string[] texts, CancellationToken ct)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new EmbeddingRequest(texts)));
        using var request = new HttpRequestMessage(HttpMethod.Post, url + "/embed") { Version=HttpVersion.Version10, VersionPolicy=HttpVersionPolicy.RequestVersionExact, Content=new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json");
        request.Content.Headers.ContentLength = bytes.Length;
        request.Headers.ConnectionClose = true;
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EmbeddingResult>(ct))!;
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snapshot = store.Snapshot();
                using var c = store.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT id,version,model FROM vectors";
                var ready = new Dictionary<string, (long Version, string Model)>();
                using (var r = cmd.ExecuteReader())
                while (r.Read())
                    ready[r.GetString(0)] = (r.GetInt64(1), r.GetString(2));
                var probe = await Embed(["Проверка"], ct);
                Model = IndexModel(probe.Model);
                var pending = snapshot.Answers.Where(a => !ready.TryGetValue(a.Id, out var v) || v.Version != a.Version || v.Model != Model).ToArray();
                Indexing = pending.Length > 0;
                // Index every alternative, so later answers are not lost to the model's token limit.
                var inputs = pending.SelectMany(a => new[] { (Id:a.Id, Text:TitleText(a)) }.Concat(a.Options.Select(o => (Id:a.Id, Text:(o.Title + ". " + o.When + "\n" + o.Body)[..Math.Min((o.Title + ". " + o.When + "\n" + o.Body).Length, 2000)])))).ToArray();
                var embedded = new Dictionary<string, List<float[]>>();
                var byId = pending.ToDictionary(a=>a.Id);
                foreach (var batch in inputs.Chunk(4))
                {
                    var output = await Embed(batch.Select(a => a.Text).ToArray(), ct);
                    for (int i = 0; i < batch.Length; i++)
                    {
                        if (!embedded.TryGetValue(batch[i].Id, out var vectors)) embedded[batch[i].Id] = vectors = [];
                        vectors.Add(output.Vectors[i]);
                        var answer = byId[batch[i].Id];
                        if(vectors.Count == answer.Options.Count+1)
                        {
                            Store.Run(c, "INSERT INTO vectors(id,version,model,vector) SELECT @id,@v,@m,@data WHERE EXISTS(SELECT 1 FROM answers WHERE id=@id AND version=@v) ON CONFLICT(id) DO UPDATE SET version=excluded.version,model=excluded.model,vector=excluded.vector", ("@id", answer.Id), ("@v", answer.Version), ("@m", IndexModel(output.Model)), ("@data", JsonSerializer.Serialize(vectors.Skip(1).Select(v => Blend(vectors[0], v)).ToArray())));
                            embedded.Remove(answer.Id);
                        }
                    }
                }
                Indexing = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine("Semantic index: " + ex.GetType().Name + ": " + ex.Message + " " + ex.InnerException?.Message); Indexing = true; }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }
    public async Task<SemanticResponse> Search(string query, int limit, CancellationToken ct)
    {
        if (query.Length > 2000 || string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Invalid query");
        query = System.Text.RegularExpressions.Regex.Replace(query.Normalize().Trim().ToLowerInvariant(), @"\s+", " ");
        await gate.WaitAsync(ct);
        try
        {
            if (!queries.TryGetValue(Model + "\n" + query, out var vector))
            {
                var result = await Embed([query], ct);
                vector = result.Vectors[0];
                Model = IndexModel(result.Model);
                if (queries.Count >= 100)
                    queries.Clear();
                queries[Model + "\n" + query] = vector;
            }
            var snapshot = store.Snapshot();
            var byId = snapshot.Answers.ToDictionary(a => a.Id);
            var keywords = new HybridKeywords(snapshot.Answers).Score(query);
            var lexical = new SearchIndex(snapshot.Answers).Find(query).Select((a, i) => (a.Id, Rank: i)).ToDictionary(x => x.Id, x => x.Rank);
            using var c = store.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT v.id,v.vector FROM vectors v JOIN answers a ON a.id=v.id AND a.version=v.version WHERE v.model=@m";
            cmd.Parameters.AddWithValue("@m", Model);
            using var r = cmd.ExecuteReader();
            var hits = new List<SearchHit>();
            int ready = 0;
            while (r.Read())
            {
                ready++;
                var alternatives = JsonSerializer.Deserialize<float[][]>(r.GetString(1))!;
                if (alternatives.Length == 0 || alternatives.Any(v => v.Length != vector.Length))
                    continue;
                double cosine = alternatives.Max(other => vector.Zip(other, (x, y) => (double)x * y).Sum());
                if (cosine < 0.35 && !lexical.ContainsKey(r.GetString(0)) && keywords.GetValueOrDefault(r.GetString(0)) < 0.5)
                    continue;
                double score = cosine + 0.18 * keywords.GetValueOrDefault(r.GetString(0)) + (lexical.TryGetValue(r.GetString(0), out var rank) ? 0.2 / (1 + rank) : 0) + Math.Min(0.3, 0.1 * SemanticKeywords.TitleOverlap(query, byId.GetValueOrDefault(r.GetString(0))?.Title ?? ""));
                hits.Add(new(r.GetString(0), score));
            }
            return new(hits.OrderByDescending(h => h.Score).Take(Math.Clamp(limit, 1, 50)).ToList(), Model, Indexing || ready < snapshot.Answers.Count);
        }
        finally { gate.Release(); }
    }
    public override void Dispose()
    {
        http.Dispose();
        gate.Dispose();
        base.Dispose();
    }
}

