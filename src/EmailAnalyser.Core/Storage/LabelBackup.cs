using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailAnalyser.Core.Storage;

/// <summary>
/// One user label with the identifiers needed to find its message again. Subjects,
/// senders and assessments are deliberately left out: after a restore the message is
/// read from Outlook and assessed again.
/// </summary>
public sealed record ExportedLabel(
    string StoreId,
    string EntryId,
    string? InternetMessageId,
    string Mailbox,
    string FolderPath,
    DateTimeOffset Received,
    string Action,
    string Urgency,
    string Topic,
    DateOnly? Deadline,
    bool AgreesWithAi,
    string Split,
    bool Blind,
    DateTimeOffset LabeledAt,
    string? Note = null);

/// <summary>
/// <paramref name="Imported"/> labels were written; <paramref name="NewMessages"/> of them
/// belonged to messages missing from the database, which were queued as references.
/// </summary>
public sealed record LabelImportResult(int Imported, int NewMessages, int KeptNewer, int Rejected);

/// <summary>
/// Passphrase-encrypted export of the user's labels. The labels are the only data the
/// application cannot reproduce, and the database key is bound to the Windows account
/// (DPAPI), so the export uses its own key: PBKDF2-SHA256 from a passphrase, then
/// AES-256-GCM. The header is authenticated as associated data.
/// </summary>
public static class LabelBackup
{
    public const string Format = "email-analyser-labels";
    public const int Version = 1;
    public const int DefaultIterations = 600_000;
    public const int MinPassphraseLength = 12;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encrypt(IReadOnlyList<ExportedLabel> labels, string passphrase, DateTimeOffset now, int iterations = DefaultIterations)
    {
        CheckPassphrase(passphrase);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var header = new Header(Format, Version, "PBKDF2-SHA256", iterations, Convert.ToBase64String(salt), Convert.ToBase64String(nonce));
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Payload(now, labels), Json);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        var key = DeriveKey(passphrase, salt, iterations);
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plain, cipher, tag, AssociatedData(header));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }

        return JsonSerializer.SerializeToUtf8Bytes(
            new Envelope(header, Convert.ToBase64String(cipher), Convert.ToBase64String(tag)),
            new JsonSerializerOptions(Json) { WriteIndented = true });
    }

    /// <summary>Decrypts an export. Throws <see cref="InvalidDataException"/> on a wrong passphrase or a changed file.</summary>
    public static IReadOnlyList<ExportedLabel> Decrypt(byte[] file, string passphrase)
    {
        Envelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(file, Json) ?? throw new InvalidDataException();
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Tiedosto ei ole merkintöjen vientitiedosto.");
        }

        var h = envelope.Header;
        if (h is null || h.Format != Format || h.Kdf != "PBKDF2-SHA256")
        {
            throw new InvalidDataException("Tiedosto ei ole merkintöjen vientitiedosto.");
        }

        if (h.Version != Version)
        {
            throw new InvalidDataException($"Vientitiedoston versiota {h.Version} ei tueta.");
        }

        if (h.Iterations is < 100_000 or > 10_000_000)
        {
            throw new InvalidDataException("Vientitiedoston avainjohdon asetus on sallitun rajan ulkopuolella.");
        }

        byte[] salt, nonce, cipher, tag;
        try
        {
            salt = Convert.FromBase64String(h.Salt);
            nonce = Convert.FromBase64String(h.Nonce);
            cipher = Convert.FromBase64String(envelope.Ciphertext);
            tag = Convert.FromBase64String(envelope.Tag);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("Vientitiedosto on vioittunut.");
        }

        if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16)
        {
            throw new InvalidDataException("Vientitiedosto on vioittunut.");
        }

        var plain = new byte[cipher.Length];
        var key = DeriveKey(passphrase, salt, h.Iterations);
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, cipher, tag, plain, AssociatedData(h));
            var payload = JsonSerializer.Deserialize<Payload>(plain, Json) ?? throw new InvalidDataException("Vientitiedosto on tyhjä.");
            return payload.Labels ?? [];
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("Väärä tunnuslause tai muuttunut tiedosto.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static void CheckPassphrase(string passphrase)
    {
        if (passphrase.Length < MinPassphraseLength)
        {
            throw new ArgumentException($"Tunnuslauseen pitää olla vähintään {MinPassphraseLength} merkkiä.");
        }
    }

    /// <summary>True for a valid label from an export; values outside the label vocabulary are rejected on import.</summary>
    public static bool IsValid(ExportedLabel label) =>
        !string.IsNullOrWhiteSpace(label.StoreId) && !string.IsNullOrWhiteSpace(label.EntryId) &&
        label.Mailbox is not null && label.FolderPath is not null &&
        Labels.Actions.Contains(label.Action) && Labels.Urgencies.Contains(label.Urgency) && Labels.Topics.Contains(label.Topic) &&
        label.Split is LabelSplit.Tuning or LabelSplit.Holdout;

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] AssociatedData(Header h) =>
        Encoding.UTF8.GetBytes($"{h.Format}|{h.Version}|{h.Kdf}|{h.Iterations}|{h.Salt}|{h.Nonce}");

    private sealed record Header(string Format, int Version, string Kdf, int Iterations, string Salt, string Nonce);

    private sealed record Envelope(Header Header, string Ciphertext, string Tag);

    private sealed record Payload(DateTimeOffset Exported, IReadOnlyList<ExportedLabel> Labels);
}
