namespace PalmierShell.Core;

public enum ExportJobState { Queued, Running, Done, Failed, Cancelled }

/// One queued export. The timeline is captured when the job starts, not when
/// it is queued — edits made while a job waits are included in its output.
public sealed class ExportJob {
    internal ExportJob(int id, string outputPath) {
        Id = id;
        OutputPath = outputPath;
    }

    public int Id { get; }
    public string OutputPath { get; }
    public ExportJobState State { get; internal set; } = ExportJobState.Queued;
    public int Progress { get; internal set; }
    public string? Error { get; internal set; }
    internal IntPtr Handle;
}

/// The encode boundary the queue drives. The app uses CoreExportRunner; tests
/// substitute a fake so queue logic never touches the native exporter.
public interface IExportRunner {
    IntPtr Start(IntPtr project, string outputPath);
    int Poll(IntPtr handle);  // 0–100 running, 101 done, -1 failed, -2 cancelled
    string Error(IntPtr handle);
    void Cancel(IntPtr handle);
    void Destroy(IntPtr handle);
}

public sealed class CoreExportRunner : IExportRunner {
    public IntPtr Start(IntPtr project, string outputPath) => CoreApi.palmier_export_start(project, outputPath);
    public int Poll(IntPtr handle) => CoreApi.palmier_export_status(handle);
    public string Error(IntPtr handle) => CoreApi.GetExportError(handle);
    public void Cancel(IntPtr handle) => CoreApi.palmier_export_cancel(handle);
    public void Destroy(IntPtr handle) => CoreApi.palmier_export_destroy(handle);
}

/// Sequential export queue: one encode at a time, in enqueue order. Driven by
/// Tick — a UI timer in the app, a direct call in tests — so the queue itself
/// has no threading or dispatcher state.
public sealed class ExportQueue {
    readonly IExportRunner runner;
    readonly Func<IntPtr> project;
    readonly List<ExportJob> jobs = new();
    int nextId = 1;

    public ExportQueue(IExportRunner runner, Func<IntPtr> project) {
        this.runner = runner;
        this.project = project;
    }

    public IReadOnlyList<ExportJob> Jobs => jobs;
    public bool HasActiveWork => jobs.Any(j => j.State is ExportJobState.Queued or ExportJobState.Running);

    public ExportJob Enqueue(string outputPath) {
        var job = new ExportJob(nextId++, outputPath);
        jobs.Add(job);
        return job;
    }

    /// Removes a queued job before it starts. Running jobs must be cancelled.
    public bool Remove(ExportJob job) =>
        job.State == ExportJobState.Queued && jobs.Remove(job);

    /// Cancels a queued job immediately, or asks the encoder to stop a running
    /// one — the running job flips to Cancelled on a later Tick.
    public void Cancel(ExportJob job) {
        switch (job.State) {
            case ExportJobState.Queued:
                job.State = ExportJobState.Cancelled;
                break;
            case ExportJobState.Running:
                runner.Cancel(job.Handle);
                break;
        }
    }

    /// Polls the running job, settles it on a terminal status, and starts the
    /// next queued job. A failed or cancelled job never blocks the queue.
    public void Tick() {
        if (jobs.FirstOrDefault(j => j.State == ExportJobState.Running) is { } running) {
            int status = runner.Poll(running.Handle);
            if (status is >= 0 and <= 100) {
                running.Progress = status;
                return;
            }
            if (status == 101) {
                running.Progress = 100;
                running.State = ExportJobState.Done;
                SessionLog.Event("export", $"done: {Path.GetFileName(running.OutputPath)}");
            } else if (status == -2) {
                running.State = ExportJobState.Cancelled;
            } else {
                running.State = ExportJobState.Failed;
                running.Error = runner.Error(running.Handle);
                SessionLog.Event("export", $"failed: {Path.GetFileName(running.OutputPath)} — {running.Error}");
            }
            runner.Destroy(running.Handle);
            running.Handle = IntPtr.Zero;
        }
        StartNext();
    }

    void StartNext() {
        while (jobs.FirstOrDefault(j => j.State == ExportJobState.Queued) is { } next) {
            IntPtr handle = runner.Start(project(), next.OutputPath);
            if (handle == IntPtr.Zero) {
                next.State = ExportJobState.Failed;
                next.Error = "Export could not start (empty timeline or encoder failure).";
                continue;
            }
            next.Handle = handle;
            next.State = ExportJobState.Running;
            return;
        }
    }
}
