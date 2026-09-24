package io.ledgercore.policy.security;

import io.ledgercore.policy.web.CorrelationIdFilter;
import jakarta.servlet.FilterChain;
import jakarta.servlet.ServletException;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import java.io.IOException;
import java.net.URI;
import java.net.URISyntaxException;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.slf4j.MDC;
import org.springframework.core.Ordered;
import org.springframework.core.annotation.Order;
import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpStatus;
import org.springframework.http.MediaType;
import org.springframework.http.ProblemDetail;
import org.springframework.stereotype.Component;
import org.springframework.web.filter.OncePerRequestFilter;
import tools.jackson.databind.json.JsonMapper;

/**
 * Minimal service authentication (ADR-013). Two separate bearer credentials:
 *
 * <ul>
 *   <li>{@code /v1/policy-decisions}: the decision credential (held by the ledger);
 *   <li>{@code /api/v1/**}: the admin credential; the management API is disabled (403) when none is
 *       configured.
 * </ul>
 *
 * Health, info and API docs stay unauthenticated; <b>every other path is refused</b> before it
 * reaches Spring MVC (deny by default). Paths are classified on the servlet path, which the
 * container has already decoded, normalized and stripped of path parameters. Matching the raw
 * request URI let {@code /v1/policy-decisions;x=y} through unauthenticated, because MVC routes it
 * to the decision endpoint while the raw URI matched no protected prefix (fixed in Milestone 5).
 *
 * <p>Credentials are compared in constant time over SHA-256 digests and are never logged or echoed.
 * This is not a substitute for workload identity or mTLS.
 */
@Component
@Order(Ordered.HIGHEST_PRECEDENCE + 10)
public class ServiceAuthenticationFilter extends OncePerRequestFilter {

  private static final Logger log = LoggerFactory.getLogger(ServiceAuthenticationFilter.class);
  private static final String BEARER = "Bearer ";
  private static final String TYPE_PREFIX = "urn:ledgercore:problem:";

  private final byte[] decisionDigest;
  private final byte[] adminDigest;
  private final JsonMapper json;

  public ServiceAuthenticationFilter(ServiceAuthProperties properties, JsonMapper json) {
    this.decisionDigest = sha256(properties.decisionToken());
    this.adminDigest = properties.managementEnabled() ? sha256(properties.adminToken()) : null;
    this.json = json;
  }

  /** Which credential a path requires. */
  enum Area {
    PUBLIC,
    DECISION,
    ADMIN,
    UNKNOWN
  }

  /**
   * Classifies a container-normalized servlet path. Anything not listed is {@link Area#UNKNOWN}.
   */
  static Area classify(String path) {
    if (path.equals("/actuator/health")
        || path.startsWith("/actuator/health/")
        || path.equals("/actuator/info")
        || path.startsWith("/openapi/")) {
      return Area.PUBLIC;
    }
    if (path.equals("/v1") || path.startsWith("/v1/")) {
      return Area.DECISION;
    }
    if (path.equals("/api") || path.startsWith("/api/")) {
      return Area.ADMIN;
    }
    return Area.UNKNOWN;
  }

  @Override
  protected void doFilterInternal(
      HttpServletRequest request, HttpServletResponse response, FilterChain chain)
      throws ServletException, IOException {
    var path =
        request.getServletPath() + (request.getPathInfo() == null ? "" : request.getPathInfo());
    switch (classify(path)) {
      case PUBLIC -> chain.doFilter(request, response);
      case DECISION -> {
        if (presented(request, decisionDigest)) {
          chain.doFilter(request, response);
        } else {
          reject(
              response,
              path,
              HttpStatus.UNAUTHORIZED,
              "SERVICE_AUTHENTICATION_REQUIRED",
              "A valid service credential is required.");
        }
      }
      case ADMIN -> {
        if (adminDigest == null) {
          reject(
              response,
              path,
              HttpStatus.FORBIDDEN,
              "MANAGEMENT_API_DISABLED",
              "The management API is disabled: no administrative credential is configured.");
        } else if (presented(request, adminDigest)) {
          chain.doFilter(request, response);
        } else {
          reject(
              response,
              path,
              HttpStatus.UNAUTHORIZED,
              "ADMIN_AUTHENTICATION_REQUIRED",
              "A valid administrative credential is required.");
        }
      }
      case UNKNOWN ->
          reject(response, path, HttpStatus.NOT_FOUND, "NOT_FOUND", "No such resource.");
    }
  }

  private static boolean presented(HttpServletRequest request, byte[] expectedDigest) {
    var header = request.getHeader(HttpHeaders.AUTHORIZATION);
    if (header == null || !header.startsWith(BEARER)) {
      return false;
    }
    return MessageDigest.isEqual(sha256(header.substring(BEARER.length())), expectedDigest);
  }

  private void reject(
      HttpServletResponse response, String path, HttpStatus status, String code, String detail)
      throws IOException {
    var loggedPath = path.length() > 200 ? path.substring(0, 200) : path;
    log.atWarn()
        .addKeyValue("path", loggedPath)
        .addKeyValue("code", code)
        .log("Request rejected by service authentication");
    var problem = ProblemDetail.forStatusAndDetail(status, detail);
    problem.setType(URI.create(TYPE_PREFIX + code));
    problem.setTitle(code);
    // The path is client-controlled; omit it rather than fail on characters URI refuses.
    try {
      problem.setInstance(new URI(null, null, path, null));
    } catch (URISyntaxException e) {
      // no instance
    }
    problem.setProperty("code", code);
    var correlationId = MDC.get(CorrelationIdFilter.MDC_KEY);
    if (correlationId != null) {
      problem.setProperty("correlationId", correlationId);
    }
    response.setStatus(status.value());
    if (status == HttpStatus.UNAUTHORIZED) {
      response.setHeader(HttpHeaders.WWW_AUTHENTICATE, "Bearer realm=\"ledgercore-policy\"");
    }
    response.setContentType(MediaType.APPLICATION_PROBLEM_JSON_VALUE);
    response.getOutputStream().write(json.writeValueAsBytes(problem));
  }

  private static byte[] sha256(String value) {
    try {
      return MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8));
    } catch (NoSuchAlgorithmException e) {
      throw new IllegalStateException("SHA-256 is required by every Java platform", e);
    }
  }
}
