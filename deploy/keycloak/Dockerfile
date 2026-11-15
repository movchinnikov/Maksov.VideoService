# syntax=docker/dockerfile:1.7
# Keycloak with the custom provider: phone sign-in and registration + login theme.
# Build context is deploy/keycloak.
FROM maven:3.9-eclipse-temurin-21 AS spi
WORKDIR /build
COPY spi/pom.xml .
RUN --mount=type=cache,target=/root/.m2 mvn -q -B dependency:go-offline
COPY spi/src src
RUN --mount=type=cache,target=/root/.m2 mvn -q -B package

FROM quay.io/keycloak/keycloak:26.8.2
COPY --from=spi /build/target/videoservice-keycloak-spi.jar /opt/keycloak/providers/

