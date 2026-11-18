using System.Text;
using System.Text.RegularExpressions;

namespace EmailAnalyser.Core;

/// <summary>
/// Cleans untrusted text (message fields and model output) before it is shown or
/// sent to the model: removes control, zero-width and bidirectional-override
/// characters, which can make a subject or sender look like something else.
/// </summary>
public static partial class SafeText
{
    public static string Clean(string? value, bool keepNewlines = true)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '\n' && keepNewlines)
            {
                sb.Append(c);
            }
            else if (c is '\t' or '\n' or '\r' or ' ')
            {
                sb.Append(c == '\r' && keepNewlines ? '\n' : ' ');
            }
            else if (!IsInvisibleOrControl(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Model output shown to the user: cleaned, links reduced to their host.</summary>
    public static string ModelText(string value) =>
        LinkPattern().Replace(Clean(value), m =>
            Uri.TryCreate(m.Value.TrimEnd('.', ',', ';', ':', ')'), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? $"[linkki: {uri.Host}]"
                : "[linkki]");

    public static bool IsInvisibleOrControl(char c) =>
        char.IsControl(c) ||
        c is '\u034F' or '\u180E' or '\u2061' or '\u2062' or '\u2063' or '\u2064' or '\u3164' or '\uFFA0' ||
        c is '​' or '‌' or '‍' or '‎' or '‏' or '﻿' or '­' or '⁠' ||
        c is >= '‪' and <= '‮' ||
        c is >= '⁦' and <= '⁩';

    [GeneratedRegex(@"(https?|ftp|file)://[^\s<>""'\])]+|www\.[^\s<>""'\])]+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LinkPattern();
}

/// <summary>
/// Flags text that addresses an AI or tries to steer classification. It does not
/// stop the attack (the model may still be influenced); it makes the message
/// visible for review. Program-level, independent of the model.
/// </summary>
public static partial class InjectionHeuristics
{
    public const string ReviewReason = "ohjailuyritys";

    /// <summary>Finnish descriptions of the rule groups, shown to the user instead of the pattern.</summary>
    public static readonly IReadOnlyDictionary<string, string> RuleNames = new Dictionary<string, string>
    {
        ["ignore"] = "kehotus ohittaa aiemmat ohjeet",
        ["addressed"] = "tekoälylle osoitettu teksti",
        ["vocabulary"] = "tekoälyn ohjailun sanasto (esim. system prompt, jailbreak)",
        ["structure"] = "väärennetty kehotteen rakenne tai erotin",        ["system"] = "järjestelmärivi, joka antaa ohjeita",
        ["fields"] = "tämän sovelluksen vastauskentät tai arvot",
        ["json"] = "pyyntö palauttaa JSONia",
        ["timeout"] = "tavattoman raskas teksti (tarkistus aikakatkaistiin)",
    };

    public static bool Detect(string? text) => Find(text) is not null;

    /// <summary>
    /// The first match: rule group, position and length in <paramref name="text"/>.
    /// The caller decides where the matched text may be shown; it is message content.
    /// </summary>
    public static InjectionMatch? Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var sample = text.Length > 200_000 ? text[..200_000] : text;
        try
        {
            var match = Pattern().Match(sample);
            if (!match.Success)
            {
                return null;
            }

            var rule = RuleNames.Keys.FirstOrDefault(name => match.Groups[name].Success) ?? "addressed";
            return new InjectionMatch(rule, match.Index, match.Length);
        }
        catch (RegexMatchTimeoutException)
        {
            return new InjectionMatch("timeout", 0, 0); // pathological input is itself suspicious
        }
    }

    [GeneratedRegex(
        @"(?<ignore>ignore\s+(all\s+|any\s+)?(the\s+)?(previous|prior|above|earlier)\s+(instructions|prompts?|rules)" +
        @"|disregard\s+(all\s+|the\s+)?(previous|prior|above|system)" +
        @"|ohita\s+(kaikki\s+)?(aiemmat|edelliset|yllä\s+olevat)\s+ohjeet)" +
        @"|(?<addressed>(note|message|instructions?|notice)\s+(to|for)\s+(any\s+|all\s+|the\s+)?(ai|a\.i\.|assistants?|llms?|language\s+models?|classifiers?|chatbots?|copilot)\b" +
        @"|\b(ai|llm|language\s+model|assistant)s?\s+(processing|reading|summari[sz]ing|classifying)\s+this\s+(e-?mail|message)" +
        @"|classify\s+(this|the)\s+(e-?mail|message)\s+as" +
        @"|(tekoälylle|tekoälyavustajalle|kielimallille|avustajalle)\s*[:,]" +
        @"|luokittele\s+(tämä|viesti))" +
        @"|(?<vocabulary>system\s+prompt|jailbreak|\b(system|developer|admin)\s+(override|mode)\b|järjestelmäkehote)" +
        // Fake prompt structure: this application's delimiter or chat-template markers.
        @"|(?<structure><<<\s*viesti|viesti\s+(alkaa|päättyy)\s*>>>" +
        // A bare ">>>" at a line start is a nested e-mail quote, so a marker needs a delimiter word or a closing pair.
        @"|^\s*(</?(email|message|system|user|assistant|instructions?)>|\[/?(system|inst)\]|###\s*(end|system|instruction))" +
        @"|^\s*(<<<|>>>)\s*(end|system|user|assistant|instructions?|järjestelmä|ohjeet?)\b|<<<[^<>\n]{1,40}>>>)" +
        // A "system" line that gives instructions.
        @"|(?<system>^\s*(järjestelmä|system)\s*:.{0,200}?\b(ohjeet?|instructions?|palauta|output)\b)" +
        // Asking for this application's output fields or JSON.
        @"|(?<fields>""(toimenpide|kiireellisyys|tarkistettava|tarkistuksen_syyt|yhteenveto|maaraaika|aihe)""\s*:" +
        @"|\b(ei_maaraaikaa|talla_viikolla|tarkistuksen_syyt|epaselva_pyynto|katkaistu_sisalto)\b)" +
        @"|(?<json>\b(palauta|output|return|respond\s+with)\b.{0,60}\bjson\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();
}

/// <summary>Where the injection heuristic matched. Rule is a key of <see cref="InjectionHeuristics.RuleNames"/>.</summary>
public sealed record InjectionMatch(string Rule, int Index, int Length);

/// <summary>
/// The matched passage with a little context, for the user to judge in the view. Message
/// content: shown on screen only, never logged or stored. Cleaned, links reduced to the host.
/// </summary>
public sealed record InjectionExcerpt(string RuleName, string Place, string Before, string Hit, string After)
{
    public const int Context = 100;
    public const int MaxHit = 300;

    /// <summary>The excerpt of the first match in subject and body; null when the heuristic no longer matches.</summary>
    public static InjectionExcerpt? Build(string? subject, string? body)
    {
        subject ??= "";
        var text = subject + "\n" + body;
        if (InjectionHeuristics.Find(text) is not { } match)
        {
            return null;
        }

        var rule = InjectionHeuristics.RuleNames[match.Rule];
        if (match.Length == 0)
        {
            return new InjectionExcerpt(rule, "", "", "", "");
        }

        var place = match.Index < subject.Length
            ? "otsikossa"
            : $"rungossa, noin {(match.Index - subject.Length) * 100L / Math.Max(1, text.Length - subject.Length)} % rungon alusta";
        var start = Math.Max(0, match.Index - Context);
        var hitEnd = match.Index + Math.Min(match.Length, MaxHit);
        var end = Math.Min(text.Length, match.Index + match.Length + Context);
        return new InjectionExcerpt(
            rule,
            place,
            (start > 0 ? "…" : "") + Plain(text[start..match.Index]),
            Plain(text[match.Index..hitEnd]) + (hitEnd < match.Index + match.Length ? "…" : ""),
            Plain(text[(match.Index + match.Length)..end]) + (end < text.Length ? "…" : ""));
    }

    /// <summary>One line of plain text: line breaks become " ⏎ " so that a line-start marker stays visible.</summary>
    private static string Plain(string value) =>
        SafeText.ModelText(SafeText.Clean(value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " ⏎ "), keepNewlines: false));
}
