using System.Text;
using System.Text.RegularExpressions;

namespace EmailAnalyser.Core;

public sealed record PreparedText(string Text, bool Truncated, bool HistoryShortened, int OriginalLength);

/// <summary>
/// Turns an Outlook plain-text body into model input: neutralises the prompt
/// delimiters, replaces links with their host, shortens quoted reply history to a
/// short excerpt and truncates at a safe boundary.
/// </summary>
public static partial class TextPreparation
{
    public const int DefaultMaxChars = 6000;
    public const int HistoryExcerptChars = 800;
    public const string TruncationMarker = "[… sisältö katkaistu]";
    public const string HistoryHeading = "[Aiempi viestiketju, lyhennetty]";

    // The top part must have this much real text before reply history is cut;
    // otherwise the message is a bare forward and the "history" is the content.
    private const int MinOwnTextChars = 40;

    public static PreparedText Prepare(string? body, int maxChars = DefaultMaxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 200);
        var original = body ?? "";
        var text = Normalise(original);
        text = text.Replace("<<<", "‹‹‹", StringComparison.Ordinal).Replace(">>>", "›››", StringComparison.Ordinal);
        text = UrlPattern().Replace(text, m => DescribeLink(m.Value));

        var historyShortened = false;
        var historyStart = FindReplyHistory(text);
        if (historyStart > 0)
        {
            var own = text[..historyStart];
            if (own.Count(c => !char.IsWhiteSpace(c)) >= MinOwnTextChars)
            {
                var history = RemoveQuotedLines(text[historyStart..]).Trim();
                var excerpt = history.Length > HistoryExcerptChars
                    ? CutAtBoundary(history, HistoryExcerptChars) + " …"
                    : history;
                text = own.TrimEnd() + "\n\n" + HistoryHeading + "\n" + excerpt;
                historyShortened = true;
            }
        }

        text = RemoveQuotedLines(text);
        text = CollapseWhitespace(text);

        var truncated = false;
        if (text.Length > maxChars)
        {
            text = CutAtBoundary(text, maxChars - TruncationMarker.Length - 2) + "\n" + TruncationMarker;
            truncated = true;
        }

        return new PreparedText(text, truncated, historyShortened, original.Length);
    }

    private static string Normalise(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            switch (c)
            {
                case ' ':
                    sb.Append(' ');
                    break;
                case '​' or '‌' or '‍' or '﻿' or '­':
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    private static string DescribeLink(string url) =>
        Uri.TryCreate(url.TrimEnd('.', ',', ';', ':'), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? $"[linkki: {uri.Host}]"
            : "[linkki]";

    private static int FindReplyHistory(string text)
    {
        var best = -1;
        foreach (var pattern in new[] { SeparatorLine(), WroteLine(), UnderscoreLine() })
        {
            var m = pattern.Match(text);
            if (m.Success && (best < 0 || m.Index < best))
            {
                best = m.Index;
            }
        }

        // "From: ... / Sent: ..." header block of an Outlook reply.
        foreach (Match m in HeaderFromLine().Matches(text))
        {
            var following = text.AsSpan(m.Index + m.Length, Math.Min(400, text.Length - m.Index - m.Length));
            if (HeaderSentLine().IsMatch(following) && (best < 0 || m.Index < best))
            {
                best = m.Index;
                break;
            }
        }

        return best;
    }

    private static string RemoveQuotedLines(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith('>')));

    private static string CollapseWhitespace(string text)
    {
        var lines = text.Split('\n').Select(line => InlineWhitespace().Replace(line, " ").TrimEnd());
        return BlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    /// <summary>Cuts at the last paragraph, sentence or word boundary before <paramref name="max"/>.</summary>
    internal static string CutAtBoundary(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var window = text[..max];
        var floor = Math.Max(0, max - 400);
        foreach (var boundary in new[] { "\n\n", ". ", "\n", " " })
        {
            var index = window.LastIndexOf(boundary, StringComparison.Ordinal);
            if (index >= floor)
            {
                return window[..(index + (boundary == ". " ? 1 : 0))].TrimEnd();
            }
        }

        return window.TrimEnd();
    }

    [GeneratedRegex(@"https?://[^\s<>""'\])]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"^-{2,}\s*(Original Message|Alkuperäinen viesti|Forwarded message|Välitetty viesti|Ursprungligt meddelande)\s*-{2,}", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SeparatorLine();

    [GeneratedRegex(@"^(On .{5,200} wrote:|.{5,200} kirjoitti:)\s*$", RegexOptions.Multiline)]
    private static partial Regex WroteLine();

    [GeneratedRegex(@"^_{20,}\s*$", RegexOptions.Multiline)]
    private static partial Regex UnderscoreLine();

    [GeneratedRegex(@"^\s*(From|Lähettäjä|Från):[^\n]*\n", RegexOptions.Multiline)]
    private static partial Regex HeaderFromLine();

    [GeneratedRegex(@"^\s*(Sent|Lähetetty|Skickat|Date|Päivämäärä):", RegexOptions.Multiline)]
    private static partial Regex HeaderSentLine();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex InlineWhitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
