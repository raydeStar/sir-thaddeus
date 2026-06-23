namespace Thaddeus.Runtime.Modules;

public interface IModuleStateStore
{
    Task<ModuleStateDocument> GetAsync(CancellationToken ct);
    Task<ModuleStateDocument> ReplaceAsync(ModuleStateDocument document, CancellationToken ct);
}
