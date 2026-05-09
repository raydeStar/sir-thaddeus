using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Search.DeepDive;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Contracts;
using SirThaddeus.Memory;
using SirThaddeus.Memory.Sqlite;
using SirThaddeus.PersonalityEngine.Profiles;
using SirThaddeus.RuntimeHost;

internal static partial class RuntimeApiServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions EditableDocumentJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    private static readonly JsonDocumentOptions EditableDocumentReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static async Task RunAsync(
        int port,
        Func<AppSettings, IHeadlessAgent> buildOrchestrator,
        Func<AppSettings> getSettings,
        Action<AppSettings> setSettings,
        Func<CancellationToken, Task<SearchStatusResponse>> getSearchStatus,
        IAuditLogger audit,
        ApiPermissionGate? permissionGate,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();

        var runs = new ConcurrentDictionary<string, RunState>(StringComparer.OrdinalIgnoreCase);
        void PersistSettings(AppSettings updatedSettings)
        {
            SettingsManager.Save(updatedSettings);
            var persistedSettings = SettingsManager.Load();
            setSettings(persistedSettings);
            permissionGate?.UpdateSettings(persistedSettings);
        }

        if (permissionGate is not null)
        {
            permissionGate.Requested += (runId, payload) =>
            {
                if (runs.TryGetValue(runId, out var run))
                {
                    run.Append(RuntimeEventTypes.ToolRequested, payload);
                }
            };

            permissionGate.Resolved += (runId, payload) =>
            {
                if (runs.TryGetValue(runId, out var run))
                {
                    run.Append(
                        payload.Approved ? RuntimeEventTypes.ToolApproved : RuntimeEventTypes.ToolDenied,
                        payload);
                }
            };
        }

        MapCoreEndpoints(app, getSearchStatus, getSettings, PersistSettings, audit);
        MapMemoryEndpoints(app, getSettings);
        MapRunEndpoints(app, runs, buildOrchestrator, getSettings, permissionGate, PersistSettings, audit);
        MapActivityEndpoints(app, audit, getSettings, PersistSettings, permissionGate);

        MapProfileEndpoints(app, getSettings, PersistSettings);
        MapPersonalityEndpoints(app, getSettings, PersistSettings);

        MapHarnessEndpoints(app, getSettings, permissionGate);

        await app.RunAsync(cancellationToken);
    }

}






