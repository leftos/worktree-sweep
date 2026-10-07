using WorktreeSweep.Unlock;

namespace WorktreeSweep.Tests;

/// <summary>The numbered line prompts and the answers they parse.</summary>
public sealed class LinePromptTests
{
    private static readonly ChoicePrompt Choices = new(["Stop process", "Close its handles", "Skip", "Done"], 1, 4, 3);

    /// <summary>A yes or no answer, whatever its case and surrounding spaces; anything else is not an answer.</summary>
    [Theory]
    [InlineData("", true, true)]
    [InlineData("   ", false, false)]
    [InlineData("Y", false, true)]
    [InlineData(" yes ", false, true)]
    [InlineData("YES", false, true)]
    [InlineData("N", true, false)]
    [InlineData("no", true, false)]
    [InlineData("1", true, null)]
    [InlineData("yy", true, null)]
    [InlineData("maybe", true, null)]
    [InlineData("ok", true, null)]
    public void ParseYesNoReadsTheAnswer(string line, bool defaultAnswer, bool? expected) =>
        Assert.Equal(expected, LinePrompt.ParseYesNo(line, defaultAnswer));

    /// <summary>A choice counts from 1 and stops at the number of choices; anything else is not a choice.</summary>
    [Theory]
    [InlineData("2", 2)]
    [InlineData(" 3 ", 3)]
    [InlineData("04", 4)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("0", null)]
    [InlineData("5", null)]
    [InlineData("-1", null)]
    [InlineData("+1", null)]
    [InlineData("99999999999", null)]
    [InlineData("1.0", null)]
    [InlineData("a", null)]
    [InlineData("1 2", null)]
    public void ParseChoiceReadsTheChoice(string line, int? expected) => Assert.Equal(expected, LinePrompt.ParseChoice(line, 4, 1));

    /// <summary>A prompt with no choices, or a default that is not one of them, is a programming error.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(4, 5)]
    [InlineData(4, 0)]
    public void ParseChoiceRejectsImpossibleChoices(int count, int defaultChoice) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LinePrompt.ParseChoice("1", count, defaultChoice));

    /// <summary>The prompt names the default, and an empty line takes it.</summary>
    [Theory]
    [InlineData(true, "Continue? [Y/n] ")]
    [InlineData(false, "Continue? [y/N] ")]
    public void AskYesNoPromptNamesTheDefault(bool defaultAnswer, string prompt)
    {
        StringWriter output = Writer();

        Assert.Equal(defaultAnswer, LinePrompt.AskYesNo(new StringReader("\n"), output, "Continue?", defaultAnswer));
        Assert.Equal(prompt, output.ToString());
    }

    /// <summary>End of input is no, whatever the default said.</summary>
    [Fact]
    public void AskYesNoTreatsEofAsNo()
    {
        StringWriter output = Writer();

        Assert.False(LinePrompt.AskYesNo(new StringReader(""), output, "Continue?", true));
        Assert.Equal("Continue? [Y/n] ", output.ToString());
    }

    /// <summary>An invalid answer is refused, the prompt is asked again, and three tries in all is the limit.</summary>
    [Fact]
    public void AskYesNoGivesUpAfterThreeInvalidAnswers()
    {
        StringWriter output = Writer();

        Assert.False(LinePrompt.AskYesNo(new StringReader("x\nx\nx\n"), output, "Continue?", true));
        Assert.Equal(3, output.ToString().Split("Enter y or n.").Length - 1);
    }

    /// <summary>An invalid answer does not lose the answer that follows it.</summary>
    [Fact]
    public void AskYesNoTakesAnAnswerAfterAnInvalidOne()
    {
        StringWriter output = Writer();

        Assert.True(LinePrompt.AskYesNo(new StringReader("x\ny\n"), output, "Continue?", false));
        Assert.Equal("Continue? [y/N] Enter y or n.\nContinue? [y/N] ", output.ToString());
    }

    /// <summary>The prompt lists the choices numbered from 1 and names the default.</summary>
    [Fact]
    public void AskChoicePromptListsTheChoices()
    {
        StringWriter output = Writer();

        Assert.Equal(2, LinePrompt.AskChoice(new StringReader("2\n"), output, Choices));
        Assert.Equal("1) Stop process  2) Close its handles  3) Skip  4) Done [1]: ", output.ToString());
    }

    /// <summary>End of input is the EOF choice, not the fallback.</summary>
    [Fact]
    public void AskChoiceTreatsEofAsTheEofChoice()
    {
        StringWriter output = Writer();

        Assert.Equal(4, LinePrompt.AskChoice(new StringReader(""), output, Choices));
        Assert.Equal("1) Stop process  2) Close its handles  3) Skip  4) Done [1]: ", output.ToString());
    }

    /// <summary>Three invalid answers in all, then the fallback choice.</summary>
    [Fact]
    public void AskChoiceFallsBackAfterThreeInvalidAnswers()
    {
        StringWriter output = Writer();

        Assert.Equal(3, LinePrompt.AskChoice(new StringReader("x\nx\nx\n"), output, Choices));
        Assert.Equal(3, output.ToString().Split("Enter a number from 1 to 4.").Length - 1);
    }

    /// <summary>An invalid answer does not lose the answer that follows it.</summary>
    [Fact]
    public void AskChoiceTakesAnAnswerAfterAnInvalidOne()
    {
        StringWriter output = Writer();

        Assert.Equal(3, LinePrompt.AskChoice(new StringReader("x\n3\n"), output, Choices));
    }

    /// <summary>A prompt with no choices, or choices that are not its labels, is a programming error.</summary>
    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(1, 2, 1, 1)]
    [InlineData(1, 1, 2, 1)]
    [InlineData(1, 1, 1, 2)]
    [InlineData(1, 0, 1, 1)]
    public void AskChoiceRejectsImpossibleChoices(int labelCount, int defaultChoice, int eofChoice, int fallbackChoice)
    {
        IReadOnlyList<string> labels = [.. Enumerable.Repeat("choice", labelCount)];
        var prompt = new ChoicePrompt(labels, defaultChoice, eofChoice, fallbackChoice);

        Assert.Throws<ArgumentOutOfRangeException>(() => LinePrompt.AskChoice(new StringReader("1\n"), Writer(), prompt));
    }

    private static StringWriter Writer() => new() { NewLine = "\n" };
}
