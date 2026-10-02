-- Version 1. Run using PostgreSqlStateStore.InitializeAsync from deployment tooling.
CREATE TABLE IF NOT EXISTS cellbridge_schema (version integer PRIMARY KEY);
INSERT INTO cellbridge_schema VALUES (1) ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS cellbridge_documents (
    resource_id uuid PRIMARY KEY,
    path_key text COLLATE "C" NOT NULL UNIQUE,
    state_version bigint NOT NULL
);
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
