internal static class RunExecutionContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? CurrentRunId => Current.Value;

    public static IDisposable Enter(string runId)
    {
        var previous = Current.Value;
        Current.Value = runId;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;

        public Scope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            Current.Value = _previous;
        }
    }
}
