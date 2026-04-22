# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [9.0.47] - 2026-04-22

### Added
- Security headers middleware and request metrics middleware.
- Custom exception hierarchy for validation, auth, not found, and rate limiting.
- Health endpoints: `/health/live`, `/health/ready` with structured JSON writer.
- Repository quality assets: `.editorconfig`, `Directory.Build.props`, CODEOWNERS, PR/issue templates, Dependabot.
- Docker and local infrastructure compose setup.

### Changed
- Hardened CORS behavior with tenant/domain-aware origin checks.
- Improved HTTP resilience with retry/timeout/circuit-breaker strategy.
- Improved Redis and MongoDB connection resilience defaults.
- Global exception handling now returns RFC7807 ProblemDetails.
- Added backward-compatible type/property shims for typo corrections.

### Deprecated
- `ConfigerAzureServiceBus` in favor of `ConfigureAzureServiceBus`.
- `ConsumerMessage.SccheduledEnqueueTimeUtc` in favor of `ScheduledEnqueueTimeUtc`.
- `SecretEnpPointAttribute` in favor of `SecretEndPointAttribute`.
