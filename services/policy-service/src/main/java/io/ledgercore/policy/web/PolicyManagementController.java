package io.ledgercore.policy.web;

import io.ledgercore.policy.application.DecisionService;
import io.ledgercore.policy.application.PolicyAdminService;
import io.ledgercore.policy.domain.PolicyDomainException;
import io.ledgercore.policy.domain.PolicyVersion;
import io.ledgercore.policy.web.ManagementContract.CreatePolicyRequest;
import io.ledgercore.policy.web.ManagementContract.CreateVersionRequest;
import io.ledgercore.policy.web.ManagementContract.DecisionDetailResponse;
import io.ledgercore.policy.web.ManagementContract.PolicyResponse;
import io.ledgercore.policy.web.ManagementContract.VersionResponse;
import jakarta.validation.Valid;
import java.net.URI;
import java.util.List;
import java.util.UUID;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestHeader;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

/**
 * Policy management. State changes are explicit commands ({@code activate}, {@code retire}); there
 * is no generic update. Writes require {@code X-Actor-Id}, which is client-asserted until
 * authentication exists (docs/architecture/policy-engine.md, "Security boundary").
 */
@RestController
@RequestMapping("/api/v1")
public class PolicyManagementController {

  public static final String ACTOR_HEADER = "X-Actor-Id";

  private final PolicyAdminService admin;
  private final DecisionService decisions;

  public PolicyManagementController(PolicyAdminService admin, DecisionService decisions) {
    this.admin = admin;
    this.decisions = decisions;
  }

  @PostMapping("/policies")
  public ResponseEntity<PolicyResponse> createPolicy(
      @RequestHeader(ACTOR_HEADER) String actor, @Valid @RequestBody CreatePolicyRequest request) {
    var policy =
        admin.createPolicy(
            request.key(), request.name(), request.description(), request.organizationId(), actor);
    return ResponseEntity.created(URI.create("/api/v1/policies/" + policy.id()))
        .body(PolicyResponse.from(policy, null));
  }

  @GetMapping("/policies")
  public List<PolicyResponse> listPolicies() {
    return admin.listPolicies().stream()
        .map(p -> PolicyResponse.from(p, activeNumber(p.id())))
        .toList();
  }

  @GetMapping("/policies/{policyId}")
  public PolicyResponse getPolicy(@PathVariable UUID policyId) {
    return PolicyResponse.from(admin.getPolicy(policyId), activeNumber(policyId));
  }

  @PostMapping("/policies/{policyId}/versions")
  public ResponseEntity<VersionResponse> createVersion(
      @PathVariable UUID policyId,
      @RequestHeader(ACTOR_HEADER) String actor,
      @Valid @RequestBody CreateVersionRequest request) {
    var version = admin.createVersion(policyId, RuleRequests.toRules(request.rules()), actor);
    return ResponseEntity.created(
            URI.create("/api/v1/policies/" + policyId + "/versions/" + version.number()))
        .body(VersionResponse.from(admin.getPolicy(policyId), version));
  }

  @GetMapping("/policies/{policyId}/versions")
  public List<VersionResponse> listVersions(@PathVariable UUID policyId) {
    var policy = admin.getPolicy(policyId);
    return admin.listVersions(policyId).stream().map(v -> VersionResponse.from(policy, v)).toList();
  }

  @GetMapping("/policies/{policyId}/versions/{version}")
  public VersionResponse getVersion(@PathVariable UUID policyId, @PathVariable int version) {
    return VersionResponse.from(admin.getPolicy(policyId), admin.getVersion(policyId, version));
  }

  @PostMapping("/policies/{policyId}/versions/{version}/activate")
  public VersionResponse activate(
      @PathVariable UUID policyId,
      @PathVariable int version,
      @RequestHeader(ACTOR_HEADER) String actor) {
    return VersionResponse.from(
        admin.getPolicy(policyId), admin.activate(policyId, version, actor));
  }

  @PostMapping("/policies/{policyId}/versions/{version}/retire")
  public VersionResponse retire(
      @PathVariable UUID policyId,
      @PathVariable int version,
      @RequestHeader(ACTOR_HEADER) String actor) {
    return VersionResponse.from(admin.getPolicy(policyId), admin.retire(policyId, version, actor));
  }

  @GetMapping("/policy-decisions/{decisionId}")
  public DecisionDetailResponse getDecision(@PathVariable UUID decisionId) {
    return decisions
        .find(decisionId)
        .map(DecisionDetailResponse::from)
        .orElseThrow(
            () ->
                PolicyDomainException.notFound(
                    "DECISION_NOT_FOUND", "Decision " + decisionId + " does not exist."));
  }

  private Integer activeNumber(UUID policyId) {
    return admin.activeVersion(policyId).map(PolicyVersion::number).orElse(null);
  }
}
