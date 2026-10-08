namespace GenshinVideoHelper.Core.Application;

internal sealed class BackgroundWork
{
    private readonly object _sync = new();
    private readonly HashSet<Task> _tasks = [];
    public Task Track(Task task)
    {
        lock (_sync) _tasks.Add(task);
        return ObserveAsync(task);
    }
    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        finally { lock (_sync) _tasks.Remove(task); }
    }
    public Task DrainAsync()
    {
        lock (_sync) return Task.WhenAll(_tasks.ToArray());
    }
}
