CREATE FUNCTION cellbridge_lists.reserve_create(
  p_workspace uuid, p_list uuid, p_record uuid, p_values text, p_max_record bigint,
  p_receipt_bytes bigint, p_audit_bytes bigint, p_max_records bigint,
  p_max_bytes bigint, p_max_audits bigint)
RETURNS TABLE(status integer, value_bytes bigint, number bigint, utc timestamptz, stored_bytes bigint, audit_count bigint, security_audit_count integer)
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog, pg_temp AS $function$
DECLARE
  v_bytes bigint;
  v_claimed integer;
  v_workspace cellbridge_lists.workspaces%ROWTYPE;
  v_counter cellbridge_lists.list_counters%ROWTYPE;
  v_next_number bigint;
  v_identity_count bigint;
  v_stored_bytes bigint;
  v_audit_count bigint;
BEGIN
  v_bytes := octet_length((p_values::jsonb)::text)::bigint;
  IF v_bytes > p_max_record THEN
    RETURN QUERY SELECT 1,v_bytes,NULL::bigint,NULL::timestamptz,NULL::bigint,NULL::bigint,NULL::integer; RETURN;
  END IF;
  INSERT INTO cellbridge_lists.record_tombstones(workspace_id,list_id,record_id)
    VALUES (p_workspace,p_list,p_record) ON CONFLICT DO NOTHING;
  GET DIAGNOSTICS v_claimed = ROW_COUNT;
  IF v_claimed = 0 THEN
    RETURN QUERY SELECT 2,v_bytes,NULL::bigint,NULL::timestamptz,NULL::bigint,NULL::bigint,NULL::integer; RETURN;
  END IF;
  SELECT w.* INTO v_workspace FROM cellbridge_lists.workspaces w
    WHERE w.workspace_id=p_workspace FOR UPDATE;
  IF NOT FOUND THEN
    RETURN QUERY SELECT 5,v_bytes,NULL::bigint,NULL::timestamptz,NULL::bigint,NULL::bigint,NULL::integer; RETURN;
  END IF;
  SELECT c.* INTO v_counter FROM cellbridge_lists.list_counters c
    WHERE c.workspace_id=p_workspace AND c.list_id=p_list FOR UPDATE;
  IF NOT FOUND THEN
    RETURN QUERY SELECT 5,v_bytes,NULL::bigint,NULL::timestamptz,NULL::bigint,NULL::bigint,NULL::integer; RETURN;
  END IF;
  IF p_max_records IS NOT NULL AND v_counter.identity_count >= p_max_records THEN
    RETURN QUERY SELECT 3,v_bytes,NULL::bigint,NULL::timestamptz,NULL::bigint,NULL::bigint,NULL::integer; RETURN;
  END IF;
  v_identity_count := v_counter.identity_count + 1;
  v_next_number := v_counter.next_number + 1;
  v_stored_bytes := v_workspace.stored_bytes + (v_bytes + p_receipt_bytes + 128 + p_audit_bytes);
  v_audit_count := v_workspace.audit_count + 1;
  IF (p_max_bytes IS NOT NULL AND v_stored_bytes > p_max_bytes) OR
     (p_max_audits IS NOT NULL AND v_audit_count > p_max_audits) THEN
    RETURN QUERY SELECT 4,v_bytes,NULL::bigint,NULL::timestamptz,NULL::bigint,NULL::bigint,NULL::integer; RETURN;
  END IF;
  UPDATE cellbridge_lists.workspaces SET stored_bytes=v_stored_bytes,audit_count=v_audit_count
    WHERE workspace_id=p_workspace;
  UPDATE cellbridge_lists.list_counters SET next_number=v_next_number,identity_count=v_identity_count
    WHERE workspace_id=p_workspace AND list_id=p_list;
  RETURN QUERY SELECT 0,v_bytes,v_counter.next_number,clock_timestamp(),v_stored_bytes,v_audit_count,v_workspace.security_audit_count;
EXCEPTION WHEN numeric_value_out_of_range THEN
  RETURN QUERY SELECT 6,v_bytes,NULL::bigint,NULL::timestamptz,NULL::bigint,NULL::bigint,NULL::integer;
END
$function$;

CREATE FUNCTION cellbridge_lists.reserve_patch(
  p_workspace uuid, p_values text, p_max_record bigint, p_previous_bytes bigint,
  p_audit_bytes bigint, p_max_bytes bigint, p_max_audits bigint)
RETURNS TABLE(status integer, value_bytes bigint, utc timestamptz, stored_bytes bigint, audit_count bigint, security_audit_count integer)
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog, pg_temp AS $function$
DECLARE
  v_bytes bigint;
  v_workspace cellbridge_lists.workspaces%ROWTYPE;
  v_stored_bytes bigint;
  v_audit_count bigint;
BEGIN
  v_bytes := octet_length((p_values::jsonb)::text)::bigint;
  IF v_bytes > p_max_record THEN
    RETURN QUERY SELECT 1,v_bytes,NULL::timestamptz,NULL::bigint,NULL::bigint,NULL::integer; RETURN;
  END IF;
  SELECT w.* INTO v_workspace FROM cellbridge_lists.workspaces w
    WHERE w.workspace_id=p_workspace FOR UPDATE;
  IF NOT FOUND THEN
    RETURN QUERY SELECT 5,v_bytes,NULL::timestamptz,NULL::bigint,NULL::bigint,NULL::integer; RETURN;
  END IF;
  v_stored_bytes := v_workspace.stored_bytes + (v_bytes - p_previous_bytes + p_audit_bytes);
  v_audit_count := v_workspace.audit_count + 1;
  IF (p_max_bytes IS NOT NULL AND v_stored_bytes > p_max_bytes) OR
     (p_max_audits IS NOT NULL AND v_audit_count > p_max_audits) THEN
    RETURN QUERY SELECT 4,v_bytes,NULL::timestamptz,NULL::bigint,NULL::bigint,NULL::integer; RETURN;
  END IF;
  UPDATE cellbridge_lists.workspaces SET stored_bytes=v_stored_bytes,audit_count=v_audit_count
    WHERE workspace_id=p_workspace;
  RETURN QUERY SELECT 0,v_bytes,clock_timestamp(),v_stored_bytes,v_audit_count,v_workspace.security_audit_count;
EXCEPTION WHEN numeric_value_out_of_range THEN
  RETURN QUERY SELECT 6,v_bytes,NULL::timestamptz,NULL::bigint,NULL::bigint,NULL::integer;
END
$function$;

REVOKE ALL ON FUNCTION cellbridge_lists.reserve_create(uuid,uuid,uuid,text,bigint,bigint,bigint,bigint,bigint,bigint) FROM PUBLIC;
REVOKE ALL ON FUNCTION cellbridge_lists.reserve_patch(uuid,text,bigint,bigint,bigint,bigint,bigint) FROM PUBLIC;
