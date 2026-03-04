using SirThaddeus.Agent;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Tests;

public sealed class UtilityIntentHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_ChatOnly_DoesNotInvokeLlmUtilityInference()
    {
        var handler = new UtilityIntentHandler();
        var inferCalled = false;

        var response = await handler.TryHandleAsync(new UtilityIntentExecutionRequest
        {
            UserMessage = "tell me about your favorite thing to help people with",
            Route = new RouterOutput { Intent = Intents.ChatOnly },
            TryInferWithLlmAsync = (_, _) =>
            {
                inferCalled = true;
                return Task.FromResult<UtilityRouter.UtilityResult?>(new UtilityRouter.UtilityResult
                {
                    Category = "fact",
                    Answer = "inferred"
                });
            }
        });

        Assert.False(inferCalled);
        Assert.Null(response);
    }

    [Fact]
    public async Task TryHandleAsync_GeneralTool_InvokesLlmUtilityInference()
    {
        var handler = new UtilityIntentHandler();
        var inferCalled = false;

        var response = await handler.TryHandleAsync(new UtilityIntentExecutionRequest
        {
            UserMessage = "some ambiguous utility phrasing",
            Route = new RouterOutput { Intent = Intents.GeneralTool },
            TryInferWithLlmAsync = (_, _) =>
            {
                inferCalled = true;
                return Task.FromResult<UtilityRouter.UtilityResult?>(new UtilityRouter.UtilityResult
                {
                    Category = "fact",
                    Answer = "inferred"
                });
            }
        });

        Assert.True(inferCalled);
        Assert.NotNull(response);
        Assert.Equal("inferred", response!.Text);
    }
}
