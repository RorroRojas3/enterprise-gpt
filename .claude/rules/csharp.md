---
paths:
  - "**/*.cs"
---

# C# Development

## Instructions

- Always use the latest version C#, currently C# 14 features.
- Make only high confidence suggestions when reviewing code changes.
- Write maintainable code; handle edge cases with clear exception handling.
- Comment only what code cannot say: why-decisions, constraints, non-obvious invariants. Never restate what the code does or narrate a change.
- Web/API-specific standards (auth, validation, versioning, performance, deployment) live in `aspnet-rest-apis.md`.

## Naming Conventions

- Follow PascalCase for component names, method names, and public members.
- Prefix private fields with an underscore and use `_camelCase` (e.g., `_userService`, `_cache`).
- Use camelCase for local variables and parameters.
- Prefix interface names with "I" (e.g., IUserService).

## Modern C# Constructs

- Prefer primary constructors for classes (services, controllers, handlers). Capture every injected dependency into a `private readonly` `_camelCase` field at the top of the class, and reference the field — not the primary-constructor parameter — in method bodies:

  ```csharp
  public class UserService(IUserRepository repository, ILogger<UserService> logger) : IUserService
  {
      private readonly IUserRepository _repository = repository;
      private readonly ILogger<UserService> _logger = logger;
  }
  ```

- Prefer collection expressions wherever the target type is clear: `[]` for empty collections, `[1, 2, 3]` for literals, and spreads like `[.. items, extra]`. Do **not** write `new List<T>()`, `new T[] { }`, or `Array.Empty<T>()` when a collection expression works:

  ```csharp
  private readonly List<string> _names = [];
  string[] merged = [.. first, .. second];
  ```

## Formatting

- Apply code-formatting style defined in `.editorconfig`.
- Prefer file-scoped namespace declarations and single-line using directives.
- Insert a newline before the opening curly brace of any code block (e.g., after `if`, `for`, `while`, `foreach`, `using`, `try`, etc.).
- Ensure that the final return statement of a method is on its own line.
- Use pattern matching and switch expressions wherever possible.
- Use `nameof` instead of string literals when referring to member names.
- Ensure that XML doc comments are created for any public APIs. When applicable, include `<example>` and `<code>` documentation in the comments.

## Project Structure

- Create projects from the appropriate .NET templates.
- Organize code with feature folders or domain-driven design; keep models, services, and data access in separate layers.
- Use the .NET configuration system with environment-specific settings.

## Nullable Reference Types

- Declare variables non-nullable, and check for `null` at entry points.
- Always use `is null` or `is not null` instead of `== null` or `!= null`.
- Trust the C# null annotations and don't add null checks when the type system says a value cannot be null.

## Data Access

- Use Entity Framework Core; choose the provider per environment (SQL Server, SQLite, In-Memory).
- Apply the repository pattern where it adds value.
- Manage schema with migrations; seed data where needed.
- Write efficient queries — avoid N+1 and over-fetching.

## Logging and Monitoring

- Use structured logging (e.g. Serilog) with appropriate log levels.
- Integrate Application Insights; correlate requests with correlation IDs.
- Monitor performance, errors, and usage.

## Testing

- Always include test cases for critical paths of the application.
- Do not emit "Act", "Arrange" or "Assert" comments.
- Copy existing style in nearby files for test method names and capitalization.
- Mock dependencies for isolated unit tests.
