using System.Diagnostics;
using System.Text.Json;

namespace BorsdataMcp.IntegrationTests;

/// <summary>
/// Requirements 6 and 7 need direct control over the server's OS process and raw stdout bytes —
/// the MCP client SDK doesn't expose the underlying <see cref="Process"/> or its exit code, and
/// routing through it would only prove stdout framing indirectly (a corrupt frame just surfaces as
/// an opaque connect failure). These tests instead speak raw JSON-RPC over stdio directly.
/// </summary>
public sealed class ProcessLifecycleTests
{
    private const string InitializeRequest = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"process-lifecycle-test","version":"1.0"}}}
        """;

    private const string InitializedNotification = """
        {"jsonrpc":"2.0","method":"notifications/initialized"}
        """;

    private const string ListToolsRequest = """
        {"jsonrpc":"2.0","id":2,"method":"tools/list"}
        """;

    // 6. The server process exits correctly when the client disconnects / stdin is closed.
    [Fact]
    public async Task Process_ExitsCleanly_WhenStdinCloses()
    {
        await using var fakeServer = new FakeBorsdataServer();
        fakeServer.Start();

        using var process = Process.Start(ServerProcess.BuildRawStartInfo(fakeServer.BaseUrl))!;
        try
        {
            using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WriteLineAsync(process, InitializeRequest, handshakeCts.Token);
            await ReadJsonLineAsync(process, handshakeCts.Token); // initialize response
            await WriteLineAsync(process, InitializedNotification, handshakeCts.Token);

            process.StandardInput.Close();

            var exited = process.WaitForExit(TimeSpan.FromSeconds(10));
            Assert.True(exited, "Server process did not exit within the timeout after stdin closed.");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    // 7. The server never writes anything to stdout that isn't valid, framed JSON-RPC — a
    // regression here (e.g. an accidental Console.WriteLine, or a logger misconfigured to write to
    // stdout instead of stderr) would corrupt the protocol stream for every real MCP client.
    [Fact]
    public async Task Stdout_ContainsOnlyValidJsonRpcFrames()
    {
        await using var fakeServer = new FakeBorsdataServer();
        fakeServer.Start();

        using var process = Process.Start(ServerProcess.BuildRawStartInfo(fakeServer.BaseUrl))!;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            await WriteLineAsync(process, InitializeRequest, cts.Token);
            var initializeResponse = await ReadJsonLineAsync(process, cts.Token);
            Assert.True(initializeResponse.RootElement.TryGetProperty("result", out _));

            await WriteLineAsync(process, InitializedNotification, cts.Token);
            await WriteLineAsync(process, ListToolsRequest, cts.Token);
            var listToolsResponse = await ReadJsonLineAsync(process, cts.Token);
            Assert.True(listToolsResponse.RootElement.TryGetProperty("result", out var result));
            Assert.True(result.TryGetProperty("tools", out _));
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
                // ignore — process may already have exited
            }
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task WriteLineAsync(Process process, string json, CancellationToken ct)
    {
        await process.StandardInput.WriteLineAsync(json.ReplaceLineEndings(""));
        await process.StandardInput.FlushAsync(ct);
    }

    // Reads exactly one line and asserts it parses as JSON — the actual "stdout is clean" check.
    // Any stray non-JSON-RPC text written to stdout (a stray log line, a plain Console.WriteLine)
    // fails this with a JsonException instead of silently being skipped.
    private static async Task<JsonDocument> ReadJsonLineAsync(Process process, CancellationToken ct)
    {
        var line = await process.StandardOutput.ReadLineAsync(ct);
        Assert.False(string.IsNullOrEmpty(line), "Expected a JSON-RPC line on stdout but got none before the timeout.");
        return JsonDocument.Parse(line!);
    }
}
