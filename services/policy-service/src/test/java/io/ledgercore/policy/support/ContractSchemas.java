package io.ledgercore.policy.support;

import com.networknt.schema.Error;
import com.networknt.schema.InputFormat;
import com.networknt.schema.Schema;
import com.networknt.schema.SchemaRegistry;
import com.networknt.schema.SchemaRegistryConfig;
import com.networknt.schema.SpecificationVersion;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import tools.jackson.databind.json.JsonMapper;
import tools.jackson.dataformat.yaml.YAMLMapper;

/**
 * The repository's contract files, loaded as-is (never copied into test code): the request and
 * response JSON Schemas and the ProblemDetails schema from the OpenAPI document. JSON Schema {@code
 * format} is asserted (uuid, date-time), not merely annotated.
 */
public final class ContractSchemas {

  private static final Path CONTRACTS = locate();
  private static final SchemaRegistry REGISTRY =
      SchemaRegistry.withDefaultDialect(
          SpecificationVersion.DRAFT_2020_12,
          b ->
              b.schemaRegistryConfig(
                  SchemaRegistryConfig.builder().formatAssertionsEnabled(true).build()));
  private static final JsonMapper JSON = JsonMapper.builder().build();

  public static final Schema REQUEST = load("schemas/policy-decision-request.v1.schema.json");
  public static final Schema RESPONSE = load("schemas/policy-decision-response.v1.schema.json");
  public static final Schema PROBLEM = problemDetails();

  private ContractSchemas() {}

  public static Path examples() {
    return CONTRACTS.resolve("schemas/examples");
  }

  public static List<String> violations(Schema schema, String json) {
    return schema.validate(json, InputFormat.JSON).stream().map(Error::toString).toList();
  }

  public static String read(Path file) {
    try {
      return Files.readString(file);
    } catch (IOException e) {
      throw new IllegalStateException(e);
    }
  }

  private static Schema load(String relative) {
    return REGISTRY.getSchema(read(CONTRACTS.resolve(relative)), InputFormat.JSON);
  }

  private static Schema problemDetails() {
    var openapi =
        YAMLMapper.builder()
            .build()
            .readTree(read(CONTRACTS.resolve("openapi/policy-decision.v1.yaml")));
    var schema = openapi.path("components").path("schemas").path("ProblemDetails");
    if (schema.isMissingNode()) {
      throw new IllegalStateException("ProblemDetails schema missing from the OpenAPI document");
    }
    return REGISTRY.getSchema(JSON.writeValueAsString(schema), InputFormat.JSON);
  }

  private static Path locate() {
    for (var dir = Path.of("").toAbsolutePath(); dir != null; dir = dir.getParent()) {
      var candidate = dir.resolve("contracts");
      if (Files.exists(candidate.resolve("openapi/policy-decision.v1.yaml"))) {
        return candidate;
      }
    }
    throw new IllegalStateException("contracts/ directory not found");
  }
}
