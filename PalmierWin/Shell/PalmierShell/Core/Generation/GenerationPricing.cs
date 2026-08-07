namespace PalmierShell.Core.Generation;

/// What a run is expected to cost, and on what basis. `Approximate` marks a
/// rate we inferred rather than one the provider publishes per endpoint.
public sealed record PriceEstimate(decimal Amount, string Basis, bool Approximate) {
    public string Text => $"≈ ${Amount:0.00}";
}

/// Published per-second rates for the models we curate, so the composer can
/// show the cost before the money is spent.
///
/// Rates are read off provider pricing pages and go stale — every estimate
/// carries the date it was taken and is labelled as an estimate. A model with
/// no rate here reports that rather than guessing: an invented number is worse
/// than none when it is standing in for a bill.
public static class GenerationPricing {
    /// When these rates were last checked against the providers' pages.
    public const string CheckedOn = "August 2026";

    /// Per-second rates in USD, keyed by provider and model id.
    /// fal publishes one rate per tier for Seedance 2.0, independent of
    /// resolution; 480p can bill under it, so the estimate is a ceiling.
    static readonly Dictionary<(string Provider, string Model), decimal> PerSecond = new() {
        [("fal", "bytedance/seedance-2.0/text-to-video")] = 0.3034m,
        [("fal", "bytedance/seedance-2.0/image-to-video")] = 0.3034m,
        [("fal", "bytedance/seedance-2.0/reference-to-video")] = 0.3034m,
        [("fal", "bytedance/seedance-2.0/fast/text-to-video")] = 0.2419m,
        [("fal", "bytedance/seedance-2.0/fast/image-to-video")] = 0.2419m,
        [("fal", "bytedance/seedance-2.0/fast/reference-to-video")] = 0.2419m,
    };

    /// Published per-second rates that vary by resolution. FLUX.3 publishes
    /// per-resolution rates on both providers ($0.17/$0.29 per second at
    /// 720p/1080p; extend-video bills higher at $0.41/$0.53 on fal).
    static readonly Dictionary<(string Provider, string Model, string Resolution), decimal> PerSecondByResolution = new() {
        [("replicate", "black-forest-labs/flux-3", "720p")] = 0.17m,
        [("replicate", "black-forest-labs/flux-3", "1080p")] = 0.29m,
        [("fal", "blackforestlabs/flux-3/first-last-frame-to-video", "720p")] = 0.17m,
        [("fal", "blackforestlabs/flux-3/first-last-frame-to-video", "1080p")] = 0.29m,
        [("fal", "blackforestlabs/flux-3/text-to-video", "720p")] = 0.17m,
        [("fal", "blackforestlabs/flux-3/text-to-video", "1080p")] = 0.29m,
        [("fal", "blackforestlabs/flux-3/extend-video", "720p")] = 0.41m,
        [("fal", "blackforestlabs/flux-3/extend-video", "1080p")] = 0.53m,
    };

    /// Rates we only have from secondary sources; shown as approximate.
    /// Kling 3.0: Replicate's page publishes nothing, so these are the
    /// pass-through rates reported for the Kling API (standard ≈ 0.28/s,
    /// pro ≈ 0.392/s) — a ceiling to sanity-check the first real bill against.
    static readonly Dictionary<(string Provider, string Model, string Resolution), decimal> Approximate = new() {
        [("replicate", "bytedance/seedance-2.0", "720p")] = 0.25m,
        [("replicate", "bytedance/seedance-2.0", "480p")] = 0.09m,
        [("replicate", "kwaivgi/kling-v3-video", "720p")] = 0.28m,
        [("replicate", "kwaivgi/kling-v3-video", "1080p")] = 0.392m,
    };

    /// The estimated cost of one run, or null when we have no rate for the
    /// model. `seconds` is the requested clip length.
    public static PriceEstimate? For(string providerId, string modelId, int seconds, string resolution) {
        if (seconds <= 0) return null;
        string model = modelId.Trim();

        if (PerSecond.TryGetValue((providerId, model), out decimal rate))
            return new PriceEstimate(Round(rate * seconds),
                $"{rate:0.####}/s · {seconds}s at {resolution} · {CheckedOn}", Approximate: false);

        if (PerSecondByResolution.TryGetValue((providerId, model, resolution), out decimal byResolution))
            return new PriceEstimate(Round(byResolution * seconds),
                $"{byResolution:0.####}/s · {seconds}s at {resolution} · {CheckedOn}", Approximate: false);

        if (Approximate.TryGetValue((providerId, model, resolution), out decimal loose))
            return new PriceEstimate(Round(loose * seconds),
                $"about {loose:0.####}/s · {seconds}s at {resolution} · {CheckedOn}", Approximate: true);

        return null;
    }

    static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
