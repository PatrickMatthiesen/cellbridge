namespace CellBridge.Client.Word;

/// <summary>Locates a direct body paragraph by its zero-based index and optional persisted Word paragraph ID.</summary>
public sealed record WordParagraphReference(int Index, string? Id = null);

/// <summary>A direct body paragraph. Heading levels, when present, are one-based.</summary>
public sealed record WordParagraph(WordParagraphReference Reference, string Text, string? StyleId, int? HeadingLevel);

public sealed record WordDocumentSummary(int ParagraphCount, int HeadingCount);

public sealed record WordParagraphChange(WordParagraph? Before, WordParagraph? After);

public sealed record WordDiffResult(IReadOnlyList<WordParagraphChange> Changes, bool Truncated);

public abstract record WordEdit;

public sealed record WordInsertParagraph(WordParagraphReference Anchor, string Text, bool After = true) : WordEdit;

public sealed record WordReplaceParagraph(WordParagraphReference Target, string Text) : WordEdit;

public sealed record WordDeleteParagraph(WordParagraphReference Target) : WordEdit;

public sealed record WordAppendParagraph(string Text) : WordEdit;

public sealed record WordAddComment(WordParagraphReference Target, string Text, string Author, string? Initials = null) : WordEdit;

public sealed record WordEditConflict(int EditIndex, WordParagraphReference? Target, string Message);

internal sealed record WordMergeResult(byte[]? Package, IReadOnlyList<WordEditConflict> Conflicts);
