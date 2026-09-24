package io.ledgercore.policy.web;

import io.ledgercore.policy.application.EvaluationUnavailableException;
import io.ledgercore.policy.application.IdempotencyConflictException;
import io.ledgercore.policy.domain.PolicyDomainException;
import java.net.URI;
import java.util.Map;
import org.postgresql.util.PSQLException;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.slf4j.MDC;
import org.springframework.dao.DuplicateKeyException;
import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpStatus;
import org.springframework.http.ProblemDetail;
import org.springframework.http.ResponseEntity;
import org.springframework.http.converter.HttpMessageNotReadableException;
import org.springframework.web.ErrorResponse;
import org.springframework.web.ErrorResponseException;
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

  private static final Logger log = LoggerFactory.getLogger(ProblemHandler.class);

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
    for (Throwable cause = e; cause != null; cause = cause.getCause()) {
      if (cause instanceof RequestSizeLimitFilter.RequestTooLargeException) {
        return problem(
            HttpStatus.CONTENT_TOO_LARGE,
            "REQUEST_TOO_LARGE",
            "The request body exceeds the allowed size.");
      }
    }
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

  /**
   * Spring's own HTTP errors (unsupported method or media type, missing route, ...) keep their
   * status, as problem details with a stable code.
   */
  @ExceptionHandler(ErrorResponseException.class)
  ResponseEntity<ProblemDetail> http(ErrorResponseException e) {
    var status = HttpStatus.resolve(e.getStatusCode().value());
    var code = status == null ? "HTTP_ERROR" : status.name();
    return problem(
        status == null ? HttpStatus.BAD_REQUEST : status, code, "The request cannot be served.");
  }

  /**
   * Anything unexpected: a generic 500, logged with the correlation id (via MDC), never exposing
   * the exception, SQL or a stack trace. Evaluation failures never reach here: they are 503.
   */
  @ExceptionHandler(Exception.class)
  ResponseEntity<ProblemDetail> unexpected(Exception e) {
    if (e instanceof ErrorResponse error) {
      var status = HttpStatus.resolve(error.getStatusCode().value());
      if (status != null && !status.is5xxServerError()) {
        return problem(status, status.name(), "The request cannot be served.");
      }
    }
    log.error("Unhandled exception", e);
    return problem(
        HttpStatus.INTERNAL_SERVER_ERROR,
        "INTERNAL_ERROR",
        "An unexpected error occurred. Quote the X-Correlation-Id when reporting it.");
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
