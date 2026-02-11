using SirThaddeus.Agent.Dialogue;

namespace SirThaddeus.DesktopRuntime.Services;

public interface IDialogueStatePersistence
{
    Task<DialogueState?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(DialogueState state, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
