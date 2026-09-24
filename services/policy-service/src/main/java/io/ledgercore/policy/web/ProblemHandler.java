package io.ledgercore.policy.web;

import io.ledgercore.policy.application.EvaluationUnavailableException;
import io.ledgercore.policy.application.IdempotencyConflictException;
import io.ledgercore.policy.domain.PolicyDomainException;
import java.net.URI;
import java.util.Map;
import org.postgresql.util.PSQLException;
import org.slf4j.MDC;
import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpStatus;
import org.springframework.http.ProblemDetail;
import org.springframework.http.ResponseEntity;
import org.springframework.http.converter.HttpMessageNotReadableException;
import org.springframework.web.bind.MethodArgumentNotValidException;
import org.springframework.web.bind.MissingRequestHeaderException;
import org.springframework.web.bind.annotation.ExceptionHandler;
import org.springframework.web.bind.annotation.RestControllerAdvice;
import org.springframework.web.method.annotation.HandlerMethodValidationException;
import org.springframework.web.method.annotation.MethodArgumentTypeMismatchException;

/**
 * RFC 9457 problem details with a stable {@code code} and the request's {@code correlationId}.
 * Internal details (stack traces, SQL) are never included.
 */
@RestControllerAdvice
public class ProblemHandler {

  /**
   * Stable, dereference-free problem type identifiers, e.g.
   * urn:ledgercore:problem:IDEMPOTENCY_CONFLICT.
   */
  static final String TYPE_PREFIX = "urn:ledgercore:problem:";

  /** Seconds the ledger should wait before retrying after a 503. */
  static final String RETRY_AFTER_SECONDS = "1";

  private static final Map<String, String> UNIQUE_CONSTRAINT_CODES =
      Map.of(
          "ux_policies_key", "POLICY_KEY_TAKEN",
          "ux_policies_organization", "POLICY_ORGANIZATION_TAKEN",
          "ux_policies_single_default", "DEFAULT_POLICY_EXISTS",
          "ux_policy_versions_one_active", "VERSION_ALREADY_ACTIVE");

  @ExceptionHandler(PolicyDomainException.class)
  ResponseEntity<ProblemDetail> domain(PolicyDomainException e) {
    var status =
        switch (e.kind()) {
          case INVALID -> HttpStatus.BAD_REQUEST;
          case NOT_FOUND -> HttpStatus.NOT_FOUND;
          case CONFLICT -> HttpStatus.CONFLICT;
        };
    return problem(status, e.code(), e.getMessage());
  }

  @ExceptionHandler(ContractVersionUnsupportedException.class)
  ResponseEntity<ProblemDetail> contractVersion(ContractVersionUnsupportedException e) {
    return problem(HttpStatus.CONFLICT, "CONTRACT_VERSION_UNSUPPORTED", e.getMessage());
  }

  @ExceptionHandler(IdempotencyConflictException.class)
  ResponseEntity<ProblemDetail> idempotency(IdempotencyConflictException e) {
    return problem(HttpStatus.CONFLICT, "IDEMPOTENCY_CONFLICT", e.getMessage());
  }

  @ExceptionHandler(EvaluationUnavailableException.class)
  ResponseEntity<ProblemDetail> unavailable(EvaluationUnavailableException e) {
    var response =
        problem(
            HttpStatus.SERVICE_UNAVAILABLE,
            "POLICY_EVALUATION_UNAVAILABLE",
            "A trustworthy policy decision cannot be produced right now. No decision was recorded.");
    return ResponseEntity.status(response.getStatusCode())
        .header(HttpHeaders.RETRY_AFTER, RETRY_AFTER_SECONDS)
        .body(response.getBody());
  }

  @ExceptionHandler(DuplicateKeyException.class)
  ResponseEntity<ProblemDetail> duplicate(DuplicateKeyException e) {
    var constraint =
        e.getMostSpecificCause() instanceof PSQLException psql
                && psql.getServerErrorMessage() != null
            ? psql.getServerErrorMessage().getConstraint()
            : null;
    var code =
        constraint == null
            ? "CONFLICT"
            : UNIQUE_CONSTRAINT_CODES.getOrDefault(constraint, "CONFLICT");
    return problem(HttpStatus.CONFLICT, code, "The request conflicts with existing data.");
  }

  @ExceptionHandler(MethodArgumentNotValidException.class)
  ResponseEntity<ProblemDetail> invalid(MethodArgumentNotValidException e) {
    var fields =
        e.getBindingResult().getFieldErrors().stream()
            .map(f -> f.getField() + ": " + f.getDefaultMessage())
            .sorted()
            .toList();
    var response = problem(HttpStatus.BAD_REQUEST, "VALIDATION_FAILED", String.join("; ", fields));
    response.getBody().setProperty("errors", fields);
    return response;
  }

  @ExceptionHandler(HandlerMethodValidationException.class)
  ResponseEntity<ProblemDetail> invalidParameters(HandlerMethodValidationException e) {
    return problem(HttpStatus.BAD_REQUEST, "VALIDATION_FAILED", "Request parameters are invalid.");
  }

  @ExceptionHandler(HttpMessageNotReadableException.class)
  ResponseEntity<ProblemDetail> unreadable(HttpMessageNotReadableException e) {
    return problem(
        HttpStatus.BAD_REQUEST,
        "REQUEST_INVALID",
        "The request body is not valid JSON for this operation (unknown field, wrong type or"
            + " malformed value).");
  }

  @ExceptionHandler(MissingRequestHeaderException.class)
  ResponseEntity<ProblemDetail> missingHeader(MissingRequestHeaderException e) {
    var code =
        PolicyManagementController.ACTOR_HEADER.equalsIgnoreCase(e.getHeaderName())
            ? "ACTOR_REQUIRED"
            : "HEADER_REQUIRED";
    return problem(
        HttpStatus.BAD_REQUEST, code, "The " + e.getHeaderName() + " header is required.");
  }

  @ExceptionHandler(MethodArgumentTypeMismatchException.class)
  ResponseEntity<ProblemDetail> typeMismatch(MethodArgumentTypeMismatchException e) {
    return problem(HttpStatus.BAD_REQUEST, "PATH_INVALID", e.getName() + " is not valid.");
  }

  private static ResponseEntity<ProblemDetail> problem(
      HttpStatus status, String code, String detail) {
    var problem = ProblemDetail.forStatusAndDetail(status, detail);
    // Explicit type: Spring omits the default "about:blank", but contract v1 requires `type`.
    problem.setType(URI.create(TYPE_PREFIX + code));
    problem.setTitle(code);
    problem.setProperty("code", code);
    var correlationId = MDC.get(CorrelationIdFilter.MDC_KEY);
    if (correlationId != null) {
      problem.setProperty("correlationId", correlationId);
    }
    return ResponseEntity.status(status).body(problem);
  }
}
