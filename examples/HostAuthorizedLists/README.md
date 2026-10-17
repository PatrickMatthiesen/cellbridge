# Host-authorized Lists example

This source example keeps list data in CellBridge PostgreSQL tables and asks an asynchronous host provider for every
operation. It does not create CellBridge membership or grant rows. The example provider accepts one trusted subject
and tenant so the setup stays small. Replace it with the application's permission service.

Pass an owner connection, a restricted runtime connection, and the runtime role name:

```sh
dotnet run --project examples/HostAuthorizedLists -- \
  "$LISTS_ADMIN_CONNECTION" "$LISTS_RUNTIME_CONNECTION" cellbridge_lists_runtime
```

The default setup does not cache decisions. `Program.cs` shows the separate cache decorator registration. Keep the
cache disabled when every operation must reach the host authority.
