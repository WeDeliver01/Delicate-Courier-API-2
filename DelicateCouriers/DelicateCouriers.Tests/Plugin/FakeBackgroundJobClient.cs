using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// Minimal in-memory <see cref="IBackgroundJobClient"/> stand-in. Hangfire's
/// real client requires a configured storage backend; tests only care that
/// the controller/service ENQUEUED the expected job — they don't need it to
/// actually run.
/// </summary>
public sealed class FakeBackgroundJobClient : IBackgroundJobClient
{
    public List<(Job Job, IState State)> Enqueued { get; } = new();

    public string Create(Job job, IState state)
    {
        Enqueued.Add((job, state));
        return Guid.NewGuid().ToString();
    }

    public bool ChangeState(string jobId, IState state, string expectedState) => true;
}
