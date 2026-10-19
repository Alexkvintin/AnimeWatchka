using AnimeApp.Services.Shikimori;
using Xunit;

namespace AnimeApp.Tests;
public class BrowserCallbackTests
{
    [Fact]
    public async Task CallbackRejectsWrongStateAndAcceptsMatchingBrowserReturn()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var callback = new BrowserCallback($"http://127.0.0.1:{port}/callback/");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receive = callback.Receive(timeout.Token);
        using var http = new HttpClient();
        var invalid = await http.GetAsync($"http://127.0.0.1:{port}/callback/?state=wrong&code=untrusted", timeout.Token);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.False(receive.IsCompleted);
        await http.GetAsync($"http://127.0.0.1:{port}/callback/?state={callback.State}&code=test-code", timeout.Token);
        Assert.Equal("test-code", await receive);
        Assert.False(BrowserCallback.Supported("http://example.org:2345/callback/"));
    }
}
