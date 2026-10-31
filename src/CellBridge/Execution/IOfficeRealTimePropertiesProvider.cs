namespace CellBridge.AspNetCore;

/// <summary>Supplies authorized, document-scoped native Office real-time discovery properties.</summary>
public interface IOfficeRealTimePropertiesProvider
{
    IReadOnlyCollection<string> PropertyNames { get; }

    ValueTask<IReadOnlyDictionary<string, string>> GetPropertiesAsync(Guid resourceId,
        CellBridgeActor actor, string publicBase, CancellationToken cancellationToken);
}
