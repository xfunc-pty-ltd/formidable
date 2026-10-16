using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Playwright;
using static Formidable.Sample.E2E.SamplePage;
using static Microsoft.Playwright.Assertions;

namespace Formidable.Sample.E2E;

/// <summary>
/// Marks a render-cost measurement: skips unless FORMIDABLE_E2E=1, FORMIDABLE_MEASURE=1 and
/// FORMIDABLE_MEASURE_OUT (the JSON file to write) are all set.
/// </summary>
public sealed class MeasureFactAttribute : FactAttribute
{
    public MeasureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FORMIDABLE_E2E") != "1"
            || Environment.GetEnvironmentVariable("FORMIDABLE_MEASURE") != "1"
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FORMIDABLE_MEASURE_OUT")))
        {
            Skip = "Set FORMIDABLE_E2E=1, FORMIDABLE_MEASURE=1 and FORMIDABLE_MEASURE_OUT=<file.json> to measure render cost.";
        }
    }
}

/// <summary>
/// Reads the browser's own main-thread counters (Chrome DevTools Protocol, Performance.getMetrics)
/// around ten committed keystrokes and one blocked submit on the Workout page, the sample's
/// largest form, on the standalone WebAssembly host, after one uncounted warm-up keystroke and a
/// settle. A measurement, not a test: it asserts nothing about the numbers and writes them to the
/// file named by FORMIDABLE_MEASURE_OUT, with FORMIDABLE_MEASURE_LABEL (a commit, say), the UTC
/// timestamp and this assembly's build configuration recorded beside them. The counters include
/// the work Playwright's own injected scripts do in the page, a constant every run shares.
/// </summary>
[Collection("e2e")]
public sealed class RenderCostMeasurement(SampleAppFixture app)
{
    private const int Keystrokes = 10;
    private const int Runs = 3;

    [MeasureFact]
    public async Task Workout_page_main_thread_time_per_keystroke_and_per_blocked_submit()
    {
        var outputPath = Environment.GetEnvironmentVariable("FORMIDABLE_MEASURE_OUT")!;
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var runs = new List<Run>();
        for (var index = 0; index < Runs; index++)
        {
            await using var session = await app.NewPageAsync("/workout");
            var page = session.Page;
            var description = Field(page, "description");
            await Expect(description).ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });

            var cdp = await page.Context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Performance.enable");

            // Each keystroke is a committed edit: one character, then Tab, which is what the
            // kit's default UpdateOn commits on. The click returns focus for the next one.
            // One uncounted keystroke and a settle come first: a fresh WebAssembly page runs cold
            // code on its first edits, and the first render's whole-form re-check lands in the
            // same second. The window below must count neither.
            await description.ClickAsync();
            await page.Keyboard.TypeAsync("a");
            await TabAsync(page);
            await description.ClickAsync();
            await page.WaitForTimeoutAsync(1000);
            var start = await ReadAsync(cdp);
            for (var key = 0; key < Keystrokes; key++)
            {
                await page.Keyboard.TypeAsync("a");
                await TabAsync(page);
                await description.ClickAsync();
            }

            // Let the last check end before reading.
            await page.WaitForTimeoutAsync(500);
            var afterKeys = await ReadAsync(cdp);
            Assert.True(afterKeys.Task > start.Task, "the browser's counters did not move");

            await page.GetByRole(AriaRole.Button, new() { Name = "Submit registration", Exact = true }).ClickAsync();
            await Expect(Summary(page)).ToBeVisibleAsync(new() { Timeout = AsyncTimeoutMs });
            await page.WaitForTimeoutAsync(700);
            var afterSubmit = await ReadAsync(cdp);

            runs.Add(new Run(
                index,
                TaskMsPerKeystroke: (afterKeys.Task - start.Task) * 1000 / Keystrokes,
                ScriptMsPerKeystroke: (afterKeys.Script - start.Script) * 1000 / Keystrokes,
                LayoutsPerKeystroke: (afterKeys.Layouts - start.Layouts) / Keystrokes,
                TaskMsForSubmit: (afterSubmit.Task - afterKeys.Task) * 1000,
                ScriptMsForSubmit: (afterSubmit.Script - afterKeys.Script) * 1000,
                LayoutsForSubmit: afterSubmit.Layouts - afterKeys.Layouts));
        }

        var result = new
        {
            label = Environment.GetEnvironmentVariable("FORMIDABLE_MEASURE_LABEL") ?? string.Empty,
            timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            // This assembly's configuration. The fixture's servers run their Debug output whatever
            // it says (dotnet run with no configuration), so the sample measured is Debug either way.
            configuration = typeof(RenderCostMeasurement).Assembly
                .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
            keystrokes = Keystrokes,
            runs,
            median = new Run(
                -1,
                Median(runs.Select(r => r.TaskMsPerKeystroke)),
                Median(runs.Select(r => r.ScriptMsPerKeystroke)),
                Median(runs.Select(r => r.LayoutsPerKeystroke)),
                Median(runs.Select(r => r.TaskMsForSubmit)),
                Median(runs.Select(r => r.ScriptMsForSubmit)),
                Median(runs.Select(r => r.LayoutsForSubmit))),
        };

        // One casing throughout: the record's members serialise in camel case beside the keys above.
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
    }

    private static async Task<(double Task, double Script, double Layouts)> ReadAsync(ICDPSession cdp)
    {
        var metrics = await cdp.SendAsync("Performance.getMetrics");
        double Get(string name) => metrics!.Value.GetProperty("metrics").EnumerateArray()
            .First(metric => metric.GetProperty("name").GetString() == name)
            .GetProperty("value").GetDouble();
        return (Get("TaskDuration"), Get("ScriptDuration"), Get("LayoutCount"));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private sealed record Run(
        int Index,
        double TaskMsPerKeystroke,
        double ScriptMsPerKeystroke,
        double LayoutsPerKeystroke,
        double TaskMsForSubmit,
        double ScriptMsForSubmit,
        double LayoutsForSubmit);
}
