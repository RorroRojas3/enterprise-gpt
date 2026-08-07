---
paths:
  - "**/*.cs"
---

# ASP.NET REST API Development

## Instructions

- Build REST APIs on ASP.NET Core 10, using Web API controllers or Minimal APIs.
- Apply best practices for API design, testing, documentation, and deployment.
- Note the reasoning behind non-obvious design decisions; no tutorial-style explanations unless asked.
- General C# standards (naming, formatting, nullability, data access, logging) live in `csharp.md`.

## API Design

- Design resource-oriented URLs with appropriate HTTP verbs, status codes, content negotiation, and consistent response formatting.
- Choose controllers or Minimal APIs per project requirements; keep the choice consistent within a service.

## Project Structure

- Create projects from the ASP.NET Core 10 Web API template.

## Controller-Based APIs

- Use attribute routing and the `[ApiController]` attribute; name resources RESTfully.
- Inject dependencies via constructors.
- Pick action return types deliberately: `ActionResult<T>`, `IActionResult`, or specific types.

## Minimal APIs

- Organize endpoints with route groups.
- Use parameter binding, validation, and dependency injection as in controllers.
- Structure larger Minimal API applications for readability.

## Authentication and Authorization

- Authenticate with JWT Bearer tokens; integrate Microsoft Entra ID where applicable.
- Apply role-based or policy-based authorization.
- Secure controller-based and Minimal APIs consistently.

## Validation and Error Handling

- Validate with DataAnnotations or FluentValidation; customize validation responses when the default shape does not fit.
- Handle exceptions globally in middleware; return Problem Details (RFC 9457) responses consistently.

## Versioning and Documentation

- Version APIs — controllers and Minimal APIs alike.
- Document endpoints, parameters, responses, and authentication with Swagger/OpenAPI.

## Testing

- Unit test controllers, Minimal API endpoints, and services; add integration tests for endpoints.
- Mock dependencies; test authentication and authorization logic.

## Performance

- Cache appropriately: in-memory, distributed, or response caching.
- Stay async end to end; paginate, filter, and sort large data sets; enable response compression.
- Measure and benchmark before optimizing.

## Deployment and DevOps

- Containerize with .NET's built-in container support (`dotnet publish --os linux --arch x64 -p:PublishProfile=DefaultContainer`) instead of a manual Dockerfile.
- Ship through CI/CD to Azure App Service, Azure Container Apps, or comparable hosting.
- Implement health checks and readiness probes; keep configuration environment-specific per stage.
