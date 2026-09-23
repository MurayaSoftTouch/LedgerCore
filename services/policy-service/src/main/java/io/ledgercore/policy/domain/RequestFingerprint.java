package io.ledgercore.policy.domain;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.HexFormat;

/**
 * SHA-256 over a canonical form of the decision-relevant request fields (ADR-011). Two requests for
 * the same transaction are "identical" when their fingerprints match.
 *
 * <p>Included: organization, transaction type, currency, amount (numerically normalised, so {@code
 * "100.50"} equals {@code "100.5"}), and the account context as a sorted multiset. Excluded: {@code
 * requestedAt}, {@code requestedBy} and {@code contractVersion}, which a legitimate retry may
 * change and which no rule reads.
 */
public final class RequestFingerprint {

  private RequestFingerprint() {}

  public static String of(EvaluationInput input) {
    var accounts =
        input.accountContext().stream()
            .map(a -> a.accountId() + ":" + a.accountType() + ":" + a.side())
            .sorted()
            .toList();
    var canonical =
        String.join(
            "|",
            "v1",
            input.organizationId().toString(),
            input.transactionType(),
            input.currency(),
            input.totalAmount().stripTrailingZeros().toPlainString(),
            String.join(",", accounts));
    try {
      var digest = MessageDigest.getInstance("SHA-256");
      return HexFormat.of().formatHex(digest.digest(canonical.getBytes(StandardCharsets.UTF_8)));
    } catch (NoSuchAlgorithmException e) {
      throw new IllegalStateException("SHA-256 is required by every Java platform", e);
    }
  }
}
