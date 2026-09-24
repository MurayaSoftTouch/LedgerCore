package io.ledgercore.policy;

import static io.ledgercore.policy.support.Requests.STANDARD_RULES;
import static io.ledgercore.policy.support.Requests.decision;
import static org.assertj.core.api.Assertions.assertThat;

import io.ledgercore.policy.support.ContractSchemas;
import io.ledgercore.policy.support.Expect;
import io.ledgercore.policy.support.PostgresIntegrationTest;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.UUID;
import java.util.stream.Stream;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;
import org.junit.jupiter.params.provider.ValueSource;
import org.springframework.test.web.servlet.MvcResult;
import tools.jackson.databind.json.JsonMapper;
import tools.jackson.databind.node.ObjectNode;

/**
 * Contract v1 over real HTTP serialization: every body the service returns is validated against the
 * repository's schemas, and the repository's example payloads are sent as-is.
 */
class ContractComplianceTests extends PostgresIntegrationTest {

  private static final JsonMapper JSON = JsonMapper.builder().build();

  static Stream<Path> validRequestExamples() throws IOException {
    return examples("request.", ".valid.json");
  }

  static Stream<Path> invalidRequestExamples() throws IOException {
    return examples("request.", ".invalid.json");
  }

  private static Stream<Path> examples(String prefix, String suffix) throws IOException {
    try (var files = Files.list(ContractSchemas.examples())) {
      var list =
          files
              .filter(p -> p.getFileName().toString().startsWith(prefix))
              .filter(p -> p.getFileName().toString().endsWith(suffix))
              .sorted()
              .toList();
      assertThat(list).isNotEmpty();
      return list.stream();
    }
  }

  private static void assertValidResponse(MvcResult result) throws Exception {
    var body = Expect.status(result, 200);
    assertThat(result.getResponse().getContentType()).startsWith("application/json");
    assertThat(ContractSchemas.violations(ContractSchemas.RESPONSE, body)).isEmpty();
  }

  private static void assertValidProblem(MvcResult result, int status) throws Exception {
    var body = Expect.status(result, status);
    assertThat(result.getResponse().getContentType()).startsWith("application/problem+json");
    assertThat(ContractSchemas.violations(ContractSchemas.PROBLEM, body)).isEmpty();
  }

  @ParameterizedTest
  @MethodSource("validRequestExamples")
  void validContractExamplesAreAcceptedAndAnsweredWithSchemaValidResponses(Path example)
      throws Exception {
    var request = ContractSchemas.read(example);
    assertThat(ContractSchemas.violations(ContractSchemas.REQUEST, request)).isEmpty();

    assertValidResponse(api().evaluate(request));
  }

  @ParameterizedTest
  @MethodSource("invalidRequestExamples")
  void invalidContractExamplesAreRejected(Path example) throws Exception {
    var request = ContractSchemas.read(example);
    assertThat(ContractSchemas.violations(ContractSchemas.REQUEST, request)).isNotEmpty();

    assertValidProblem(api().evaluate(request), 400);
  }

  @ParameterizedTest
  @ValueSource(strings = {"PAYMENT|1.00", "PAYMENT|20000", "ADJUSTMENT|60000.5", "FEE|0"})
  void everyDecisionShapeValidatesAgainstTheResponseSchema(String scenario) throws Exception {
    var parts = scenario.split("\\|");
    var scope = api().activePolicy(STANDARD_RULES);
    var request = decision(UUID.randomUUID(), scope.organizationId(), parts[0], parts[1]);
    assertThat(ContractSchemas.violations(ContractSchemas.REQUEST, request)).isEmpty();

    var result = api().evaluate(request);

    assertValidResponse(result);
    String evaluatedAt = Expect.json(result, "$.evaluatedAt");
    assertThat(evaluatedAt).endsWith("Z");
  }

  @Test
  void transactionTypeFromALaterMinorVersionIsToleratedAsReview() throws Exception {
    // "WIRE" is outside the 1.0 schema's enum: it stands for a value a later 1.x adds. ADR-005
    // requires receivers to answer REVIEW_REQUIRED rather than reject the request.
    var scope = api().activePolicy(STANDARD_RULES);
    var request = decision(UUID.randomUUID(), scope.organizationId(), "WIRE", "1");
    assertThat(ContractSchemas.violations(ContractSchemas.REQUEST, request))
        .singleElement()
        .asString()
        .contains("/transactionType");

    var result = api().evaluate(request);

    assertValidResponse(result);
    assertThat((String) Expect.json(result, "$.decision")).isEqualTo("REVIEW_REQUIRED");
  }

  @Test
  void noPolicyResponseIsSchemaValid() throws Exception {
    // An organization with a paused policy: REVIEW_REQUIRED with policyVersion "none".
    var api = api();
    var org = UUID.randomUUID();
    api.createVersion(api.createPolicy("contract-" + org.toString().substring(0, 8), org), "[]");

    assertValidResponse(api.evaluate(decision(UUID.randomUUID(), org, "PAYMENT", "1")));
  }

  static Stream<String> requiredFields() {
    return Stream.of(
        "contractVersion",
        "transactionId",
        "organizationId",
        "transactionType",
        "currency",
        "totalAmount",
        "accountContext",
        "requestedBy",
        "requestedAt");
  }

  @ParameterizedTest
  @MethodSource("requiredFields")
  void everyRequiredFieldIsRequired(String field) throws Exception {
    var node =
        (ObjectNode) JSON.readTree(decision(UUID.randomUUID(), UUID.randomUUID(), "PAYMENT", "1"));
    node.remove(field);
    var request = JSON.writeValueAsString(node);
    assertThat(ContractSchemas.violations(ContractSchemas.REQUEST, request)).isNotEmpty();

    assertValidProblem(api().evaluate(request), 400);
  }

  @ParameterizedTest
  @ValueSource(
      strings = {
        "\"totalAmount\": \"1\"|\"totalAmount\": 1",
        "\"totalAmount\": \"1\"|\"totalAmount\": \"-1\"",
        "\"totalAmount\": \"1\"|\"totalAmount\": \"1.00001\"",
        "\"currency\": \"KES\"|\"currency\": \"kes\"",
        "\"side\": \"DEBIT\"|\"side\": \"debit\"",
        "\"accountType\": \"EXPENSE\"|\"accountType\": \"CASH\"",
        "\"principalType\": \"USER\"|\"principalType\": \"ROBOT\"",
        "\"requestedAt\": \"2026-09-23T09:15:00Z\"|\"requestedAt\": \"yesterday\"",
        "\"transactionType\": \"PAYMENT\"|\"transactionType\": \"payment\"",
        "\"principalId\": \"user-4821\"|\"principalId\": \"\""
      })
  void typeAndPatternViolationsAreRejected(String replacement) throws Exception {
    var parts = replacement.split("\\|");
    var request =
        decision(UUID.randomUUID(), UUID.randomUUID(), "PAYMENT", "1").replace(parts[0], parts[1]);
    assertThat(request).contains(parts[1]);
    assertThat(ContractSchemas.violations(ContractSchemas.REQUEST, request)).isNotEmpty();

    assertValidProblem(api().evaluate(request), 400);
  }

  @Test
  void unknownTopLevelFieldIsRejected() throws Exception {
    var request =
        decision(UUID.randomUUID(), UUID.randomUUID(), "PAYMENT", "1")
            .replace("\"currency\"", "\"accountBalance\": \"100.00\", \"currency\"");

    assertValidProblem(api().evaluate(request), 400);
  }

  @Test
  void singleAccountContextIsRejected() throws Exception {
    var node =
        (ObjectNode) JSON.readTree(decision(UUID.randomUUID(), UUID.randomUUID(), "PAYMENT", "1"));
    ((tools.jackson.databind.node.ArrayNode) node.get("accountContext")).remove(1);

    assertValidProblem(api().evaluate(JSON.writeValueAsString(node)), 400);
  }

  @ParameterizedTest
  @ValueSource(strings = {"2.0.0", "0.9.0", "1", "v1.0.0"})
  void unsupportedContractVersionIsConflict(String version) throws Exception {
    var request =
        decision(UUID.randomUUID(), UUID.randomUUID(), "PAYMENT", "1")
            .replace("\"1.0.0\"", "\"" + version + "\"");

    var result = api().evaluate(request);

    assertValidProblem(result, 409);
    assertThat((String) Expect.json(result, "$.code")).isEqualTo("CONTRACT_VERSION_UNSUPPORTED");
  }

  @Test
  void laterMinorContractVersionIsAccepted() throws Exception {
    var scope = api().activePolicy(STANDARD_RULES);
    var request =
        decision(UUID.randomUUID(), scope.organizationId(), "PAYMENT", "1")
            .replace("\"1.0.0\"", "\"1.7.3\"");

    assertValidResponse(api().evaluate(request));
  }

  @Test
  void idempotencyConflictIsASchemaValidProblem() throws Exception {
    var scope = api().activePolicy(STANDARD_RULES);
    var transaction = UUID.randomUUID();
    api().evaluate(decision(transaction, scope.organizationId(), "PAYMENT", "1"));

    assertValidProblem(
        api().evaluate(decision(transaction, scope.organizationId(), "PAYMENT", "2")), 409);
  }

  @Test
  void correlationIdIsEchoedOnSuccessAndOnErrors() throws Exception {
    var scope = api().activePolicy(STANDARD_RULES);
    var ok = api().evaluate(decision(UUID.randomUUID(), scope.organizationId(), "PAYMENT", "1"));
    var bad = api().evaluate("{}");

    assertThat(ok.getResponse().getHeader("X-Correlation-Id")).isEqualTo("test-correlation");
    assertThat(bad.getResponse().getHeader("X-Correlation-Id")).isEqualTo("test-correlation");
    assertThat((String) Expect.json(bad, "$.correlationId")).isEqualTo("test-correlation");
  }

  @Test
  void problemsNeverLeakInternals() throws Exception {
    var result = api().evaluate("{\"contractVersion\": ");
    var body = Expect.status(result, 400);

    assertThat(body).doesNotContain("Exception", "at io.", "tools.jackson", "SQL");
    List<String> keys = List.copyOf(JSON.readTree(body).propertyNames());
    assertThat(keys).doesNotContain("trace", "exception", "stackTrace");
  }
}
