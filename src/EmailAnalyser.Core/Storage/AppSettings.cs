using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailAnalyser.Core.Storage;

/// <summary>Per-user data folder, outside OneDrive. Overridable for tests.</summary>
public static class AppPaths
{
    private static readonly Lazy<string> Directory_ = new(CreateDataDirectory);

    public static string DataDirectory => Directory_.Value;

    /// <summary>
    /// Restricts a folder to the current user and SYSTEM, without inherited entries.
    /// %LOCALAPPDATA% can carry broader inherited access (on koti-pc a sandbox group
    /// had read access), which must not reach the database or the key file.
    /// </summary>
    public static void MakePrivate(string path)
    {
        var info = new DirectoryInfo(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private static string CreateDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable("EMAIL_ANALYSER_HOME");
        var path = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmailAnalyser")
            : overridden;
        Directory.CreateDirectory(path);
        MakePrivate(path);
        return path;
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
    /// <summary>The program that must own the model server's port (checked before each batch).</summary>
    public string LmStudioExecutable { get; init; } = LoopbackPortOwnerCheck.DefaultLmStudioExecutable;

    // Continuous monitoring (phase 5).
    public int MonitorIntervalMinutes { get; init; } = 2;
    public int BatchMinQueue { get; init; } = 10;
    public int BatchMaxWaitMinutes { get; init; } = 20;
    public int DeferRetryMinutes { get; init; } = 5;
    public int CatchUpDays { get; init; } = 3;
    public int ScanMaxPerStore { get; init; } = 300;
    public int ScanOverlapMinutes { get; init; } = 60;
    public int RetentionDays { get; init; } = 90;

    /// <summary>Outlook categories: off by default; "test" writes only to the message with <see cref="CategoryTestSubject"/>.</summary>
    public CategoryMode CategoryMode { get; init; } = CategoryMode.Off;
    public string CategoryTestSubject { get; init; } = "AI: testiviesti";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static AppSettings Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings() : new AppSettings();

    public void Save(string path)
    {
        // Write and rename, so a crash cannot leave a half-written settings file.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    public ClassifierOptions ToClassifierOptions() => new(MaxBodyChars, MaxTokens, ContextLength);

    public IServerIdentityCheck ToIdentityCheck() =>
        new LoopbackPortOwnerCheck(LoopbackGuard.Ensure(LmStudioUrl).Port, LmStudioExecutable);
}
