using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LedgerCore.Ledger.Api.Persistence;

namespace LedgerCore.Ledger.Api.Application;

/// <summary>
/// What makes two requests "the same" for an idempotency key (ADR-015): a SHA-256 over a versioned,
/// JSON-encoded tuple of the fields that define the command. JSON encoding keeps the tuple
/// unambiguous (no delimiter tricks). Transport metadata such as the correlation id, timestamps or
/// headers other than the actor is deliberately excluded, so a genuine retry always matches.
/// </summary>
/// <remarks>
/// Posting: operation, ledger, journal, actor. Reversal: operation, ledger, original journal,
/// actor, requested description (null and "" differ). Changing these fields requires a new version.
/// </remarks>
internal static class CommandFingerprint
{
    public const int Version = 1;

    public static string ForPosting(Guid ledgerId, Guid journalId, string actor) =>
        Hash(CommandOperation.PostJournal, ledgerId, journalId, actor);

    public static string ForReversal(Guid ledgerId, Guid originalJournalId, string actor, string? description) =>
        Hash(CommandOperation.ReverseJournal, ledgerId, originalJournalId, actor, description);

    private static string Hash(CommandOperation operation, Guid ledgerId, Guid journalId, string actor, string? description = null)
    {
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            Version, EnumText.ToText(operation), ledgerId, journalId, actor, description,
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
