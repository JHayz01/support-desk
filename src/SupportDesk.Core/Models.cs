using System.Text.Json.Serialization;
namespace SupportDesk.Core;
public sealed record ReplyOption {
 public string Id {get;set;} = Guid.NewGuid().ToString("N");
 public string Title {get;set;} = "Вариант ответа";
 public string Body {get;set;} = "";
 public string When {get;set;} = "";
 public string Source {get;set;} = "";
 public int Step {get;set;}
 [JsonIgnore] public string DisplayTitle => (Step > 0 ? $"Шаг {Step} · " : "") + Title;
 public override string ToString() => DisplayTitle;
}
public sealed record Answer {
 public string Id {get;set;} = Guid.NewGuid().ToString("N");
 public string Title {get;set;} = "";
 public string Category {get;set;} = "";
 public string Platform {get;set;} = "Все";
 public string Body {get;set;} = "";
 public List<ReplyOption> Responses {get;set;} = [];
 [JsonIgnore] public IReadOnlyList<ReplyOption> Options => Responses.Count > 0 ? Responses : [new ReplyOption { Id="primary", Title="Основной ответ", Body=Body }];
 [JsonIgnore] public string SearchBody => Responses.Count > 0 ? string.Join("\n", Responses.Select(o => o.Title + "\n" + o.When + "\n" + o.Body)) : Body;
 public string Kind {get;set;} = "reply";
 public string Source {get;set;} = "";
 public string Notes {get;set;} = "";
 public bool NeedsReview {get;set;}
 public long Version {get;set;}
 [JsonIgnore] public string Preview => Body.Replace('\r',' ').Replace('\n',' ') is var s && s.Length>150 ? s[..150]+"…" : Body.Replace('\r',' ').Replace('\n',' ');
}
public sealed record Snapshot(long Revision,List<Answer> Answers);
public sealed record Changes(long Revision,bool Reset,List<Answer> Answers,List<string> Deleted);
public sealed record LoginRequest(string Password,string Device);
public sealed record SessionTokens(string AccessToken,string RefreshToken,DateTimeOffset ExpiresAt);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ChangeRequest(long BaseVersion,Answer? Answer);
public sealed record SearchRequest(string Query,int Limit=20);
public sealed record SearchHit(string Id,double Score);
public sealed record SemanticResponse(List<SearchHit> Hits,string Model,bool Indexing);
