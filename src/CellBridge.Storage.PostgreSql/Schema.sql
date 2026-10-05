-- Current development schema. Initialize a fresh database with the setup tool.
CREATE TABLE IF NOT EXISTS cellbridge_schema (version integer PRIMARY KEY);
INSERT INTO cellbridge_schema SELECT 4 WHERE NOT EXISTS (SELECT 1 FROM cellbridge_schema);
CREATE TABLE IF NOT EXISTS cellbridge_documents (
    resource_id uuid PRIMARY KEY,
    path_key text COLLATE "C" NOT NULL,
    state_version bigint NOT NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
-- Explicit initialization upgrades schema 3. Old hosts fail their version health check.
ALTER TABLE cellbridge_documents ADD COLUMN IF NOT EXISTS is_deleted boolean NOT NULL DEFAULT false;
ALTER TABLE cellbridge_documents DROP CONSTRAINT IF EXISTS cellbridge_documents_path_key_key;
CREATE UNIQUE INDEX IF NOT EXISTS cellbridge_live_paths ON cellbridge_documents(path_key) WHERE NOT is_deleted;
UPDATE cellbridge_schema SET version=4 WHERE version=3;
CREATE TABLE IF NOT EXISTS cellbridge_states (
    resource_id uuid NOT NULL,
    state_version bigint NOT NULL,
    state_json text NOT NULL,
    PRIMARY KEY (resource_id, state_version)
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
