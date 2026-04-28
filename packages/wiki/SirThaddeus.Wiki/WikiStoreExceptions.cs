namespace SirThaddeus.Wiki;

public sealed class WikiPathException : InvalidOperationException
{
    public WikiPathException(string message) : base(message)
    {
    }
}

public sealed class WikiVersionConflictException : InvalidOperationException
{
    public WikiVersionConflictException(string pageId, long expectedVersion, long currentVersion)
        : base($"Wiki page '{pageId}' is at version {currentVersion}, not {expectedVersion}.")
    {
        PageId = pageId;
        ExpectedVersion = expectedVersion;
        CurrentVersion = currentVersion;
    }

    public string PageId { get; }

    public long ExpectedVersion { get; }

    public long CurrentVersion { get; }
}