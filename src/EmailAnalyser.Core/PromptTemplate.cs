using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace EmailAnalyser.Core;

/// <summary>
/// The classification prompt and JSON schema, embedded from the Prompts folder.
/// <see cref="Version"/> changes whenever any of the three files changes, and is
/// stored with every assessment.
/// </summary>
public sealed class PromptTemplate
{
    public const string Name = "classify-v1";

    private PromptTemplate(string system, string user, string schema)
    {
        System = system;
        UserTemplate = user;
        SchemaJson = schema;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(system + "\u0000" + user + "\u0000" + schema));
        Version = $"{Name}-{Convert.ToHexString(hash)[..8].ToLowerInvariant()}";
    }

    public string System { get; }
    public string UserTemplate { get; }
    public string SchemaJson { get; }
    public string Version { get; }

    public const int MaxUserContextChars = 3000;

    /// <param name="userContext">
    /// The user's own background from settings (e.g. who invests through which company), trusted
    /// and appended to the system prompt. It changes <see cref="Version"/>, so every assessment
    /// records the exact prompt it was made with.
    /// </param>
    public static PromptTemplate LoadEmbedded(IReadOnlyList<string>? userContext = null) =>
        new(Read("classify-system.txt") + ContextSection(userContext), Read("classify-user.txt"), Read("classify-schema.json"));

    private static string ContextSection(IReadOnlyList<string>? lines)
    {
        var clean = (lines ?? []).Select(l => SafeText.Clean(l, keepNewlines: false).Trim()).Where(l => l.Length > 0).ToList();
        if (clean.Count == 0)
        {
            return "";
        }

        var text = string.Join("\n", clean.Select(l => "- " + l));
        if (text.Length > MaxUserContextChars)
        {
            throw new InvalidOperationException($"Käyttäjän tausta (userContext) on yli {MaxUserContextChars} merkkiä.");
        }

        return "\n\nKäyttäjän itse antama tausta (luotettavaa tietoa, ei viestistä):\n" + text + "\n";
    }

    public string BuildUser(MessageForAssessment message, PreparedText body, DateTimeOffset now)
    {
        var helsinki = HelsinkiTime.Zone;
        var from = string.IsNullOrWhiteSpace(message.SenderAddress)
            ? message.SenderName
            : $"{message.SenderName} <{message.SenderAddress}>";

        var localNow = TimeZoneInfo.ConvertTime(now, helsinki);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        return UserTemplate
            .Replace("{{now}}", Format(localNow), StringComparison.Ordinal)
            .Replace("{{tomorrow}}", FormatDate(today.AddDays(1)), StringComparison.Ordinal)
            .Replace("{{week_limit}}", FormatDate(today.AddDays(7)), StringComparison.Ordinal)
            .Replace("{{mailbox}}", OneLine(message.Mailbox), StringComparison.Ordinal)
            .Replace("{{from}}", OneLine(from), StringComparison.Ordinal)
            .Replace("{{to}}", OneLine(Limit(message.To, 300)), StringComparison.Ordinal)
            .Replace("{{cc}}", OneLine(Limit(message.Cc, 300)), StringComparison.Ordinal)
            .Replace("{{role}}", DescribeRole(message.Role), StringComparison.Ordinal)
            .Replace("{{received}}", Format(TimeZoneInfo.ConvertTime(message.Received, helsinki)), StringComparison.Ordinal)
            .Replace("{{subject}}", OneLine(Limit(message.Subject, 300)), StringComparison.Ordinal)
            .Replace("{{truncated}}", body.Truncated ? "kyllä" : "ei", StringComparison.Ordinal)
            .Replace("{{body}}", body.Text, StringComparison.Ordinal);
    }

    private static string Format(DateTimeOffset time) =>
        time.ToString("yyyy'-'MM'-'dd HH':'mm '('dddd')'", CultureInfo.GetCultureInfo("fi-FI"));

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy'-'MM'-'dd '('dddd')'", CultureInfo.GetCultureInfo("fi-FI"));

    private static string DescribeRole(RecipientRole role) => role switch
    {
        RecipientRole.Direct => "suora vastaanottaja (Vastaanottajat-kentässä)",
        RecipientRole.Copy => "vain kopiona (Kopio-kentässä)",
        RecipientRole.NotNamed => "ei nimetty vastaanottajissa (esim. jakelulista tai piilokopio)",
        _ => "ei tiedossa",
    };

    private static string OneLine(string value) =>
        SafeText.Clean(value, keepNewlines: false).Replace("<<<", "‹‹‹", StringComparison.Ordinal).Replace(">>>", "›››", StringComparison.Ordinal).Trim();

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + " …";

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Prompts.{name}")
                           ?? throw new InvalidOperationException($"Upotettu kehotetiedosto puuttuu: {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}

public static class HelsinkiTime
{
    public static TimeZoneInfo Zone { get; } = Find();

    public static DateTimeOffset Now(TimeProvider time) => TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone);

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Europe/Helsinki", "FLE Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        throw new InvalidOperationException("Aikavyöhykettä Europe/Helsinki ei löytynyt.");
    }
}
