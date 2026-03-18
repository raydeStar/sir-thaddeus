using SirThaddeus.Config;
using SirThaddeus.Memory.Sqlite;
using SirThaddeus.RuntimeHost;

internal static partial class RuntimeApiServer
{
    private static SqliteMemoryStore CreateMemoryStore(AppSettings settings)
    {
        var dbPath = RuntimeMcpEnvironmentBuilder.ResolveMemoryDbPath(settings.Memory.DbPath);
        return new SqliteMemoryStore(dbPath);
    }
}
