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

    public static bool Detect(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var sample = text.Length > 200_000 ? text[..200_000] : text;
        try
        {
            return Pattern().IsMatch(sample);
        }
        catch (RegexMatchTimeoutException)
        {
            return true; // pathological input is itself suspicious
        }
    }

    [GeneratedRegex(
        @"ignore\s+(all\s+|any\s+)?(the\s+)?(previous|prior|above|earlier)\s+(instructions|prompts?|rules)" +
        @"|disregard\s+(all\s+|the\s+)?(previous|prior|above|system)" +
        @"|(note|message|instructions?|notice)\s+(to|for)\s+(any\s+|all\s+|the\s+)?(ai|a\.i\.|assistants?|llms?|language\s+models?|classifiers?|chatbots?|copilot)\b" +
        @"|\b(ai|llm|language\s+model|assistant)s?\s+(processing|reading|summari[sz]ing|classifying)\s+this\s+(e-?mail|message)" +
        @"|classify\s+(this|the)\s+(e-?mail|message)\s+as" +
        @"|system\s+prompt|jailbreak|\b(system|developer|admin)\s+(override|mode)\b" +
        @"|ohita\s+(kaikki\s+)?(aiemmat|edelliset|yllä\s+olevat)\s+ohjeet" +
        @"|(tekoälylle|tekoälyavustajalle|kielimallille|avustajalle)\s*[:,]" +
        @"|luokittele\s+(tämä|viesti)|järjestelmäkehote" +
        // Fake prompt structure: this application's delimiter or chat-template markers.
        @"|<<<\s*viesti|viesti\s+(alkaa|päättyy)\s*>>>" +
        @"|^\s*(<<<|>>>|</?(email|message|system|user|assistant|instructions?)>|\[/?(system|inst)\]|###\s*(end|system|instruction))" +
        // A "system" line that gives instructions.
        @"|^\s*(järjestelmä|system)\s*:.{0,200}?\b(ohjeet?|instructions?|palauta|output)\b" +
        // Asking for this application's output fields or JSON.
        @"|""(toimenpide|kiireellisyys|tarkistettava|tarkistuksen_syyt|yhteenveto|maaraaika|aihe)""\s*:" +
        @"|\b(ei_maaraaikaa|talla_viikolla|tarkistuksen_syyt|epaselva_pyynto|katkaistu_sisalto)\b" +
        @"|\b(palauta|output|return|respond\s+with)\b.{0,60}\bjson\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();
}
