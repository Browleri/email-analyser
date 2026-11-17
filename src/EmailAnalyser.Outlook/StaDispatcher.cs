using System.Collections.Concurrent;

namespace EmailAnalyser.Outlook;

/// <summary>
/// A dedicated single-threaded-apartment thread. Every Outlook COM call runs here,
/// so COM objects never cross threads and awaiting callers never block it.
/// </summary>
public sealed class StaDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaDispatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Outlook STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(10));
        _queue.Dispose();
    }

    private void Run()
    {
        // Blocking waits on an STA thread pump COM messages in the CLR.
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            work();
        }
    }
}
