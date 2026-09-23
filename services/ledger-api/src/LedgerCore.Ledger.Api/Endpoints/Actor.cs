using LedgerCore.Ledger.Domain;

namespace LedgerCore.Ledger.Api.Endpoints;

/// <summary>
/// The caller identity recorded on journal transitions. Milestone 1 has no authentication, so this is
/// an <b>unverified, client-asserted</b> header; it becomes the authenticated principal later (Milestone 5).
/// </summary>
internal static class Actor
{
    public const string Header = "X-Actor-Id";

    public static string From(HttpContext context)
    {
        var value = context.Request.Headers[Header].ToString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new LedgerDomainException(DomainErrorKind.Invalid, "ACTOR_REQUIRED", $"The {Header} header is required.")
            : value;
    }
}
