package io.ledgercore.policy;

import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

import io.ledgercore.policy.support.PostgresIntegrationTest;
import org.junit.jupiter.api.Test;

class PolicyServiceApplicationTests extends PostgresIntegrationTest {

  @Test
  void healthEndpointReportsUp() throws Exception {
    mockMvc
        .perform(get("/actuator/health"))
        .andExpect(status().isOk())
        .andExpect(jsonPath("$.status").value("UP"));
  }

  @Test
  void readinessProbeIsExposed() throws Exception {
    mockMvc.perform(get("/actuator/health/readiness")).andExpect(status().isOk());
  }

  @Test
  void infoEndpointAdvertisesContractVersion() throws Exception {
    mockMvc
        .perform(get("/actuator/info"))
        .andExpect(status().isOk())
        .andExpect(jsonPath("$.contract-version").value("1.1.0"));
  }

  @Test
  void openApiDocumentIsServed() throws Exception {
    mockMvc
        .perform(get("/openapi/v3/api-docs"))
        .andExpect(status().isOk())
        .andExpect(jsonPath("$.openapi").exists());
  }
}
