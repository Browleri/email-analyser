using EmailAnalyser.Core;
using EmailAnalyser.Core.Storage;
using EmailAnalyser.Outlook;

namespace EmailAnalyser.App;

/// <summary>
/// Runs continuous monitoring in the background of the view with its own database
/// connection. The Outlook adapter is shared; its STA thread serialises calls.
/// </summary>
public sealed class MonitorHost : IDisposable
{
    private readonly AnalyserStore _store;
    private readonly LmStudioClient _lmStudio;
    private readonly Automatic1111Probe _a1111;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    public MonitorHost(OutlookMailbox outlook, AppSettings settings)
    {
        _store = AnalyserStore.Open(AppPaths.Database, KeyStore.GetOrCreateHexKey(AppPaths.DatabaseKey));
        _lmStudio = new LmStudioClient(settings.LmStudioUrl, TimeSpan.FromMinutes(5));
        _a1111 = new Automatic1111Probe(settings.Automatic1111Url);
        var classifier = new Classifier(_lmStudio, PromptTemplate.LoadEmbedded(), settings.ToClassifierOptions());
        var runner = new BatchRunner(_store, _lmStudio, _a1111, outlook, classifier, TimeProvider.System,
            new NvidiaSmiProbe(), new LmStudioPrivacyCheck(), settings.ToIdentityCheck());
        Monitor = new MonitorService(_store, outlook, runner, new CategorySync(_store, outlook, TimeProvider.System),
            () => AppSettings.Load(AppPaths.Settings), TimeProvider.System);
    }

    public MonitorService Monitor { get; }

    public void Start() => _task = Task.Run(() => Monitor.RunAsync(_cts.Token));

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            // A running batch unloads its model on cancellation (at most about a minute).
            _task?.Wait(TimeSpan.FromSeconds(90));
        }
        catch (AggregateException)
        {
        }

        _lmStudio.Dispose();
        _a1111.Dispose();
        _store.Dispose();
        _cts.Dispose();
    }
}
