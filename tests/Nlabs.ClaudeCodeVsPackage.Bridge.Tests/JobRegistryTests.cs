using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class JobRegistryTests
{
    [Fact]
    public void Start_returns_a_running_job_with_an_id()
    {
        var registry = new JobRegistry();

        var job = registry.Start("subagent");

        Assert.False(string.IsNullOrEmpty(job.Id));
        Assert.Equal("subagent", job.Kind);
        Assert.Equal(JobState.Running, job.State);
        Assert.Equal(1, registry.RunningCount);
    }

    [Fact]
    public void Completion_is_matched_by_id_not_arrival_order()
    {
        var registry = new JobRegistry();

        // A starts first, B second - but B finishes first (short job).
        var a = registry.Start("long");
        var b = registry.Start("short");

        Assert.True(registry.TryComplete(b.Id, "b-done", out var settledB));
        Assert.True(registry.TryComplete(a.Id, "a-done", out var settledA));

        // Each result landed on its OWN job, despite the out-of-order finish.
        Assert.Equal("b-done", settledB!.Result);
        Assert.Equal("a-done", settledA!.Result);
        Assert.Same(a, settledA);
        Assert.Same(b, settledB);
        Assert.Equal(0, registry.RunningCount);
    }

    [Fact]
    public void Unknown_id_settles_nothing()
    {
        var registry = new JobRegistry();

        var ok = registry.TryComplete("does-not-exist", "x", out var job);

        Assert.False(ok);
        Assert.Null(job);
    }

    [Fact]
    public void Failure_records_the_error_and_clears_running()
    {
        var registry = new JobRegistry();
        var job = registry.Start("subagent");

        Assert.True(registry.TryFail(job.Id, "boom", out var failed));

        Assert.Equal(JobState.Failed, failed!.State);
        Assert.Equal("boom", failed.Result);
        Assert.Equal(0, registry.RunningCount);
    }
}
