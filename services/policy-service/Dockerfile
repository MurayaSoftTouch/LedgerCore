# syntax=docker/dockerfile:1
# Build context: services/policy-service. Tests run in CI, not in the image build.

FROM eclipse-temurin:21-jdk-alpine AS build
WORKDIR /src
COPY .mvn/ .mvn/
COPY mvnw pom.xml ./
RUN ./mvnw -B -ntp -q dependency:go-offline
COPY src/ src/
RUN ./mvnw -B -ntp -q package -DskipTests && cp target/policy-service-*.jar /policy-service.jar

FROM eclipse-temurin:21-jre-alpine AS runtime
RUN addgroup -S app && adduser -S -G app app
COPY --from=build /policy-service.jar /app/policy-service.jar
USER app
EXPOSE 8081
HEALTHCHECK --interval=5s --timeout=3s --start-period=40s --retries=12 \
  CMD wget -qO- http://127.0.0.1:8081/actuator/health/readiness >/dev/null || exit 1
ENTRYPOINT ["java", "-XX:MaxRAMPercentage=75", "-jar", "/app/policy-service.jar"]
