package io.ledgercore.policy;

import static io.ledgercore.policy.support.Requests.STANDARD_RULES;
import static org.assertj.core.api.Assertions.assertThat;

import io.ledgercore.policy.application.DecisionOutcome;
import io.ledgercore.policy.application.DecisionService;
import io.ledgercore.policy.application.IdempotencyConflictException;
import io.ledgercore.policy.application.PolicyAdminService;
import io.ledgercore.policy.domain.AccountType;
import io.ledgercore.policy.domain.EntrySide;
import io.ledgercore.policy.domain.EvaluationInput;
import io.ledgercore.policy.domain.EvaluationInput.AccountRef;
import io.ledgercore.policy.support.PolicyPostgres;
import io.ledgercore.policy.support.PostgresIntegrationTest;
import io.ledgercore.policy.support.Sql;
import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.Callable;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.Test;
import org.postgresql.util.PSQLException;
import org.springframework.beans.factory.annotation.Autowired;

/**
 * Real concurrent transactions on separate pooled connections, started together by a latch. No
 * fixed sleeps: lock waits are observed through pg_locks.
 */
class ConcurrencyTests extends PostgresIntegrationTest {

  private static final int THREADS = 8;

  @Autowired private PolicyAdminService admin;
  @Autowired private DecisionService decisions;

  private static <T> List<Future<T>> race(List<Callable<T>> tasks) throws InterruptedException {
    var pool = Executors.newFixedThreadPool(tasks.size());
    var start = new CountDownLatch(1);
    try {
      var futures = new ArrayList<Future<T>>();
      for (var task : tasks) {
        futures.add(
            pool.submit(
                () -> {
                  start.await();
                  return task.call();
                }));
      }
      start.countDown();
      pool.shutdown();
      assertThat(pool.awaitTermination(60, TimeUnit.SECONDS)).isTrue();
      return futures;
    } finally {
      pool.shutdownNow();
    }
  }

  private UUID policy() {
    var org = UUID.randomUUID();
    return admin
        .createPolicy("c-" + org.toString().substring(0, 12), "Concurrency", null, org, "tester")
        .id();
  }

  @Test
  void competingActivationsLeaveExactlyOneActiveVersion() throws Exception {
    var policy = policy();
    var tasks = new ArrayList<Callable<Integer>>();
    for (int i = 0; i < THREADS; i++) {
      var number = admin.createVersion(policy, List.of(), "tester").number();
      tasks.add(() -> admin.activate(policy, number, "activator").number());
    }

    var results = race(tasks);

    for (var f : results) {
      f.get(); // every activation of a DRAFT succeeds; they serialise on the policy lock
    }
    assertThat(
            Sql.count(
                "SELECT count(*) FROM policy_versions WHERE policy_id = ? AND status = 'ACTIVE'",
                policy))
        .isEqualTo(1);
    assertThat(
            Sql.count(
                "SELECT count(*) FROM policy_versions WHERE policy_id = ? AND status = 'RETIRED'",
                policy))
        .isEqualTo(THREADS - 1);
    // Retired versions were all active at some point: each activation superseded the previous one.
    assertThat(
            Sql.count(
                "SELECT count(*) FROM policy_versions WHERE policy_id = ? AND activated_at IS NOT NULL",
                policy))
        .isEqualTo(THREADS);
  }

  @Test
  void sqlThatBypassesTheLockStillCannotCreateTwoActiveVersions() throws Exception {
    var policy = policy();
    var v1 = admin.createVersion(policy, List.of(), "tester").number();
    var v2 = admin.createVersion(policy, List.of(), "tester").number();
    var activate =
        "UPDATE policy_versions SET status = 'ACTIVE', activated_at = now(), activated_by = 'sql'"
            + " WHERE policy_id = ? AND version_number = ?";

    try (var first = PolicyPostgres.connect()) {
      first.setAutoCommit(false);
      Sql.execute(first, activate, policy, v1);

      var pool = Executors.newSingleThreadExecutor();
      try {
        var second =
            pool.submit(
                () -> {
                  try (var c = PolicyPostgres.connect()) {
                    Sql.execute(c, activate, policy, v2);
                    return null;
                  }
                });
        Sql.awaitBlockedBackend(); // the second UPDATE waits on the unique index entry
        first.commit();

        var failure = org.assertj.core.api.Assertions.catchThrowable(second::get);
        assertThat(failure)
            .isInstanceOf(ExecutionException.class)
            .cause()
            .isInstanceOf(PSQLException.class);
        var psql = (PSQLException) failure.getCause();
        assertThat(psql.getSQLState()).isEqualTo("23505");
        assertThat(psql.getServerErrorMessage().getConstraint())
            .isEqualTo("ux_policy_versions_one_active");
      } finally {
        pool.shutdownNow();
      }
    }
    assertThat(
            Sql.count(
                "SELECT count(*) FROM policy_versions WHERE policy_id = ? AND status = 'ACTIVE'",
                policy))
        .isEqualTo(1);
  }

  @Test
  void concurrentVersionCreationNumbersContiguously() throws Exception {
    var policy = policy();
    var tasks = new ArrayList<Callable<Integer>>();
    for (int i = 0; i < THREADS; i++) {
      tasks.add(() -> admin.createVersion(policy, List.of(), "tester").number());
    }

    var numbers = new ArrayList<Integer>();
    for (var f : race(tasks)) {
      numbers.add(f.get());
    }

    assertThat(numbers).containsExactlyInAnyOrder(1, 2, 3, 4, 5, 6, 7, 8);
  }

  @Test
  void activationWaitsForAnInFlightEvaluationUsingTheActiveVersion() throws Exception {
    var policy = policy();
    admin.activate(policy, admin.createVersion(policy, List.of(), "tester").number(), "tester");
    var next = admin.createVersion(policy, List.of(), "tester").number();

    try (var evaluation = PolicyPostgres.connect()) {
      // What DecisionService does while evaluating: share-lock the ACTIVE version until commit.
      evaluation.setAutoCommit(false);
      Sql.execute(
          evaluation,
          "SELECT 1 FROM policy_versions WHERE policy_id = ? AND status = 'ACTIVE' FOR SHARE",
          policy);

      var pool = Executors.newSingleThreadExecutor();
      try {
        var activation = pool.submit(() -> admin.activate(policy, next, "activator"));
        Sql.awaitBlockedBackend();
        assertThat(activation.isDone()).isFalse();

        evaluation.commit();
        assertThat(activation.get(30, TimeUnit.SECONDS).number()).isEqualTo(next);
      } finally {
        pool.shutdownNow();
      }
    }
  }

  private static EvaluationInput input(UUID org, String amount) {
    return new EvaluationInput(
        org,
        "PAYMENT",
        "KES",
        new BigDecimal(amount),
        List.of(
            new AccountRef(UUID.randomUUID(), AccountType.EXPENSE, EntrySide.DEBIT),
            new AccountRef(UUID.randomUUID(), AccountType.ASSET, EntrySide.CREDIT)));
  }

  @Test
  void concurrentIdenticalEvaluationsRecordOneDecision() throws Exception {
    var scope = api().activePolicy(STANDARD_RULES);
    var transaction = UUID.randomUUID();
    var request = input(scope.organizationId(), "20000");
    var tasks = new ArrayList<Callable<DecisionOutcome>>();
    for (int i = 0; i < THREADS; i++) {
      tasks.add(() -> decisions.evaluate(transaction, request, "1.0.0", null));
    }

    var outcomes = new ArrayList<DecisionOutcome>();
    for (var f : race(tasks)) {
      outcomes.add(f.get());
    }

    assertThat(outcomes)
        .extracting(o -> o.decision().id())
        .containsOnly(outcomes.get(0).decision().id());
    assertThat(outcomes).filteredOn(o -> !o.replayed()).hasSize(1);
    assertThat(outcomes)
        .extracting(o -> o.decision().decision().name())
        .containsOnly("REVIEW_REQUIRED");
    assertThat(
            Sql.count(
                "SELECT count(*) FROM policy_decisions WHERE transaction_id = ?", transaction))
        .isEqualTo(1);
    assertThat(
            Sql.count(
                "SELECT count(*) FROM policy_decision_matches m JOIN policy_decisions d ON d.id = m.decision_id"
                    + " WHERE d.transaction_id = ?",
                transaction))
        .isEqualTo(1);
  }

  @Test
  void concurrentConflictingEvaluationsRecordOneDecisionAndRejectTheRest() throws Exception {
    var scope = api().activePolicy(STANDARD_RULES);
    var transaction = UUID.randomUUID();
    var small = input(scope.organizationId(), "100");
    var large =
        new EvaluationInput(
            small.organizationId(),
            "PAYMENT",
            "KES",
            new BigDecimal("60000"),
            small.accountContext());
    var tasks = new ArrayList<Callable<Object>>();
    for (int i = 0; i < THREADS; i++) {
      var input = i % 2 == 0 ? small : large;
      tasks.add(
          () -> {
            try {
              return decisions.evaluate(transaction, input, "1.0.0", null);
            } catch (IdempotencyConflictException e) {
              return e;
            }
          });
    }

    var outcomes = new ArrayList<>();
    for (var f : race(tasks)) {
      outcomes.add(f.get());
    }

    var accepted =
        outcomes.stream()
            .filter(DecisionOutcome.class::isInstance)
            .map(DecisionOutcome.class::cast)
            .toList();
    var rejected =
        outcomes.stream().filter(IdempotencyConflictException.class::isInstance).toList();
    assertThat(accepted).hasSize(THREADS / 2);
    assertThat(rejected).hasSize(THREADS / 2);
    assertThat(accepted)
        .extracting(o -> o.decision().id())
        .containsOnly(accepted.get(0).decision().id());
    assertThat(
            Sql.count(
                "SELECT count(*) FROM policy_decisions WHERE transaction_id = ?", transaction))
        .isEqualTo(1);
  }
}
