package io.ledgercore.policy;

import static org.assertj.core.api.Assertions.assertThat;

import io.ledgercore.policy.support.PolicyPostgres;
import io.ledgercore.policy.support.PostgresIntegrationTest;
import io.ledgercore.policy.support.TestTokens;
import java.io.ByteArrayInputStream;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.util.Collections;
import java.util.UUID;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.system.CapturedOutput;
import org.springframework.boot.test.system.OutputCaptureExtension;
import org.springframework.boot.test.web.server.LocalServerPort;
import org.springframework.test.context.DynamicPropertyRegistry;
import org.springframework.test.context.DynamicPropertySource;

/**
 * HTTP hardening over real HTTP (Milestone 5): bounded bodies and lists, problem details for every
 * error without internals, no server banner, and no secret in the logs of failing requests.
 */
@SpringBootTest(webEnvironment = SpringBootTest.WebEnvironment.RANDOM_PORT)
@ExtendWith(OutputCaptureExtension.class)
class HttpHardeningTests {

  private static final String BODY_SECRET = "body-secret-value-5c3e";

  @LocalServerPort int port;

  private final HttpClient http = HttpClient.newHttpClient();

  @DynamicPropertySource
  static void properties(DynamicPropertyRegistry registry) {
    PostgresIntegrationTest.database(registry);
    registry.add("ledgercore.policy.auth.admin-token", () -> TestTokens.ADMIN);
  }

  private HttpResponse<String> send(HttpRequest.Builder request) throws Exception {
    return http.send(request.build(), HttpResponse.BodyHandlers.ofString());
  }

  private HttpRequest.Builder at(String path) {
    return HttpRequest.newBuilder(URI.create("http://127.0.0.1:" + port + path));
  }

  @Test
  void aDeclaredBodyOverTheLimitIs413BeforeAuthenticationReadsAnything() throws Exception {
    var body = "x".repeat((int) RequestSizeLimit.MAX + 1) + BODY_SECRET;

    var response =
        send(
            at("/v1/policy-decisions")
                .header("Authorization", TestTokens.DECISION_BEARER)
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body)));

    assertThat(response.statusCode()).isEqualTo(413);
    assertThat(response.body()).contains("REQUEST_TOO_LARGE").doesNotContain(BODY_SECRET);
  }

  @Test
  void aChunkedBodyOverTheLimitIsCutOffWith413() throws Exception {
    var bytes =
        ("{\"contractVersion\":\"1.1.0\",\"x\":\""
                + "y".repeat((int) RequestSizeLimit.MAX)
                + BODY_SECRET
                + "\"}")
            .getBytes(StandardCharsets.UTF_8);

    var response =
        send(
            at("/v1/policy-decisions")
                .header("Authorization", TestTokens.DECISION_BEARER)
                .header("Content-Type", "application/json")
                .POST(
                    HttpRequest.BodyPublishers.ofInputStream(
                        () -> new ByteArrayInputStream(bytes))));

    assertThat(response.statusCode()).isEqualTo(413);
    assertThat(response.body()).contains("REQUEST_TOO_LARGE").doesNotContain(BODY_SECRET);
  }

  @Test
  void listsInsideARuleAreBounded() throws Exception {
    var ids = String.join(",", Collections.nCopies(51, "\"" + UUID.randomUUID() + "\""));
    // Scoped to its own organization: only one default (unscoped) policy may exist.
    var body =
        "{\"key\":\"bounded-"
            + UUID.randomUUID()
            + "\",\"name\":\"n\",\"organizationId\":\""
            + UUID.randomUUID()
            + "\"}";
    var created =
        send(
            at("/api/v1/policies")
                .header("Authorization", TestTokens.ADMIN_BEARER)
                .header("X-Actor-Id", "tester")
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body)));
    var policy = created.body().replaceAll(".*\"id\":\"([0-9a-f-]{36})\".*", "$1");
    var rules =
        "{\"rules\":[{\"type\":\"ACCOUNT_CONTEXT\",\"outcome\":\"REVIEW_REQUIRED\",\"reasonCode\":\"MANY_ACCOUNTS\",\"accountIds\":["
            + ids
            + "]}]}";

    var response =
        send(
            at("/api/v1/policies/" + policy + "/versions")
                .header("Authorization", TestTokens.ADMIN_BEARER)
                .header("X-Actor-Id", "tester")
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(rules)));

    assertThat(created.statusCode()).isEqualTo(201);
    assertThat(response.statusCode()).isEqualTo(400);
    assertThat(response.body()).contains("VALIDATION_FAILED").contains("accountIds");
  }

  @Test
  void springErrorsAreProblemDetailsWithoutInternalsOrABanner() throws Exception {
    var method =
        send(at("/v1/policy-decisions").header("Authorization", TestTokens.DECISION_BEARER).GET());
    var media =
        send(
            at("/v1/policy-decisions")
                .header("Authorization", TestTokens.DECISION_BEARER)
                .header("Content-Type", "text/plain")
                .POST(HttpRequest.BodyPublishers.ofString("hello")));
    var missing =
        send(at("/v1/nothing-here").header("Authorization", TestTokens.DECISION_BEARER).GET());

    assertThat(method.statusCode()).isEqualTo(405);
    assertThat(method.body()).contains("\"code\":\"METHOD_NOT_ALLOWED\"");
    assertThat(media.statusCode()).isEqualTo(415);
    assertThat(media.body()).contains("\"code\":\"UNSUPPORTED_MEDIA_TYPE\"");
    assertThat(missing.statusCode()).isEqualTo(404);
    assertThat(missing.body()).contains("\"code\":\"NOT_FOUND\"");
    for (var response : new HttpResponse<?>[] {method, media, missing}) {
      assertThat(response.headers().firstValue("Server")).isEmpty();
      assertThat(response.headers().firstValue("Content-Type"))
          .hasValue("application/problem+json");
      assertThat(response.body().toString()).doesNotContain("Exception").doesNotContain("at org.");
    }
  }

  @Test
  void failingRequestsLogNoSecrets(CapturedOutput output) throws Exception {
    send(
        at("/v1/policy-decisions")
            .header("Authorization", "Bearer wrong-" + TestTokens.DECISION)
            .header("Content-Type", "application/json")
            .POST(HttpRequest.BodyPublishers.ofString("{\"x\":\"" + BODY_SECRET + "\"}")));
    send(
        at("/v1/policy-decisions")
            .header("Authorization", TestTokens.DECISION_BEARER)
            .header("Content-Type", "application/json")
            .POST(HttpRequest.BodyPublishers.ofString("{\"contractVersion\":\"" + BODY_SECRET)));
    send(
        at("/api/v1/policies")
            .header("Authorization", TestTokens.DECISION_BEARER)
            .header("Content-Type", "application/json")
            .POST(HttpRequest.BodyPublishers.ofString("{\"key\":\"" + BODY_SECRET + "\"}")));
    send(at("/api/v1/policies").header("Authorization", TestTokens.ADMIN_BEARER).GET());

    assertThat(output.getAll())
        .contains("Request rejected by service authentication")
        .doesNotContain(
            TestTokens.DECISION,
            TestTokens.ADMIN,
            PolicyPostgres.POLICY_PASSWORD,
            PolicyPostgres.POLICY_RUNTIME_PASSWORD,
            BODY_SECRET)
        .doesNotContainIgnoringCase("password=");
  }

  /** The production limit, named for readability. */
  private static final class RequestSizeLimit {
    static final long MAX = io.ledgercore.policy.web.RequestSizeLimitFilter.MAX_BODY_BYTES;
  }
}
