using Runesmith.Sdk.Plugins;
using Runesmith.Sdk.Tasks;

namespace Runesmith.CSharp.Tests.Debugging;

/// <summary>Gives the plugin a fixed storage folder.</summary>
internal sealed class TestStorage(string folder) : IPluginStorage
{
    public string Folder { get; } = folder;

    public string GetFolder()
    {
        Directory.CreateDirectory(Folder);
        return Folder;
    }
}

/// <summary>Remembers the background tasks started and what they reported.</summary>
internal sealed class TestBackgroundTasks : IBackgroundTasks
{
    public List<TestTask> Started { get; } = [];

    public IReadOnlyList<IBackgroundTask> Running => [.. Started.Where(task => !task.Disposed)];

    public IBackgroundTask Start(string title, Action? cancel = null)
    {
        var task = new TestTask(title);
        Started.Add(task);
        return task;
    }

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    internal sealed class TestTask(string title) : IBackgroundTask
    {
        public string Title { get; } = title;

        public string? Detail { get; private set; }

        public double? Fraction { get; private set; }

        public List<string?> Details { get; } = [];

        public bool Disposed { get; private set; }

        public void Report(string? detail, double? fraction = null)
        {
            lock (Details)
                Details.Add(detail);
            Detail = detail;
            Fraction = fraction;
        }

        public void Dispose() => Disposed = true;
    }
}
