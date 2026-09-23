package io.ledgercore.policy;

import static org.assertj.core.api.Assertions.assertThat;

import io.ledgercore.policy.support.Expect;
import io.ledgercore.policy.support.PostgresIntegrationTest;
import io.ledgercore.policy.support.Requests;
import java.util.List;
import java.util.UUID;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.springframework.http.MediaType;
import org.springframework.test.web.servlet.request.MockMvcRequestBuilders;

class PolicyManagementApiTests extends PostgresIntegrationTest {

  private static final String ONE_RULE =
      """
      [{"type": "AMOUNT_ABOVE", "currency": "KES", "threshold": "100",
        "outcome": "REVIEW_REQUIRED", "reasonCode": "AMOUNT_OVER_100"}]
      """;

  private static String uniqueKey() {
    return "k-" + UUID.randomUUID().toString().substring(0, 12);
  }

  @Test
  void createsAndRetrievesPolicy() throws Exception {
    var api = api();
    var org = UUID.randomUUID();
    var key = uniqueKey();

    var id = api.createPolicy(key, org);

    var result = api.get("/api/v1/policies/" + id);
    Expect.status(result, 200);
    assertThat((String) Expect.json(result, "$.key")).isEqualTo(key);
    assertThat((String) Expect.json(result, "$.organizationId")).isEqualTo(org.toString());
    assertThat((String) Expect.json(result, "$.createdBy")).isEqualTo("tester");
    assertThat(Expect.<Object>json(result, "$.activeVersion")).isNull();
    List<String> keys = Expect.json(api.get("/api/v1/policies"), "$[*].key");
    assertThat(keys).contains(key);
  }

  @Test
  void duplicateKeyIsConflict() throws Exception {
    var api = api();
    var key = uniqueKey();
    api.createPolicy(key, UUID.randomUUID());

    Expect.problem(
        api.post(
            "/api/v1/policies",
            "{\"key\": \""
                + key
                + "\", \"name\": \"x\", \"organizationId\": \""
                + UUID.randomUUID()
                + "\"}"),
        409,
        "POLICY_KEY_TAKEN");
  }

  @Test
  void oneOrganizationHasAtMostOnePolicy() throws Exception {
    var api = api();
    var org = UUID.randomUUID();
    api.createPolicy(uniqueKey(), org);

    Expect.problem(
        api.post(
            "/api/v1/policies",
            "{\"key\": \""
                + uniqueKey()
                + "\", \"name\": \"x\", \"organizationId\": \""
                + org
                + "\"}"),
        409,
        "POLICY_ORGANIZATION_TAKEN");
  }

  @Test
  void invalidKeyAndMissingActorAreBadRequests() throws Exception {
    var api = api();
    Expect.problem(
        api.post("/api/v1/policies", "{\"key\": \"Has Spaces\", \"name\": \"x\"}"),
        400,
        "POLICY_KEY_INVALID");

    var noActor =
        mockMvc
            .perform(
                MockMvcRequestBuilders.post("/api/v1/policies")
                    .contentType(MediaType.APPLICATION_JSON)
                    .content("{\"key\": \"" + uniqueKey() + "\", \"name\": \"x\"}"))
            .andReturn();
    Expect.problem(noActor, 400, "ACTOR_REQUIRED");
  }

  @Test
  void unknownPolicyIsNotFound() throws Exception {
    Expect.problem(api().get("/api/v1/policies/" + UUID.randomUUID()), 404, "POLICY_NOT_FOUND");
  }

  @Test
  void versionsAreNumberedMonotonicallyAndStartAsDraft() throws Exception {
    var api = api();
    var policy = api.createPolicy(uniqueKey(), UUID.randomUUID());

    assertThat(api.createVersion(policy, ONE_RULE)).isEqualTo(1);
    assertThat(api.createVersion(policy, "[]")).isEqualTo(2);
    assertThat(api.createVersion(policy, ONE_RULE)).isEqualTo(3);

    var list = api.get("/api/v1/policies/" + policy + "/versions");
    List<Integer> numbers = Expect.json(list, "$[*].version");
    List<String> statuses = Expect.json(list, "$[*].status");
    assertThat(numbers).containsExactly(1, 2, 3);
    assertThat(statuses).containsOnly("DRAFT");
  }

  @Test
  void activationSupersedesThePreviousActiveVersion() throws Exception {
    var api = api();
    var key = uniqueKey();
    var policy = api.createPolicy(key, UUID.randomUUID());
    api.createVersion(policy, ONE_RULE);
    api.createVersion(policy, "[]");

    api.activate(policy, 1);
    api.activate(policy, 2);

    var v1 = api.get("/api/v1/policies/" + policy + "/versions/1");
    var v2 = api.get("/api/v1/policies/" + policy + "/versions/2");
    assertThat((String) Expect.json(v1, "$.status")).isEqualTo("RETIRED");
    assertThat((String) Expect.json(v1, "$.activatedAt")).isNotNull();
    assertThat((String) Expect.json(v1, "$.retiredAt")).isEqualTo(Expect.json(v2, "$.activatedAt"));
    assertThat((String) Expect.json(v2, "$.status")).isEqualTo("ACTIVE");
    assertThat((String) Expect.json(v2, "$.label")).isEqualTo(key + "@2");
    assertThat((Integer) Expect.json(api.get("/api/v1/policies/" + policy), "$.activeVersion"))
        .isEqualTo(2);
  }

  @Test
  void historicalVersionKeepsItsRulesAfterSupersession() throws Exception {
    var api = api();
    var policy = api.createPolicy(uniqueKey(), UUID.randomUUID());
    api.createVersion(policy, ONE_RULE);
    api.activate(policy, 1);
    api.createVersion(policy, "[]");
    api.activate(policy, 2);

    var v1 = api.get("/api/v1/policies/" + policy + "/versions/1");

    assertThat((String) Expect.json(v1, "$.rules[0].reasonCode")).isEqualTo("AMOUNT_OVER_100");
    assertThat((String) Expect.json(v1, "$.rules[0].threshold")).isEqualTo("100.0000");
    assertThat((Integer) Expect.json(v1, "$.rules[0].position")).isEqualTo(1);
  }

  @Test
  void retiredVersionIsTerminal() throws Exception {
    var api = api();
    var policy = api.createPolicy(uniqueKey(), UUID.randomUUID());
    api.createVersion(policy, ONE_RULE);
    api.activate(policy, 1);

    Expect.status(api.postNoBody("/api/v1/policies/" + policy + "/versions/1/retire"), 200);

    Expect.problem(
        api.postNoBody("/api/v1/policies/" + policy + "/versions/1/activate"),
        409,
        "VERSION_INVALID_STATE");
    Expect.problem(
        api.postNoBody("/api/v1/policies/" + policy + "/versions/1/retire"),
        409,
        "VERSION_ALREADY_RETIRED");
    assertThat(Expect.<Object>json(api.get("/api/v1/policies/" + policy), "$.activeVersion"))
        .isNull();
  }

  @Test
  void activeVersionCannotBeActivatedAgain() throws Exception {
    var api = api();
    var policy = api.createPolicy(uniqueKey(), UUID.randomUUID());
    api.createVersion(policy, ONE_RULE);
    api.activate(policy, 1);

    Expect.problem(
        api.postNoBody("/api/v1/policies/" + policy + "/versions/1/activate"),
        409,
        "VERSION_INVALID_STATE");
  }

  @Test
  void versionsHaveNoGenericUpdateEndpoint() throws Exception {
    var api = api();
    var policy = api.createPolicy(uniqueKey(), UUID.randomUUID());
    api.createVersion(policy, ONE_RULE);

    var patch =
        mockMvc
            .perform(
                MockMvcRequestBuilders.patch("/api/v1/policies/" + policy + "/versions/1")
                    .contentType(MediaType.APPLICATION_JSON)
                    .content("{\"status\": \"ACTIVE\"}"))
            .andReturn();
    assertThat(patch.getResponse().getStatus()).isEqualTo(405);
    var put =
        mockMvc
            .perform(
                MockMvcRequestBuilders.put("/api/v1/policies/" + policy + "/versions/1")
                    .contentType(MediaType.APPLICATION_JSON)
                    .content("{\"rules\": []}"))
            .andReturn();
    assertThat(put.getResponse().getStatus()).isEqualTo(405);
    assertThat(
            (String) Expect.json(api.get("/api/v1/policies/" + policy + "/versions/1"), "$.status"))
        .isEqualTo("DRAFT");
  }

  @ParameterizedTest(name = "{0}")
  @CsvSource(
      delimiter = '|',
      textBlock =
          """
          field of another type   | [{"type":"AMOUNT_ABOVE","currency":"KES","threshold":"1","allowedCurrencies":["KES"],"outcome":"REJECTED","reasonCode":"X_Y"}] | RULE_FIELD_NOT_ALLOWED
          missing currency        | [{"type":"AMOUNT_ABOVE","threshold":"1","outcome":"REJECTED","reasonCode":"X_Y"}]                                             | RULE_CURRENCY_INVALID
          threshold as number     | [{"type":"AMOUNT_ABOVE","currency":"KES","threshold":1,"outcome":"REJECTED","reasonCode":"X_Y"}]                               | REQUEST_INVALID
          threshold too precise   | [{"type":"AMOUNT_ABOVE","currency":"KES","threshold":"1.00001","outcome":"REJECTED","reasonCode":"X_Y"}]                       | RULE_THRESHOLD_INVALID
          unknown rule type       | [{"type":"SCRIPT","outcome":"REJECTED","reasonCode":"X_Y"}]                                                                    | REQUEST_INVALID
          approve outcome         | [{"type":"CURRENCY_NOT_ALLOWED","allowedCurrencies":["KES"],"outcome":"APPROVED","reasonCode":"X_Y"}]                         | REQUEST_INVALID
          bad reason code         | [{"type":"CURRENCY_NOT_ALLOWED","allowedCurrencies":["KES"],"outcome":"REJECTED","reasonCode":"lower"}]                       | REASON_CODE_INVALID
          empty transaction types | [{"type":"TRANSACTION_TYPE","transactionTypes":[],"outcome":"REJECTED","reasonCode":"X_Y"}]                                   | RULE_TRANSACTION_TYPES_REQUIRED
          unknown transaction     | [{"type":"TRANSACTION_TYPE","transactionTypes":["WIRE"],"outcome":"REJECTED","reasonCode":"X_Y"}]                             | REQUEST_INVALID
          no account criteria     | [{"type":"ACCOUNT_CONTEXT","side":"DEBIT","outcome":"REJECTED","reasonCode":"X_Y"}]                                            | RULE_ACCOUNT_CRITERIA_REQUIRED
          unknown field           | [{"type":"CURRENCY_NOT_ALLOWED","allowedCurrencies":["KES"],"outcome":"REJECTED","reasonCode":"X_Y","expression":"1==1"}]    | REQUEST_INVALID
          bad currency code       | [{"type":"CURRENCY_NOT_ALLOWED","allowedCurrencies":["kes"],"outcome":"REJECTED","reasonCode":"X_Y"}]                         | RULE_CURRENCIES_INVALID
          """)
  void malformedRuleConfigurationIsRejectedAndNothingIsStored(
      String description, String rules, String code) throws Exception {
    var api = api();
    var policy = api.createPolicy(uniqueKey(), UUID.randomUUID());

    Expect.problem(
        api.post("/api/v1/policies/" + policy + "/versions", "{\"rules\": " + rules + "}"),
        400,
        code);

    List<Object> versions = Expect.json(api.get("/api/v1/policies/" + policy + "/versions"), "$");
    assertThat(versions).isEmpty();
  }

  @Test
  void standardRuleSetRoundTrips() throws Exception {
    var api = api();
    var policy = api.createPolicy(uniqueKey(), UUID.randomUUID());
    api.createVersion(policy, Requests.STANDARD_RULES);

    var v1 = api.get("/api/v1/policies/" + policy + "/versions/1");

    List<String> types = Expect.json(v1, "$.rules[*].type");
    assertThat(types)
        .containsExactly(
            "AMOUNT_ABOVE",
            "AMOUNT_ABOVE",
            "TRANSACTION_TYPE",
            "CURRENCY_NOT_ALLOWED",
            "ACCOUNT_CONTEXT");
    assertThat((String) Expect.json(v1, "$.rules[4].side")).isEqualTo("DEBIT");
    List<String> allowed = Expect.json(v1, "$.rules[3].allowedCurrencies");
    assertThat(allowed).containsExactly("KES", "USD");
  }
}
