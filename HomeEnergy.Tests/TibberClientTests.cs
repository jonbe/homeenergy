using HomeEnergy.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeEnergy.Tests;

public class TibberClientTests
{
    [Fact]
    public async Task FetchAsync_ReturnsNull_AndSkipsHttp_WhenTokenMissing()
    {
        var secrets = new FakeSecretProvider();   // no TIBBER_TOKEN
        var client = new TibberClient(
            new ThrowingHttpClientFactory(), secrets, NullLogger<TibberClient>.Instance);

        var result = await client.FetchAsync(CancellationToken.None);

        Assert.Null(result);   // ThrowingHttpClientFactory proves no network call was made
    }
}
