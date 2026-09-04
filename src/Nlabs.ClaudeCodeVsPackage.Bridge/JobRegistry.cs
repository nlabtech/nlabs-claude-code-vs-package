using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Nlabs.ClaudeCodeVsPackage.Bridge;

public enum JobState
{
    Running,
    Completed,
    Failed
}

/// <summary>A background unit of work (e.g. a subagent) tracked by a stable id.</summary>
public sealed class Job
{
    internal Job(string id, string kind)
    {
        Id = id;
        Kind = kind;
        State = JobState.Running;
    }

    public string Id { get; }
    public string Kind { get; }
    public JobState State { get; internal set; }
    public string? Result { get; internal set; }
}

/// <summary>
/// Correlates background jobs by id.
///
/// Subagents run asynchronously and independently, so their "finished" events do NOT
/// arrive in the order the jobs were started - a short job started second can complete
/// before a long job started first. Matching a completion to its job by ARRIVAL ORDER
/// would attribute results to the wrong job. The only safe key is the id handed out at
/// start; every completion is matched back through it.
/// </summary>
public sealed class JobRegistry
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new ConcurrentDictionary<string, Job>();

    public Job Start(string kind)
    {
        var job = new Job(Guid.NewGuid().ToString("n"), kind);
        _jobs[job.Id] = job;
        return job;
    }

    public bool TryComplete(string id, string result, out Job? job) => TrySettle(id, JobState.Completed, result, out job);

    public bool TryFail(string id, string error, out Job? job) => TrySettle(id, JobState.Failed, error, out job);

    private bool TrySettle(string id, JobState state, string result, out Job? job)
    {
        if (!_jobs.TryGetValue(id, out job))
            return false;

        job.State = state;
        job.Result = result;
        return true;
    }

    public int RunningCount => _jobs.Values.Count(j => j.State == JobState.Running);
}
