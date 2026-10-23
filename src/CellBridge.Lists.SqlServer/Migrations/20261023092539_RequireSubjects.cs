using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CellBridge.Lists.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class RequireSubjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Repair databases initialized by the first non-shipping foundation.
            // Fresh databases already have NOT NULL from the corrected initial migration.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('cellbridge_lists.memberships') AND name='subject' AND is_nullable=1)
                   OR EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('cellbridge_lists.list_grants') AND name='subject' AND is_nullable=1)
                BEGIN
                    ALTER SECURITY POLICY cellbridge_lists.memberships_isolation WITH (STATE=OFF);
                    ALTER SECURITY POLICY cellbridge_lists.list_grants_isolation WITH (STATE=OFF);
                    IF EXISTS (SELECT 1 FROM cellbridge_lists.memberships WHERE subject IS NULL)
                       OR EXISTS (SELECT 1 FROM cellbridge_lists.list_grants WHERE subject IS NULL)
                        THROW 51003, 'Null Lists identities require explicit repair; no data was changed.', 1;
                    ALTER TABLE cellbridge_lists.list_grants DROP CONSTRAINT FK_list_grants_memberships_workspace_id_subject_key;
                    ALTER TABLE cellbridge_lists.list_grants DROP CONSTRAINT PK_list_grants;
                    ALTER TABLE cellbridge_lists.memberships DROP CONSTRAINT PK_memberships;
                    ALTER TABLE cellbridge_lists.list_grants DROP COLUMN subject_key;
                    ALTER TABLE cellbridge_lists.memberships DROP COLUMN subject_key;
                    ALTER TABLE cellbridge_lists.list_grants ALTER COLUMN subject varbinary(max) NOT NULL;
                    ALTER TABLE cellbridge_lists.memberships ALTER COLUMN subject varbinary(max) NOT NULL;
                    ALTER TABLE cellbridge_lists.memberships ADD subject_key AS ISNULL(CONVERT(binary(32), HASHBYTES('SHA2_256', [subject])), CONVERT(binary(32), 0x)) PERSISTED;
                    ALTER TABLE cellbridge_lists.list_grants ADD subject_key AS ISNULL(CONVERT(binary(32), HASHBYTES('SHA2_256', [subject])), CONVERT(binary(32), 0x)) PERSISTED;
                    ALTER TABLE cellbridge_lists.memberships ADD CONSTRAINT PK_memberships PRIMARY KEY (workspace_id,subject_key);
                    ALTER TABLE cellbridge_lists.list_grants ADD CONSTRAINT PK_list_grants PRIMARY KEY (workspace_id,list_id,subject_key);
                    ALTER TABLE cellbridge_lists.list_grants ADD CONSTRAINT FK_list_grants_memberships_workspace_id_subject_key
                        FOREIGN KEY (workspace_id,subject_key) REFERENCES cellbridge_lists.memberships(workspace_id,subject_key);
                    ALTER SECURITY POLICY cellbridge_lists.memberships_isolation WITH (STATE=ON);
                    ALTER SECURITY POLICY cellbridge_lists.list_grants_isolation WITH (STATE=ON);
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The preceding frozen model also requires subjects. Removing this
            // repair migration must not relax the identity constraint.
        }
    }
}
