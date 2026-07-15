using SirThaddeus.Agent;
using Thaddeus.Runtime.Chat;

namespace Thaddeus.Runtime.Tests;

public sealed class HarnessToolEvidenceStoreTests
{
    [Fact]
    public void Capture_GetAndClear_IsolateEvidenceByMessage()
    {
        var store = new HarnessToolEvidenceStore();
        store.Capture("message-1",
        [
            new ToolCallRecord
            {
                ToolName = "web_search",
                Arguments = "{\"query\":\"example\"}",
                Result = "full model-visible result",
                Success = true
            }
        ]);

        var captured = Assert.Single(store.Get("message-1"));
        Assert.Equal("full model-visible result", captured.Result);
        Assert.Empty(store.Get("message-2"));

        store.Clear();
        Assert.Empty(store.Get("message-1"));
    }
}
