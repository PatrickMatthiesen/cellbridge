-- Immutable SQL for EF migration 20261022040000_EfQueryFunctions.
CREATE OR REPLACE FUNCTION cellbridge_lists.utf16_key(value text)
RETURNS bytea LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT AS $function$
  SELECT CASE WHEN value IS NULL THEN NULL ELSE decode(coalesce((
    SELECT string_agg(CASE WHEN ascii(substring(value FROM utf16.n FOR 1))<=65535
      THEN lpad(to_hex(ascii(substring(value FROM utf16.n FOR 1))),4,'0')
      ELSE lpad(to_hex(((ascii(substring(value FROM utf16.n FOR 1))-65536)>>10)+55296),4,'0') ||
           lpad(to_hex(((ascii(substring(value FROM utf16.n FOR 1))-65536)&1023)+56320),4,'0')
      END,'' ORDER BY utf16.n)
    FROM generate_series(1,char_length(value)) utf16(n)
  ),''),'hex') END
$function$;

CREATE OR REPLACE FUNCTION cellbridge_lists.timestamp_key(value text)
RETURNS text LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT AS $function$
  SELECT CASE WHEN value IS NULL THEN NULL ELSE
    substring(value FROM 1 FOR 19) ||
    rpad(CASE WHEN length(value)>20 THEN substring(value FROM 21 FOR length(value)-21) ELSE '' END,7,'0')
  END
$function$;

CREATE OR REPLACE FUNCTION cellbridge_lists.utf16_compare(left_value text, right_value text)
RETURNS integer LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT AS $function$
  SELECT CASE
    WHEN cellbridge_lists.utf16_key(left_value) < cellbridge_lists.utf16_key(right_value) THEN -1
    WHEN cellbridge_lists.utf16_key(left_value) > cellbridge_lists.utf16_key(right_value) THEN 1
    ELSE 0 END
$function$;

CREATE OR REPLACE FUNCTION cellbridge_lists.timestamp_compare(left_value text, right_value text)
RETURNS integer LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT AS $function$
  SELECT CASE
    WHEN cellbridge_lists.timestamp_key(left_value) COLLATE "C" < cellbridge_lists.timestamp_key(right_value) COLLATE "C" THEN -1
    WHEN cellbridge_lists.timestamp_key(left_value) COLLATE "C" > cellbridge_lists.timestamp_key(right_value) COLLATE "C" THEN 1
    ELSE 0 END
$function$;

CREATE OR REPLACE FUNCTION cellbridge_lists.c_compare(left_value text, right_value text)
RETURNS integer LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT AS $function$
  SELECT CASE
    WHEN left_value COLLATE "C" < right_value COLLATE "C" THEN -1
    WHEN left_value COLLATE "C" > right_value COLLATE "C" THEN 1
    ELSE 0 END
$function$;

UPDATE cellbridge_lists.schema_version SET version=4 WHERE singleton AND version=3;
