using System.Globalization;
using System.Text.Json;

namespace EmailAnalyser.Core;

public static class Labels
{
    public static readonly IReadOnlySet<string> Topics =
        new HashSet<string> { "tyo", "sijoitukset", "perheyhtio", "kohdeyhtiot", "markkinat", "sijoitusmahdollisuudet", "tapahtumat", "hallinto", "ilmoitukset", "henkilokohtainen", "uutiskirje", "muu" };

    public static readonly IReadOnlySet<string> Actions =
        new HashSet<string> { "vastattava", "tehtava", "tiedoksi", "tarkista" };

    public static readonly IReadOnlySet<string> Urgencies =
        new HashSet<string> { "tanaan", "talla_viikolla", "myohemmin", "ei_maaraaikaa" };

    public static readonly IReadOnlySet<string> ReviewReasons =
        new HashSet<string> { "puuttuva_konteksti", "epaselva_pyynto", "katkaistu_sisalto", "ristiriita" };

    /// <summary>Actions that need the user's attention.</summary>
    public static readonly IReadOnlySet<string> AttentionActions = new HashSet<string> { "vastattava", "tehtava" };
}

public sealed record Assessment(
    string Summary,
    string Rationale,
    string Topic,
    string Action,
    string Urgency,
    DateOnly? Deadline,
    bool NeedsReview,
    IReadOnlyList<string> ReviewReasons);

public sealed record ValidationResult(Assessment? Assessment, IReadOnlyList<string> Errors)
{
    public bool IsValid => Assessment is not null;
}

/// <summary>
/// Checks the model's JSON programmatically. The schema-constrained output is not
/// trusted on its own: enums, dates, lengths and consistency are verified here,
/// and truncated input always forces review.
/// </summary>
public static class AssessmentValidator
{
    public const int MaxSummaryChars = 600;
    public const int MaxRationaleChars = 1000;

    private static readonly string[] Required =
        ["yhteenveto", "perustelu", "aihe", "toimenpide", "kiireellisyys", "maaraaika", "tarkistettava", "tarkistuksen_syyt"];

    public static ValidationResult Validate(string? json, bool inputTruncated)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return Fail("tyhjä vastaus");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Fail("vastaus ei ole JSONia");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Fail("vastaus ei ole JSON-objekti");
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!Required.Contains(property.Name))
                {
                    errors.Add($"ylimääräinen kenttä {property.Name}");
                }
            }

            foreach (var name in Required)
            {
                if (!root.TryGetProperty(name, out _))
                {
                    errors.Add($"puuttuva kenttä {name}");
                }
            }

            if (errors.Count > 0)
            {
                return new ValidationResult(null, errors);
            }

            var summary = Text(root, "yhteenveto", MaxSummaryChars, errors);
            var rationale = Text(root, "perustelu", MaxRationaleChars, errors);
            var topic = Enum(root, "aihe", Labels.Topics, errors);
            var action = Enum(root, "toimenpide", Labels.Actions, errors);
            var urgency = Enum(root, "kiireellisyys", Labels.Urgencies, errors);
            var deadline = Deadline(root, errors);
            var needsReview = root.GetProperty("tarkistettava") is { ValueKind: JsonValueKind.True or JsonValueKind.False } flag
                ? flag.GetBoolean()
                : AddError(errors, "tarkistettava ei ole totuusarvo");
            var reasons = Reasons(root, errors);

            if (errors.Count > 0)
            {
                return new ValidationResult(null, errors);
            }

            // Program-level rules: reasons imply review; truncated input always needs review.
            needsReview |= reasons.Count > 0;
            if (inputTruncated)
            {
                needsReview = true;
                if (!reasons.Contains("katkaistu_sisalto"))
                {
                    reasons.Add("katkaistu_sisalto");
                }
            }

            return new ValidationResult(
                new Assessment(summary!, rationale!, topic!, action!, urgency!, deadline, needsReview, reasons),
                errors);
        }

        ValidationResult Fail(string error) => new(null, [error]);
    }

    private static string? Text(JsonElement root, string name, int max, List<string> errors)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            errors.Add($"{name} puuttuu tai on tyhjä");
            return null;
        }

        // Model output is untrusted: strip invisible/bidi characters and reduce links to their host.
        var text = SafeText.ModelText(value.GetString()!).Trim();
        if (text.Length > max)
        {
            errors.Add($"{name} on liian pitkä ({text.Length} merkkiä)");
        }

        return text;
    }

    private static string? Enum(JsonElement root, string name, IReadOnlySet<string> allowed, List<string> errors)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind == JsonValueKind.String && allowed.Contains(value.GetString()!))
        {
            return value.GetString();
        }

        errors.Add($"{name} ei ole sallittu arvo");
        return null;
    }

    private static DateOnly? Deadline(JsonElement root, List<string> errors)
    {
        var value = root.GetProperty("maaraaika");
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        errors.Add("maaraaika ei ole muotoa VVVV-KK-PP tai null");
        return null;
    }

    private static List<string> Reasons(JsonElement root, List<string> errors)
    {
        var reasons = new List<string>();
        var value = root.GetProperty("tarkistuksen_syyt");
        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add("tarkistuksen_syyt ei ole lista");
            return reasons;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && Labels.ReviewReasons.Contains(item.GetString()!))
            {
                if (!reasons.Contains(item.GetString()!))
                {
                    reasons.Add(item.GetString()!);
                }
            }
            else
            {
                errors.Add("tarkistuksen_syyt sisältää sallimattoman arvon");
                break;
            }
        }

        return reasons;
    }

    private static bool AddError(List<string> errors, string error)
    {
        errors.Add(error);
        return false;
    }
}
