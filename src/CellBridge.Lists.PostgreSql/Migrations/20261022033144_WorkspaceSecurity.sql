REVOKE ALL ON SCHEMA cellbridge_lists FROM PUBLIC;

CREATE OR REPLACE FUNCTION cellbridge_lists.reject_authorization_anchor_rebinding()
RETURNS trigger LANGUAGE plpgsql AS $binding$
BEGIN
  IF ROW(NEW.authorization_mode,NEW.authorization_domain,NEW.authorization_scope)
     IS DISTINCT FROM ROW(OLD.authorization_mode,OLD.authorization_domain,OLD.authorization_scope) THEN
    RAISE EXCEPTION 'A workspace authorization anchor is immutable' USING ERRCODE='23514';
  END IF;
  RETURN NEW;
END $binding$;
REVOKE ALL ON FUNCTION cellbridge_lists.reject_authorization_anchor_rebinding() FROM PUBLIC;
CREATE OR REPLACE TRIGGER immutable_workspace_authorization_anchor
  BEFORE UPDATE OF authorization_mode,authorization_domain,authorization_scope ON cellbridge_lists.workspaces
  FOR EACH ROW EXECUTE FUNCTION cellbridge_lists.reject_authorization_anchor_rebinding();

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
