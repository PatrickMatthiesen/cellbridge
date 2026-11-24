using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace CellBridge.Client.PowerPoint;

public sealed record PowerPointShape(uint Id, string Name, string? Text);

/// <summary>A slide identified by its presentation slide ID, in presentation order.</summary>
public sealed record PowerPointSlide(uint Id, IReadOnlyList<PowerPointShape> Shapes, string? Notes)
{
    /// <summary>The zero-based position of the slide.</summary>
    public int Position { get; init; }
}

public sealed record PowerPointSlideChange(uint SlideId, PowerPointSlide? Before, PowerPointSlide? After);

public abstract record PowerPointEdit;
public sealed record PowerPointSetShapeText(uint SlideId, uint ShapeId, string Text) : PowerPointEdit;
public sealed record PowerPointSetSlideNotes(uint SlideId, string Text) : PowerPointEdit;
public sealed record PowerPointAddSlide(uint? LayoutFromSlideId = null) : PowerPointEdit;
public sealed record PowerPointDeleteSlide(uint SlideId) : PowerPointEdit;

public sealed record PowerPointMergeResult(PowerPointPresentation? Presentation, uint? AddedSlideId, string? Conflict)
{
    public bool Succeeded => Presentation is not null;
}

/// <summary>Reads and edits an OOXML presentation package in memory.</summary>
public sealed class PowerPointPresentation
{
    private byte[] _bytes;

    private PowerPointPresentation(byte[] bytes) => _bytes = bytes;

    public static PowerPointPresentation Open(byte[] package)
    {
        ArgumentNullException.ThrowIfNull(package);
        PowerPointPackage.CheckSize(package);
        var bytes = package.ToArray();
        using var raw = new PowerPointPackage(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        using var document = PresentationDocument.Open(stream, false, new OpenSettings
        {
            MarkupCompatibilityProcessSettings = new MarkupCompatibilityProcessSettings(
                MarkupCompatibilityProcessMode.NoProcess, FileFormatVersions.Office2007),
        });
        if (document.DocumentType != PresentationDocumentType.Presentation ||
            document.PresentationPart?.Uri.OriginalString != "/" + raw.PresentationPart)
            throw new InvalidDataException("The package must be a .pptx presentation.");
        return new(bytes);
    }

    public byte[] ToArray() => _bytes.ToArray();

    public IReadOnlyList<PowerPointSlide> ListSlides()
    {
        using var package = new PowerPointPackage(_bytes);
        return package.ListSlides();
    }

    public PowerPointSlide ReadSlide(uint slideId)
    {
        using var package = new PowerPointPackage(_bytes);
        return package.ReadSlide(slideId);
    }

    public void SetShapeText(uint slideId, uint shapeId, string text)
    {
        text = NormalizeText(text);
        using var package = new PowerPointPackage(_bytes);
        package.SetShapeText(slideId, shapeId, text);
        _bytes = package.Save();
    }

    public void SetSlideNotes(uint slideId, string text)
    {
        text = NormalizeText(text);
        using var package = new PowerPointPackage(_bytes);
        package.SetSlideNotes(slideId, text);
        _bytes = package.Save();
    }

    /// <summary>Appends a slide using the specified slide's layout, or an available blank layout.</summary>
    public uint AddSlide(uint? layoutFromSlideId = null)
    {
        using var package = new PowerPointPackage(_bytes);
        var id = package.AddSlide(package.SelectLayout(layoutFromSlideId));
        _bytes = package.Save();
        return id;
    }

    public void DeleteSlide(uint slideId)
    {
        using var package = new PowerPointPackage(_bytes);
        package.DeleteSlide(slideId);
        _bytes = package.Save();
    }

    /// <summary>Reports changes in slide order, shape IDs, names and text, and speaker notes.</summary>
    public IReadOnlyList<PowerPointSlideChange> Diff(PowerPointPresentation latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        var before = ListSlides().ToDictionary(slide => slide.Id);
        var after = latest.ListSlides().ToDictionary(slide => slide.Id);
        var changes = new List<PowerPointSlideChange>();
        foreach (var id in before.Keys.Concat(after.Keys.Where(id => !before.ContainsKey(id))))
        {
            before.TryGetValue(id, out var previous);
            after.TryGetValue(id, out var next);
            if (previous is null || next is null || previous.Position != next.Position ||
                previous.Notes != next.Notes || !previous.Shapes.SequenceEqual(next.Shapes))
                changes.Add(new(id, previous, next));
        }

        return changes.AsReadOnly();
    }

    /// <summary>Applies an edit based on the original presentation to the latest presentation.</summary>
    public static PowerPointMergeResult Merge(
        PowerPointPresentation original, PowerPointPresentation latest, PowerPointEdit edit)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(edit);
        using var current = new PowerPointPackage(latest._bytes);
        var samePackage = ReferenceEquals(original, latest) || original._bytes.AsSpan().SequenceEqual(latest._bytes);
        using var baseline = samePackage ? null : new PowerPointPackage(original._bytes);
        uint? addedSlideId = null;
        try
        {
            switch (edit)
            {
                case PowerPointSetShapeText shape:
                    var shapeText = NormalizeText(shape.Text);
                    var currentShape = current.ShapeTarget(shape.SlideId, shape.ShapeId);
                    if (baseline is not null &&
                        !CanUpdate(baseline.ShapeTarget(shape.SlideId, shape.ShapeId), currentShape, shapeText))
                        return Conflict("The shape was replaced or its text changed.");
                    current.SetShapeText(shape.SlideId, shape.ShapeId, shapeText);
                    break;

                case PowerPointSetSlideNotes notes:
                    var notesText = NormalizeText(notes.Text);
                    var currentNotes = current.NotesTarget(notes.SlideId);
                    if (baseline is not null &&
                        !CanUpdate(baseline.NotesTarget(notes.SlideId), currentNotes, notesText))
                        return Conflict("The slide or its speaker notes changed.");
                    current.SetSlideNotes(notes.SlideId, notesText);
                    break;

                case PowerPointAddSlide add:
                    var layout = (baseline ?? current).SelectLayout(add.LayoutFromSlideId);
                    if (baseline is not null &&
                        (!current.HasLayout(layout) || baseline.LayoutFingerprint(layout) != current.LayoutFingerprint(layout) ||
                         (add.LayoutFromSlideId is uint template &&
                          (current.SelectLayout(template) != layout ||
                           baseline.SlideIdentity(template) != current.SlideIdentity(template)))))
                        return Conflict("The selected slide layout changed or was removed.");
                    addedSlideId = current.AddSlide(layout);
                    break;

                case PowerPointDeleteSlide delete:
                    if (baseline is not null &&
                        baseline.SlideFingerprint(delete.SlideId) != current.SlideFingerprint(delete.SlideId))
                        return Conflict("The slide or a part it references changed.");
                    current.DeleteSlide(delete.SlideId);
                    break;

                default:
                    throw new ArgumentException("Unknown PowerPoint edit.", nameof(edit));
            }

            return new(new PowerPointPresentation(current.Save()), addedSlideId, null);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
        {
            return Conflict(error.Message);
        }
    }

    private static bool CanUpdate(PowerPointPackage.TextTarget original, PowerPointPackage.TextTarget current, string text)
    {
        if (original.SlideIdentity != current.SlideIdentity)
            return false;
        if (original.Identity != current.Identity && !(original.Identity is null && current.Text == text))
            return false;
        return current.Text == text || original.Markup == current.Markup;
    }

    private static PowerPointMergeResult Conflict(string detail) => new(null, null, detail);

    private static string NormalizeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > PowerPointPackage.MaxTextCharacters)
            throw new ArgumentOutOfRangeException(nameof(text));
        return XmlConvert.VerifyXmlChars(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
