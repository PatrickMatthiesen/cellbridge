CREATE SCHEMA IF NOT EXISTS cellbridge_lists;
REVOKE ALL ON SCHEMA cellbridge_lists FROM PUBLIC;

CREATE TABLE IF NOT EXISTS cellbridge_lists.schema_version (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    version integer NOT NULL
);
INSERT INTO cellbridge_lists.schema_version(singleton, version)
VALUES (true, 2) ON CONFLICT (singleton) DO NOTHING;

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
    audit_count bigint NOT NULL DEFAULT 0 CHECK (audit_count >= 0),
    security_audit_count integer NOT NULL DEFAULT 0 CHECK (security_audit_count >= 0)
);
ALTER TABLE cellbridge_lists.workspaces ALTER COLUMN audit_count TYPE bigint;
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
CREATE TABLE IF NOT EXISTS cellbridge_lists.list_counters (
    workspace_id uuid NOT NULL,
    list_id uuid NOT NULL,
    identity_count bigint NOT NULL DEFAULT 0 CHECK (identity_count >= 0),
    next_number bigint NOT NULL DEFAULT 1 CHECK (next_number > 0),
    PRIMARY KEY (workspace_id, list_id),
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
CREATE TABLE IF NOT EXISTS cellbridge_lists.record_references (
    workspace_id uuid NOT NULL,
    source_list_id uuid NOT NULL,
    source_record_id uuid NOT NULL,
    field_id uuid NOT NULL,
    target_list_id uuid NOT NULL,
    target_record_id uuid NOT NULL,
    PRIMARY KEY (workspace_id, source_list_id, source_record_id, field_id),
    CONSTRAINT record_references_source_fk FOREIGN KEY (workspace_id, source_list_id, source_record_id)
      REFERENCES cellbridge_lists.records(workspace_id, list_id, record_id) ON DELETE CASCADE,
    CONSTRAINT record_references_target_fk FOREIGN KEY (workspace_id, target_list_id, target_record_id)
      REFERENCES cellbridge_lists.records(workspace_id, list_id, record_id) ON DELETE NO ACTION
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

DROP INDEX IF EXISTS cellbridge_lists.records_by_list;
CREATE INDEX IF NOT EXISTS record_references_by_target
  ON cellbridge_lists.record_references(workspace_id, target_list_id, target_record_id);
DROP INDEX IF EXISTS cellbridge_lists.audit_by_workspace;

DO $migration_rls$
BEGIN
  IF (SELECT version FROM cellbridge_lists.schema_version WHERE singleton) = 1 THEN
    ALTER TABLE cellbridge_lists.lists NO FORCE ROW LEVEL SECURITY;
    ALTER TABLE cellbridge_lists.list_schemas NO FORCE ROW LEVEL SECURITY;
    ALTER TABLE cellbridge_lists.records NO FORCE ROW LEVEL SECURITY;
    ALTER TABLE cellbridge_lists.record_tombstones NO FORCE ROW LEVEL SECURITY;
  END IF;
END $migration_rls$;

DO $migration$
BEGIN
  IF (SELECT version FROM cellbridge_lists.schema_version WHERE singleton) = 1 THEN
    INSERT INTO cellbridge_lists.list_counters(workspace_id,list_id,identity_count,next_number)
    SELECT l.workspace_id,l.list_id,COALESCE(t.identity_count,0),
      GREATEST(l.next_number,COALESCE(r.next_number,1))
    FROM cellbridge_lists.lists l
    LEFT JOIN (
      SELECT workspace_id,list_id,count(*) AS identity_count
      FROM cellbridge_lists.record_tombstones GROUP BY workspace_id,list_id
    ) t ON t.workspace_id=l.workspace_id AND t.list_id=l.list_id
    LEFT JOIN (
      SELECT workspace_id,list_id,max(number)+1 AS next_number
      FROM cellbridge_lists.records GROUP BY workspace_id,list_id
    ) r ON r.workspace_id=l.workspace_id AND r.list_id=l.list_id;

    INSERT INTO cellbridge_lists.record_references(
      workspace_id,source_list_id,source_record_id,field_id,target_list_id,target_record_id)
    SELECT r.workspace_id,r.list_id,r.record_id,(field->>'id')::uuid,
      (field->>'referenceListId')::uuid,(r.values_json->>(field->>'id'))::uuid
    FROM cellbridge_lists.records r
    JOIN cellbridge_lists.list_schemas s
      ON s.workspace_id=r.workspace_id AND s.list_id=r.list_id AND s.schema_version=r.schema_version
    CROSS JOIN LATERAL jsonb_array_elements(s.definition->'fields') field
    JOIN cellbridge_lists.records target
      ON target.workspace_id=r.workspace_id
      AND target.list_id=CASE WHEN field->>'type'='7'
        THEN (field->>'referenceListId')::uuid END
      AND target.record_id=CASE WHEN field->>'type'='7'
        AND jsonb_typeof(r.values_json->(field->>'id'))='string'
        THEN (r.values_json->>(field->>'id'))::uuid END
    WHERE field->>'type'='7'
      AND jsonb_typeof(r.values_json->(field->>'id'))='string';
  END IF;
END $migration$;

DO $setup$
DECLARE table_name text;
BEGIN
  FOREACH table_name IN ARRAY ARRAY['workspaces','memberships','lists','list_schemas','list_counters','list_grants','records','record_tombstones','create_receipts','record_references','saved_views','audit_events']
  LOOP
    EXECUTE format('ALTER TABLE cellbridge_lists.%I ENABLE ROW LEVEL SECURITY', table_name);
    EXECUTE format('ALTER TABLE cellbridge_lists.%I FORCE ROW LEVEL SECURITY', table_name);
    EXECUTE format('DROP POLICY IF EXISTS workspace_isolation ON cellbridge_lists.%I', table_name);
    EXECUTE format($policy$CREATE POLICY workspace_isolation ON cellbridge_lists.%I
      USING (workspace_id = NULLIF(current_setting('cellbridge.workspace_id', true), '')::uuid)
      WITH CHECK (workspace_id = NULLIF(current_setting('cellbridge.workspace_id', true), '')::uuid)$policy$, table_name);
  END LOOP;
END $setup$;

UPDATE cellbridge_lists.schema_version SET version=2 WHERE singleton;
