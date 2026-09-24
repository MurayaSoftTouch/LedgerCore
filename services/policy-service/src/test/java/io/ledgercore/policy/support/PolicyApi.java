package io.ledgercore.policy.support;

import com.jayway.jsonpath.JsonPath;
import java.util.UUID;
import org.springframework.http.MediaType;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.MvcResult;
import org.springframework.test.web.servlet.request.MockMvcRequestBuilders;

/** Small HTTP helper over MockMvc (real serialization, real controllers). */
public final class PolicyApi {

  public static final String ACTOR = "X-Actor-Id";

  private final MockMvc mvc;

  public PolicyApi(MockMvc mvc) {
    this.mvc = mvc;
  }

  public MvcResult post(String path, String json) throws Exception {
    return mvc.perform(
            MockMvcRequestBuilders.post(path)
                .contentType(MediaType.APPLICATION_JSON)
                .header(ACTOR, "tester")
                .header("Authorization", TestTokens.ADMIN_BEARER)
                .content(json))
        .andReturn();
  }

  public MvcResult postNoBody(String path) throws Exception {
    return mvc.perform(
            MockMvcRequestBuilders.post(path)
                .header(ACTOR, "tester")
                .header("Authorization", TestTokens.ADMIN_BEARER))
        .andReturn();
  }

  public MvcResult get(String path) throws Exception {
    return mvc.perform(
            MockMvcRequestBuilders.get(path).header("Authorization", TestTokens.ADMIN_BEARER))
        .andReturn();
  }

  /** Creates an organization-scoped policy (isolated from other tests) and returns its id. */
  public UUID createPolicy(String key, UUID organizationId) throws Exception {
    var result =
        post(
            "/api/v1/policies",
            """
            {"key": "%s", "name": "Test %s", "organizationId": %s}
            """
                .formatted(
                    key, key, organizationId == null ? "null" : "\"" + organizationId + "\""));
    Expect.status(result, 201);
    return UUID.fromString(JsonPath.read(result.getResponse().getContentAsString(), "$.id"));
  }

  public int createVersion(UUID policyId, String rulesJson) throws Exception {
    var result =
        post("/api/v1/policies/" + policyId + "/versions", "{\"rules\": " + rulesJson + "}");
    Expect.status(result, 201);
    return JsonPath.read(result.getResponse().getContentAsString(), "$.version");
  }

  public void activate(UUID policyId, int version) throws Exception {
    Expect.status(
        postNoBody("/api/v1/policies/" + policyId + "/versions/" + version + "/activate"), 200);
  }

  /**
   * Creates, versions and activates a policy for a fresh organization; returns the organization.
   */
  public Scoped activePolicy(String rulesJson) throws Exception {
    var org = UUID.randomUUID();
    var key = "p-" + org.toString().substring(0, 8);
    var policyId = createPolicy(key, org);
    var version = createVersion(policyId, rulesJson);
    activate(policyId, version);
    return new Scoped(org, policyId, key);
  }

  public MvcResult evaluate(String requestJson) throws Exception {
    return mvc.perform(
            MockMvcRequestBuilders.post("/v1/policy-decisions")
                .contentType(MediaType.APPLICATION_JSON)
                .header("X-Correlation-Id", "test-correlation")
                .header("Authorization", TestTokens.DECISION_BEARER)
                .content(requestJson))
        .andReturn();
  }

  public record Scoped(UUID organizationId, UUID policyId, String key) {}
}
