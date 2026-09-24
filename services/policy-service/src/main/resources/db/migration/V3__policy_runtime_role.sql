-- Least-privilege runtime role (Milestone 3, ADR-007 applied to the policy database).
-- policy_app owns the schema and runs Flyway; the service connects as policy_runtime, which
-- cannot run DDL, TRUNCATE or DELETE and, as a non-owner, cannot disable or replace the V2
-- guard triggers. The guards themselves are unchanged.
--
-- Skipped when the role does not exist (a deployment without the repository's init script must
-- create it; see docs/architecture/service-integration.md).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'policy_runtime') THEN
        REVOKE ALL ON policies, policy_versions, policy_rules, policy_decisions,
                      policy_decision_matches, flyway_schema_history FROM policy_runtime;

        -- UPDATE on policies exists only so the service can take the per-policy row lock
        -- (SELECT ... FOR UPDATE requires it). policies_immutable still rejects every UPDATE.
        GRANT SELECT, INSERT, UPDATE ON policies TO policy_runtime;
        -- Lifecycle transitions; FOR SHARE locks during evaluation.
        GRANT SELECT, INSERT, UPDATE ON policy_versions TO policy_runtime;
        GRANT SELECT, INSERT ON policy_rules TO policy_runtime;
        GRANT SELECT, INSERT ON policy_decisions TO policy_runtime;
        GRANT SELECT, INSERT ON policy_decision_matches TO policy_runtime;
    END IF;
END $$;
