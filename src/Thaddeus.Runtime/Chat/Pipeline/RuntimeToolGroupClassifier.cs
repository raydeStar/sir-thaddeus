using SirThaddeus.Agent.Pipeline;
using Thaddeus.Runtime.Tools;

namespace Thaddeus.Runtime.Chat.Pipeline;

/// <summary>
/// Runtime implementation of <see cref="IToolGroupClassifier"/>. Delegates
/// to the existing <see cref="ToolGroupClassifier"/>, which is what the
/// UI and permission gate already use — keeps group labels consistent
/// across the event stream and the settings-driven permission policy.
/// </summary>
public sealed class RuntimeToolGroupClassifier : IToolGroupClassifier
{
    public static readonly RuntimeToolGroupClassifier Instance = new();

    public string Classify(string toolName)
        => ToolGroupClassifier.Classify(toolName).ToString();
}
