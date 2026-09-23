package io.ledgercore.policy;

import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.webmvc.test.autoconfigure.AutoConfigureMockMvc;
import org.springframework.test.web.servlet.MockMvc;

@SpringBootTest
@AutoConfigureMockMvc
class PolicyServiceApplicationTests {

  @Autowired private MockMvc mockMvc;

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
        .andExpect(jsonPath("$.contract-version").value("1.0.0"));
  }

  @Test
  void openApiDocumentIsServed() throws Exception {
    mockMvc
        .perform(get("/openapi/v3/api-docs"))
        .andExpect(status().isOk())
        .andExpect(jsonPath("$.openapi").exists());
  }
}
