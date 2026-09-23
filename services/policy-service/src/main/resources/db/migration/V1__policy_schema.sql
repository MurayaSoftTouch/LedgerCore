-- Policy engine schema (Milestone 2). Owned by policy_app in the dedicated `policy` database.
-- The ledger's tables live in a different database that policy_app cannot connect to (ADR-002/003).

CREATE TABLE policies (
    id              uuid          PRIMARY KEY,
    key             varchar(63)   NOT NULL,
    name            varchar(200)  NOT NULL,
    description     varchar(1000),
    -- NULL: the default policy, used when an organization has no policy of its own.
    organization_id uuid,
    created_at      timestamptz   NOT NULL,
    created_by      varchar(128)  NOT NULL,
    CONSTRAINT ux_policies_key UNIQUE (key),
    CONSTRAINT ck_policies_key CHECK (key ~ '^[a-z0-9][a-z0-9-]{1,62}$')
);

-- One policy per organization, and at most one default policy.
CREATE UNIQUE INDEX ux_policies_organization ON policies (organization_id) WHERE organization_id IS NOT NULL;
CREATE UNIQUE INDEX ux_policies_single_default ON policies ((true)) WHERE organization_id IS NULL;

CREATE TABLE policy_versions (
    id             uuid         PRIMARY KEY,
    policy_id      uuid         NOT NULL REFERENCES policies (id) ON DELETE RESTRICT,
    version_number integer      NOT NULL,
    status         varchar(8)   NOT NULL,
    created_at     timestamptz  NOT NULL,
    created_by     varchar(128) NOT NULL,
    activated_at   timestamptz,
    activated_by   varchar(128),
    retired_at     timestamptz,
    retired_by     varchar(128),
    CONSTRAINT ux_policy_versions_number UNIQUE (policy_id, version_number),
    CONSTRAINT ux_policy_versions_id_policy UNIQUE (id, policy_id),
    CONSTRAINT ck_policy_versions_number CHECK (version_number >= 1),
    CONSTRAINT ck_policy_versions_status CHECK (status IN ('DRAFT', 'ACTIVE', 'RETIRED')),
    CONSTRAINT ck_policy_versions_activation CHECK ((activated_at IS NULL) = (activated_by IS NULL)),
    CONSTRAINT ck_policy_versions_retirement CHECK ((retired_at IS NULL) = (retired_by IS NULL)),
    CONSTRAINT ck_policy_versions_lifecycle CHECK (
        (status = 'DRAFT'   AND activated_at IS NULL     AND retired_at IS NULL) OR
        (status = 'ACTIVE'  AND activated_at IS NOT NULL AND retired_at IS NULL) OR
        (status = 'RETIRED' AND retired_at IS NOT NULL))
);

-- At most one ACTIVE version per policy (ADR-009). The activation lock is the primary mechanism;
-- this index is the backstop that holds even for SQL that bypasses the application.
CREATE UNIQUE INDEX ux_policy_versions_one_active ON policy_versions (policy_id) WHERE status = 'ACTIVE';

-- Typed rules: one column set per rule type, enforced by ck_policy_rules_shape. Rules are data;
-- nothing here is ever executed. Required columns are tested with explicit IS NOT NULL: a CHECK
-- whose expression is NULL passes, so `threshold >= 0` alone would admit a NULL threshold.
CREATE TABLE policy_rules (
    id                 uuid          PRIMARY KEY,
    policy_version_id  uuid          NOT NULL REFERENCES policy_versions (id) ON DELETE RESTRICT,
    position           integer       NOT NULL,
    rule_type          varchar(24)   NOT NULL,
    outcome            varchar(16)   NOT NULL,
    reason_code        varchar(64)   NOT NULL,
    currency           character(3),
    threshold          numeric(22,4),
    transaction_types  text[],
    account_types      text[],
    entry_side         varchar(6),
    account_ids        uuid[],
    allowed_currencies text[],
    CONSTRAINT ux_policy_rules_position UNIQUE (policy_version_id, position),
    CONSTRAINT ux_policy_rules_id_version UNIQUE (id, policy_version_id),
    CONSTRAINT ck_policy_rules_position CHECK (position BETWEEN 1 AND 100),
    CONSTRAINT ck_policy_rules_outcome CHECK (outcome IN ('REVIEW_REQUIRED', 'REJECTED')),
    CONSTRAINT ck_policy_rules_reason_code CHECK (reason_code ~ '^[A-Z][A-Z0-9_]{1,63}$'),
    CONSTRAINT ck_policy_rules_shape CHECK (
        (rule_type = 'AMOUNT_ABOVE'
            AND currency IS NOT NULL AND currency ~ '^[A-Z]{3}$'
            AND threshold IS NOT NULL AND threshold >= 0
            AND transaction_types IS NULL AND account_types IS NULL AND entry_side IS NULL
            AND account_ids IS NULL AND allowed_currencies IS NULL)
        OR (rule_type = 'TRANSACTION_TYPE'
            AND transaction_types IS NOT NULL AND cardinality(transaction_types) > 0
            AND transaction_types <@ ARRAY['PAYMENT', 'TRANSFER', 'ADJUSTMENT', 'REVERSAL', 'FEE']
            AND currency IS NULL AND threshold IS NULL AND account_types IS NULL
            AND entry_side IS NULL AND account_ids IS NULL AND allowed_currencies IS NULL)
        OR (rule_type = 'ACCOUNT_CONTEXT'
            AND (account_types IS NOT NULL OR account_ids IS NOT NULL)
            AND (account_types IS NULL OR (cardinality(account_types) > 0
                 AND account_types <@ ARRAY['ASSET', 'LIABILITY', 'EQUITY', 'REVENUE', 'EXPENSE']))
            AND (account_ids IS NULL OR cardinality(account_ids) > 0)
            AND (entry_side IS NULL OR entry_side IN ('DEBIT', 'CREDIT'))
            AND currency IS NULL AND threshold IS NULL AND transaction_types IS NULL
            AND allowed_currencies IS NULL)
        OR (rule_type = 'CURRENCY_NOT_ALLOWED'
            AND allowed_currencies IS NOT NULL AND cardinality(allowed_currencies) > 0
            AND currency IS NULL AND threshold IS NULL AND transaction_types IS NULL
            AND account_types IS NULL AND entry_side IS NULL AND account_ids IS NULL))
);

-- One immutable record per evaluated transaction (ADR-011). Stores the evaluated facts needed to
-- explain the decision; not account ids, not the requesting principal, never balances.
CREATE TABLE policy_decisions (
    id                   uuid          PRIMARY KEY,
    transaction_id       uuid          NOT NULL,
    organization_id      uuid          NOT NULL,
    request_fingerprint  character(64) NOT NULL,
    policy_id            uuid          REFERENCES policies (id) ON DELETE RESTRICT,
    policy_version_id    uuid,
    policy_version_label varchar(128)  NOT NULL,
    decision             varchar(16)   NOT NULL,
    reason_codes         text[]        NOT NULL,
    transaction_type     varchar(64)   NOT NULL,
    currency             character(3)  NOT NULL,
    total_amount         numeric(22,4) NOT NULL,
    contract_version     varchar(32)   NOT NULL,
    correlation_id       varchar(128),
    evaluated_at         timestamptz   NOT NULL,
    -- Idempotency key: one decision per transaction (ADR-011).
    CONSTRAINT ux_policy_decisions_transaction UNIQUE (transaction_id),
    CONSTRAINT ux_policy_decisions_id_version UNIQUE (id, policy_version_id),
    CONSTRAINT fk_policy_decisions_version FOREIGN KEY (policy_version_id, policy_id)
        REFERENCES policy_versions (id, policy_id) ON DELETE RESTRICT,
    CONSTRAINT ck_policy_decisions_decision CHECK (decision IN ('APPROVED', 'REVIEW_REQUIRED', 'REJECTED')),
    CONSTRAINT ck_policy_decisions_fingerprint CHECK (request_fingerprint ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_policy_decisions_reasons CHECK ((decision = 'APPROVED') = (cardinality(reason_codes) = 0)),
    -- Fail-safe at the storage level: nothing is approved without a concrete policy version.
    CONSTRAINT ck_policy_decisions_approval_needs_version CHECK (decision <> 'APPROVED' OR policy_version_id IS NOT NULL),
    CONSTRAINT ck_policy_decisions_version_needs_policy CHECK (policy_version_id IS NULL OR policy_id IS NOT NULL)
);

CREATE INDEX ix_policy_decisions_version ON policy_decisions (policy_version_id);

-- Which rules matched. The composite keys guarantee a match references a rule of the very
-- version that produced the decision.
CREATE TABLE policy_decision_matches (
    decision_id       uuid         NOT NULL,
    rule_id           uuid         NOT NULL,
    policy_version_id uuid         NOT NULL,
    position          integer      NOT NULL,
    outcome           varchar(16)  NOT NULL,
    reason_code       varchar(64)  NOT NULL,
    CONSTRAINT pk_policy_decision_matches PRIMARY KEY (decision_id, rule_id),
    CONSTRAINT fk_policy_decision_matches_decision FOREIGN KEY (decision_id, policy_version_id)
        REFERENCES policy_decisions (id, policy_version_id) ON DELETE RESTRICT,
    CONSTRAINT fk_policy_decision_matches_rule FOREIGN KEY (rule_id, policy_version_id)
        REFERENCES policy_rules (id, policy_version_id) ON DELETE RESTRICT
);
