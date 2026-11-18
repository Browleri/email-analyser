using System.Text;
using EmailAnalyser.Core.Storage;

namespace EmailAnalyser.App;

/// <summary>
/// Replaces message text with stable placeholders, so the view can be shared or captured
/// without revealing mail. The same sender or mailbox always gets the same placeholder, and
/// text keeps roughly its length, so the layout stays realistic. Only structure leaks:
/// lengths, times, and which rows share a sender.
/// </summary>
public sealed class DisplayMask
{
    public const string HiddenProblem = "yksityiskohdat piilotettu";

    private static readonly string[] Words =
    [
        "viesti", "kokous", "lasku", "tiedote", "aikataulu", "projekti", "päivitys", "vahvistus", "raportti", "tarjous",
        "muistutus", "sopimus", "kutsu", "tilaus", "palaute", "esitys", "budjetti", "huolto", "ilmoitus", "kysely",
        "työpaja", "suunnitelma", "katsaus", "pöytäkirja", "hakemus", "toimitus", "varaus", "tapaaminen",
    ];

    private readonly Dictionary<string, int> _mailboxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _senders = new(StringComparer.OrdinalIgnoreCase);

    public string Mailbox(string name) => $"Laatikko {Number(_mailboxes, name)}";

    public string SenderName(ReviewItem item) =>
        HasSender(item) ? $"Lähettäjä {Number(_senders, SenderKey(item))}" : "";

    public string SenderAddress(ReviewItem item) =>
        HasSender(item) ? $"lahettaja{Number(_senders, SenderKey(item))}@example.invalid" : "";

    /// <summary>Placeholder words, deterministic for the seed, about as long as the original (at most <paramref name="max"/>).</summary>
    public static string Text(long seed, int length, int max)
    {
        var target = Math.Min(length, max);
        if (target <= 0)
        {
            return "";
        }

        var state = unchecked((ulong)seed * 2654435761UL + 1);
        var sb = new StringBuilder();
        while (sb.Length < target)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(Words[(int)((state >> 33) % (ulong)Words.Length)]);
        }

        sb[0] = char.ToUpperInvariant(sb[0]);
        return sb.ToString();
    }

    private static bool HasSender(ReviewItem item) =>
        !string.IsNullOrEmpty(item.SenderAddress) || !string.IsNullOrEmpty(item.SenderName);

    private static string SenderKey(ReviewItem item) =>
        string.IsNullOrEmpty(item.SenderAddress) ? item.SenderName ?? "" : item.SenderAddress;

    private static int Number(Dictionary<string, int> numbers, string key)
    {
        if (!numbers.TryGetValue(key, out var number))
        {
            number = numbers.Count + 1;
            numbers[key] = number;
        }

        return number;
    }
}
