namespace CellBridge.Storage.Abstractions;

/// <summary>Optionally reads immutable objects together without a query per object.</summary>
public interface IContentBatchReader
{
    /// <summary>Returns every requested object by key, or fails if any object is missing or corrupt.</summary>
    ValueTask<IReadOnlyDictionary<string, byte[]>> ReadManyAsync(IReadOnlyCollection<ContentHandle> handles,
        CancellationToken cancellationToken = default);
}
