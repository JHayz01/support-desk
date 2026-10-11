using System.Text.RegularExpressions;
namespace SupportDesk.Core;
public static class SemanticKeywords {
 static readonly HashSet<string> Stop=["после","сразу","нажатия","нажатие","пожалуйста","здравствуйте","привет","меня","мне","мой","моя","при","как","что","это","когда","почему","через","можно","есть","был","была","так","только"];
 static readonly string[] Endings="ается яется ивается ывается ать ять ить еть ает яет иет ется ого его ому ему ами ями ыми ими ой ий ый ая яя ое ее ые ие ов ев ам ям ом ем ах ях а я ы и у ю е о ь".Split(' ').OrderByDescending(s=>s.Length).ToArray();
 public static HashSet<string> Terms(string text)=>Regex.Matches(SearchIndex.Normalize(text),"[a-zа-я0-9]+").Select(m=>m.Value).Where(s=>s.Length>2&&!Stop.Contains(s)).Select(Stem).ToHashSet();
 static string Stem(string s){if(!s.Any(c=>c>='а'&&c<='я'))return s;foreach(var end in Endings)if(s.Length-end.Length>=3&&s.EndsWith(end,StringComparison.Ordinal))return s[..^end.Length];return s;}
 public static int TitleOverlap(string query,string title){var q=Terms(query);q.IntersectWith(Terms(title));return q.Count;}
}
