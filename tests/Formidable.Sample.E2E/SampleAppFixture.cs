using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Playwright;

namespace Formidable.Sample.E2E;

/// <summary>Binds <see cref="SampleAppFixture"/> to the "e2e" collection, so one pair of sample
/// servers and one browser serve every E2E test class.</summary>
[CollectionDefinition("e2e")]
public sealed class E2ECollection : ICollectionFixture<SampleAppFixture>
{
}

/// <summary>
/// Owns everything the browser tests talk to: both sample servers (the API on 5180, the
/// standalone WASM sample on 5181) and one headless Chromium.
/// </summary>
/// <remarks>
/// <para>
/// Two things are needed before an E2E run. The solution must be built — the fixture starts the
/// servers with <c>--no-build</c>, so a stale or missing output is the usual cause of a start-up
/// timeout. And Chromium must be installed once per machine:
/// <c>pwsh tests/Formidable.Sample.E2E/bin/Debug/net10.0/playwright.ps1 install chromium</c>.
/// </para>
/// <para>
/// Nothing here runs unless FORMIDABLE_E2E=1. A plain <c>dotnet test</c> skips every
/// <see cref="E2EFactAttribute"/> test, and the fixture must not launch servers or demand a
/// browser for a suite that will not run.
/// </para>
/// </remarks>
public sealed class SampleAppFixture : IAsyncLifetime
{
    private const int ApiPort = 5180;
    private const int SamplePort = 5181;
    private const string SampleOrigin = "http://localhost:5181";
    private const int StartupTimeoutSeconds = 60;
    private const int NavTimeoutMilliseconds = 30_000;

    private readonly List<Server> _servers = [];
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary>The headless browser shared by every E2E test.</summary>
    public IBrowser Browser => _browser ?? throw new InvalidOperationException(
        "The E2E browser was never launched — mark browser tests with [E2EFact] so they self-skip unless FORMIDABLE_E2E=1.");

    /// <summary>Opens the sample at <paramref name="path"/> in a fresh browser context and waits
    /// for the app's nav, which is the first chrome a booted WASM app renders.</summary>
    public async Task<IPage> NewPageAsync(string path)
    {
        var context = await Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(SampleOrigin + path);

        // A cold WASM boot downloads and starts the runtime before anything renders, so the
        // first navigation of a run is far slower than the rest.
        await page.WaitForSelectorAsync("nav", new PageWaitForSelectorOptions { Timeout = NavTimeoutMilliseconds });
        return page;
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("FORMIDABLE_E2E") != "1")
        {
            return;
        }

        try
        {
            var repoRoot = FindRepoRoot();
            EnsurePortFree(ApiPort);
            EnsurePortFree(SamplePort);

            // The API first: the sample's server pages call it, and the order keeps the two
            // start-up logs readable when one of them fails.
            var api = StartServer(repoRoot, "samples/Formidable.Sample.Api");
            var sample = StartServer(repoRoot, "samples/Formidable.Sample");

            await WaitForServerAsync(api, ApiPort);
            await WaitForServerAsync(sample, SamplePort);

            _playwright = await Playwright.CreateAsync();
            _browser = await LaunchBrowserAsync(_playwright);
        }
        catch
        {
            await ShutdownAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public Task DisposeAsync() => ShutdownAsync();

    private static async Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright)
    {
        try
        {
            return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                "Chromium could not be launched. Install it once with: " +
                "pwsh tests/Formidable.Sample.E2E/bin/Debug/net10.0/playwright.ps1 install chromium",
                ex);
        }
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.sln").Any())
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No solution file was found above {AppContext.BaseDirectory} — the E2E fixture locates the repository root by walking up to the .sln.");
    }

    private static void EnsurePortFree(int port)
    {
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port))
        {
            throw new InvalidOperationException(
                $"Port {port} is already listening — stop the process using port {port}; the E2E fixture owns both sample ports (5180 API, 5181 sample).");
        }
    }

    private Server StartServer(string repoRoot, string project)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(project);

        var server = new Server(new Process { StartInfo = startInfo }, project, new StringBuilder());
        server.Process.OutputDataReceived += (_, args) => server.Append(args.Data);
        server.Process.ErrorDataReceived += (_, args) => server.Append(args.Data);
        server.Process.Start();
        server.Process.BeginOutputReadLine();
        server.Process.BeginErrorReadLine();
        _servers.Add(server);
        return server;
    }

    private static async Task WaitForServerAsync(Server server, int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(StartupTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            if (server.Process.HasExited)
            {
                throw server.Failure($"exited with code {server.Process.ExitCode} before it listened on port {port}.");
            }

            try
            {
                // Any HTTP answer proves the host is up; a 404 is as good as a 200 here.
                using var response = await client.GetAsync(new Uri($"http://localhost:{port}/"));
                return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(250);
        }

        throw server.Failure(
            $"did not answer on port {port} within {StartupTimeoutSeconds}s — did you build the solution first? The fixture runs with --no-build.");
    }

    // Each leg stands alone and swallows its own failure: teardown must always reach the server
    // kills, or a browser that died mid-run would leave two listening servers behind it.
    private async Task ShutdownAsync()
    {
        try
        {
            if (_browser is not null)
            {
                await _browser.CloseAsync();
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _browser = null;
        }

        try
        {
            _playwright?.Dispose();
        }
        catch (Exception)
        {
        }
        finally
        {
            _playwright = null;
        }

        foreach (var server in _servers)
        {
            await StopServerAsync(server);
        }

        _servers.Clear();
    }

    private static async Task StopServerAsync(Server server)
    {
        try
        {
            // dotnet run launches the real host as a child, so only the whole tree stops listening.
            if (!server.Process.HasExited)
            {
                server.Process.Kill(entireProcessTree: true);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.Process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception)
        {
        }
        finally
        {
            server.Process.Dispose();
        }
    }

    private sealed record Server(Process Process, string Project, StringBuilder Log)
    {
        public void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (Log)
            {
                Log.AppendLine(line);
            }
        }

        public InvalidOperationException Failure(string problem)
        {
            string output;
            lock (Log)
            {
                output = Log.ToString();
            }

            return new InvalidOperationException($"{Project} {problem}{Environment.NewLine}Server output:{Environment.NewLine}{output}");
        }
    }
}
