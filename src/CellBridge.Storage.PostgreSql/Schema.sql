-- Legacy schema-6 bootstrap. Explicit EF migrations advance this to the current
-- schema; initialize databases with the setup tool rather than this file alone.
CREATE TABLE IF NOT EXISTS cellbridge_schema (version integer PRIMARY KEY);
INSERT INTO cellbridge_schema SELECT 6 WHERE NOT EXISTS (SELECT 1 FROM cellbridge_schema);
CREATE TABLE IF NOT EXISTS cellbridge_documents (
    resource_id uuid PRIMARY KEY,
    path_key text COLLATE "C" NOT NULL,
    state_version bigint NOT NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
-- Explicit initialization upgrades schema 3, 4 or 5. Old hosts fail their version health check.
ALTER TABLE cellbridge_documents ADD COLUMN IF NOT EXISTS is_deleted boolean NOT NULL DEFAULT false;
ALTER TABLE cellbridge_documents DROP CONSTRAINT IF EXISTS cellbridge_documents_path_key_key;
CREATE UNIQUE INDEX IF NOT EXISTS cellbridge_live_paths ON cellbridge_documents(path_key) WHERE NOT is_deleted;
UPDATE cellbridge_schema SET version=6 WHERE version IN (3,4,5);
CREATE TABLE IF NOT EXISTS cellbridge_states (
    resource_id uuid NOT NULL,
    state_version bigint NOT NULL,
    state_json text NOT NULL,
    PRIMARY KEY (resource_id, state_version)
);
-- Rebuildable indexes of the current authoritative snapshot. These contain no graph
-- or payload bytes and are excluded from the logical state/content quota ledger.
CREATE TABLE IF NOT EXISTS cellbridge_read_heads (
    resource_id uuid PRIMARY KEY,
    state_version bigint NOT NULL,
    lifecycle_generation bigint NOT NULL,
    revision_count integer NOT NULL CHECK (revision_count > 0),
    metadata_json text NOT NULL
);
CREATE TABLE IF NOT EXISTS cellbridge_revision_summaries (
    resource_id uuid NOT NULL,
    lifecycle_generation bigint NOT NULL,
    revision_number numeric(20,0) NOT NULL CHECK (revision_number BETWEEN 1 AND 18446744073709551615),
    summary_json text NOT NULL,
    PRIMARY KEY (resource_id, lifecycle_generation, revision_number)
);
CREATE TABLE IF NOT EXISTS cellbridge_objects (
    object_key text PRIMARY KEY,
    length bigint NOT NULL CHECK (length >= 0),
    sha256 text NOT NULL
);
CREATE TABLE IF NOT EXISTS cellbridge_chunks (
    object_key text NOT NULL REFERENCES cellbridge_objects(object_key),
    ordinal integer NOT NULL,
    bytes bytea NOT NULL,
    PRIMARY KEY (object_key, ordinal)
);
CREATE TABLE IF NOT EXISTS cellbridge_usage (
    singleton boolean PRIMARY KEY CHECK (singleton),
    stored_bytes bigint NOT NULL CHECK (stored_bytes >= 0),
    document_count bigint NOT NULL CHECK (document_count >= 0),
    max_stored_bytes bigint NOT NULL CHECK (max_stored_bytes > 0),
    max_documents bigint NOT NULL CHECK (max_documents > 0)
);
INSERT INTO cellbridge_usage
SELECT true,
    COALESCE((SELECT SUM(length) FROM cellbridge_objects),0) +
    COALESCE((SELECT SUM(octet_length(state_json)) FROM cellbridge_states),0),
    (SELECT COUNT(*) FROM cellbridge_documents), 10737418240, 10000
ON CONFLICT DO NOTHING;
