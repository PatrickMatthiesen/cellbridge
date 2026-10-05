using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage;

/// <summary>Whole-provider copy/recovery. Source state is never changed. Only validated, complete destination state is published.</summary>
public sealed partial class PortableArchive(StorageLimits? storageLimits = null, PortableRecoveryLimits? recoveryLimits = null)
{
    private readonly StorageLimits _storage = storageLimits ?? new();
    private readonly PortableRecoveryLimits _archive = recoveryLimits ?? new();

    public async ValueTask<PortableArchiveInfo> ExportAsync(StorageProvider source, string outputPath,
        RecoveryContext context, CancellationToken cancellationToken = default)
    {
        CheckLimits(); CheckContext(context);
        var recovery = RecoveryStore(source);
        await recovery.CheckRecoveryAsync(cancellationToken);
        var snapshot = await recovery.CaptureRecoveryAsync(cancellationToken);
        var handles = ContentStoreReader.UniqueHandles(snapshot.Snapshots.SelectMany(StorageReferences.Handles))
            .OrderBy(h => h.Key, StringComparer.Ordinal).ToImmutableArray();
        var manifest = new PortableManifest(PortableManifest.CurrentVersion, Guid.NewGuid(), context, snapshot, handles);
        CheckManifest(manifest);
        await ValidateStateAsync(manifest, source.Content, cancellationToken);
        string destination = Path.GetFullPath(outputPath);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
                    StorageLimits.Check("recovery manifest bytes", bytes.LongLength, _archive.MaxManifestBytes);
                    long expanded = bytes.LongLength;
                    await using (var entry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open())
                        await entry.WriteAsync(bytes, cancellationToken);
                    for (int i = 0; i < handles.Length; i++)
                    {
                        expanded = checked(expanded + handles[i].Length);
                        StorageLimits.Check("recovery expanded bytes", expanded, _archive.MaxExpandedBytes);
                        await using var input = await source.Content.OpenReadAsync(handles[i], cancellationToken);
                        await using var entry = zip.CreateEntry(ObjectName(i), CompressionLevel.NoCompression).Open();
                        await CopyVerifiedAsync(input, entry, handles[i], cancellationToken);
                    }
                }
                StorageLimits.Check("recovery archive bytes", output.Length, _archive.MaxArchiveBytes);
                await output.FlushAsync(cancellationToken); output.Flush(flushToDisk: true);
            }
            // Reopen the finished artifact through the same validation path consumers use.
            using var staged = await StageAsync(temporary, context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            return staged.Info;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async ValueTask<PortableArchiveInfo> ValidateAsync(string archivePath, RecoveryContext context,
        CancellationToken cancellationToken = default)
    {
        using var staged = await StageAsync(archivePath, context, cancellationToken);
        return staged.Info;
    }

    public async ValueTask<RecoveryReceipt> ImportAsync(StorageProvider destination, string archivePath,
        RecoveryContext context, CancellationToken cancellationToken = default)
    {
        CheckLimits(); CheckContext(context);
        var recovery = RecoveryStore(destination);
        // Capability and schema failures must precede destination staging.
        await recovery.CheckRecoveryAsync(cancellationToken);
        using var staged = await StageAsync(archivePath, context, cancellationToken);
        var expected = new RecoveryReceipt(staged.Manifest.ArchiveId, staged.Info.Sha256,
            staged.Info.Documents, staged.Info.Snapshots);
        if (await recovery.FindRecoveryReceiptAsync(expected.OperationId, cancellationToken) is { } accepted)
        {
            ProviderRecovery.CheckReceipt(accepted, expected);
            return accepted;
        }
        // Early gate avoids charged orphans for known collisions and active coordination.
        var existing = await recovery.CaptureRecoveryAsync(cancellationToken);
        if (!existing.Heads.IsEmpty || !existing.Snapshots.IsEmpty || !existing.Receipts.IsEmpty)
        {
            // Another identical importer may have committed since the first receipt lookup.
            if (await recovery.FindRecoveryReceiptAsync(expected.OperationId, cancellationToken) is { } concurrent)
            {
                ProviderRecovery.CheckReceipt(concurrent, expected);
                return concurrent;
            }
            throw new RecoveryConflictException("Recovery destination must be empty, including retired identities and recovery receipts.");
        }
        ProviderRecovery.CheckAdmission(staged.Manifest.State, destination.Limits, DateTime.UtcNow);
        if (_storage != destination.Limits)
        {
            var admission = new PortableArchive(destination.Limits, _archive);
            admission.CheckManifest(staged.Manifest);
            await admission.ValidateStateAsync(staged.Manifest, staged.Content, cancellationToken);
        }
        var remapping = new Dictionary<string, ContentHandle>(StringComparer.Ordinal);
        foreach (var handle in staged.Manifest.Objects)
        {
            await using var input = await staged.Content.OpenReadAsync(handle, cancellationToken);
            var imported = await destination.Content.WriteAsync(input, cancellationToken);
            if (imported.Length != handle.Length || imported.Sha256 != handle.Sha256)
                throw new StorageCorruptionException("Destination changed imported content.");
            // Do not trust a custom provider's returned handle without reading it back.
            await using var verify = await destination.Content.OpenReadAsync(imported, cancellationToken);
            await CopyVerifiedAsync(verify, Stream.Null, imported, cancellationToken);
            remapping.Add(handle.Key, imported);
        }
        var snapshot = Remap(staged.Manifest.State, remapping);
        return await recovery.ImportRecoveryAsync(snapshot, expected, cancellationToken);
    }

    /// <summary>Copies through a validated archive. Final cutover requires stopping source writers and a fresh export.</summary>
    public async ValueTask<RecoveryReceipt> MigrateAsync(StorageProvider source, StorageProvider destination,
        string archivePath, RecoveryContext context, CancellationToken cancellationToken = default)
    {
        await ExportAsync(source, archivePath, context, cancellationToken);
        return await ImportAsync(destination, archivePath, context, cancellationToken);
    }

    private void CheckLimits() { _storage.Validate(); _archive.Validate(); }
    private static IProviderRecoveryStore RecoveryStore(StorageProvider provider) => provider.State as IProviderRecoveryStore
        ?? throw new NotSupportedException("The selected state provider has no atomic recovery capability.");
    private static string ObjectName(int ordinal) => $"objects/{ordinal:D8}";

    private static void CheckContext(RecoveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(context.Authorization);
        context.Authorization.Validate();
        if (context.ExternalDestinations.IsDefault || context.ExternalDestinations.Any(e => e is null || e.BindingId == Guid.Empty ||
            string.IsNullOrWhiteSpace(e.Destination) || string.IsNullOrWhiteSpace(e.ReceiptDomain)) ||
            context.ExternalDestinations.Select(e => e.BindingId).Distinct().Count() != context.ExternalDestinations.Length)
            throw new NotSupportedException("Explicit unique external destination/receipt contexts are required for bound documents.");
    }

    private void CheckManifest(PortableManifest manifest)
    {
        if (manifest.ArchiveVersion != PortableManifest.CurrentVersion || manifest.ArchiveId == Guid.Empty)
            throw new NotSupportedException("Unsupported portable archive schema or empty archive identity.");
        CheckContext(manifest.Context);
        if (manifest.Objects.IsDefault) throw new StorageCorruptionException("Missing archive object inventory.");
        StorageLimits.Check("recovery snapshots", manifest.State.Snapshots.Length, _archive.MaxSnapshots);
        StorageLimits.Check("recovery entries", manifest.Objects.Length + 1L, _archive.MaxEntries);
        ProviderRecovery.CheckAdmission(manifest.State, _storage, DateTime.MinValue, requireExpired: false);
        if (manifest.State.Receipts.Any(r => r.OperationId == manifest.ArchiveId))
            throw new StorageCorruptionException("Archive identity collides with retained receipt.");
        var required = ContentStoreReader.UniqueHandles(manifest.State.Snapshots.SelectMany(StorageReferences.Handles))
            .ToDictionary(h => h.Key, StringComparer.Ordinal);
        if (manifest.Objects.Select(h => h.Key).Distinct(StringComparer.Ordinal).Count() != manifest.Objects.Length ||
            required.Count != manifest.Objects.Length || manifest.Objects.Any(h => !required.TryGetValue(h.Key, out var expected) || expected != h ||
                string.IsNullOrWhiteSpace(h.Key) || h.Length < 0 || h.Sha256.Length != 64 || h.Sha256.Any(c => !char.IsAsciiHexDigit(c)) ||
                h.Sha256 != h.Sha256.ToLowerInvariant()))
            throw new StorageCorruptionException("Archive inventory does not exactly match retained content references.");
        foreach (var handle in manifest.Objects) StorageLimits.Check("recovery object bytes", handle.Length, _storage.MaxObjectBytes);
        foreach (var state in manifest.State.Snapshots)
            if (state.Publication is { } publication && !manifest.Context.ExternalDestinations.Any(e =>
                    e.BindingId == publication.BindingId && e.Destination == publication.Destination))
                throw new RecoveryConflictException("An external delivery binding lacks a compatible receipt-domain context.");
    }

    private async ValueTask<StagedArchive> StageAsync(string path, RecoveryContext context, CancellationToken cancellationToken)
    {
        CheckLimits(); CheckContext(context);
        var staged = new StagedArchive();
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            StorageLimits.Check("recovery archive bytes", input.Length, _archive.MaxArchiveBytes);
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
            input.Position = 0;
            CheckZipDirectory(input);
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            StorageLimits.Check("recovery entries", zip.Entries.Count, _archive.MaxEntries);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                if (!entries.TryAdd(entry.FullName, entry)) throw new StorageCorruptionException("Duplicate archive entry.");
                expanded = checked(expanded + entry.Length);
                StorageLimits.Check("recovery expanded bytes", expanded, _archive.MaxExpandedBytes);
            }
            if (!entries.TryGetValue("manifest.json", out var metadata)) throw new StorageCorruptionException("Missing recovery manifest.");
            StorageLimits.Check("recovery manifest bytes", metadata.Length, Math.Min(_archive.MaxManifestBytes, Array.MaxLength));
            byte[] bytes = new byte[(int)metadata.Length];
            await using (var entry = metadata.Open())
            {
                await entry.ReadExactlyAsync(bytes, cancellationToken);
                if (await entry.ReadAsync(new byte[1], cancellationToken) != 0) throw new StorageCorruptionException("Manifest length mismatch.");
            }
            staged.Manifest = RecoveryJson.Read<PortableManifest>(bytes);
            CheckManifest(staged.Manifest);
            if (JsonSerializer.Serialize(staged.Manifest.Context) != JsonSerializer.Serialize(context))
                throw new RecoveryConflictException("Archive authorization or external destination context differs from trusted destination configuration.");
            if (entries.Count != staged.Manifest.Objects.Length + 1) throw new StorageCorruptionException("Unexpected archive entries.");
            for (int i = 0; i < staged.Manifest.Objects.Length; i++)
            {
                var handle = staged.Manifest.Objects[i];
                if (!entries.TryGetValue(ObjectName(i), out var entry) || entry.Length != handle.Length)
                    throw new StorageCorruptionException("Incomplete archive content inventory.");
                await using var content = entry.Open();
                await using var output = staged.Content.Create(handle, i);
                await CopyVerifiedAsync(content, output, handle, cancellationToken);
            }
            await ValidateStateAsync(staged.Manifest, staged.Content, cancellationToken);
            staged.Info = new(staged.Manifest.ArchiveId, staged.Manifest.State.Heads.Length,
                staged.Manifest.State.Snapshots.Length, staged.Manifest.Objects.Length, digest);
            return staged;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or JsonException or OverflowException or ArgumentException or NullReferenceException)
        {
            staged.Dispose(); throw new StorageCorruptionException("Invalid or incompatible portable archive.", ex);
        }
        catch { staged.Dispose(); throw; }
    }

    private static async ValueTask CopyVerifiedAsync(Stream input, Stream output, ContentHandle handle, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long length = 0;
        while (length < handle.Length)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, handle.Length - length)), cancellationToken);
            if (read == 0) throw new StorageCorruptionException("Truncated recovery content.");
            length += read; hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0 ||
            Convert.ToHexStringLower(hash.GetHashAndReset()) != handle.Sha256)
            throw new StorageCorruptionException("Recovery content integrity failure.");
    }

    private static ProviderSnapshot Remap(ProviderSnapshot snapshot, IReadOnlyDictionary<string, ContentHandle> handles)
    {
        ContentHandle Map(ContentHandle h) => handles[h.Key];
        ImmutableArray<PartitionState> Partitions(ImmutableArray<PartitionState> partitions) => partitions.Select(p => p with
        { Content = Map(p.Content), Elements = p.Elements.Select(e => e with { Payload = Map(e.Payload) }).ToImmutableArray() }).ToImmutableArray();
        return snapshot with { Snapshots = snapshot.Snapshots.Select(s => s with
        {
            Content = Map(s.Content), Partitions = Partitions(s.Partitions),
            Receipts = s.Receipts.Select(r => r with { Response = r.Response is null ? null : Map(r.Response) }).ToImmutableArray(),
            Revisions = s.Revisions.Select(r => r with { Content = Map(r.Content), Partitions = Partitions(r.Partitions) }).ToImmutableArray(),
            Publication = s.Publication is null ? null : s.Publication with
            { Pending = s.Publication.Pending.Select(r => r with { Content = Map(r.Content) }).ToImmutableArray() },
        }).ToImmutableArray() };
    }

    private sealed class StagedArchive : IDisposable
    {
        public PortableManifest Manifest { get; set; } = null!;
        public PortableArchiveInfo Info { get; set; } = null!;
        public StageContent Content { get; } = new();
        public void Dispose() => Content.Dispose();
    }

    private sealed class StageContent : IContentStore, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cellbridge-recovery-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, (ContentHandle Handle, string Path)> _paths = new(StringComparer.Ordinal);
        public bool Durable => false; public bool Shared => false;
        public StageContent()
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_root);
            else Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public Stream Create(ContentHandle handle, int ordinal)
        {
            var path = Path.Combine(_root, ordinal.ToString("D8")); _paths.Add(handle.Key, (handle, path));
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_paths.TryGetValue(handle.Key, out var entry) || entry.Handle != handle)
                throw new StorageCorruptionException("Missing validated recovery content.");
            return ValueTask.FromResult<Stream>(new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous));
        }
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
