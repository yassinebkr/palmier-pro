using System.Text;

namespace PalmierShell.Core.Generation;

/// What the composer knows about the shot being asked for. The style uses it
/// to say things the user should not have to type twice.
public sealed record PromptContext(bool HasFirstFrame, bool HasLastFrame,
                                   bool IsTransition, int Seconds) {
    public static readonly PromptContext Plain = new(false, false, false, 5);

    /// How the endpoint receives the stills. On a reference endpoint they are
    /// nameless until the prompt says which is which, so the wording differs.
    public FrameInput Frames { get; init; } = FrameInput.FirstLast;

    /// User-attached reference media riding with the request. The prompt has
    /// to address them by label or the model may not use them at all.
    public int ImageReferences { get; init; }
    public int VideoReferences { get; init; }
}

/// Per-model prompt engineering. Each model family reads prompts differently,
/// so the text the user writes is adapted to the model actually selected
/// rather than sent raw and hoped for.
///
/// A style only ever *adds* direction the model is known to reward — it never
/// rewrites the user's own words, and the composer shows the result before it
/// is sent.
public interface IPromptStyle {
    string Name { get; }
    bool Matches(string modelId);
    /// The prompt as it will be sent.
    string Build(string prompt, PromptContext context);
    /// For endpoints with a dedicated negative-prompt field; null when the
    /// model has none or exclusions belong inside the prompt instead.
    string? Negative(PromptContext context) => null;
    /// Short guidance for this model, shown next to the composer.
    IReadOnlyList<string> Notes { get; }
    /// Warnings about the prompt as written — empty when it looks fine.
    IReadOnlyList<string> Review(string prompt, PromptContext context);
}

public static class PromptStyles {
    public static IReadOnlyList<IPromptStyle> All { get; } =
        [new SeedanceTwoStyle(), new KlingThreeStyle()];

    static readonly IPromptStyle Fallback = new PassthroughStyle();

    /// The style for a model id, or a pass-through when none matches — a model
    /// we have not studied must not get another model's conventions.
    public static IPromptStyle For(string modelId) =>
        All.FirstOrDefault(s => s.Matches(modelId)) ?? Fallback;
}

/// No adaptation: the prompt goes as written.
sealed class PassthroughStyle : IPromptStyle {
    public string Name => "Plain";
    public bool Matches(string modelId) => false;
    public string Build(string prompt, PromptContext context) => prompt.Trim();
    public IReadOnlyList<string> Notes { get; } = [
        "No model-specific tuning for this one yet — the prompt is sent as written.",
    ];
    public IReadOnlyList<string> Review(string prompt, PromptContext context) => [];
}

/// ByteDance Seedance 2.0 (and the Fast tier).
///
/// Follows Volcengine's own prompt guide: the six-slot formula
/// `[Subject], [Action], in [Environment], camera [Camera], style [Style],
/// avoid [Constraints]`, a 60–100 word target, and exclusions expressed as an
/// `avoid` clause — the model does have a negative channel, it just lives in
/// the prompt rather than in a separate field.
///
/// What this class deliberately does *not* do is pad. The official guide lists
/// overloaded prompts and quality-tag suffixes ("4K, ultra HD, no blur") as
/// anti-patterns: past ~100 words the added text competes with the user's own
/// direction instead of supporting it. So the additions here are short,
/// conditional, and never contradict what the user asked for.
/// See docs/seedance-prompt-engineering.md for the sources behind each rule.
public sealed class SeedanceTwoStyle : IPromptStyle {
    public string Name => "Seedance 2.0";

    /// Hard limit the model enforces.
    public const int MaxCharacters = 3000;
    /// The official target band. Past the ceiling, directions start competing.
    public const int MinWords = 30, MaxWords = 100;

    public bool Matches(string modelId) =>
        modelId.Contains("seedance-2", StringComparison.OrdinalIgnoreCase);

    /// The guide's standard exclusion set. All four are technical artefacts,
    /// so they can be added to any prompt without fighting its content — which
    /// is the whole test for anything appended automatically.
    const string Avoid = "Avoid jitter, bent limbs, temporal flicker, identity drift.";

    /// Travelling between two stills: say it is one shot, not a cut. Kept to
    /// one sentence — the stills already say where it starts and ends.
    const string TransitionDirection =
        "Start on the first frame and land on the last, in one continuous shot.";

    /// On a reference endpoint the stills arrive as an unlabelled array, so
    /// the prompt has to assign the roles. Order matches what the provider
    /// sends: first frame, then last.
    const string ReferenceRoles = "[Image1] is the first frame, [Image2] the last. ";

    /// The stills carry the look — grain, palette, lens, light. Pointing at
    /// them is the guide's "style by visual reference", and it is what stops
    /// the model rendering a correct move in its own house style.
    const string MatchReferenceLook =
        "Match the reference frames for lighting, colour grade, lens and grain.";

    public string Build(string prompt, PromptContext context) {
        string body = prompt.Trim().TrimEnd('.', ' ');
        if (body.Length == 0) return "";

        bool travelling = context.HasFirstFrame && context.HasLastFrame;
        var built = new StringBuilder();
        if (travelling && context.Frames == FrameInput.References)
            built.Append(ReferenceRoles);
        built.Append(body).Append('.');
        if (context.IsTransition || travelling)
            built.Append(' ').Append(TransitionDirection);
        if (context.HasFirstFrame || context.HasLastFrame)
            built.Append(' ').Append(MatchReferenceLook);
        built.Append(' ').Append(Avoid);

        string result = built.ToString();
        return result.Length <= MaxCharacters ? result : result[..MaxCharacters];
    }

    public IReadOnlyList<string> Notes { get; } = [
        "Subject, action, environment, camera, style, avoid — in that order.",
        "Name a camera move or you get a static or random one: slow dolly in, track left, " +
            "orbit, crane down, handheld, locked off. One move only.",
        "One action per shot. Describe camera motion and subject motion separately.",
        $"One or two people. Identity drifts past {MaxReliableSubjects}; keep extras in the " +
            "background.",
        $"{MinWords}–{MaxWords} words. Past {MaxWords} the instructions start competing " +
            "and the model drops some of them.",
        "Skip \"4K\", \"cinematic\", \"epic\", \"amazing\" — vague quality words measurably " +
            "hurt. Concrete references work: \"35mm film tone\", \"golden hour\", \"rim light\".",
        "Style comes from the reference frames, not from words. Describe the motion; let the " +
            "stills carry the look.",
    ];

    public IReadOnlyList<string> Review(string prompt, PromptContext context) {
        var warnings = new List<string>();
        if (prompt.Trim().Length == 0) return warnings;

        int words = PromptText.WordCount(prompt);
        if (words < 8)
            warnings.Add("Very short — name the subject, the action and the camera move.");
        if (!PromptText.NamesACameraMove(prompt))
            warnings.Add("No camera move named. Without one the shot comes out static or " +
                         "drifts at random — try \"slow dolly in\" or \"track left\".");
        if (PromptText.WordCount(Build(prompt, context)) > MaxWords)
            warnings.Add($"Over {MaxWords} words once assembled; directions start competing " +
                         "at that length. Cut adjectives before content.");

        // Reference media are nameless until the prompt addresses them; an
        // unmentioned reference is money spent on an input the model may
        // never look at.
        if (context.ImageReferences > 0 && !prompt.Contains("[Image", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"{context.ImageReferences} reference image(s) attached but the prompt " +
                         "never says [Image1]. Name each one, e.g. \"the boy from [Image1]\".");
        if (context.VideoReferences > 0 && !prompt.Contains("[Video", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"{context.VideoReferences} reference video(s) attached but the prompt " +
                         "never says [Video1]. Name what to take from it, e.g. \"match the " +
                         "motion and grade of [Video1]\".");

        int people = SubjectCount(prompt);
        if (people > MaxReliableSubjects)
            warnings.Add($"{people} people in shot — Seedance holds one or two identities " +
                         "before faces start drifting. Keep the extras in the background " +
                         "(silhouetted, out of focus) or cut them.");

        var vague = PromptText.VagueWords.Where(w => PromptText.Mentions(prompt, w)).ToList();
        if (vague.Count > 0)
            warnings.Add($"\"{string.Join("\", \"", vague)}\" — vague quality words degrade " +
                         "the result. Say what it should look like instead.");
        return warnings;
    }

    /// Identities the model keeps stable. Past this, faces drift between frames.
    public const int MaxReliableSubjects = 2;

    /// Roughly how many people a prompt puts on screen. It only has to be
    /// right about "more than two", so it counts conservatively: an explicit
    /// number wins, a bare plural counts as two, and a definite noun ("the
    /// woman") counts once however often it is repeated — that is a
    /// back-reference to someone already on screen, not another person.
    public static int SubjectCount(string prompt) {
        string[] words = PromptText.Punctuation.Replace(prompt.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var countedOnce = new HashSet<string>();
        int total = 0;

        for (int i = 0; i < words.Length; i++) {
            bool plural = PluralPeople.Contains(words[i]);
            if (!plural && !SingularPeople.Contains(words[i])) continue;

            // Look back past adjectives ("young", "male") for a quantifier.
            int? number = null;
            bool definite = false;
            for (int back = 1; back <= 3 && i - back >= 0; back++) {
                string word = words[i - back];
                if (NumberWords.TryGetValue(word, out int value)) { number = value; break; }
                if (int.TryParse(word, out int digits) && digits is > 0 and < 100) {
                    number = digits;
                    break;
                }
                if (Indefinite.Contains(word)) break;
                if (Definite.Contains(word)) { definite = true; break; }
            }

            bool firstMention = countedOnce.Add(words[i]);
            if (number is { } explicitCount) {
                total += explicitCount;
                continue;
            }
            // No number: a plural is at least a pair, a singular is one — and a
            // definite singular only the first time that noun appears, however
            // it was introduced.
            if (plural) total += 2;
            else if (!definite || firstMention) total += 1;
        }
        return total;
    }

    static readonly HashSet<string> SingularPeople = new(StringComparer.Ordinal) {
        "man", "woman", "boy", "girl", "person", "character", "figure", "guy", "lady",
        "child", "kid", "dancer", "player", "rider", "driver", "worker", "soldier",
    };

    static readonly HashSet<string> PluralPeople = new(StringComparer.Ordinal) {
        "men", "women", "boys", "girls", "people", "characters", "figures", "guys",
        "ladies", "children", "kids", "dancers", "players", "riders", "drivers",
        "workers", "soldiers",
    };

    static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal) {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["a"] = 1, ["an"] = 1,
    };

    static readonly HashSet<string> Indefinite = new(StringComparer.Ordinal) { "another", "some" };

    static readonly HashSet<string> Definite = new(StringComparer.Ordinal) {
        "the", "this", "that", "his", "her", "their", "its", "same",
    };

}

/// Kuaishou Kling 3.0 (kwaivgi/kling-v3-video).
///
/// Kling reads a prompt as direction — scene, characters, action, camera,
/// sound, in that order — and rewards a described progression from beginning
/// to end over a frozen scene description. Unlike Seedance it has a dedicated
/// negative-prompt field, so exclusions leave the prompt entirely, and the
/// guides agree a short focused negative beats a catalogue of fears.
/// See docs/kling-prompt-engineering.md for the sources behind each rule.
public sealed class KlingThreeStyle : IPromptStyle {
    public string Name => "Kling 3.0";

    /// Hard limit the schema enforces, on the negative prompt too.
    public const int MaxCharacters = 2500;
    /// Kling holds structure far past Seedance's band, but past this the
    /// directions start competing even in multi-shot prompts.
    public const int MaxWords = 250;

    public bool Matches(string modelId) =>
        modelId.Contains("kling-v3", StringComparison.OrdinalIgnoreCase);

    /// Travelling between two stills, in Kling's temporal-flow idiom.
    const string TransitionDirection =
        "One continuous shot: start on the first frame and end exactly on the last frame.";

    /// The focused artifact set the guides recommend: 3–5 targeted items that
    /// name the glitches actually seen, never a generic dump.
    const string DefaultNegative =
        "morphing, identity drift, extra fingers, sliding feet, flicker";

    public string Build(string prompt, PromptContext context) {
        string body = prompt.Trim().TrimEnd('.', ' ');
        if (body.Length == 0) return "";
        var built = new StringBuilder(body).Append('.');
        if (context.IsTransition || (context.HasFirstFrame && context.HasLastFrame))
            built.Append(' ').Append(TransitionDirection);
        string result = built.ToString();
        return result.Length <= MaxCharacters ? result : result[..MaxCharacters];
    }

    public string? Negative(PromptContext context) => DefaultNegative;

    public IReadOnlyList<string> Notes { get; } = [
        "Write it as direction, in order: scene, characters, action, camera, sound.",
        "Describe how the shot evolves — beginning, middle, end. Kling rewards temporal " +
            "progression over a frozen description.",
        "Name the camera like a director: slow dolly push, handheld tracking, whip-pan, " +
            "crash zoom. One move per shot.",
        "With first and last frames attached, describe the journey between them — the " +
            "frames already carry the look.",
        "Give characters fixed labels (\"the boy\", \"the diver\") and reuse them; " +
            "pronouns drift identity.",
        "A short negative prompt targeting common artifacts is sent automatically; keep " +
            "the main prompt about what should happen.",
        "Dialogue in double quotes triggers spoken audio, and model audio is switched off " +
            "here — leave dialogue out.",
        "Up to 15 seconds, but long clips need described progression or the motion stalls.",
    ];

    public IReadOnlyList<string> Review(string prompt, PromptContext context) {
        var warnings = new List<string>();
        if (prompt.Trim().Length == 0) return warnings;

        if (PromptText.WordCount(prompt) < 8)
            warnings.Add("Very short — set the scene, the action and the camera move.");
        if (!PromptText.NamesACameraMove(prompt))
            warnings.Add("No camera move named. Kling treats camera direction as near-" +
                         "mandatory — try \"slow dolly push\" or \"handheld tracking\".");
        if (PromptText.WordCount(Build(prompt, context)) > MaxWords)
            warnings.Add($"Over {MaxWords} words once assembled — even Kling starts " +
                         "dropping directions at that length.");
        if (prompt.Count(c => c == '"') >= 2)
            warnings.Add("Quoted dialogue makes Kling speak it, and model audio is " +
                         "switched off here — the lips will move with no sound.");

        var vague = PromptText.VagueWords.Where(w => PromptText.Mentions(prompt, w)).ToList();
        if (vague.Count > 0)
            warnings.Add($"\"{string.Join("\", \"", vague)}\" — vague quality words degrade " +
                         "the result. Say what it should look like instead.");
        return warnings;
    }
}

/// Vocabulary and matching shared by the styles. Both model families reward
/// named camera moves and are measurably hurt by vague quality words, so the
/// lists live once.
static class PromptText {
    /// Camera vocabulary the guides' movement types are described with.
    public static readonly string[] CameraMoves = [
        "dolly", "push in", "pushes in", "pull out", "pulls out", "pull back", "pulls back",
        "zoom", "pan", "pans", "track", "tracks", "tracking", "orbit", "orbits", "arc",
        "crane", "aerial", "drone", "handheld", "gimbal", "locked off", "static camera",
        "steadicam", "tilt", "tilts", "whip",
    ];

    /// Words the guides call out as actively degrading output.
    public static readonly string[] VagueWords = [
        "epic", "amazing", "beautiful", "stunning", "gorgeous", "breathtaking",
        "cinematic", "4k", "8k", "ultra hd", "masterpiece", "high quality",
    ];

    public static bool NamesACameraMove(string prompt) =>
        CameraMoves.Any(move => Mentions(prompt, move));

    /// Whole-word (or whole-phrase) match, so "track" does not fire on
    /// "soundtrack" and "pan" does not fire on "company".
    public static bool Mentions(string prompt, string term) {
        string padded = " " + Punctuation.Replace(prompt.ToLowerInvariant(), " ") + " ";
        return padded.Contains(" " + term + " ");
    }

    public static readonly System.Text.RegularExpressions.Regex Punctuation =
        new(@"[^\p{L}\p{N}]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
