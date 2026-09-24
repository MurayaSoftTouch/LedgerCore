package io.ledgercore.policy.support;

import static org.assertj.core.api.Assertions.assertThat;

import com.jayway.jsonpath.JsonPath;
import org.springframework.test.web.servlet.MvcResult;

public final class Expect {

  private Expect() {}

  public static String status(MvcResult result, int expected) throws Exception {
    var body = result.getResponse().getContentAsString();
    assertThat(result.getResponse().getStatus())
        .as("HTTP status; body: %s", body)
        .isEqualTo(expected);
    return body;
  }

  public static void problem(MvcResult result, int expected, String code) throws Exception {
    var body = status(result, expected);
    assertThat(result.getResponse().getContentType()).startsWith("application/problem+json");
    assertThat((String) JsonPath.read(body, "$.code")).isEqualTo(code);
  }

  public static <T> T json(MvcResult result, String path) throws Exception {
    return JsonPath.read(result.getResponse().getContentAsString(), path);
  }
}
