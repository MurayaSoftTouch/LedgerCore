using LedgerCore.Ledger.Api.Application;
using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Persistence;
using LedgerCore.Ledger.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LedgerCore.Ledger.Api.Endpoints;

/// <summary>
/// Maps domain rule violations and database constraint/guard violations to RFC 9457 problem details
/// with a stable <c>code</c> extension.
/// </summary>
internal sealed partial class LedgerExceptionHandler(IProblemDetailsService problems, ILogger<LedgerExceptionHandler> logger)
    : IExceptionHandler
{
    private static readonly Dictionary<string, string> UniqueConstraintCodes = new(StringComparer.Ordinal)
    {
        [DatabaseConstraints.LedgerCodeUnique] = "LEDGER_CODE_TAKEN",
        [DatabaseConstraints.AccountCodeUnique] = "ACCOUNT_CODE_TAKEN",
        [DatabaseConstraints.ExternalReferenceUnique] = "EXTERNAL_REFERENCE_TAKEN",
        [DatabaseConstraints.LiveReversalUnique] = "JOURNAL_ALREADY_REVERSED",
    };

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var mapped = exception switch
        {
            LedgerDomainException domain => new Problem(StatusFor(domain.Kind), domain.Code, domain.Message),
            PolicyApprovalUnavailableException policy => FromPolicyFailure(policy.Failure),
            // Malformed JSON, unknown enum values, JSON numbers where strings are required.
            BadHttpRequestException bad => new Problem(bad.StatusCode, "REQUEST_INVALID", bad.Message),
            _ when FindPostgres(exception) is { } pg => FromPostgres(pg),
            _ => null,
        };

        if (mapped is not { } problem)
        {
            return false;
        }

        httpContext.Response.StatusCode = problem.Status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = problem.Status,
                Title = problem.Code,
                Detail = problem.Detail,
                Extensions = { ["code"] = problem.Code },
            },
        });
    }

    private static int StatusFor(DomainErrorKind kind) => kind switch
    {
        DomainErrorKind.Invalid => StatusCodes.Status400BadRequest,
        DomainErrorKind.NotFound => StatusCodes.Status404NotFound,
        DomainErrorKind.RuleViolation => StatusCodes.Status422UnprocessableEntity,
        DomainErrorKind.InvalidState or DomainErrorKind.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>No trustworthy decision was obtained; the journal is unchanged (still PENDING_APPROVAL).</summary>
    private static Problem FromPolicyFailure(PolicyFailureKind failure) => failure switch
    {
        PolicyFailureKind.Timeout => new(StatusCodes.Status503ServiceUnavailable, "POLICY_TIMEOUT", "The policy service did not answer in time. The journal remains PENDING_APPROVAL; retry later."),
        PolicyFailureKind.Unavailable => new(StatusCodes.Status503ServiceUnavailable, "POLICY_UNAVAILABLE", "The policy service is unavailable. The journal remains PENDING_APPROVAL; retry later."),
        PolicyFailureKind.Conflict => new(StatusCodes.Status409Conflict, "POLICY_CONFLICT", "The policy service already decided this transaction with different inputs."),
        _ => new(StatusCodes.Status502BadGateway, "POLICY_" + EnumText.ToText(failure), "The policy service response could not be trusted. The journal remains PENDING_APPROVAL."),
    };

    private static PostgresException? FindPostgres(Exception exception) => exception switch
    {
        PostgresException pg => pg,
        DbUpdateException { InnerException: PostgresException pg } => pg,
        _ => null,
    };

    private Problem? FromPostgres(PostgresException pg)
    {
        switch (pg.SqlState)
        {
            case PostgresErrorCodes.UniqueViolation when pg.ConstraintName is { } name && UniqueConstraintCodes.TryGetValue(name, out var code):
                return new Problem(StatusCodes.Status409Conflict, code, $"Violates unique constraint {name}.");

            case DatabaseConstraints.LedgerInvariantSqlState:
                // The domain should have refused first; reaching a database guard means a race or a bypass.
                var guardCode = pg.MessageText.Split(':', 2)[0];
                LogGuardRejected(guardCode, pg.MessageText);
                return new Problem(StatusCodes.Status409Conflict, guardCode, pg.MessageText);

            case PostgresErrorCodes.ForeignKeyViolation:
                return new Problem(StatusCodes.Status422UnprocessableEntity, "REFERENCE_INVALID", "A referenced ledger, account or journal is not valid here.");

            default:
                return null;
        }
    }

    private sealed record Problem(int Status, string Code, string Detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database ledger guard rejected a write: {GuardCode} ({Detail})")]
    private partial void LogGuardRejected(string guardCode, string detail);
}
