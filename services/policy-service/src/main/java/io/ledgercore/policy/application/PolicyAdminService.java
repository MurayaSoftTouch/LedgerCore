package io.ledgercore.policy.application;

import io.ledgercore.policy.domain.Policy;
import io.ledgercore.policy.domain.PolicyDomainException;
import io.ledgercore.policy.domain.PolicyVersion;
import io.ledgercore.policy.domain.rules.Rule;
import io.ledgercore.policy.persistence.PolicyRepository;
import io.ledgercore.policy.persistence.PolicyVersionRepository;
import java.time.Clock;
import java.time.Instant;
import java.time.temporal.ChronoUnit;
import java.util.List;
import java.util.Optional;
import java.util.UUID;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

/**
 * Policy management. Every version change locks the policy row first, so numbering and activation
 * are serialised per policy; database constraints back this up (ADR-009).
 */
@Service
public class PolicyAdminService {

  private static final Logger log = LoggerFactory.getLogger(PolicyAdminService.class);

  private final PolicyRepository policies;
  private final PolicyVersionRepository versions;
  private final Clock clock;

  public PolicyAdminService(
      PolicyRepository policies, PolicyVersionRepository versions, Clock clock) {
    this.policies = policies;
    this.versions = versions;
    this.clock = clock;
  }

  @Transactional
  public Policy createPolicy(
      String key, String name, String description, UUID organizationId, String actor) {
    var policy = Policy.create(key, name, description, organizationId, actor, now());
    policies.insert(policy);
    log.atInfo()
        .addKeyValue("policyId", policy.id())
        .addKeyValue("policyKey", policy.key())
        .addKeyValue("organizationId", policy.organizationId())
        .addKeyValue("actor", policy.createdBy())
        .log("Policy created");
    return policy;
  }

  @Transactional
  public PolicyVersion createVersion(UUID policyId, List<Rule> rules, String actor) {
    var policy = policies.lock(policyId).orElseThrow(() -> policyNotFound(policyId));
    var version =
        PolicyVersion.draft(policy.id(), versions.nextNumber(policy.id()), rules, actor, now());
    versions.insert(version);
    log.atInfo()
        .addKeyValue("policyId", policy.id())
        .addKeyValue("policyVersion", policy.versionLabel(version.number()))
        .addKeyValue("ruleCount", version.rules().size())
        .addKeyValue("actor", version.createdBy())
        .log("Policy version created");
    return version;
  }

  /**
   * Activates a DRAFT version. The currently active version, if any, is retired in the same
   * transaction, so the policy never has two active versions and never a gap between them.
   */
  @Transactional
  public PolicyVersion activate(UUID policyId, int number, String actor) {
    var policy = policies.lock(policyId).orElseThrow(() -> policyNotFound(policyId));
    var target = versions.find(policyId, number).orElseThrow(() -> versionNotFound(number));
    var now = now();
    var activated = target.activate(actor, now);

    var previous = versions.findActive(policyId);
    if (previous.isPresent()) {
      versions.updateLifecycle(previous.get().retire(actor, now));
    }
    versions.updateLifecycle(activated);

    log.atInfo()
        .addKeyValue("policyId", policyId)
        .addKeyValue("policyVersion", policy.versionLabel(number))
        .addKeyValue(
            "supersededVersion", previous.map(v -> policy.versionLabel(v.number())).orElse(null))
        .addKeyValue("actor", activated.activatedBy())
        .log("Policy version activated");
    return activated;
  }

  @Transactional
  public PolicyVersion retire(UUID policyId, int number, String actor) {
    var policy = policies.lock(policyId).orElseThrow(() -> policyNotFound(policyId));
    var target = versions.find(policyId, number).orElseThrow(() -> versionNotFound(number));
    var retired = target.retire(actor, now());
    versions.updateLifecycle(retired);
    log.atInfo()
        .addKeyValue("policyId", policyId)
        .addKeyValue("policyVersion", policy.versionLabel(number))
        .addKeyValue("wasActive", target.activatedAt() != null)
        .addKeyValue("actor", retired.retiredBy())
        .log("Policy version retired");
    return retired;
  }

  @Transactional(readOnly = true)
  public List<Policy> listPolicies() {
    return policies.findAll();
  }

  @Transactional(readOnly = true)
  public Policy getPolicy(UUID policyId) {
    return policies.findById(policyId).orElseThrow(() -> policyNotFound(policyId));
  }

  @Transactional(readOnly = true)
  public Optional<PolicyVersion> activeVersion(UUID policyId) {
    return versions.findActive(policyId);
  }

  @Transactional(readOnly = true)
  public List<PolicyVersion> listVersions(UUID policyId) {
    getPolicy(policyId);
    return versions.findAll(policyId);
  }

  @Transactional(readOnly = true)
  public PolicyVersion getVersion(UUID policyId, int number) {
    getPolicy(policyId);
    return versions.find(policyId, number).orElseThrow(() -> versionNotFound(number));
  }

  /** PostgreSQL timestamptz has microsecond precision; truncate so stored and returned agree. */
  private Instant now() {
    return clock.instant().truncatedTo(ChronoUnit.MICROS);
  }

  private static PolicyDomainException policyNotFound(UUID id) {
    return PolicyDomainException.notFound("POLICY_NOT_FOUND", "Policy " + id + " does not exist.");
  }

  private static PolicyDomainException versionNotFound(int number) {
    return PolicyDomainException.notFound(
        "VERSION_NOT_FOUND", "Version " + number + " does not exist.");
  }
}
