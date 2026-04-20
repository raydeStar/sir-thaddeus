using Thaddeus.Runtime.Hosting;

namespace Thaddeus.Runtime.Tests;

public sealed class TokenGeneratorTests
{
    [Fact]
    public void Tokens_are_unique_and_url_safe()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 64; i++)
        {
            var t = TokenGenerator.NewToken();
            Assert.True(seen.Add(t), "Duplicate token generated.");
            Assert.DoesNotContain('+', t);
            Assert.DoesNotContain('/', t);
            Assert.DoesNotContain('=', t);
            // 32 random bytes -> 43 base64url chars (no padding).
            Assert.Equal(43, t.Length);
        }
    }
}
