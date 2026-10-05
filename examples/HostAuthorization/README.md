# Host-owned authorization consumer

This source consumer runs validated JWT authentication and an application-owned
permission service. It uses no sample Identity accounts and writes no CellBridge
grants. The imported owner is attribution only. The host policy allows
`example:writer` to edit, `example:reader` to read, and denies other subjects.

Run the complete check from the repository root:

```sh
python3 examples/HostAuthorization/verify.py
```

The script generates a disposable JWT signing key, starts this example's own
Aspire AppHost with `--isolated`, waits for health, checks download/catalog access,
commits a permission revision as a separate permission operator, checks revocation,
and stops only its AppHost. Tokens and keys remain in memory. .NET 10, Python 3
and the repository's Aspire CLI/SDK are required. Docker is not needed.

For interactive use, supply `Parameters__SigningKey` privately, then run:

```sh
aspire start --apphost examples/HostAuthorization/AppHost/AppHost.csproj --isolated --non-interactive
aspire wait authorization-consumer --apphost examples/HostAuthorization/AppHost/AppHost.csproj --non-interactive
aspire describe --apphost examples/HostAuthorization/AppHost/AppHost.csproj --non-interactive
aspire stop --apphost examples/HostAuthorization/AppHost/AppHost.csproj --non-interactive
```

Use JWTs with HS256, issuer `cellbridge-host-example`, audience `cellbridge`, a
future `exp`, and `sub` equal to `writer`, `reader`, or `permission-operator`.
Only the operator can POST to `/example/permissions/{resourceId}` with
`{"expectedRevision":1,"nextRevision":2,"access":"none"}`. Document Write does
not grant permission administration. The sample resource is
`0a0a19bc-9b47-4183-8764-41c117bb1f14`.

Both document storage and host snapshots are volatile. A production host needs a
durable permission source, immutable revision retention, and snapshot distribution
to every instance. Installing a revision alone changes no access; the coordinated
update is the effective change. See the [contract](../../docs/authentication.md#host-owned-policy).
The signing helper is a local fixture issuer, not a production identity service.
Desktop Office still needs its own compatible authentication exchange; this check
establishes API authorization behavior only.
