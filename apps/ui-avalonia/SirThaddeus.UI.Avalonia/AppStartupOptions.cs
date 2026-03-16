namespace SirThaddeus.UI.Avalonia;

internal sealed record AppStartupOptions(
    bool HeadlessMode,
    bool SmokeTestMode)
{
    public static AppStartupOptions Current { get; private set; } = new(false, false);

    public static string[] Initialize(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var filteredArgs = new List<string>(args.Length);
        var headlessMode = false;
        var smokeTestMode = false;

        foreach (var arg in args)
        {
            if (string.Equals(arg, "--headless", StringComparison.OrdinalIgnoreCase))
            {
                headlessMode = true;
                continue;
            }

            if (string.Equals(arg, "--smoke-test", StringComparison.OrdinalIgnoreCase))
            {
                smokeTestMode = true;
                continue;
            }

            filteredArgs.Add(arg);
        }

        Current = new AppStartupOptions(headlessMode, smokeTestMode);
        return filteredArgs.ToArray();
    }
}
