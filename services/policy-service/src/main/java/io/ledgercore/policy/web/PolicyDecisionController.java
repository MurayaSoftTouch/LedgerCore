package io.ledgercore.policy.web;

import io.ledgercore.policy.application.DecisionService;
import io.ledgercore.policy.config.PolicyServiceProperties;
import io.ledgercore.policy.domain.EvaluationInput;
import io.ledgercore.policy.domain.EvaluationInput.AccountRef;
import io.ledgercore.policy.web.DecisionContract.PolicyDecisionRequest;
import io.ledgercore.policy.web.DecisionContract.PolicyDecisionResponse;
import jakarta.validation.Valid;
import java.math.BigDecimal;
import org.slf4j.MDC;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RestController;

/** Contract v1 evaluation endpoint: {@code POST /v1/policy-decisions}. */
@RestController
public class PolicyDecisionController {

  /** Non-contract response header: {@code true} when the stored decision was returned. */
  public static final String REPLAYED_HEADER = "X-Decision-Replayed";

  private static final java.util.regex.Pattern CONTRACT_VERSION =
      java.util.regex.Pattern.compile(DecisionContract.CONTRACT_VERSION_PATTERN);

  private final DecisionService decisions;
  private final PolicyServiceProperties properties;

  public PolicyDecisionController(DecisionService decisions, PolicyServiceProperties properties) {
    this.decisions = decisions;
    this.properties = properties;
  }

  @PostMapping(path = "/v1/policy-decisions", consumes = "application/json")
  public ResponseEntity<PolicyDecisionResponse> evaluate(
      @Valid @RequestBody PolicyDecisionRequest request) {
    if (!CONTRACT_VERSION.matcher(request.contractVersion()).matches()) {
      throw new ContractVersionUnsupportedException(request.contractVersion());
    }
    var input =
        new EvaluationInput(
            request.organizationId(),
            request.transactionType(),
            request.currency(),
            new BigDecimal(request.totalAmount()),
            request.accountContext().stream()
                .map(a -> new AccountRef(a.accountId(), a.accountType(), a.side()))
                .toList());

    var outcome =
        decisions.evaluate(
            request.transactionId(),
            input,
            request.contractVersion(),
            MDC.get(CorrelationIdFilter.MDC_KEY));
    var d = outcome.decision();
    return ResponseEntity.ok()
        .header(REPLAYED_HEADER, Boolean.toString(outcome.replayed()))
        .body(
            new PolicyDecisionResponse(
                properties.contractVersion(),
                d.id(),
                d.transactionId(),
                d.policyVersionLabel(),
                d.decision(),
                d.reasonCodes(),
                d.evaluatedAt()));
  }
}
