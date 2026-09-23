using System.Diagnostics;
using ModelContextProtocol.Client;

namespace BorsdataMcp.IntegrationTests;

/// <summary>
/// Resolves how to launch the real BorsdataMcp server under test and builds the launch
/// configuration (env vars pointing it at a <see cref="FakeBorsdataServer"/>) shared by both the
/// MCP-client-based tests and the raw-process tests.
/// </summary>
internal static class ServerProcess
{
    private const string ExecutableOverrideEnvVar = "BORSDATA_MCP_TEST_EXECUTABLE";
    private const string DummyApiKey = "integration-test-dummy-key";

    /// <summary>
    /// The command to launch the server with: either an explicit pre-published executable (set via
    /// <c>BORSDATA_MCP_TEST_EXECUTABLE</c>, e.g. to smoke-test a published .mcpb artifact), or
    /// "dotnet" against the BorsdataMcp.dll that MSBuild already copied next to this test
    /// assembly's own output via the ProjectReference to src/BorsdataMcp/BorsdataMcp.csproj.
    /// </summary>
    public static (string Command, string[] Arguments) ResolveCommand()
    {
        var executableOverride = Environment.GetEnvironmentVariable(ExecutableOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(executableOverride))
        {
            if (!File.Exists(executableOverride))
            {
                throw new InvalidOperationException(
                    $"{ExecutableOverrideEnvVar} was set to '{executableOverride}', but no file exists there.");
            }
            return (executableOverride, []);
        }

        var testDir = Path.GetDirectoryName(typeof(ServerProcess).Assembly.Location)!;
        var dllPath = Path.Combine(testDir, "BorsdataMcp.dll");
        if (!File.Exists(dllPath))
        {
            throw new InvalidOperationException(
                $"Expected the BorsdataMcp server build output at '{dllPath}', copied automatically " +
                "via the ProjectReference to src/BorsdataMcp/BorsdataMcp.csproj. Ensure the solution " +
                "built successfully before running these tests.");
        }
        return ("dotnet", [dllPath]);
    }

    public static StdioClientTransportOptions BuildTransportOptions(string fakeBorsdataBaseUrl)
    {
        var (command, arguments) = ResolveCommand();
        return new StdioClientTransportOptions
        {
            Name = "borsdata-mcp-under-test",
            Command = command,
            Arguments = arguments,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = BuildEnvironmentVariables(fakeBorsdataBaseUrl),
            ShutdownTimeout = TimeSpan.FromSeconds(10),
        };
    }

    public static ProcessStartInfo BuildRawStartInfo(string fakeBorsdataBaseUrl)
    {
        var (command, arguments) = ResolveCommand();
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var (key, value) in BuildEnvironmentVariables(fakeBorsdataBaseUrl))
        {
            psi.Environment[key] = value;
        }
        return psi;
    }

    private static Dictionary<string, string?> BuildEnvironmentVariables(string fakeBorsdataBaseUrl) => new()
    {
        ["Borsdata__BaseUrl"] = fakeBorsdataBaseUrl,
        ["Borsdata__ApiKey"] = DummyApiKey,
    };
}
