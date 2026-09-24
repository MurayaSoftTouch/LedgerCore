package io.ledgercore.policy.web;

import jakarta.servlet.FilterChain;
import jakarta.servlet.ReadListener;
import jakarta.servlet.ServletException;
import jakarta.servlet.ServletInputStream;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletRequestWrapper;
import jakarta.servlet.http.HttpServletResponse;
import java.io.IOException;
import java.net.URI;
import org.slf4j.MDC;
import org.springframework.core.Ordered;
import org.springframework.core.annotation.Order;
import org.springframework.http.HttpStatus;
import org.springframework.http.MediaType;
import org.springframework.http.ProblemDetail;
import org.springframework.stereotype.Component;
import org.springframework.web.filter.OncePerRequestFilter;
import tools.jackson.databind.json.JsonMapper;

/**
 * Bounds request bodies (Milestone 5). The largest legitimate request is a policy version with the
 * maximum number of rules, each with full lists: well under {@link #MAX_BODY_BYTES}. A declared
 * length over the limit is refused with 413 before anything reads the body; a body without a
 * declared length (chunked) is cut off while it is read, which surfaces as 413 too.
 */
@Component
@Order(Ordered.HIGHEST_PRECEDENCE + 5)
public class RequestSizeLimitFilter extends OncePerRequestFilter {

  public static final long MAX_BODY_BYTES = 256 * 1024;

  /** Thrown while reading a body that exceeds the limit. */
  public static final class RequestTooLargeException extends IOException {
    private static final long serialVersionUID = 1L;

    RequestTooLargeException() {
      super("request body exceeds " + MAX_BODY_BYTES + " bytes");
    }
  }

  private final JsonMapper json;

  public RequestSizeLimitFilter(JsonMapper json) {
    this.json = json;
  }

  @Override
  protected void doFilterInternal(
      HttpServletRequest request, HttpServletResponse response, FilterChain chain)
      throws ServletException, IOException {
    if (request.getContentLengthLong() > MAX_BODY_BYTES) {
      var problem =
          ProblemDetail.forStatusAndDetail(
              HttpStatus.CONTENT_TOO_LARGE, "The request body exceeds the allowed size.");
      problem.setType(URI.create(ProblemHandler.TYPE_PREFIX + "REQUEST_TOO_LARGE"));
      problem.setTitle("REQUEST_TOO_LARGE");
      problem.setProperty("code", "REQUEST_TOO_LARGE");
      var correlationId = MDC.get(CorrelationIdFilter.MDC_KEY);
      if (correlationId != null) {
        problem.setProperty("correlationId", correlationId);
      }
      response.setStatus(HttpStatus.CONTENT_TOO_LARGE.value());
      response.setContentType(MediaType.APPLICATION_PROBLEM_JSON_VALUE);
      response.getOutputStream().write(json.writeValueAsBytes(problem));
      return;
    }
    chain.doFilter(new Bounded(request), response);
  }

  /** Counts bytes as the body is read and fails past the limit. */
  private static final class Bounded extends HttpServletRequestWrapper {
    private ServletInputStream stream;

    Bounded(HttpServletRequest request) {
      super(request);
    }

    @Override
    public ServletInputStream getInputStream() throws IOException {
      if (stream == null) {
        var inner = super.getInputStream();
        stream =
            new ServletInputStream() {
              private long read;

              @Override
              public int read() throws IOException {
                var b = inner.read();
                if (b >= 0) {
                  count(1);
                }
                return b;
              }

              @Override
              public int read(byte[] buffer, int offset, int length) throws IOException {
                var n = inner.read(buffer, offset, length);
                if (n > 0) {
                  count(n);
                }
                return n;
              }

              private void count(long n) throws RequestTooLargeException {
                read += n;
                if (read > MAX_BODY_BYTES) {
                  throw new RequestTooLargeException();
                }
              }

              @Override
              public boolean isFinished() {
                return inner.isFinished();
              }

              @Override
              public boolean isReady() {
                return inner.isReady();
              }

              @Override
              public void setReadListener(ReadListener listener) {
                inner.setReadListener(listener);
              }
            };
      }
      return stream;
    }
  }
}
