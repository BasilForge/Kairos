using Kairos.App;

namespace Kairos.Tests;

/// <summary>
/// The settings window asks the engine for its status twice a second without holding a thread while the engine
/// answers (the blocked thread cost about 7 ms of CPU per question; the question itself 0.3 ms).
/// </summary>
public static class PipeClientTests
{
    [Test]
    public static void An_async_question_gets_the_engines_answer()
    {
        string name = "kairos-test-" + Guid.NewGuid().ToString("N");
        using var server = new PipeServer(command => "ok:" + command, () => [], name);
        for (int i = 0; i < 3; i++)
            Assert.Equal("ok:status", PipeClient.SendAsync("status", 2000, name).GetAwaiter().GetResult(), $"answer {i}");
    }

    /// <summary>The engine knows which program asked it something (its log names who asked it to exit).</summary>
    [Test]
    public static void The_engine_knows_which_program_asked()
    {
        string name = "kairos-test-" + Guid.NewGuid().ToString("N");
        int asker = 0;
        string? asked = null;
        using var server = new PipeServer(command => "ok:" + command, () => [], name, onRequest: (request, pid) => (asked, asker) = (request, pid));
        PipeClient.SendAsync("exit", 2000, name).GetAwaiter().GetResult();
        Assert.Equal("exit", asked, "the request");
        Assert.Equal(Environment.ProcessId, asker, "this process asked");
    }

    [Test]
    public static void With_no_engine_the_answer_is_nothing_soon()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string? reply = PipeClient.SendAsync("status", 300, "kairos-test-nobody-" + Guid.NewGuid().ToString("N")).GetAwaiter().GetResult();
        Assert.Equal(null, reply, "no engine");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"gave up in time ({watch.Elapsed.TotalMilliseconds:0} ms)");
    }

    [Test]
    public static void Questions_cost_little_cpu()
    {
        string name = "kairos-test-" + Guid.NewGuid().ToString("N");
        using var server = new PipeServer(command => "ok:" + command, () => [], name);
        PipeClient.SendAsync("status", 2000, name).GetAwaiter().GetResult(); // warm up
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var before = self.TotalProcessorTime;
        for (int i = 0; i < 40; i++)
            PipeClient.SendAsync("status", 2000, name).GetAwaiter().GetResult();
        self.Refresh();
        double perQuestion = (self.TotalProcessorTime - before).TotalMilliseconds / 40;
        // Both ends run in this process here (the engine's answer too): well under the 7 ms one question cost before.
        Assert.True(perQuestion < 3, $"{perQuestion:0.00} ms of CPU per question, both ends");
    }
}
