using System.Text.RegularExpressions;
namespace SupportDesk.Core;

public sealed class CommandMatcher
{
    string candidate = "";
    public static string Normalize(string value)
    {
        string command = value.Trim().Normalize().ToLowerInvariant();
        if (command.StartsWith("..")) command = "/" + command[2..];
        if (!command.StartsWith('/')) command = "/" + command;
        if (!Regex.IsMatch(command, @"^/[\p{L}\p{Nd}_-]{1,48}$")) throw new ArgumentException("Используйте .. или / и слово без пробелов, например ..интернет.");
        return command;
    }
    public void Reset() => candidate = "";
    public void Backspace() { if (candidate.Length > 0) candidate = candidate[..^1]; }
    public string? Feed(string text, Func<string, bool> exists)
    {
        if (text == " ")
        {
            var complete = candidate;
            Reset();
            if (!(complete.StartsWith('/') && complete.Length > 1 || complete.StartsWith("..") && complete.Length > 2)) return null;
            return exists(Normalize(complete)) ? complete : null;
        }
        foreach (char c in text)
        {
            if (c == '/' && candidate.Length == 0) candidate = "/";
            else if (c == '.' && candidate is "" or ".") candidate += c;
            else if ((candidate.StartsWith('/') || candidate.StartsWith("..")) && (char.IsLetterOrDigit(c) || c is '_' or '-') && candidate.Length < 49) candidate += c;
            else Reset();
        }
        return null;
    }
}
