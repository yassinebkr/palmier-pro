using PalmierShell.Core.Generation;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

public class PromptBuilderTests {
    [Fact]
    public void AnEmptyPromptBecomesThePhrase() {
        Assert.Equal("slow dolly push-in", PromptBuilder.AppendPhrase("", "slow dolly push-in"));
    }

    [Fact]
    public void AWhitespacePromptBecomesThePhrase() {
        Assert.Equal("slow dolly push-in",
            PromptBuilder.AppendPhrase("  \n ", "slow dolly push-in"));
    }

    [Fact]
    public void ATrailingCommaGetsExactlyOneSpace() {
        Assert.Equal("a cat, slow dolly push-in",
            PromptBuilder.AppendPhrase("a cat,", "slow dolly push-in"));
    }

    [Fact]
    public void ATrailingCommaAndSpacesAreNormalizedToOneSpace() {
        Assert.Equal("a cat, slow dolly push-in",
            PromptBuilder.AppendPhrase("a cat,  ", "slow dolly push-in"));
    }

    [Fact]
    public void TrailingWhitespaceJoinsWithoutAComma() {
        Assert.Equal("a cat slow dolly push-in",
            PromptBuilder.AppendPhrase("a cat ", "slow dolly push-in"));
    }

    [Fact]
    public void ATrailingPeriodStillGetsTheCommaSeparator() {
        Assert.Equal("a cat., slow dolly push-in",
            PromptBuilder.AppendPhrase("a cat.", "slow dolly push-in"));
    }

    [Fact]
    public void PlainTextGetsACommaSeparator() {
        Assert.Equal("a cat, slow dolly push-in",
            PromptBuilder.AppendPhrase("a cat", "slow dolly push-in"));
    }

    [Fact]
    public void RepeatedAppendsAccumulateAsOneCommaSeparatedList() {
        string prompt = "";
        prompt = PromptBuilder.AppendPhrase(prompt, "narrow alley after rain, steam rising from grates");
        prompt = PromptBuilder.AppendPhrase(prompt, "slow dolly push-in");
        prompt = PromptBuilder.AppendPhrase(prompt, "warm nostalgic 35mm film grain");
        Assert.Equal("narrow alley after rain, steam rising from grates, slow dolly push-in, " +
                     "warm nostalgic 35mm film grain", prompt);
    }

    [Fact]
    public void AppendsNeverProduceDoubleSpacesOrEmptyItems() {
        foreach (string prompt in new[] { "", "a cat", "a cat ", "a cat,", "a cat, ", "a cat." }) {
            string result = PromptBuilder.AppendPhrase(prompt, "slow dolly push-in");
            Assert.DoesNotContain("  ", result);
            Assert.DoesNotContain(", ,", result);
        }
    }

    [Fact]
    public void TheBuilderHasTheFiveExplainedSectionsInOrder() {
        Assert.Equal(["Scene", "Subject", "Camera", "Lighting", "Mood"],
            PromptBuilder.Groups.Select(group => group.Title));
    }

    [Fact]
    public void EveryGroupExplainsItselfAndOffersChips() {
        foreach (var group in PromptBuilder.Groups) {
            Assert.False(string.IsNullOrWhiteSpace(group.Hint));
            Assert.NotEmpty(group.Chips);
        }
    }

    [Fact]
    public void EveryChipHasALabelAndAPhrase() {
        foreach (var chip in PromptBuilder.Groups.SelectMany(group => group.Chips)) {
            Assert.False(string.IsNullOrWhiteSpace(chip.Label));
            Assert.False(string.IsNullOrWhiteSpace(chip.Phrase));
            Assert.Equal(chip.Phrase, chip.Phrase.Trim());
        }
    }

    [Fact]
    public void AChipFeedsTheSameEditablePrompt() {
        var panel = new GeneratePanelViewModel((_, _) => Task.CompletedTask, () => []);
        panel.Prompt = "a cat";
        panel.InsertPromptChipCommand.Execute("slow dolly push-in");
        Assert.Equal("a cat, slow dolly push-in", panel.Prompt);
    }
}
