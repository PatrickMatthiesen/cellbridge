using CellBridge.AspNetCore;
using CellBridge.Authentication;
using Microsoft.AspNetCore.Identity;

namespace CellBridge.WebDemo;

/// <summary>Registers lookup for recipients represented by this WebDemo's existing Identity accounts.</summary>
public static class WebDemoSharingRecipients
{
    public static IServiceCollection AddWebDemoSharingRecipients(this IServiceCollection services)
    {
        services.AddScoped<ICellBridgeSharingRecipientResolver, WebDemoSharingRecipientResolver>();
        return services;
    }
}

internal sealed class WebDemoSharingRecipientResolver(UserManager<CellBridgeUser> users)
    : ICellBridgeSharingRecipientResolver
{
    private const string LocalSubjectPrefix = "local:";

    public async ValueTask<CellBridgeSharingRecipient?> ResolveAsync(string subject,
        CancellationToken cancellationToken = default)
    {
        if (!subject.StartsWith(LocalSubjectPrefix, StringComparison.Ordinal) ||
            subject.Length == LocalSubjectPrefix.Length)
            return null;

        var user = await users.FindByIdAsync(subject[LocalSubjectPrefix.Length..]);
        if (user is null || !user.Enabled || !StringComparer.Ordinal.Equals(user.Subject, subject)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        return new(user.Subject, user.UserName ?? user.Id, user.DisplayName, user.Email,
            IsActive: true, IsExternal: false);
    }
}
