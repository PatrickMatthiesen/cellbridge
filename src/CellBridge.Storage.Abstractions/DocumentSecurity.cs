using System.Collections.Immutable;

namespace CellBridge.Storage.Abstractions;

public sealed record SubjectIdentity(string Subject, string Login, string DisplayName);

[Flags]
public enum DocumentAccess { None = 0, Read = 1, Write = 2 }

/// <summary>Export-safe reference to host-owned policy, never the policy data itself.</summary>
public sealed record DocumentAuthorizationBinding(string PolicyDomain, int ContractVersion, long Revision);

public sealed record DocumentSecurity(string? Owner, ImmutableDictionary<string, DocumentAccess> Grants,
    SubjectIdentity? CreatedBy, SubjectIdentity? ModifiedBy)
{
    /// <summary>Null retains the stored-grant policy. Bound documents require their exact host policy revision.</summary>
    public DocumentAuthorizationBinding? AuthorizationPolicy { get; init; }
    public static DocumentSecurity Empty { get; } = new(null,
        ImmutableDictionary<string, DocumentAccess>.Empty, null, null);

    public DocumentAccess AccessFor(string subject)
    {
        if (AuthorizationPolicy is not null) return DocumentAccess.None;
        var access = Owner == subject ? DocumentAccess.Read | DocumentAccess.Write : Grants.GetValueOrDefault(subject);
        return access.HasFlag(DocumentAccess.Write) ? access | DocumentAccess.Read : access;
    }

    public static DocumentSecurity Create(SubjectIdentity owner, SubjectIdentity? author = null) =>
        new(owner.Subject, ImmutableDictionary<string, DocumentAccess>.Empty, author ?? owner, author ?? owner);
}
