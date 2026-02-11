namespace SirThaddeus.Agent.Dialogue;

public interface IDialogueStateStore
{
    DialogueState Get();
    void Seed(DialogueState state);
    void Update(DialogueState state);
    void Reset();
    event Action<DialogueState>? Changed;
}
