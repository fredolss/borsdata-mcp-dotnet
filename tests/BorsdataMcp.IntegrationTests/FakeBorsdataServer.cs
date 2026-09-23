using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BorsdataMcp.IntegrationTests;

/// <summary>
/// A minimal local stand-in for the Börsdata API, started on a random loopback port. The real
/// server under test is pointed at it via the <c>Borsdata__BaseUrl</c> env var, so no real
/// Börsdata API key or network access is needed for integration tests.
/// </summary>
internal sealed class FakeBorsdataServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public string BaseUrl { get; }

    public FakeBorsdataServer()
    {
        BaseUrl = $"http://127.0.0.1:{GetFreePort()}/";
        _listener.Prefixes.Add(BaseUrl);
    }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return; // listener was stopped while a GetContextAsync call was pending
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var path = context.Request.Url!.AbsolutePath;
            var body = Routes.FirstOrDefault(route => path.Contains(route.PathSubstring, StringComparison.OrdinalIgnoreCase)).Json
                       ?? """{"error":"no fake-server fixture for this path"}""";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = 200;
            await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            context.Response.Close();
        }
    }

    // Routed by path substring, in the same spirit as the StubHandler routing already used in
    // tests/BorsdataMcp.Tests/CalendarToolsTests.cs — just served over a real socket here since
    // the server under test runs as a separate OS process.
    private static readonly (string PathSubstring, string Json)[] Routes =
    [
        ("markets", MarketsFixture),
        ("instruments", InstrumentsFixture),
    ];

    internal const string MarketsFixture = """
        {"markets":[
          {"id":1,"name":"Stockholm Large Cap","countryId":1,"isIndex":false,"exchangeName":"Nasdaq Stockholm"},
          {"id":2,"name":"Stockholm Mid Cap","countryId":1,"isIndex":false,"exchangeName":"Nasdaq Stockholm"}
        ]}
        """;

    internal const string InstrumentsFixture = """
        {"instruments":[
          {"insId":3,"name":"Volvo B","ticker":"VOLV B","isin":"SE0000115446","marketId":1,"countryId":1,"sectorId":3,"branchId":21}
        ]}
        """;

    private static int GetFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }
        if (_acceptLoop is not null)
        {
            await Task.WhenAny(_acceptLoop, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        _listener.Close();
        _cts.Dispose();
    }
}
