using ModelContextProtocol.Client;

namespace BorsdataMcp.IntegrationTests;

/// <summary>
/// Spawns the real BorsdataMcp server as a subprocess, connected via stdio to a real MCP client,
/// with its outbound Börsdata calls redirected to a local <see cref="FakeBorsdataServer"/>. A
/// fresh instance per test class (via <see cref="IClassFixture{TFixture}"/>) so the server's
/// 7-day reference-data cache never carries state between test classes.
/// </summary>
public sealed class McpServerFixture : IAsyncLifetime
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private FakeBorsdataServer? _fakeServer;
    private McpClient? _client;

    public McpClient Client => _client ?? throw new InvalidOperationException("Fixture not initialized.");

    public async Task InitializeAsync()
    {
        _fakeServer = new FakeBorsdataServer();
        _fakeServer.Start();

        var options = ServerProcess.BuildTransportOptions(_fakeServer.BaseUrl);
        var transport = new StdioClientTransport(options);

        using var connectCts = new CancellationTokenSource(ConnectTimeout);
        _client = await McpClient.CreateAsync(transport, cancellationToken: connectCts.Token);
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch
        {
            // best-effort — the fake server must still be torn down below even if the client
            // (or its underlying process) failed to shut down cleanly.
        }
        finally
        {
            if (_fakeServer is not null)
            {
                await _fakeServer.DisposeAsync();
            }
        }
    }
}
