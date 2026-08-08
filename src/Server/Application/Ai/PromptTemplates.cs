using System.Text;
using Compendio.Application.Abstractions;

namespace Compendio.Application.Ai;

/// <summary>
/// Every prompt the product sends, in one file.
/// </summary>
/// <remarks>
/// <para>
/// Together rather than scattered through the handlers, because prompts are the part of an AI
/// feature most likely to be tuned, and tuning them one handler at a time is how six features end up
/// with six different opinions about what Markdown to emit.
/// </para>
/// <para>
/// Two rules run through all of them. The model is told to answer in Markdown, because that is the
/// storage format and anything else has to be converted by somebody. And the model is told it is
/// working inside a company wiki whose content it must not invent — a plausible SOP step that was
/// never true is the failure mode that matters here.
/// </para>
/// </remarks>
public static class PromptTemplates
{
    private const string Common =
        "You are a writing assistant inside a company knowledge base. " +
        "Always answer in GitHub-flavoured Markdown. " +
        "Never invent facts, procedures, names, versions or dates that are not in the text you are given. " +
        "Preserve the original language of the document unless you are explicitly asked to translate.";

    public static AiPrompt Improve(string text) => new(
        $"{Common} Improve the clarity, grammar and structure of the text. Keep every fact, every step and " +
        "every heading. Do not add new sections. Return only the improved Markdown, with no preamble.",
        text)
    { Temperature = 0.2 };

    public static AiPrompt Draft(string bullets, string? template) => new(
        $"{Common} Turn the notes into a well-structured procedure. " +
        (template is { Length: > 0 }
            ? "Follow the structure of the template below, keeping its headings.\n\n--- TEMPLATE ---\n" + template
            : "Use clear headings, numbered steps where the order matters, and a short purpose section.") +
        "\nReturn only the Markdown, with no preamble.",
        bullets)
    { Temperature = 0.4 };

    public static AiPrompt Summarize(string text) => new(
        $"{Common} Write a short summary of the document: three to five sentences, or up to five bullet points " +
        "if the document is a list of steps. Return only the summary, with no heading and no preamble.",
        text)
    { Temperature = 0.2, MaxOutputTokens = 400 };

    public static AiPrompt Translate(string text, string targetLanguageName) => new(
        $"{Common} Translate the document into {targetLanguageName}. " +
        "Translate the prose and the headings. Do not translate code, commands, file paths, host names, " +
        "URLs or the keys in the YAML front matter. Keep the Markdown structure identical. " +
        "Return only the translated Markdown, with no preamble.",
        text)
    { Temperature = 0.1 };

    /// <summary>
    /// Rewrites a question into keyword queries.
    /// </summary>
    /// <remarks>
    /// The step that makes FTS retrieval usable: a question is a bad BM25 query and a keyword set is
    /// not. Asking for the words a document would contain, rather than the words the question
    /// contains, is the whole trick.
    /// </remarks>
    public static AiPrompt ExpandQuery(string question) => new(
        "Rewrite the user's question into two to four short keyword search queries that would appear " +
        "in a document answering it. Prefer nouns and proper names over verbs. " +
        "Answer with one query per line and nothing else — no numbering, no explanation.",
        question)
    { Temperature = 0.1, MaxOutputTokens = 120 };

    /// <summary>
    /// Answers from retrieved passages only.
    /// </summary>
    /// <remarks>
    /// The instruction to refuse when the passages do not contain the answer is doing real work: the
    /// retriever is BM25, so "nothing relevant was found" is a normal outcome, and a model that
    /// answers anyway turns a retrieval miss into a confident invention.
    /// </remarks>
    public static AiPrompt Ask(string question, IReadOnlyList<RetrievedPassage> passages)
    {
        var context = new StringBuilder();

        foreach (var passage in passages)
        {
            context.Append("--- SOURCE: ").Append(passage.Path).Append(" (").Append(passage.Title).AppendLine(")");
            context.AppendLine(passage.Text);
            context.AppendLine();
        }

        context.Append("--- QUESTION ---").AppendLine();
        context.Append(question);

        return new AiPrompt(
            $"{Common} Answer the question using only the sources above it. " +
            "If the sources do not contain the answer, say so plainly and stop — do not answer from general " +
            "knowledge. Cite the sources you used by their exact path, in a final line of the form " +
            "`Sources: path/one.md, path/two.md`. Cite only paths that appear in the sources given to you.",
            context.ToString())
        { Temperature = 0.1 };
    }

    /// <summary>
    /// Flags content that looks out of date.
    /// </summary>
    /// <remarks>
    /// On demand only, never a background sweep: a nightly pass over ten thousand pages against a
    /// metered endpoint is a bill nobody agreed to.
    /// </remarks>
    public static AiPrompt Freshness(string text, DateTimeOffset today) => new(
        $"{Common} Today is {today:yyyy-MM-dd}. List anything in the document that looks out of date: " +
        "dates that have passed, software versions that are old, products that have reached end of life, " +
        "people or systems referred to as current. " +
        "Answer as a Markdown list, one finding per line, each naming the exact text you are flagging and why. " +
        "If nothing looks out of date, answer with the single line `No findings.`",
        text)
    { Temperature = 0.2, MaxOutputTokens = 600 };
}
