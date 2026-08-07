// Chip vocabularies adapted from the Apache-2.0 kling-3-prompting-skill: https://github.com/aedev-tools/kling-3-prompting-skill
namespace PalmierShell.Core.Generation;

/// One quick-pick in the prompt builder: a short button label and the phrase
/// it appends to the prompt.
public sealed record PromptChip(string Label, string Phrase);

/// A section of the builder — the title names what the section decides, the
/// hint is the one-line craft note shown beside it.
public sealed record PromptChipGroup(string Title, string Hint, IReadOnlyList<PromptChip> Chips);

/// The guided prompt builder: five explained sections whose chips append
/// phrases to the composer's prompt box. An assist, never a gate — the text
/// stays fully editable and a hand-written prompt round-trips untouched.
public static class PromptBuilder {
    public static IReadOnlyList<PromptChipGroup> Groups { get; } = [
        new PromptChipGroup("Scene", "where are we — be specific", [
            new PromptChip("Rainy alley", "narrow alley after rain, steam rising from grates"),
            new PromptChip("Neon street", "neon-lit street at night, wet pavement reflections"),
            new PromptChip("Golden hour beach", "empty beach at golden hour, long shadows"),
            new PromptChip("Forest canopy", "dense forest canopy, dappled sunlight"),
            new PromptChip("Night kitchen", "dim kitchen late at night, refrigerator glow"),
            new PromptChip("Warehouse", "warehouse interior, dusty windows, shafts of light"),
            new PromptChip("Rooftop dusk", "city rooftop at dusk, skyline silhouette"),
            new PromptChip("Parking garage", "empty parking garage, humming fluorescents"),
        ]),
        new PromptChipGroup("Subject", "how the subject sits in frame", [
            new PromptChip("Face close-up", "tight close-up on the subject's face"),
            new PromptChip("Centered", "subject centered in frame"),
            new PromptChip("Silhouette", "subject silhouetted against the light"),
            new PromptChip("From behind", "subject seen from behind"),
            new PromptChip("Over the shoulder", "over-the-shoulder framing"),
        ]),
        new PromptChipGroup("Camera", "movement verbs beat 'moves'", [
            new PromptChip("Push-in", "slow dolly push-in"),
            new PromptChip("Dolly zoom", "dolly zoom creating a disorienting depth shift"),
            new PromptChip("Tracking", "camera tracks alongside the subject"),
            new PromptChip("Whip-pan", "whip-pan to reveal"),
            new PromptChip("Crash zoom", "sudden crash zoom"),
            new PromptChip("Rack focus", "rack focus from foreground to background"),
            new PromptChip("Handheld", "handheld shoulder-cam with subtle sway"),
            new PromptChip("Static", "locked-off static tripod shot"),
            new PromptChip("FPV drone", "dynamic FPV drone shot chasing through the scene"),
            new PromptChip("Low angle", "low-angle shot, subject towers above"),
            new PromptChip("Truck", "camera trucks right, revealing the scene"),
            new PromptChip("Tilt up", "slow tilt up"),
        ]),
        new PromptChipGroup("Lighting", "name the source, not the adjective", [
            new PromptChip("Golden hour", "golden hour sun, warm rim light"),
            new PromptChip("Neon", "flickering neon casting magenta and cyan"),
            new PromptChip("Single bulb", "single bare bulb, hard moving shadows"),
            new PromptChip("LED panels", "cool blue LED panels reflecting off glass"),
            new PromptChip("Candlelight", "candlelight warming skin tones, deep shadows beyond"),
            new PromptChip("Overcast", "soft overcast light, gentle shadows"),
            new PromptChip("Moonlight", "cold moonlight through the window"),
            new PromptChip("Practicals", "warm practical lamps in frame"),
        ]),
        new PromptChipGroup("Mood", "grade and feeling", [
            new PromptChip("Teal grade", "desaturated teal grade, crushed blacks"),
            new PromptChip("Nightclub", "amber strobe cutting through smoke"),
            new PromptChip("Blue haze", "cool blue haze filling the space"),
            new PromptChip("Neo-noir", "magenta neon on wet asphalt, neo-noir mood"),
            new PromptChip("Nostalgic", "warm nostalgic 35mm film grain"),
            new PromptChip("Tense", "tense, claustrophobic atmosphere"),
            new PromptChip("Airy", "calm, airy, light-filled mood"),
        ]),
    ];

    /// Joins a chip phrase onto the prompt as one list item: comma-separated,
    /// exactly one space, never a double space or ", ,".
    public static string AppendPhrase(string prompt, string phrase) {
        if (string.IsNullOrWhiteSpace(prompt)) return phrase;
        string trimmed = prompt.TrimEnd();
        if (trimmed.EndsWith(',') || trimmed.Length != prompt.Length)
            return trimmed + " " + phrase;
        return prompt + ", " + phrase;
    }
}
