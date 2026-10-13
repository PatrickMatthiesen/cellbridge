CREATE SCHEMA IF NOT EXISTS cellbridge_lists;
REVOKE ALL ON SCHEMA cellbridge_lists FROM PUBLIC;

CREATE TABLE IF NOT EXISTS cellbridge_lists.schema_version (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    version integer NOT NULL
);
INSERT INTO cellbridge_lists.schema_version(singleton, version)
VALUES (true, 1) ON CONFLICT (singleton) DO NOTHING;

CREATE TABLE IF NOT EXISTS cellbridge_lists.templates (
    template_id uuid NOT NULL,
    version bigint NOT NULL CHECK (version > 0),
    definition jsonb NOT NULL,
    PRIMARY KEY (template_id, version)
);

CREATE TABLE IF NOT EXISTS cellbridge_lists.workspaces (
    workspace_id uuid PRIMARY KEY,
    name text NOT NULL,
    stored_bytes bigint NOT NULL DEFAULT 0 CHECK (stored_bytes >= 0),
    audit_count integer NOT NULL DEFAULT 0 CHECK (audit_count >= 0),
    security_audit_count integer NOT NULL DEFAULT 0 CHECK (security_audit_count >= 0)
);
CREATE TABLE IF NOT EXISTS cellbridge_lists.memberships (
    workspace_id uuid NOT NULL REFERENCES cellbridge_lists.workspaces(workspace_id) ON DELETE CASCADE,
    subject text NOT NULL,
    can_create_lists boolean NOT NULL DEFAULT false,
    can_manage_grants boolean NOT NULL DEFAULT false,
    PRIMARY KEY (workspace_id, subject)
);
CREATE TABLE IF NOT EXISTS cellbridge_lists.lists (
    workspace_id uuid NOT NULL REFERENCES cellbridge_lists.workspaces(workspace_id) ON DELETE CASCADE,
    list_id uuid NOT NULL,
    name text NOT NULL,
    schema_version bigint NOT NULL CHECK (schema_version > 0),
    template_id uuid NULL,
    template_version bigint NULL,
    number_prefix text NULL,
    next_number bigint NOT NULL DEFAULT 1 CHECK (next_number > 0),
    PRIMARY KEY (workspace_id, list_id),
    FOREIGN KEY (template_id, template_version) REFERENCES cellbridge_lists.templates(template_id, version)
);
CREATE TABLE IF NOT EXISTS cellbridge_lists.list_schemas (
    workspace_id uuid NOT NULL,
    list_id uuid NOT NULL,
    schema_version bigint NOT NULL CHECK (schema_version > 0),
    definition jsonb NOT NULL,
    PRIMARY KEY (workspace_id, list_id, schema_version),
    FOREIGN KEY (workspace_id, list_id) REFERENCES cellbridge_lists.lists(workspace_id, list_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS cellbridge_lists.list_grants (
    workspace_id uuid NOT NULL,
    list_id uuid NOT NULL,
    subject text NOT NULL,
    access integer NOT NULL CHECK (access >= 0 AND access <= 7),
    PRIMARY KEY (workspace_id, list_id, subject),
    FOREIGN KEY (workspace_id, list_id) REFERENCES cellbridge_lists.lists(workspace_id, list_id) ON DELETE CASCADE,
    FOREIGN KEY (workspace_id, subject) REFERENCES cellbridge_lists.memberships(workspace_id, subject) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS cellbridge_lists.records (
    workspace_id uuid NOT NULL,
    list_id uuid NOT NULL,
    record_id uuid NOT NULL,
    schema_version bigint NOT NULL,
    revision bigint NOT NULL CHECK (revision > 0),
    number bigint NOT NULL CHECK (number > 0),
    values_json jsonb NOT NULL,
    created_utc timestamptz NOT NULL,
    modified_utc timestamptz NOT NULL,
    created_by text NOT NULL,
    modified_by text NOT NULL,
    PRIMARY KEY (workspace_id, list_id, record_id),
    UNIQUE (workspace_id, list_id, number),
    FOREIGN KEY (workspace_id, list_id, schema_version) REFERENCES cellbridge_lists.list_schemas(workspace_id, list_id, schema_version)
);
CREATE TABLE IF NOT EXISTS cellbridge_lists.record_tombstones (
    workspace_id uuid NOT NULL,
    list_id uuid NOT NULL,
    record_id uuid NOT NULL,
    PRIMARY KEY (workspace_id, list_id, record_id),
    FOREIGN KEY (workspace_id, list_id) REFERENCES cellbridge_lists.lists(workspace_id, list_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS cellbridge_lists.create_receipts (
    workspace_id uuid NOT NULL,
    list_id uuid NOT NULL,
    record_id uuid NOT NULL,
    actor text NOT NULL,
    schema_version bigint NOT NULL,
    request_digest text NOT NULL,
    revision bigint NOT NULL,
    number bigint NOT NULL,
    created_utc timestamptz NOT NULL,
    expires_utc timestamptz NOT NULL,
    PRIMARY KEY (workspace_id, list_id, record_id),
    FOREIGN KEY (workspace_id, list_id) REFERENCES cellbridge_lists.lists(workspace_id, list_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS cellbridge_lists.saved_views (
    workspace_id uuid NOT NULL,
    list_id uuid NOT NULL,
    view_id uuid NOT NULL,
    definition jsonb NOT NULL,
    PRIMARY KEY (workspace_id, list_id, view_id),
    FOREIGN KEY (workspace_id, list_id) REFERENCES cellbridge_lists.lists(workspace_id, list_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS cellbridge_lists.audit_events (
    id bigint GENERATED ALWAYS AS IDENTITY,
    workspace_id uuid NOT NULL REFERENCES cellbridge_lists.workspaces(workspace_id) ON DELETE CASCADE,
    utc timestamptz NOT NULL,
    actor text NOT NULL,
    action text NOT NULL,
    list_id uuid NULL,
    record_id uuid NULL,
    revision bigint NULL,
    field_ids uuid[] NOT NULL DEFAULT '{}',
    target_subject text NULL,
    security boolean NOT NULL DEFAULT false,
    PRIMARY KEY (workspace_id, id)
);
ALTER TABLE cellbridge_lists.audit_events ADD COLUMN IF NOT EXISTS target_subject text NULL;

CREATE INDEX IF NOT EXISTS records_by_list ON cellbridge_lists.records(workspace_id, list_id, record_id);
CREATE INDEX IF NOT EXISTS audit_by_workspace ON cellbridge_lists.audit_events(workspace_id, id DESC);

DO $setup$
DECLARE table_name text;
BEGIN
  FOREACH table_name IN ARRAY ARRAY['workspaces','memberships','lists','list_schemas','list_grants','records','record_tombstones','create_receipts','saved_views','audit_events']
  LOOP
    EXECUTE format('ALTER TABLE cellbridge_lists.%I ENABLE ROW LEVEL SECURITY', table_name);
    EXECUTE format('ALTER TABLE cellbridge_lists.%I FORCE ROW LEVEL SECURITY', table_name);
    EXECUTE format('DROP POLICY IF EXISTS workspace_isolation ON cellbridge_lists.%I', table_name);
    EXECUTE format($policy$CREATE POLICY workspace_isolation ON cellbridge_lists.%I
      USING (workspace_id = NULLIF(current_setting('cellbridge.workspace_id', true), '')::uuid)
      WITH CHECK (workspace_id = NULLIF(current_setting('cellbridge.workspace_id', true), '')::uuid)$policy$, table_name);
  END LOOP;
END $setup$;
