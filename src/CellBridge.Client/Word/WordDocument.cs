using System.Xml;
using System.Xml.Linq;

namespace CellBridge.Client.Word;

/// <summary>Reads and edits direct body paragraphs in a Word .docx package.</summary>
public sealed class WordDocument
{
    private byte[] _bytes;
    private WordParagraphs _paragraphs;

    private WordDocument(byte[] bytes, WordParagraphs paragraphs)
    {
        _bytes = bytes;
        _paragraphs = paragraphs;
    }

    public static WordDocument Open(byte[] package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var bytes = package.ToArray();
        using var document = new WordPackage(bytes);
        return new(bytes, new WordParagraphs(document));
    }

    public byte[] ToArray() => _bytes.ToArray();

    public WordDocumentSummary GetSummary() => new(_paragraphs.Elements.Length,
        Enumerable.Range(0, _paragraphs.Elements.Length).Count(index => _paragraphs.HeadingLevel(index) is not null));

    public IReadOnlyList<WordParagraph> ReadParagraphs(int startIndex = 0, int maxParagraphs = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParagraphs);
        var count = Math.Min(maxParagraphs, Math.Max(0, _paragraphs.Elements.Length - startIndex));
        return Enumerable.Range(startIndex, count).Select(_paragraphs.Read).ToArray();
    }

    public IReadOnlyList<WordParagraph> ListHeadings() => Enumerable.Range(0, _paragraphs.Elements.Length)
        .Where(index => _paragraphs.HeadingLevel(index) is not null).Select(_paragraphs.Read).ToArray();

    /// <summary>Compares direct body paragraph text, headings, and relative order with an earlier package.</summary>
    public WordDiffResult Diff(WordDocument previous, int maxChanges = 1000)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChanges);
        var before = previous._paragraphs;
        var after = _paragraphs;
        var map = before.MapTo(after, matchByMarkup: true);
        if (before.Elements.Length == after.Elements.Length && map.Select((current, index) => current is null || current == index).All(same => same))
        {
            // Positional pairing here is display only. An unmatched edit target still conflicts.
            for (var i = 0; i < map.Length; i++)
                if (map[i] is null && before.Ids[i] is null && after.Ids[i] is null)
                    map[i] = i;
        }

        var reordered = WordParagraphs.Reordered(map);
        var changes = new List<WordParagraphChange>();
        var matched = new bool[after.Elements.Length];
        for (var i = 0; i < map.Length; i++)
        {
            var old = before.Read(i);
            WordParagraph? current = null;
            if (map[i] is { } index)
            {
                matched[index] = true;
                current = after.Read(index);
                if (!reordered[i] && current.Text == old.Text && current.StyleId == old.StyleId && current.HeadingLevel == old.HeadingLevel)
                    continue;
            }
            if (changes.Count == maxChanges)
                return new(changes.ToArray(), true);
            changes.Add(new(old, current));
        }
        for (var i = 0; i < matched.Length; i++)
        {
            if (matched[i])
                continue;
            if (changes.Count == maxChanges)
                return new(changes.ToArray(), true);
            changes.Add(new(null, after.Read(i)));
        }
        return new(changes.ToArray(), false);
    }

    /// <summary>Applies a batch atomically. All indices refer to the document before the batch.</summary>
    public void ApplyEdits(IEnumerable<WordEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var batch = edits.ToArray();
        if (batch.Length == 0)
            return;
        using var latest = new WordPackage(_bytes);
        var conflicts = Apply(_paragraphs, latest, batch);
        if (conflicts.Count != 0)
            throw new InvalidOperationException(conflicts[0].Message);
        var bytes = latest.Save();
        var paragraphs = new WordParagraphs(latest);
        _bytes = bytes;
        _paragraphs = paragraphs;
    }

    internal static WordMergeResult Merge(byte[] original, byte[] latest, IReadOnlyList<WordEdit> edits)
    {
        using var current = new WordPackage(latest);
        using var basis = ReferenceEquals(original, latest) ? null : new WordPackage(original);
        var conflicts = Apply(new WordParagraphs(basis ?? current), current, edits);
        return conflicts.Count == 0 ? new(current.Save(), []) : new(null, conflicts);
    }

    private static IReadOnlyList<WordEditConflict> Apply(WordParagraphs original, WordPackage latest, IReadOnlyList<WordEdit> edits)
    {
        var paragraphs = new WordParagraphs(latest);
        int?[]? map = null;
        var plan = new List<(WordEdit Edit, XElement? Target, string Text)>();
        var conflicts = new List<WordEditConflict>();
        var targeted = new Dictionary<XElement, WordEdit>();
        for (var i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            var (reference, text) = edit switch
            {
                WordInsertParagraph insert => (insert.Anchor, Normalize(insert.Text)),
                WordReplaceParagraph replace => (replace.Target, Normalize(replace.Text)),
                WordDeleteParagraph delete => (delete.Target, ""),
                WordAppendParagraph append => (null, Normalize(append.Text)),
                WordAddComment comment => (comment.Target, ValidateComment(comment)),
                _ => throw new ArgumentException("The Word edit type is unsupported.", nameof(edits)),
            };
            if (edit is WordAppendParagraph)
            {
                plan.Add((edit, null, text));
                continue;
            }
            ArgumentNullException.ThrowIfNull(reference);
            if (original.Resolve(reference) is not { } index)
            {
                conflicts.Add(new(i, reference, "The paragraph reference could not be resolved in the base revision."));
                continue;
            }
            map ??= original.MapTo(paragraphs, matchByMarkup: false);
            if (map[index] is not { } currentIndex)
            {
                conflicts.Add(new(i, reference, "The paragraph was removed or lacks a matching stable ID that is unique in both revisions."));
                continue;
            }
            var target = paragraphs.Elements[currentIndex];
            if (edit is WordInsertParagraph insertEdit)
            {
                if (!SameGap(original, paragraphs, map, index, currentIndex, insertEdit.After))
                {
                    conflicts.Add(new(i, reference, "The blocks next to the insertion point changed."));
                    continue;
                }
            }
            else if (!WordParagraphs.SameMarkup(original.Elements[index], target))
            {
                conflicts.Add(new(i, reference, "The target paragraph changed in the latest revision."));
                continue;
            }

            if (!CanEdit(latest.W, target, edit))
            {
                conflicts.Add(new(i, reference, "The paragraph contains markup that this operation cannot preserve."));
                continue;
            }
            if (targeted.TryGetValue(target, out var existing) && (edit is WordDeleteParagraph || existing is WordDeleteParagraph ||
                    (edit is WordReplaceParagraph && existing is WordReplaceParagraph)))
            {
                conflicts.Add(new(i, reference, "The batch contains overlapping edits to the same paragraph."));
                continue;
            }
            if (edit is WordDeleteParagraph or WordReplaceParagraph || existing is null)
                targeted[target] = edit;
            plan.Add((edit, target, text));
        }

        if (conflicts.Count != 0)
            return conflicts.ToArray();

        // Replace before adding comment markers so that both operations can target the same paragraph.
        var afterInsertions = new Dictionary<XElement, XElement>();
        foreach (var entry in plan.Where(entry => entry.Edit is not WordAddComment))
        {
            var (edit, target, text) = entry;
            switch (edit)
            {
                case WordReplaceParagraph:
                    if (!ReplaceText(latest, target!, text))
                        continue;
                    break;
                case WordDeleteParagraph:
                    target!.Remove();
                    break;
                case WordAppendParagraph:
                    var appended = latest.NewParagraph(text);
                    if (latest.Body.Element(latest.W + "sectPr") is { } section)
                        section.AddBeforeSelf(appended);
                    else
                        latest.Body.Add(appended);
                    break;
                case WordInsertParagraph insert:
                    var inserted = latest.NewParagraph(text);
                    if (insert.After)
                    {
                        (afterInsertions.GetValueOrDefault(target!) ?? target!).AddAfterSelf(inserted);
                        afterInsertions[target!] = inserted;
                    }
                    else
                        target!.AddBeforeSelf(inserted);
                    break;
            }
            latest.MarkDocumentChanged();
        }
        foreach (var (edit, target, text) in plan.Where(entry => entry.Edit is WordAddComment))
        {
            latest.AddComment(target!, (WordAddComment)edit, text);
            latest.MarkDocumentChanged();
        }
        return [];
    }

    private static bool SameGap(WordParagraphs original, WordParagraphs latest, int?[] map, int index, int current, bool after)
    {
        var before = original.Elements[index];
        var target = latest.Elements[current];
        var nearBefore = after ? before.ElementsAfterSelf().FirstOrDefault() : before.ElementsBeforeSelf().LastOrDefault();
        var nearCurrent = after ? target.ElementsAfterSelf().FirstOrDefault() : target.ElementsBeforeSelf().LastOrDefault();
        if (nearBefore?.Name == before.Name)
        {
            var adjacent = map[after ? index + 1 : index - 1];
            return adjacent is { } position && ReferenceEquals(latest.Elements[position], nearCurrent);
        }
        return XNode.DeepEquals(nearBefore, nearCurrent);
    }

    private static bool CanEdit(XNamespace w, XElement target, WordEdit edit)
    {
        if (edit is WordDeleteParagraph && target.Element(w + "pPr") is { } properties &&
            properties.Descendants().Any(node => node.Name == w + "sectPr" || node.Name == w + "pPrChange" ||
                node.Name == w + "rPrChange" || node.Name == w + "ins" || node.Name == w + "del" ||
                node.Name == w + "moveFrom" || node.Name == w + "moveTo"))
            return false;
        return edit is not (WordDeleteParagraph or WordReplaceParagraph) || target.Elements().All(node =>
            node.Name == w + "pPr" || (node.Name == w + "r" && node.Elements().All(child => child.Name == w + "rPr" ||
                child.Name == w + "t" || child.Name == w + "tab" || (child.Name == w + "br" && !child.HasAttributes) || child.Name == w + "cr"))) &&
            (edit is not WordDeleteParagraph || !target.Descendants(w + "rPrChange").Any());
    }

    private static bool ReplaceText(WordPackage package, XElement target, string text)
    {
        var w = package.W;
        var runs = target.Elements(w + "r").ToArray();
        var runTexts = runs.Select(run => string.Concat(run.Elements().Select(node => node.Name == w + "t" ? node.Value :
            node.Name == w + "tab" ? "\t" : node.Name == w + "br" || node.Name == w + "cr" ? "\n" : ""))).ToArray();
        var original = string.Concat(runTexts);
        if (original == text)
            return false;
        if (runs.Length == 0)
        {
            target.Add(new XElement(w + "r", package.TextElements(text)));
            return true;
        }

        var prefix = 0;
        while (prefix < Math.Min(original.Length, text.Length) && original[prefix] == text[prefix])
            prefix++;
        if (prefix > 0 && char.IsHighSurrogate(original[prefix - 1]))
            prefix--;
        var suffix = 0;
        while (suffix < Math.Min(original.Length, text.Length) - prefix && original[^(suffix + 1)] == text[^(suffix + 1)])
            suffix++;
        if (suffix > 0 && char.IsLowSurrogate(original[^suffix]))
            suffix--;
        var oldEnd = original.Length - suffix;
        var newEnd = text.Length - suffix;
        var offset = 0;
        var consumed = prefix;
        for (var i = 0; i < runs.Length; i++)
        {
            var run = runTexts[i];
            var end = offset + run.Length;
            if (end >= prefix && offset <= oldEnd && (end > prefix || oldEnd == prefix || i == runs.Length - 1))
            {
                var startInRun = Math.Clamp(prefix - offset, 0, run.Length);
                var endInRun = Math.Clamp(oldEnd - offset, startInRun, run.Length);
                var take = end >= oldEnd ? newEnd - consumed : Math.Min(newEnd - consumed, endInRun - startInRun);
                if (take > 0 && char.IsHighSurrogate(text[consumed + take - 1]))
                    take--;
                var replacement = run[..startInRun] + text.Substring(consumed, take) + run[endInRun..];
                consumed += take;
                if (replacement != run)
                {
                    runs[i].Elements().Where(node => node.Name != w + "rPr").Remove();
                    runs[i].Add(package.TextElements(replacement));
                }
            }
            offset = end;
        }
        return true;
    }

    private static string ValidateComment(WordAddComment comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comment.Author);
        XmlConvert.VerifyXmlChars(comment.Author);
        if (comment.Initials is not null)
            XmlConvert.VerifyXmlChars(comment.Initials);
        return Normalize(comment.Text);
    }

    private static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return XmlConvert.VerifyXmlChars(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
