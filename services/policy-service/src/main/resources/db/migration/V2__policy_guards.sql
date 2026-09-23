-- Database guards for policy invariants (ADR-009, ADR-011). The domain enforces the same rules
-- first; these hold even for SQL that bypasses the service. Violations raise SQLSTATE PC001 with a
-- message starting with a stable code, e.g. 'VERSION_IMMUTABLE: ...'.

CREATE FUNCTION policy_violation(code text, detail text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION USING ERRCODE = 'PC001', MESSAGE = code || ': ' || detail;
END $$;

CREATE FUNCTION policy_forbid_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM policy_violation(TG_ARGV[0], TG_TABLE_NAME || ' rows cannot be ' ||
        CASE TG_OP WHEN 'UPDATE' THEN 'updated' WHEN 'DELETE' THEN 'deleted' ELSE 'truncated' END);
    RETURN NULL;
END $$;

-- True when the row with this xmin was inserted by the current transaction.
CREATE FUNCTION policy_inserted_in_current_tx(row_xmin xid) RETURNS boolean
LANGUAGE sql STABLE AS $$ SELECT row_xmin = pg_current_xact_id()::xid $$;

-- Policies: immutable, never deleted.
CREATE TRIGGER policies_immutable BEFORE UPDATE OR DELETE ON policies
    FOR EACH ROW EXECUTE FUNCTION policy_forbid_mutation('POLICY_IMMUTABLE');

-- Versions: created as DRAFT with the next contiguous number; only lifecycle columns move, and
-- only along DRAFT -> ACTIVE -> RETIRED or DRAFT -> RETIRED.
CREATE FUNCTION policy_versions_before_insert() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    expected integer;
BEGIN
    IF NEW.status <> 'DRAFT' THEN
        PERFORM policy_violation('VERSION_INVALID_STATE', 'versions must be created as DRAFT');
    END IF;
    SELECT coalesce(max(version_number), 0) + 1 INTO expected FROM policy_versions WHERE policy_id = NEW.policy_id;
    IF NEW.version_number <> expected THEN
        PERFORM policy_violation('VERSION_NUMBER_NOT_MONOTONIC',
            format('policy %s: expected version %s, got %s', NEW.policy_id, expected, NEW.version_number));
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER policy_versions_before_insert BEFORE INSERT ON policy_versions
    FOR EACH ROW EXECUTE FUNCTION policy_versions_before_insert();

CREATE FUNCTION policy_versions_before_update() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id <> OLD.id OR NEW.policy_id <> OLD.policy_id OR NEW.version_number <> OLD.version_number
       OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
        PERFORM policy_violation('VERSION_IMMUTABLE', 'version identity cannot change');
    END IF;
    IF (OLD.status, NEW.status) NOT IN (('DRAFT', 'ACTIVE'), ('ACTIVE', 'RETIRED'), ('DRAFT', 'RETIRED')) THEN
        PERFORM policy_violation('VERSION_INVALID_TRANSITION',
            format('%s -> %s is not a permitted transition', OLD.status, NEW.status));
    END IF;
    IF OLD.status = 'ACTIVE' AND (NEW.activated_at IS DISTINCT FROM OLD.activated_at
                                  OR NEW.activated_by IS DISTINCT FROM OLD.activated_by) THEN
        PERFORM policy_violation('VERSION_IMMUTABLE', 'activation evidence cannot change');
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER policy_versions_before_update BEFORE UPDATE ON policy_versions
    FOR EACH ROW EXECUTE FUNCTION policy_versions_before_update();

CREATE TRIGGER policy_versions_no_delete BEFORE DELETE ON policy_versions
    FOR EACH ROW EXECUTE FUNCTION policy_forbid_mutation('VERSION_IMMUTABLE');

-- Rules: written only together with their (DRAFT) version, in the transaction that created it.
-- After that a version's rules never change, whatever its status.
CREATE FUNCTION policy_rules_before_insert() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    version record;
BEGIN
    SELECT status, xmin INTO version FROM policy_versions WHERE id = NEW.policy_version_id;
    IF version.status IS DISTINCT FROM 'DRAFT' OR NOT policy_inserted_in_current_tx(version.xmin) THEN
        PERFORM policy_violation('VERSION_IMMUTABLE',
            'rules can only be added in the transaction that creates their version');
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER policy_rules_before_insert BEFORE INSERT ON policy_rules
    FOR EACH ROW EXECUTE FUNCTION policy_rules_before_insert();

CREATE TRIGGER policy_rules_immutable BEFORE UPDATE OR DELETE ON policy_rules
    FOR EACH ROW EXECUTE FUNCTION policy_forbid_mutation('VERSION_IMMUTABLE');

-- Decisions: only from an ACTIVE version; append-only afterwards.
CREATE FUNCTION policy_decisions_before_insert() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.policy_version_id IS NOT NULL
       AND (SELECT status FROM policy_versions WHERE id = NEW.policy_version_id FOR SHARE) IS DISTINCT FROM 'ACTIVE' THEN
        PERFORM policy_violation('VERSION_NOT_ACTIVE', 'decisions can only be made by an ACTIVE version');
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER policy_decisions_before_insert BEFORE INSERT ON policy_decisions
    FOR EACH ROW EXECUTE FUNCTION policy_decisions_before_insert();

CREATE TRIGGER policy_decisions_immutable BEFORE UPDATE OR DELETE ON policy_decisions
    FOR EACH ROW EXECUTE FUNCTION policy_forbid_mutation('DECISION_IMMUTABLE');

-- Matches: written only with their decision, in the same transaction.
CREATE FUNCTION policy_decision_matches_before_insert() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT policy_inserted_in_current_tx((SELECT xmin FROM policy_decisions WHERE id = NEW.decision_id)) THEN
        PERFORM policy_violation('DECISION_IMMUTABLE',
            'matches can only be recorded in the transaction that creates the decision');
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER policy_decision_matches_before_insert BEFORE INSERT ON policy_decision_matches
    FOR EACH ROW EXECUTE FUNCTION policy_decision_matches_before_insert();

CREATE TRIGGER policy_decision_matches_immutable BEFORE UPDATE OR DELETE ON policy_decision_matches
    FOR EACH ROW EXECUTE FUNCTION policy_forbid_mutation('DECISION_IMMUTABLE');

CREATE TRIGGER policies_no_truncate BEFORE TRUNCATE ON policies
    FOR EACH STATEMENT EXECUTE FUNCTION policy_forbid_mutation('POLICY_IMMUTABLE');
CREATE TRIGGER policy_versions_no_truncate BEFORE TRUNCATE ON policy_versions
    FOR EACH STATEMENT EXECUTE FUNCTION policy_forbid_mutation('VERSION_IMMUTABLE');
CREATE TRIGGER policy_rules_no_truncate BEFORE TRUNCATE ON policy_rules
    FOR EACH STATEMENT EXECUTE FUNCTION policy_forbid_mutation('VERSION_IMMUTABLE');
CREATE TRIGGER policy_decisions_no_truncate BEFORE TRUNCATE ON policy_decisions
    FOR EACH STATEMENT EXECUTE FUNCTION policy_forbid_mutation('DECISION_IMMUTABLE');
CREATE TRIGGER policy_decision_matches_no_truncate BEFORE TRUNCATE ON policy_decision_matches
    FOR EACH STATEMENT EXECUTE FUNCTION policy_forbid_mutation('DECISION_IMMUTABLE');
