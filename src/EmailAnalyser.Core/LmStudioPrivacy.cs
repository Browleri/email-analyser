using System.Text.Json;

namespace EmailAnalyser.Core;

public interface IPrivacyCheck
{
    /// <summary>A reason not to send message content, or null when it is safe.</summary>
    string? Problem();
}

/// <summary>
/// Fails closed unless LM Studio's server configuration shows that prompt logging
/// (logSensitiveData) and verbose logging are off. An update or a click in the
/// Developer tab could turn logging back on and write message content to disk.
/// </summary>
public sealed class LmStudioPrivacyCheck(string? configPath = null) : IPrivacyCheck
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", ".internal", "http-server-config.json");

    public string? Problem()
    {
        var path = configPath ?? DefaultPath;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("logSensitiveData", out var sensitive) || sensitive.ValueKind != JsonValueKind.False)
            {
                return "LM Studion kehotteiden lokitus (logSensitiveData) ei ole pois päältä";
            }

            if (root.TryGetProperty("verbose", out var verbose) && verbose.ValueKind == JsonValueKind.True)
            {
                return "LM Studion yksityiskohtainen lokitus (verbose) on päällä";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return "LM Studion lokitusasetusta ei voitu tarkistaa";
        }
    }
}
