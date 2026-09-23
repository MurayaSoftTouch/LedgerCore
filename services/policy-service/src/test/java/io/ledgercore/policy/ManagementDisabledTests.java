package io.ledgercore.policy;

import static io.ledgercore.policy.support.Requests.decision;

import io.ledgercore.policy.support.Expect;
import io.ledgercore.policy.support.PostgresIntegrationTest;
import io.ledgercore.policy.support.TestTokens;
import java.util.UUID;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.webmvc.test.autoconfigure.AutoConfigureMockMvc;
import org.springframework.http.MediaType;
import org.springframework.test.context.DynamicPropertyRegistry;
import org.springframework.test.context.DynamicPropertySource;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.request.MockMvcRequestBuilders;

/** Without an admin credential the management API is off; evaluation still works. */
@SpringBootTest
@AutoConfigureMockMvc
class ManagementDisabledTests {

  @Autowired private MockMvc mockMvc;

  @DynamicPropertySource
  static void properties(DynamicPropertyRegistry registry) {
    PostgresIntegrationTest.database(registry);
  }

  @Test
  void managementApiIsDisabledEvenWithACredential() throws Exception {
    for (var bearer : new String[] {TestTokens.ADMIN_BEARER, TestTokens.DECISION_BEARER}) {
      Expect.problem(
          mockMvc
              .perform(
                  MockMvcRequestBuilders.get("/api/v1/policies").header("Authorization", bearer))
              .andReturn(),
          403,
          "MANAGEMENT_API_DISABLED");
    }
  }

  @Test
  void evaluationStillWorks() throws Exception {
    var result =
        mockMvc
            .perform(
                MockMvcRequestBuilders.post("/v1/policy-decisions")
                    .header("Authorization", TestTokens.DECISION_BEARER)
                    .contentType(MediaType.APPLICATION_JSON)
                    .content(decision(UUID.randomUUID(), UUID.randomUUID(), "PAYMENT", "1")))
            .andReturn();

    Expect.status(result, 200);
  }
}
