using System.Globalization;

namespace WorktreeSweep.Unlock;

/// <summary>The numbered line prompts the unlock step asks in the terminal, and the answers they parse.</summary>
public static class LinePrompt
{
    private const int Tries = 3;
    private const string InvalidAnswer = "Enter y or n.";

    /// <summary>Reads a yes or no answer from a line.</summary>
    /// <param name="line">The line the user typed.</param>
    /// <param name="defaultAnswer">The answer an empty or blank line means.</param>
    /// <returns><see langword="true"/> or <see langword="false"/> for <c>y</c>, <c>yes</c>, <c>n</c> or <c>no</c> in any case,
    /// <see langword="null"/> for anything else.</returns>
    public static bool? ParseYesNo(string line, bool defaultAnswer)
    {
        ArgumentNullException.ThrowIfNull(line);
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return defaultAnswer;
        }
        if (trimmed.Equals("y", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (trimmed.Equals("n", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return null;
    }

    /// <summary>Reads a choice from a line, counting from 1.</summary>
    /// <param name="line">The line the user typed.</param>
    /// <param name="count">How many choices there are.</param>
    /// <param name="defaultChoice">The choice an empty or blank line means.</param>
    /// <returns>The choice for a whole number from 1 to <paramref name="count"/>, <see langword="null"/> for anything else.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below 1, or <paramref name="defaultChoice"/> is
    /// not one of the choices.</exception>
    public static int? ParseChoice(string line, int count, int defaultChoice)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "a prompt needs at least one choice");
        }
        if (defaultChoice < 1 || defaultChoice > count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultChoice),
                defaultChoice,
                string.Create(CultureInfo.InvariantCulture, $"the default choice is not one of the {count} choices")
            );
        }
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return defaultChoice;
        }
        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int choice) && choice >= 1 && choice <= count
            ? choice
            : null;
    }

    /// <summary>Asks a yes or no question, at most <see cref="Tries"/> times, and reads the answer.</summary>
    /// <param name="input">Where the answers come from.</param>
    /// <param name="output">Where the question goes, flushed before each read so a user sees it before typing.</param>
    /// <param name="question">The question, without its prompt suffix.</param>
    /// <param name="defaultAnswer">The answer an empty line means, shown as <c>[Y/n]</c> or <c>[y/N]</c>.</param>
    /// <returns>The answer; no for end of input, and for three answers that are neither yes nor no.</returns>
    public static bool AskYesNo(TextReader input, TextWriter output, string question, bool defaultAnswer)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(question);
        string prompt = question + (defaultAnswer ? " [Y/n] " : " [y/N] ");
        for (int attempt = 0; attempt < Tries; attempt++)
        {
            output.Write(prompt);
            output.Flush();
            string? line = input.ReadLine();
            if (line is null)
            {
                return false;
            }
            if (ParseYesNo(line, defaultAnswer) is bool answer)
            {
                return answer;
            }
            output.WriteLine(InvalidAnswer);
            output.Flush();
        }
        return false;
    }

    /// <summary>Asks which of a few things to do, at most <see cref="Tries"/> times, and reads the choice.</summary>
    /// <param name="input">Where the answers come from.</param>
    /// <param name="output">Where the choices and their default go, as <c>1) Stop  2) Skip [1]: </c>, flushed before each
    /// read so a user sees it before typing.</param>
    /// <param name="prompt">The choices, the default, and what end of input and three unusable answers mean.</param>
    /// <returns>The choice that was made.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The prompt offers no choice, or one of its three choices is not one of
    /// its labels.</exception>
    public static int AskChoice(TextReader input, TextWriter output, ChoicePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(prompt);
        Validate(prompt);
        string choices = string.Join(
            "  ",
            prompt.Labels.Select((label, index) => string.Create(CultureInfo.InvariantCulture, $"{index + 1}) {label}"))
        );
        string question = string.Create(CultureInfo.InvariantCulture, $"{choices} [{prompt.DefaultChoice}]: ");
        string invalid = string.Create(CultureInfo.InvariantCulture, $"Enter a number from 1 to {prompt.Labels.Count}.");
        for (int attempt = 0; attempt < Tries; attempt++)
        {
            output.Write(question);
            output.Flush();
            string? line = input.ReadLine();
            if (line is null)
            {
                return prompt.EofChoice;
            }
            if (ParseChoice(line, prompt.Labels.Count, prompt.DefaultChoice) is int choice)
            {
                return choice;
            }
            output.WriteLine(invalid);
            output.Flush();
        }
        return prompt.FallbackChoice;
    }

    /// <summary>Refuses a prompt whose choices could never be answered.</summary>
    /// <param name="prompt">The prompt.</param>
    /// <exception cref="ArgumentOutOfRangeException">The prompt offers no choice, or one of its three choices is not one of
    /// its labels.</exception>
    private static void Validate(ChoicePrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt.Labels);
        int count = prompt.Labels.Count;
        if (count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prompt), 0, "a prompt needs at least one choice");
        }
        int[] chosen = [prompt.DefaultChoice, prompt.EofChoice, prompt.FallbackChoice];
        foreach (int choice in chosen)
        {
            if (choice < 1 || choice > count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(prompt),
                    choice,
                    string.Create(CultureInfo.InvariantCulture, $"choice {choice} is not one of the {count} choices")
                );
            }
        }
    }
}
