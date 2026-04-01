namespace SirThaddeus.Agent.Routing;

/// <summary>
/// The 7 task lanes that every incoming user request is classified into
/// before any model action occurs. This is the foundation of the
/// discipline layer.
/// </summary>
public enum TaskLane
{
    /// <summary>Calculation, date math, unit conversion.</summary>
    Deterministic,

    /// <summary>"What is this?", "summarize", "describe", "is this legit".</summary>
    Explain,

    /// <summary>"Walk me through", "help me do", "what do I click".</summary>
    Guide,

    /// <summary>"When does X open?", "is Y in stock?", "what is the price of".</summary>
    Lookup,

    /// <summary>"Which is better?", "compare A vs B", "is this a good deal".</summary>
    Compare,

    /// <summary>Read/write/organize files on disk.</summary>
    FileSystem,

    /// <summary>Chitchat, meta questions, unclear intent.</summary>
    Conversation
}
