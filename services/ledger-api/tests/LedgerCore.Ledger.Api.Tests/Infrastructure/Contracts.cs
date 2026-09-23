using System.Text.Json;
using Json.Schema;

namespace LedgerCore.Ledger.Api.Tests.Infrastructure;

/// <summary>The repository's contract files, loaded as-is (never copied into test code).</summary>
internal static class Contracts
{
    private static readonly Lazy<JsonSchema> RequestSchema = new(() => JsonSchema.FromText(Read("schemas/policy-decision-request.v1.schema.json")));
    private static readonly Lazy<JsonSchema> ResponseSchema = new(() => JsonSchema.FromText(Read("schemas/policy-decision-response.v1.schema.json")));

    public static string Root
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "contracts");
                if (File.Exists(Path.Combine(candidate, "openapi", "policy-decision.v1.yaml")))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("contracts/ not found");
        }
    }

    public static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    public static IEnumerable<string> Examples(string prefix, string suffix) =>
        Directory.GetFiles(Path.Combine(Root, "schemas", "examples"))
            .Select(Path.GetFileName)
            .Where(n => n!.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(suffix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)!;

    public static bool IsValidRequest(string json) => Evaluate(RequestSchema.Value, json);

    public static bool IsValidResponse(string json) => Evaluate(ResponseSchema.Value, json);

    private static bool Evaluate(JsonSchema schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        return schema.Evaluate(document.RootElement, new EvaluationOptions { RequireFormatValidation = true }).IsValid;
    }
}
