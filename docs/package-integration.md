# Embed CellBridge in another host

The beta targets .NET 10. Use `CellBridge.AspNetCore` with a storage provider;
the protocol and provider packages can also be consumed separately. See
[provider composition](storage-providers.md#consume-the-packages) and the
[package consumer](../examples/NuGetConsumer/README.md).

## Execute parsed SOAP requests

`AddCellBridge` registers `CellBridgeRequestProcessor` and
`CellBridgeDocumentService`. A host can call the processor without mapping
CellBridge's HTTP endpoints:

```csharp
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;

// Obtain these from validated host identity and file permission checks.
CellBridgeActor actor = new(new SubjectIdentity(subject, login, displayName))
{
    AccessLimit = new DocumentAccessLimit(resourceId, DocumentAccess.Read),
};
CellStorageRequest request = CellStorageRequestParser.Parse(soapXml);
CellStorageExecution execution = await processor.ExecuteAsync(
    request, "https://documents.example", actor, cancellationToken);
string responseXml = execution.Response.ToSoapEnvelope();
```

The processor uses the same implementation as `/_vti_bin/cellstorage.svc`,
including supported metadata, editor/session, lock and Cell operations, ordered
subrequest dependencies, current-state authorization and durable provider
coordination. Unsupported operations retain explicit protocol errors.

The host owns authentication, request admission limits, SOAP/MTOM parsing,
resolving MIME references into binary subrequest data, cancellation, response
serialization and transport headers. Use `MtomMessageParser` and
the host's MIME reference resolution for multipart requests. Inline SOAP alone does not cover
Office's MTOM uploads. Pass an HTTP(S) origin with no credentials, query,
fragment or non-root path. The processor preserves the input request and uses
the resolved document for canonical response URLs and metadata.

## Resource identities

The additive `CreateAsync(Guid resourceId, ...)` and
`ImportAsync(Guid resourceId, ...)` overloads accept stable host-selected GUIDs.
Existing overloads still generate identities. Both the path and ID are unique:
a conflict returns null and never replaces the existing document. Imports are
privileged operations and record the supplied owner and importing actor.

Protocols still use GUID resource identities. Hosts with arbitrary string IDs,
such as opaque WOPI IDs, must retain a stable mapping or supply their own
collision-safe GUID derivation. Host-selected GUIDs do not remove that obligation.
Explicit unknown or invalid ResourceIDs are never replaced with URL lookup.

## Request permissions

`DocumentAccessLimit` is an immutable ceiling bound to one resource. The service
intersects it with `ICellBridgeAccessEvaluator.Evaluate`; `Write` includes `Read`.
A ceiling for another resource grants no access. It cannot elevate the default
stored ACL, bypass current-state permission checks, or grant creation permission.
Resolve asynchronous request ceilings before executing the request. For host-owned
authorization, use the [versioned policy contract](authentication.md#host-owned-policy)
and its coordinated permission-update boundary. The source consumer demonstrates
creation bindings, exact snapshots and revocation without mirrored grants.

Evaluators run synchronously during coordinated operations. They must be bounded,
perform no I/O and return consistent permissions for the supplied document state.
They also evaluate editor-owner actors, which have no current request ceiling.
Do not depend on ambient HTTP state or require an `AccessLimit` to be present.
Persist permission changes through versioned document/coordination updates that
advance the affected editor graph knowledge; changing a mutable external
permission cache alone can change graph bytes without changing their version.
The legacy evaluator remains available for unbound beta integrations. It does not
replace the coordinated policy-update contract. The published `0.1.0-beta.1`
packages predate this new source API; use project references until a release
includes it.

## Accepted saves and external content

Both binary `CellExecution` and SOAP `CellStorageExecution` expose `AcceptedSaves`.
Each `AcceptedSave` identifies the resource, content version, immutable content
handle, receipt operation key and whether this was a receipt replay. Accepted
no-ops are included; queries and rejected saves are not. `IsReplay` means the
operation was accepted earlier and did not publish again. Durability depends on
the provider: in-memory receipts disappear at restart.

Inspect all receipts even if a later subrequest reports an error. If a later
storage/operation exception prevents returning a result, `AcceptedSaveException`
preserves known earlier receipts and the original exception. The processor can
return those receipts alongside a mapped `CellRequestFail` for a failed Cell
subrequest. An exception's receipts are not a complete journal: the failing
operation may still have an unknown commit outcome.

Cancellation stays cancellation and may prevent returning receipts. Recover by
replaying the same stable save request and inspecting its accepted receipt.
Current-version retries require the original writer, current Write access and
identical operation content. Superseded retries return a coherency error.
Do not assume an interrupted call rolled back every earlier operation.

Read the exact content handle from a receipt through the provider, rather than
rereading the document's current revision after another save. The handle is
protected by retained state snapshots, not an indefinite external delivery lease.
This API does not provide ordered delivery, an outbox or external atomic
publication. Hosts need their own recovery, serialization and destination CAS
before claiming reliable write-back. Replaying a receipt may be needed to
recover an interrupted external write; simply ignoring every replay can lose it.
CellBridge's revision history describes its own accepted saves and does not
implement an external file store's revision or lock domain.

## WOPI integration boundary

These APIs address the reusable execution, request permission and GUID identity
needs reported by [WopiHost PR #735](https://github.com/petrsvihlik/WopiHost/pull/735#issuecomment-5993115599).
Accepted receipts provide a narrower alternative to comparing version counters;
they are not a reliable post-publication callback.

Beta.1 did not provide shared WOPI/FSSHTTP leases or conditional lifecycle APIs.
The beta.2 candidate adds opt-in shared base-lock authority, recoverable publication
and lifecycle operations. The host must supply destination CAS and durable receipts.
Do not expose conflicting write paths outside that authority. Testing the existing WopiHost adapter against
CellBridge packages establishes package compatibility, not Office Online Server
editing qualification or that WopiHost has adopted these new APIs upstream.

For external write-back, shared WOPI/FSSHTTP locks and conditional state deletion,
follow [external host reliability](external-host-reliability.md). Accepted-save
receipts alone do not provide recoverable external delivery.
