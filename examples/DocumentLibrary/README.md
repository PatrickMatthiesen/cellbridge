# Documents and Lists composition

This small host maps the CellBridge Documents and Lists endpoint families in one
ASP.NET Core app. It stores document state in memory and uses PostgreSQL for
Lists. The document disappears when the process stops. The Lists database must
already have the CellBridge schema, workspace and grants configured.

The example login accepts `owner` and `reader` with password `example-only`.
Those credentials are for local demonstration only. Replace
`ExampleLoginAuthenticator` with the application's account system before
deployment. The host maps authenticated principals to stable CellBridge and
Lists subjects in `Program.cs`.

The sample Lists authority grants the owner all actions and the reader list
discovery and read actions for one fixed workspace and tenant. Replace
`ExampleListAuthorization` with a provider backed by the application's own
permission store. Hosts must also provision that workspace and grant the
restricted runtime database role the access required by the Lists provider.
See the [host-authorized Lists example](../HostAuthorizedLists/README.md) for a
complete PostgreSQL setup and permission-store example. It uses its own
authorization domain and workspace, so it does not provision this example's
fixed domain or workspace ID.

Build and run after `python3 tools/verify_packages.py` has built the current
packages:

```sh
dotnet build examples/DocumentLibrary/CellBridge.DocumentLibrary.csproj -c Release \
  --configfile artifacts/packages/NuGet.Config \
  -p:RestoreConfigFile=artifacts/packages/NuGet.Config \
  -p:RestorePackagesPath=artifacts/packages/consumer-cache

ConnectionStrings__lists="$LISTS_RUNTIME_CONNECTION" \
  dotnet run -c Release --no-build --project examples/DocumentLibrary
```

Provision the `example-tenant` workspace with ID
`b2cb2be3-9f25-48e8-95df-92fd21cb0918` and policy domain
`example:document-library`. Configure a restricted runtime role before
connecting it. The host does not create or migrate database objects at startup.

The package-fed tests exercise document discovery, Lists discovery, the actor
subjects passed to both endpoint families, denied access, and the packaged
cookie login's antiforgery check. They do not test Office desktop editing.
