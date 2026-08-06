namespace PalmierShell.Core;

/// One meter channel: instantaneous engine peaks in, display level out, with
/// upstream's ballistics — 24 dB/s level decay, 18 dB/s peak decay, 1.5 s
/// peak hold, clip latch, −60…0 dB window.
public sealed class TrackMeters {
    public double LevelDb { get; private set; } = -60;
    public double PeakDb { get; private set; } = -60;
    public bool Clipped { get; private set; }

    double holdSeconds;

    public void Reset() { LevelDb = -60; PeakDb = -60; Clipped = false; holdSeconds = 0; }

    static double ToDb(float peak) => peak <= 0 ? -60 : Math.Max(-60, 20 * Math.Log10(peak));

    public void Tick(float enginePeak, double dtSeconds) {
        double sample = ToDb(enginePeak);
        // Level: rise instantly, decay 24 dB/s.
        LevelDb = Math.Clamp(Math.Max(sample, LevelDb - 24 * dtSeconds), -60, 0);
        // Peak: hold 1.5 s, then decay 18 dB/s; clip latches at ≥ 0 dB.
        if (sample >= PeakDb) { PeakDb = sample; holdSeconds = 1.5; }
        else if (holdSeconds > 0) holdSeconds -= dtSeconds;
        else PeakDb = Math.Max(-60, PeakDb - 18 * dtSeconds);
        PeakDb = Math.Clamp(PeakDb, -60, 0);
        if (sample >= 0) Clipped = true;
    }
}
