using System;
using System.Text.RegularExpressions;

namespace Reepax.Services.Storage;

internal static class DiagnosticPrivacy
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex QuotedLocation = new(
        "(?<quote>[\"'])(?<location>(?:(?:https?|ftps?|wss?|file|blob|data):|[a-z]:[\\\\/]+|[\\\\/]{2}|/[^/\\s\"'<>]+/)[^\\r\\n]*)\\k<quote>",
        Options, MatchTimeout);
    private static readonly Regex Url = new(
        "\\b(?:https?|ftps?|wss?|file|blob|data):[^\\s<>]+", Options, MatchTimeout);
    private static readonly Regex WindowsPath = new(
        @"(?<![\p{L}\p{N}_])(?:[a-z]:[\\/]+|[\\/]{2}|\\\?\?\\|\\Device\\)[^\r\n]*", Options, MatchTimeout);
    private static readonly Regex UnixPath = new(
        @"(?<![\p{L}\p{N}_:/\\])/(?:[^/\s]+/)[^\r\n]*", Options, MatchTimeout);
    private static readonly Regex StackLine = new(@":(?:line|Zeile)\s+\d+\s*$", Options, MatchTimeout);

    internal static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        try
        {
            text = QuotedLocation.Replace(text, match =>
            {
                string location = match.Groups["location"].Value;
                var url = Url.Match(location);
                string replacement = url.Success && url.Index == 0
                    ? RedactUrl(location) : "[path]";
                return match.Groups["quote"].Value + replacement + match.Groups["quote"].Value;
            });
            text = Url.Replace(text, match => RedactUrl(match.Value));
            text = WindowsPath.Replace(text, RedactPath);
            return UnixPath.Replace(text, RedactPath);
        }
        catch (RegexMatchTimeoutException)
        {
            // Never return unfiltered content when inspection exceeds its limit.
            return "[diagnostic redacted]";
        }
    }

    private static string RedactUrl(string value)
    {
        value = value.Replace("\\/", "/", StringComparison.Ordinal);
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" or "ftp" or "ftps" or "ws" or "wss" && !string.IsNullOrEmpty(uri.Host))
        {
            // Retain the server for diagnosis, excluding credentials and the entire resource location.
            return $"[url: {uri.Scheme} {uri.Authority}]";
        }
        return "[url]";
    }

    private static string RedactPath(Match match)
    {
        // Unquoted paths may contain spaces; retain only a trailing source line number.
        var line = StackLine.Match(match.Value);
        return "[path]" + (line.Success ? line.Value : string.Empty);
    }
}
