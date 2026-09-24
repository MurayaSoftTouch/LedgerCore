package io.ledgercore.policy;

import static org.assertj.core.api.Assertions.assertThat;

import io.ledgercore.policy.support.PostgresIntegrationTest;
import io.ledgercore.policy.support.TestTokens;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.web.server.LocalServerPort;
import org.springframework.test.context.DynamicPropertyRegistry;
import org.springframework.test.context.DynamicPropertySource;

/**
 * The authentication boundary over <b>real HTTP</b> (embedded Tomcat, raw socket requests), because
 * path normalization happens in the container, which MockMvc does not run. Regression test for the
 * Milestone 5 finding: {@code POST /v1/policy-decisions;x=y} reached the decision endpoint without
 * a credential. A request that reaches the controller answers {@code 400 VALIDATION_FAILED} for the
 * incomplete body used here, so that is what must never happen without a credential.
 */
@SpringBootTest(webEnvironment = SpringBootTest.WebEnvironment.RANDOM_PORT)
class HttpAuthenticationBoundaryTests {

  private static final String BODY = "{\"contractVersion\":\"1.1.0\"}";

  @LocalServerPort int port;

  @DynamicPropertySource
  static void properties(DynamicPropertyRegistry registry) {
    PostgresIntegrationTest.database(registry);
    registry.add("ledgercore.policy.auth.admin-token", () -> TestTokens.ADMIN);
  }

  private record Response(int status, String body) {}

  /** Sends the request line exactly as given: no client-side normalization or encoding. */
  private Response send(String method, String rawPath, String authorization) throws IOException {
    try (var socket = new Socket("127.0.0.1", port)) {
      var request =
          new StringBuilder()
              .append(method)
              .append(' ')
              .append(rawPath)
              .append(" HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n");
      if (authorization != null) {
        request.append("Authorization: ").append(authorization).append("\r\n");
      }
      if (method.equals("POST")) {
        request
            .append("Content-Type: application/json\r\nContent-Length: ")
            .append(BODY.getBytes(StandardCharsets.UTF_8).length)
            .append("\r\n\r\n")
            .append(BODY);
      } else {
        request.append("\r\n");
      }
      OutputStream out = socket.getOutputStream();
      out.write(request.toString().getBytes(StandardCharsets.UTF_8));
      out.flush();
      InputStream in = socket.getInputStream();
      var raw = new String(in.readAllBytes(), StandardCharsets.UTF_8);
      var status = Integer.parseInt(raw.substring(9, 12));
      var bodyStart = raw.indexOf("\r\n\r\n");
      return new Response(status, bodyStart < 0 ? "" : raw.substring(bodyStart + 4));
    }
  }

  @ParameterizedTest
  @ValueSource(
      strings = {
        "/v1/policy-decisions",
        "/v1/policy-decisions;x=y",
        "/v1/policy-decisions;",
        "/v1;x=y/policy-decisions",
        "/v1/policy-decisions/",
        "/v1/policy-decisions/;x=y",
        "//v1/policy-decisions",
        "/v1//policy-decisions",
        "/v1/./policy-decisions",
        "/v1/x/../policy-decisions",
        "/V1/policy-decisions",
        "/v1/policy-decisions%3Bx=y",
        "/v1/policy%2Ddecisions",
        "/v1/policy-decisions?x=y"
      })
  void noPathVariantReachesTheDecisionEndpointWithoutACredential(String path) throws IOException {
    var response = send("POST", path, null);

    assertThat(response.status()).as(path).isIn(400, 401, 404);
    assertThat(response.body()).as(path).doesNotContain("VALIDATION_FAILED");
  }

  @ParameterizedTest
  @ValueSource(
      strings = {"/v1/policy-decisions;x=y", "//v1/policy-decisions", "/v1/./policy-decisions"})
  void theAdminCredentialDoesNotOpenTheDecisionEndpointUnderAnyPathVariant(String path)
      throws IOException {
    var response = send("POST", path, "Bearer " + TestTokens.ADMIN);

    assertThat(response.body()).as(path).doesNotContain("VALIDATION_FAILED");
  }

  @ParameterizedTest
  @ValueSource(
      strings = {
        "/api/v1/policies",
        "/api/v1/policies;x=y",
        "/api;x=y/v1/policies",
        "//api/v1/policies",
        "/api/v1/./policies",
        "/API/v1/policies"
      })
  void theDecisionCredentialDoesNotOpenTheManagementApi(String path) throws IOException {
    var response = send("GET", path, "Bearer " + TestTokens.DECISION);

    assertThat(response.status()).as(path).isNotEqualTo(200);
    assertThat(response.body()).as(path).doesNotContain(TestTokens.DECISION);
  }

  /**
   * Regression for the most severe variant: with the raw-URI filter, {@code /api;x=y/...} reached
   * the management API with no credential at all, so anyone could create and activate a policy.
   */
  @ParameterizedTest
  @ValueSource(strings = {"/api;x=y/v1/policies", "/api/v1/policies;x=y", "/api;/v1/policies"})
  void noPathVariantReachesTheManagementApiWithoutACredential(String path) throws IOException {
    var read = send("GET", path, null);
    var write = send("POST", path, null);

    assertThat(read.status()).as(path).isIn(400, 401, 404);
    assertThat(write.status()).as(path).isIn(400, 401, 404);
    assertThat(write.body())
        .as(path)
        .doesNotContain("VALIDATION_FAILED")
        .doesNotContain("ACTOR_REQUIRED");
  }

  @Test
  void aValidCredentialStillWorksAndPathParametersDoNotChangeWhichOneIsNeeded() throws IOException {
    assertThat(send("POST", "/v1/policy-decisions", "Bearer " + TestTokens.DECISION).body())
        .contains("VALIDATION_FAILED");
    assertThat(send("POST", "/v1/policy-decisions;x=y", "Bearer " + TestTokens.DECISION).body())
        .contains("VALIDATION_FAILED");
    assertThat(send("GET", "/api/v1/policies", "Bearer " + TestTokens.ADMIN).status())
        .isEqualTo(200);
  }

  @Test
  void onlyHealthInfoAndDocsArePublicAndEverythingElseIsRefused() throws IOException {
    assertThat(send("GET", "/actuator/health/liveness", null).status()).isEqualTo(200);
    assertThat(send("GET", "/actuator/health/readiness", null).status()).isEqualTo(200);
    assertThat(send("GET", "/actuator/info", null).status()).isEqualTo(200);
    for (var path :
        new String[] {"/actuator", "/actuator/env", "/actuator/health;x=y/../env", "/"}) {
      var response = send("GET", path, "Bearer " + TestTokens.ADMIN);
      assertThat(response.status()).as(path).isIn(400, 404);
      assertThat(response.body()).as(path).doesNotContain(TestTokens.ADMIN);
    }
  }

  @Test
  void rejectionsNeverEchoTheCredential() throws IOException {
    var wrong = "Bearer not-the-token-" + TestTokens.DECISION.substring(0, 8);
    for (var path : new String[] {"/v1/policy-decisions", "/api/v1/policies"}) {
      var response = send(path.startsWith("/v1") ? "POST" : "GET", path, wrong);
      assertThat(response.status()).isEqualTo(401);
      assertThat(response.body())
          .doesNotContain("not-the-token-")
          .doesNotContain(TestTokens.DECISION);
    }
  }
}
