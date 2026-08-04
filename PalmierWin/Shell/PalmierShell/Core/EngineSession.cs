namespace PalmierShell.Core;

/// Owns the Swift engine handle and the render clock. The clock ticks at the
/// timeline's 30 fps on a worker task; all engine calls happen on that loop.
public sealed class EngineSession : IDisposable {
    readonly IntPtr engine;
    readonly PeriodicTimer timer = new(TimeSpan.FromMilliseconds(33));
    readonly CancellationTokenSource cts = new();
    Task? loop;
    int playheadFrame;
    int totalFrames = 60 * 30;

    bool playing;
    int rate = 1;
    // Loop range in frames; -1 when unset. Read on the render thread.
    int loopStart = -1, loopEnd = -1;

    /// Sets or clears the playback loop range (either side null clears it).
    public void SetLoop(int? start, int? end) {
        Volatile.Write(ref loopStart, start ?? -1);
        Volatile.Write(ref loopEnd, end ?? -1);
    }

    /// Fired when playback starts/stops, with the frame it starts from.
    public event Action<bool, int>? PlayingChanged;

    public bool Playing {
        get => playing;
        set {
            if (playing == value) return;
            playing = value;
            if (value) {
                if (rate == 0) rate = 1;
                // A loop only wraps playback inside it; starting outside
                // jumps in. Forward play only — reverse shuttle stays put.
                if (Volatile.Read(ref rate) > 0) {
                    int jumped = TimelineMath.LoopEntryFrame(PlayheadFrame,
                        Volatile.Read(ref loopStart), Volatile.Read(ref loopEnd));
                    if (jumped != PlayheadFrame) {
                        PlayheadFrame = jumped;
                        PlayheadAdvanced?.Invoke(jumped);
                    }
                }
            }
            PlayingChanged?.Invoke(value, PlayheadFrame);
        }
    }

    /// Signed frames per tick — the JKL shuttle. 1 is normal play, 2/4/8 fast
    /// forward, negatives reverse. Zero pauses via `Playing`; the rate itself
    /// never stores zero so resuming keeps the last direction.
    public int Rate {
        get => Volatile.Read(ref rate);
        set {
            if (value == 0) { Playing = false; return; }
            Volatile.Write(ref rate, Math.Clamp(value, -8, 8));
            Playing = true;
        }
    }

    public int PlayheadFrame {
        get => Volatile.Read(ref playheadFrame);
        set => Volatile.Write(ref playheadFrame, Math.Max(0, value));
    }

    public int TotalFrames {
        get => Volatile.Read(ref totalFrames);
        set => Volatile.Write(ref totalFrames, Math.Max(1, value));
    }

    /// Fired from the render loop each time playback advances the playhead.
    public event Action<int>? PlayheadAdvanced;

    /// Fired from the render loop when playback wraps past the end back to the
    /// start. The audio mixer runs its own clock and has to be told: without
    /// this its position keeps counting past the timeline and it plays silence
    /// for every later pass.
    public event Action<int>? PlayheadLooped;

    public EngineSession(IntPtr hwnd) {
        engine = CoreApi.palmier_engine_create(hwnd);
        if (engine == IntPtr.Zero) throw new InvalidOperationException("palmier_engine_create failed");
    }

    /// Attaches the project so render frames composite its timeline.
    public void SetProject(IntPtr project) => CoreApi.palmier_engine_set_project(engine, project);

    /// Draws the manipulation frame around this clip; null clears it.
    public void SetSelection(string? clipId) => CoreApi.palmier_engine_set_selection(engine, clipId);

    public void Start() => loop = Task.Run(RenderLoop);

    /// Consecutive failed frames. Zero whenever the preview is healthy.
    public int StalledFrames { get; private set; }

    async Task RenderLoop() {
        // A frame can fail transiently: a resize race, a busy device, a
        // swapchain being rebuilt. The loop never exits over it. Giving up
        // leaves a preview frozen for the rest of the session with nothing on
        // screen to say why, which is indistinguishable from a crashed engine.
        try {
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false)) {
                if (Playing) {
                    int step = Rate;
                    var (next, wrapped, stop) = TimelineMath.AdvancePlayhead(
                        PlayheadFrame, step, TotalFrames,
                        Volatile.Read(ref loopStart), Volatile.Read(ref loopEnd));
                    PlayheadFrame = next;
                    PlayheadAdvanced?.Invoke(next);
                    if (wrapped) PlayheadLooped?.Invoke(next);
                    if (stop) Playing = false;
                }
                if (CoreApi.palmier_engine_render_frame(engine, PlayheadFrame) == 0) {
                    StalledFrames++;
                    if (StalledFrames == 1 || StalledFrames % 150 == 0)
                        Console.Error.WriteLine($"preview: {StalledFrames} failed frame(s) in a row");
                } else if (StalledFrames > 0) {
                    Console.Error.WriteLine($"preview: recovered after {StalledFrames} failed frame(s)");
                    StalledFrames = 0;
                }
            }
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            // A managed exception here would otherwise be swallowed with the
            // task and the preview would just stop.
            Console.Error.WriteLine($"preview render loop stopped: {ex}");
        }
    }

    int disposed;

    /// Idempotent: both the window teardown and the view model can end up
    /// disposing the session, and a second palmier_engine_destroy on the same
    /// handle is a use-after-free.
    public void Dispose() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cts.Cancel();
        // Long enough to outlast a frame stuck on the engine's own GPU timeout:
        // destroying the engine while the loop is still inside it is a crash on
        // the way out, which reads to the user as "closing broke it".
        try { loop?.Wait(2500); } catch { }
        CoreApi.palmier_engine_destroy(engine);
        cts.Dispose();
        timer.Dispose();
    }
}
