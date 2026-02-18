namespace SirThaddeus.Agent.ConversationSegmentation;

public interface IConversationSegmenter
{
    ConversationSegmentationResult Segment(string userMessage);
}

