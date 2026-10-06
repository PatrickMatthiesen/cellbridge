# Upgrade from beta.1

`0.1.0-beta.2` is a release candidate. It is not a published release until the
release record identifies the final source commit and public package evidence.
Use the same version for all nine CellBridge packages. The immutable beta.1
packages and their historical release notes remain unchanged.

## Prepare and migrate

1. Record the installed package versions, database schema and content provider.
   Keep the previous binaries and configuration for recovery.
2. Stop every writer using the database, including external publication workers.
   Back up PostgreSQL. For filesystem content, also preserve every immutable
   object referenced by that database recovery point. Test recovery into a
   separate environment before upgrading the live installation.
3. Update package references to `0.1.0-beta.2` and build the consuming application.
   Restore from the candidate feed before publication, or NuGet.org afterward.
4. Run the matching `CellBridge.Storage.Setup` tool against the stopped database.
   It explicitly migrates storage schema 3 to 4, preserves existing documents,
   and initializes recovery schema 1. Schema-3 writers fail closed afterward.
   Older development schemas require a separate reviewed migration or recreation.
5. Start one upgraded host. Verify provider health, existing document identities,
   permissions, downloads and a save/reopen cycle before enabling other writers.
   Start publication workers only after checking their destination binding and
   durable receipt store.

The setup and portability tools are repository tools, not additional NuGet
libraries. Use tools built from the same candidate commit as the packages.

## Changed contracts

Request validation is stricter. Hosts constructing SOAP or parsed request models
must supply schema-required versions, identities, tokens and operation data.
Do not rely on the previous parser's defaults for omitted required fields.
Handle protocol error replies explicitly; HTTP success alone does not establish
a successful Cell operation.

External publication, shared base-lock authority and conditional lifecycle
operations are opt-in host integrations. An accepted-save notification alone
does not update an application's file safely. Adopt the destination revision CAS
and durable receipt contract described in [external host reliability](external-host-reliability.md).

Revision history seeds only known current state for legacy documents. Upgrading
does not invent earlier revisions. Pending delivery and retained history consume
storage; configured limits reject admission rather than discard accepted work.

[Portable recovery](provider-portability.md) supports stored-grant authorization
with matching subject and external-destination contexts. It rejects documents
bound to external host policy, including retained snapshots. Restore the host's
own coordinated policy and destination receipt stores through its recovery
procedure. Matching subject names alone does not establish equivalent authority.

## Rollback

Do not point beta.1 binaries at the migrated database. There is no schema-4-to-3
downgrade. Stop upgraded writers and restore the pre-upgrade database, matching
filesystem objects and previous application configuration into an isolated
installation. Confirm identities and permissions before switching traffic.
That recovery discards changes made after the backup unless a separate reviewed
reconciliation preserves them.

## Candidate validation

Run `python tools/testing/run.py --packages` with this workspace's Aspire stopped.
It also runs the durable package-consumer check against disposable PostgreSQL.
Then run
`python tools/check_release.py --version 0.1.0-beta.2 --commit <candidate-sha>`
from a clean, unchanged candidate commit. Keep the tested packages and symbols;
do not repack different source under the same version.

Package and server tests do not close the intermittent Word issue. Record repeated
single-save Word text/image and Excel desktop results separately before claiming
client qualification.
