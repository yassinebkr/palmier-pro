using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class ExportQueueTests {
    /// Scripted runner: each started handle gets the next poll plan; a plan
    /// that runs out reports done, and a cancelled handle reports -2.
    sealed class FakeRunner : IExportRunner {
        public readonly List<string> Started = new();
        public readonly List<IntPtr> Cancelled = new();
        public readonly List<IntPtr> Destroyed = new();
        public readonly Queue<Queue<int>> PollPlans = new();
        readonly Dictionary<IntPtr, Queue<int>> plans = new();
        public int StartFailures;
        int nextHandle = 1;

        public IntPtr Start(IntPtr project, string outputPath) {
            if (StartFailures > 0) {
                StartFailures--;
                return IntPtr.Zero;
            }
            var handle = new IntPtr(nextHandle++);
            Started.Add(outputPath);
            plans[handle] = PollPlans.Count > 0 ? PollPlans.Dequeue() : new Queue<int>();
            return handle;
        }

        public int Poll(IntPtr handle) {
            if (Cancelled.Contains(handle)) return -2;
            var plan = plans[handle];
            return plan.Count > 0 ? plan.Dequeue() : 101;
        }

        public string Error(IntPtr handle) => "fake encode failure";
        public void Cancel(IntPtr handle) => Cancelled.Add(handle);
        public void Destroy(IntPtr handle) => Destroyed.Add(handle);
    }

    static (ExportQueue queue, FakeRunner runner) MakeQueue() {
        var runner = new FakeRunner();
        return (new ExportQueue(runner, () => new IntPtr(7)), runner);
    }

    [Fact]
    public void QueuedJobsRunSequentiallyInEnqueueOrder() {
        var (queue, runner) = MakeQueue();
        runner.PollPlans.Enqueue(new Queue<int>([40, 101]));
        var first = queue.Enqueue("a.mp4");
        var second = queue.Enqueue("b.mp4");

        queue.Tick();
        Assert.Equal(ExportJobState.Running, first.State);
        Assert.Equal(ExportJobState.Queued, second.State);

        queue.Tick();
        Assert.Equal(40, first.Progress);
        Assert.Equal(ExportJobState.Queued, second.State);
        Assert.Single(runner.Started);

        queue.Tick();
        Assert.Equal(ExportJobState.Done, first.State);
        Assert.Equal(ExportJobState.Running, second.State);
        Assert.Equal(["a.mp4", "b.mp4"], runner.Started);
    }

    [Fact]
    public void TickIsANoOpOnAnEmptyQueue() {
        var (queue, runner) = MakeQueue();
        queue.Tick();
        Assert.Empty(runner.Started);
        Assert.False(queue.HasActiveWork);
    }

    [Fact]
    public void RemovingAQueuedJobPreventsItFromStarting() {
        var (queue, runner) = MakeQueue();
        queue.Enqueue("a.mp4");
        var second = queue.Enqueue("b.mp4");
        queue.Tick();

        Assert.True(queue.Remove(second));
        Assert.DoesNotContain(second, queue.Jobs);

        queue.Tick();  // first finishes (unscripted → done)
        Assert.Single(runner.Started);
        Assert.False(queue.HasActiveWork);
    }

    [Fact]
    public void RemoveRefusesARunningJob() {
        var (queue, _) = MakeQueue();
        var job = queue.Enqueue("a.mp4");
        queue.Tick();
        Assert.False(queue.Remove(job));
        Assert.Equal(ExportJobState.Running, job.State);
    }

    [Fact]
    public void CancellingAQueuedJobMarksItCancelledWithoutStartingIt() {
        var (queue, runner) = MakeQueue();
        queue.Enqueue("a.mp4");
        var second = queue.Enqueue("b.mp4");
        queue.Tick();

        queue.Cancel(second);
        Assert.Equal(ExportJobState.Cancelled, second.State);

        queue.Tick();
        Assert.Single(runner.Started);
        Assert.False(queue.HasActiveWork);
    }

    [Fact]
    public void CancellingARunningJobSettlesAsCancelledAndStartsTheNextJob() {
        var (queue, runner) = MakeQueue();
        runner.PollPlans.Enqueue(new Queue<int>([10, 10, 10]));  // would run forever
        var first = queue.Enqueue("a.mp4");
        var second = queue.Enqueue("b.mp4");
        queue.Tick();
        queue.Tick();

        queue.Cancel(first);
        Assert.Equal(ExportJobState.Running, first.State);  // settles on the next tick

        queue.Tick();
        Assert.Equal(ExportJobState.Cancelled, first.State);
        Assert.Equal(ExportJobState.Running, second.State);
        Assert.Single(runner.Destroyed);
    }

    [Fact]
    public void AFailedJobKeepsItsErrorAndDoesNotStopTheQueue() {
        var (queue, runner) = MakeQueue();
        runner.PollPlans.Enqueue(new Queue<int>([-1]));
        var first = queue.Enqueue("a.mp4");
        var second = queue.Enqueue("b.mp4");

        queue.Tick();
        queue.Tick();

        Assert.Equal(ExportJobState.Failed, first.State);
        Assert.Equal("fake encode failure", first.Error);
        Assert.Equal(ExportJobState.Running, second.State);
    }

    [Fact]
    public void AStartFailureMarksTheJobFailedAndContinuesWithTheNext() {
        var (queue, runner) = MakeQueue();
        runner.StartFailures = 1;
        var first = queue.Enqueue("a.mp4");
        var second = queue.Enqueue("b.mp4");

        queue.Tick();

        Assert.Equal(ExportJobState.Failed, first.State);
        Assert.NotNull(first.Error);
        Assert.Equal(ExportJobState.Running, second.State);
        Assert.Equal(["b.mp4"], runner.Started);
    }

    [Fact]
    public void HasActiveWorkTracksOnlyQueuedAndRunningJobs() {
        var (queue, _) = MakeQueue();
        var job = queue.Enqueue("a.mp4");
        Assert.True(queue.HasActiveWork);

        queue.Tick();
        Assert.True(queue.HasActiveWork);

        queue.Tick();
        Assert.Equal(ExportJobState.Done, job.State);
        Assert.False(queue.HasActiveWork);
    }
}
