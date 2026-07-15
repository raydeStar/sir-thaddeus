using Thaddeus.Runtime.Chat;

namespace Thaddeus.Runtime.Tests;

public sealed class HarnessToolEvidenceStoreTests
{
    [Fact]
    public void Capture_GetAndClear_IsolateEvidenceByMessage()
    {
        var store = new HarnessToolEvidenceStore();
        store.Append(
            "message-1",
            "web_search",
            "{\"query\":\"example\"}",
            "full model-visible result",
            success: true);

        var captured = Assert.Single(store.Get("message-1"));
        Assert.Equal("full model-visible result", captured.Result);
        Assert.Empty(store.Get("message-2"));

        store.Clear();
        Assert.Empty(store.Get("message-1"));
    }

    [Fact]
    public void Append_PreservesToolCallOrder()
    {
        var store = new HarnessToolEvidenceStore();

        store.Append("message-1", "web_search", "{\"query\":\"first\"}", "first result", true);
        store.Append("message-1", "web_search", "{\"query\":\"second\"}", "second result", true);

        var calls = store.Get("message-1");
        Assert.Equal(2, calls.Count);
        Assert.Equal("first result", calls[0].Result);
        Assert.Equal("second result", calls[1].Result);
    }
}
