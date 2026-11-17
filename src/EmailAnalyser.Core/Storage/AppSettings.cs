using System.Text.Json;

namespace EmailAnalyser.Core.Storage;

/// <summary>Per-user data folder, outside OneDrive. Overridable for tests.</summary>
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("EMAIL_ANALYSER_HOME");
            var path = string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmailAnalyser")
                : overridden;
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string Database => Path.Combine(DataDirectory, "analyser.db");
    public static string DatabaseKey => Path.Combine(DataDirectory, "database.key");
    public static string Settings => Path.Combine(DataDirectory, "settings.json");
}

public sealed record MonitoredStore(string StoreId, string Name);

public sealed record AppSettings
{
    public List<MonitoredStore> Stores { get; init; } = [];
    public string Model { get; init; } = "google/gemma-4-12b-qat";
    public int ContextLength { get; init; } = 8192;
    public int MaxBodyChars { get; init; } = TextPreparation.DefaultMaxChars;
    public int MaxTokens { get; init; } = 600;
    public int BatchMaxMinutes { get; init; } = 15;
    public string LmStudioUrl { get; init; } = "http://127.0.0.1:1234";
    public string Automatic1111Url { get; init; } = "http://127.0.0.1:7860";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static AppSettings Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings() : new AppSettings();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public ClassifierOptions ToClassifierOptions() => new(MaxBodyChars, MaxTokens, ContextLength);
}
