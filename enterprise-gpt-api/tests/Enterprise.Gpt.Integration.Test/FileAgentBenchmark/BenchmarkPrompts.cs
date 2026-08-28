namespace Enterprise.Gpt.Integration.Test.FileAgentBenchmark;

/// <summary>
/// One prompt the benchmark sends, and the file it must come back with.
/// </summary>
/// <param name="Verb">Which of the four the prompt exercises, so a failure names the capability.</param>
/// <param name="Extension">The extension the answer's artifact must carry, including its dot.</param>
/// <param name="Source">
/// A file the run works from, or <see langword="null"/> for a create. Written into the sandbox before
/// the prompt is sent, exactly as a turn's own inputs are.
/// </param>
internal sealed record BenchmarkPrompt(
    string Id, string Verb, string Extension, string Instruction, BenchmarkSource? Source = null);

/// <summary>A file mounted into the sandbox before a prompt runs.</summary>
internal sealed record BenchmarkSource(string FileName, string MediaType, string Content);

/// <summary>
/// The fixed benchmark: thirty prompts across the seven create formats and the four verbs.
/// </summary>
/// <remarks>
/// Fixed rather than sampled, so two runs a month apart are comparable. Every source is plain text the
/// run converts or reads from, because a benchmark that had to ship binary fixtures would drift from
/// them silently.
/// </remarks>
internal static class BenchmarkPrompts
{
    private const string Csv =
        "region,quarter,revenue\nNorth,Q1,412000\nNorth,Q2,455000\nSouth,Q1,388000\nSouth,Q2,401000\n";

    private const string Markdown =
        "# Quarterly review\n\n## Highlights\n\n- Revenue up 9%\n- Two new markets\n\n## Risks\n\n"
        + "| Risk | Owner |\n| --- | --- |\n| Supply | Ops |\n| Churn | CS |\n";

    private const string Text =
        "Meeting notes, 3 March.\nAttendees: Priya, Sam, Alex.\nDecision: ship the pilot in April.\n"
        + "Action: Sam to confirm the vendor by Friday.\n";

    /// <summary>Every prompt, in a stable order.</summary>
    public static IReadOnlyList<BenchmarkPrompt> All { get; } =
    [
        // Create, across the seven formats it must cover.
        new("create-docx-1", "create", ".docx", "Write me a one-page Word document summarising a fictional Q2 sales review, with a heading and three short sections."),
        new("create-docx-2", "create", ".docx", "Make a Word document containing a table of five example project risks with an owner for each."),
        new("create-docx-3", "create", ".docx", "Produce a Word document: a short welcome letter for a new employee named Priya."),
        new("create-xlsx-1", "create", ".xlsx", "Make me a spreadsheet with a sheet called Revenue holding four quarters of made-up figures for two regions."),
        new("create-xlsx-2", "create", ".xlsx", "Build an Excel workbook with two sheets, Budget and Actuals, each with a header row and five rows of numbers."),
        new("create-xlsx-3", "create", ".xlsx", "Create a spreadsheet tracking ten fictional support tickets: id, opened, priority, status."),
        new("create-pptx-1", "create", ".pptx", "Turn these three points into a four-slide deck: we grew 9%, we entered two markets, churn is the risk."),
        new("create-pptx-2", "create", ".pptx", "Make a five-slide PowerPoint introducing a fictional product called Northwind Ledger."),
        new("create-pptx-3", "create", ".pptx", "Produce a three-slide deck with a title slide and two content slides about onboarding."),
        new("create-pdf-1", "create", ".pdf", "Write a two-page PDF explaining a fictional expenses policy."),
        new("create-pdf-2", "create", ".pdf", "Make a one-page PDF invoice for three made-up line items with a total."),
        new("create-csv-1", "create", ".csv", "Give me a CSV of twelve months with a made-up headcount figure for each."),
        new("create-csv-2", "create", ".csv", "Produce a CSV listing eight fictional employees with a name, team and start date."),
        new("create-md-1", "create", ".md", "Write a markdown README for a fictional command line tool called ledgerctl."),
        new("create-md-2", "create", ".md", "Give me a markdown checklist for running a product launch."),
        new("create-txt-1", "create", ".txt", "Write a plain text file holding a short standup agenda."),
        new("create-txt-2", "create", ".txt", "Produce a text file listing ten fictional server hostnames, one per line."),

        // Convert, at the tiers the confirmed matrix records.
        new("convert-csv-xlsx", "convert", ".xlsx", "Convert revenue.csv into an Excel workbook.", new BenchmarkSource("revenue.csv", "text/csv", Csv)),
        new("convert-csv-docx", "convert", ".docx", "Convert revenue.csv into a Word document with the rows as a table.", new BenchmarkSource("revenue.csv", "text/csv", Csv)),
        new("convert-csv-pdf", "convert", ".pdf", "Convert revenue.csv into a PDF.", new BenchmarkSource("revenue.csv", "text/csv", Csv)),
        new("convert-md-docx", "convert", ".docx", "Convert review.md into a Word document.", new BenchmarkSource("review.md", "text/markdown", Markdown)),
        new("convert-md-pdf", "convert", ".pdf", "Convert review.md into a PDF.", new BenchmarkSource("review.md", "text/markdown", Markdown)),
        new("convert-txt-docx", "convert", ".docx", "Convert notes.txt into a Word document.", new BenchmarkSource("notes.txt", "text/plain", Text)),
        new("convert-md-txt", "convert", ".txt", "Convert review.md into plain text.", new BenchmarkSource("review.md", "text/markdown", Markdown)),

        // Edit, which must always write a new file.
        new("edit-csv", "edit", ".csv", "Add a column to revenue.csv holding revenue in thousands, and give me the result.", new BenchmarkSource("revenue.csv", "text/csv", Csv)),
        new("edit-md", "edit", ".md", "Add a Next steps section with three bullets to review.md and give me the updated file.", new BenchmarkSource("review.md", "text/markdown", Markdown)),
        new("edit-txt", "edit", ".txt", "Rewrite notes.txt so every action item is on its own line prefixed with ACTION, and give me the file.", new BenchmarkSource("notes.txt", "text/plain", Text)),

        // Compare, where a file is produced only because this prompt asks for one.
        new("compare-md", "compare", ".md", "Compare review.md against a version where churn is no longer a risk, and write the differences to a markdown file.", new BenchmarkSource("review.md", "text/markdown", Markdown)),
        new("compare-csv", "compare", ".csv", "Compare revenue.csv against the same data with South Q2 at 450000, and write the differing rows to a CSV.", new BenchmarkSource("revenue.csv", "text/csv", Csv)),
        new("compare-txt", "compare", ".txt", "Compare notes.txt against a version where the vendor deadline moved to Monday, and write what changed to a text file.", new BenchmarkSource("notes.txt", "text/plain", Text))
    ];
}
