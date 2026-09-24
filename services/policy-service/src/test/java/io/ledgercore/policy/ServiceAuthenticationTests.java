package io.ledgercore.policy;

import static io.ledgercore.policy.support.Requests.STANDARD_RULES;
import static io.ledgercore.policy.support.Requests.decision;
import static org.assertj.core.api.Assertions.assertThat;

import io.ledgercore.policy.support.ContractSchemas;
import io.ledgercore.policy.support.Expect;
import io.ledgercore.policy.support.PostgresIntegrationTest;
import io.ledgercore.policy.support.Sql;
import io.ledgercore.policy.support.TestTokens;
import java.util.UUID;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;
import org.springframework.boot.test.system.CapturedOutput;
import org.springframework.boot.test.system.OutputCaptureExtension;
import org.springframework.http.MediaType;
import org.springframework.test.web.servlet.MvcResult;
import org.springframework.test.web.servlet.request.MockMvcRequestBuilders;

@ExtendWith(OutputCaptureExtension.class)
class ServiceAuthenticationTests extends PostgresIntegrationTest {

  private MvcResult evaluateWith(String authorization, UUID transaction, UUID org)
      throws Exception {
    var request =
        MockMvcRequestBuilders.post("/v1/policy-decisions")
            .contentType(MediaType.APPLICATION_JSON)
            .content(decision(transaction, org, "PAYMENT", "1"));
    if (authorization != null) {
      request.header("Authorization", authorization);
    }
    return mockMvc.perform(request).andReturn();
  }

  @ParameterizedTest
  @ValueSource(
      strings = {
        "",
        "Bearer ",
        "Bearer wrong-token-value-that-is-long-enough-000",
        "Basic dGVzdDp0ZXN0",
        "bearer " + TestTokens.DECISION,
        "Bearer " + TestTokens.DECISION + " ",
        "Bearer " + TestTokens.ADMIN
      })
  void decisionEndpointRejectsMissingOrInvalidServiceCredential(String authorization)
      throws Exception {
    var transaction = UUID.randomUUID();
    var scope = api().activePolicy(STANDARD_RULES);

    var result =
        evaluateWith(
            authorization.isEmpty() ? null : authorization, transaction, scope.organizationId());

    Expect.problem(result, 401, "SERVICE_AUTHENTICATION_REQUIRED");
    assertThat(result.getResponse().getHeader("WWW-Authenticate")).startsWith("Bearer");
    assertThat(
            ContractSchemas.violations(
                ContractSchemas.PROBLEM, result.getResponse().getContentAsString()))
        .isEmpty();
    assertThat(
            Sql.count(
                "SELECT count(*) FROM policy_decisions WHERE transaction_id = ?", transaction))
        .isZero();
  }

  @Test
  void decisionEndpointAcceptsTheDecisionCredential() throws Exception {
    var scope = api().activePolicy(STANDARD_RULES);

    Expect.status(
        evaluateWith(TestTokens.DECISION_BEARER, UUID.randomUUID(), scope.organizationId()), 200);
  }

  @Test
  void managementApiRejectsTheDecisionCredential() throws Exception {
    var result =
        mockMvc
            .perform(
                MockMvcRequestBuilders.get("/api/v1/policies")
                    .header("Authorization", TestTokens.DECISION_BEARER))
            .andReturn();

    Expect.problem(result, 401, "ADMIN_AUTHENTICATION_REQUIRED");
  }

  @Test
  void managementApiRejectsMissingCredential() throws Exception {
    Expect.problem(
        mockMvc.perform(MockMvcRequestBuilders.get("/api/v1/policies")).andReturn(),
        401,
        "ADMIN_AUTHENTICATION_REQUIRED");
  }

  @Test
  void operationalEndpointsStayUnauthenticated() throws Exception {
    for (var path :
        new String[] {"/actuator/health", "/actuator/health/readiness", "/actuator/info"}) {
      assertThat(
              mockMvc
                  .perform(MockMvcRequestBuilders.get(path))
                  .andReturn()
                  .getResponse()
                  .getStatus())
          .as(path)
          .isEqualTo(200);
    }
  }

  @Test
  void credentialsNeverAppearInLogs(CapturedOutput output) throws Exception {
    var scope = api().activePolicy(STANDARD_RULES);
    evaluateWith(
        "Bearer wrong-token-value-that-is-long-enough-000",
        UUID.randomUUID(),
        scope.organizationId());
    evaluateWith(TestTokens.DECISION_BEARER, UUID.randomUUID(), scope.organizationId());

    assertThat(output.getAll())
        .contains("Request rejected by service authentication")
        .doesNotContain(
            TestTokens.DECISION, TestTokens.ADMIN, "wrong-token-value-that-is-long-enough-000");
  }
}
