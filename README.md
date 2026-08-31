# Enterprise GPT

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Angular](https://img.shields.io/badge/Angular-21-DD0031?logo=angular)](https://angular.dev/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![TypeScript](https://img.shields.io/badge/TypeScript-5.9-3178C6?logo=typescript)](https://www.typescriptlang.org/)
[![SQL Server](https://img.shields.io/badge/SQL%20Server-2025-CC2927?logo=microsoft-sql-server)](https://www.microsoft.com/sql-server)

An enterprise AI chat platform. Users sign in with Microsoft Entra ID, hold streaming conversations
with large language models, attach documents that become searchable context, and — with the right
permission — have the assistant generate files. Administrators manage the model catalog, the MCP
tool servers, and user permissions.

**Documentation lives in [`docs/`](docs/README.md)** — start with
[the architecture overview](docs/architecture/overview.md).

## Layout

```
enterprise-gpt/
├── enterprise-gpt-api/    .NET 10 backend (Enterprise.Gpt.sln)
│   ├── Enterprise.Gpt.Api/          minimal-API endpoints, hosting, providers
│   ├── Enterprise.Gpt.Service/      business logic and every subsystem
│   ├── Enterprise.Gpt.Repository/   EF Core DbContext and migrations
│   ├── Enterprise.Gpt.Entity/       entities and Cosmos document shapes
│   ├── Enterprise.Gpt.Dto/          request and response DTOs, id catalogs
│   ├── Enterprise.Gpt.Common/       shared enums and telemetry names
│   └── tests/                       xUnit v3 unit and integration tests
├── enterprise-gpt-ui/     Angular 21 frontend (standalone, zoneless)
└── docs/                  engineering documentation
```

## Prerequisites

| | |
| --- | --- |
| .NET 10 SDK | [download](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Node.js 24 | the `engines.node` range in `enterprise-gpt-ui/package.json` |
| SQL Server 2025 | earlier engines and LocalDB are rejected — the DbContext pins `UseCompatibilityLevel(170)` |
| Docker | integration tests only |
| An Azure OpenAI resource | required: it serves the default model and every document embedding |
| A Microsoft Entra ID app registration | authentication |

Azure Cosmos DB, Blob Storage, Document Intelligence and Application Insights are used by the
transcript, document, extraction and telemetry subsystems respectively.

## Quick start

```bash
git clone https://github.com/RorroRojas3/enterprise-gpt.git
cd enterprise-gpt
```

**1. Configure the API.** Never put a secret in `appsettings.json` — it is checked in.

```bash
cd enterprise-gpt-api/Enterprise.Gpt.Api
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Database=EnterpriseGpt;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;"
dotnet user-secrets set "AzureOpenAI:Url" "https://<resource>.services.ai.azure.com"
dotnet user-secrets set "AzureOpenAI:ApiKey" "<key>"
dotnet user-secrets set "AzureAd:TenantId" "<tenant>"
dotnet user-secrets set "AzureAd:ClientId" "<client>"
```

`AzureOpenAI:Url` is the resource **root** — a URL that already carries `/openai/v1` fails at
startup. Every other section, and the optional providers, are listed in
[docs/operations/configuration.md](docs/operations/configuration.md).

**2. Trust the development certificate**, or every call to the API will fail while *looking* like a
CORS error:

```bash
dotnet dev-certs https --trust
```

**3. Run the API.** The schema is created and seeded on first start — `Database.Migrate()` runs at
startup, so there is no separate migration step.

```bash
cd enterprise-gpt-api
dotnet run --project Enterprise.Gpt.Api
```

It listens on `https://localhost:7045`. In Development, the OpenAPI document is at `/openapi` and
the Scalar reference at `/scalar`.

**4. Run the client.**

```bash
cd enterprise-gpt-ui
npm install
npm start
```

It serves `http://localhost:4200`, which is the only origin the API's CORS policy allows by default.
Runtime configuration comes from `public/config.json`, which is fetched and validated before
bootstrap — an invalid file renders a fatal shell rather than starting the app.

## Commands

```bash
# enterprise-gpt-api/
dotnet build
dotnet test                                     # integration tests need Docker
dotnet test --filter "Category!=Integration"    # unit only

# enterprise-gpt-ui/
npm start            # ng serve
npm run build        # production build, then the initial-chunk gate
npm run lint         # eslint, then the icon, forbidden-API and token checks
npm test             # Vitest, single run
npm run test:a11y    # the axe suite — not part of npm test
npm run format       # Prettier (format:check for the read-only variant)
```

Both sides have CI; see [docs/development/testing-and-ci.md](docs/development/testing-and-ci.md).

## Documentation

| Area | Start here |
| --- | --- |
| How it all fits together | [architecture/overview.md](docs/architecture/overview.md) |
| Backend structure | [architecture/backend.md](docs/architecture/backend.md) |
| Frontend structure | [architecture/frontend.md](docs/architecture/frontend.md) |
| Data model and migrations | [architecture/data-model.md](docs/architecture/data-model.md) |
| Sign-in and permissions | [architecture/auth-and-permissions.md](docs/architecture/auth-and-permissions.md) |
| Chat turns and streaming | [conversations/](docs/conversations/turn-lifecycle.md) |
| Documents, search and summarization | [documents/](docs/documents/ingestion.md) |
| MCP servers and the File Agent | [tools/](docs/tools/mcp-servers.md) |
| Models and providers | [models/](docs/models/providers.md) |
| Configuration and runbooks | [operations/](docs/operations/configuration.md) |

The full index is [docs/README.md](docs/README.md).

## Contributing

Read [`.claude/CLAUDE.md`](.claude/CLAUDE.md) for the coding standards this repository enforces —
C# conventions, Angular and NgRx Signals patterns, and the comment style. Run `npm run lint`,
`npm run format` and both test suites before opening a pull request.

## License

MIT — see [LICENSE](LICENSE).

## Authors

- Rodrigo Rojas — [@RorroRojas3](https://github.com/RorroRojas3)
