using System.Collections.Immutable;

namespace CellBridge.Storage.Abstractions;

public sealed record SubjectIdentity(string Subject, string Login, string DisplayName);

[Flags]
public enum DocumentAccess { None = 0, Read = 1, Write = 2 }

public sealed record DocumentSecurity(string? Owner, ImmutableDictionary<string, DocumentAccess> Grants,
    SubjectIdentity? CreatedBy, SubjectIdentity? ModifiedBy)
{
    public static DocumentSecurity Empty { get; } = new(null,
        ImmutableDictionary<string, DocumentAccess>.Empty, null, null);

    public DocumentAccess AccessFor(string subject)
    {
        var access = Owner == subject ? DocumentAccess.Read | DocumentAccess.Write : Grants.GetValueOrDefault(subject);
        return access.HasFlag(DocumentAccess.Write) ? access | DocumentAccess.Read : access;
    }

    public static DocumentSecurity Create(SubjectIdentity owner, SubjectIdentity? author = null) =>
        new(owner.Subject, ImmutableDictionary<string, DocumentAccess>.Empty, author ?? owner, author ?? owner);
}
