# Enterprise GPT

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Angular](https://img.shields.io/badge/Angular-21-DD0031?logo=angular)](https://angular.dev/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![TypeScript](https://img.shields.io/badge/TypeScript-5.9-3178C6?logo=typescript)](https://www.typescriptlang.org/)
[![SQL Server](https://img.shields.io/badge/SQL%20Server-Vector%20Search-CC2927?logo=microsoft-sql-server)](https://www.microsoft.com/sql-server)

Enterprise GPT is a full-stack AI chat platform built with a .NET 10 Web API backend and an Angular 21 frontend. It supports multiple AI service providers including Ollama, OpenAI, Azure AI Foundry, and Anthropic, with document management and vector search capabilities.

## 📋 Table of Contents

- [Features](#-features)
- [Architecture](#️-architecture)
- [Tech Stack](#️-tech-stack)
- [Prerequisites](#-prerequisites)
- [Quick Start](#-quick-start)
- [Configuration](#-configuration)
- [Database Setup](#-database-setup)
- [API Endpoints](#-api-endpoints)
- [Examples](#-examples)
- [Development](#-development)
- [Testing](#-testing)
- [Troubleshooting](#-troubleshooting)
- [Contributing](#-contributing)
- [License](#-license)
- [Authors](#-authors)
- [Acknowledgments](#-acknowledgments)

## 🚀 Features

- **Multi-AI Provider Support**: Integrate with Ollama, OpenAI, Azure AI Foundry, and Anthropic
- **Real-time Chat**: Server-sent events for streaming responses
- **Document Management**: Upload and search documents with vector embeddings
- **Session Management**: Persistent chat sessions with history
- **Modern UI**: Responsive Angular frontend with Bootstrap 5
- **Vector Search**: AI-powered document search using SQL Server Vector Search

## 🏗️ Architecture

```
ai-chat/
├── enterprise-gpt-api/             # .NET 10 Web API Backend
│   ├── Enterprise.Gpt.Api/         # API Controllers & Program.cs
│   ├── Enterprise.Gpt.Service/     # Business Logic Services
│   ├── Enterprise.Gpt.Repository/  # Data Access Layer
│   ├── Enterprise.Gpt.Entity/      # Entity Framework Models
│   └── Enterprise.Gpt.Dto/         # Data Transfer Objects
└── enterprise-ui/                  # Angular 21 Frontend
    ├── src/app/services/       # HTTP Services
    ├── src/app/dtos/           # TypeScript DTOs
    └── src/environments/       # Environment Configuration
```

## 🛠️ Tech Stack

### Backend (.NET API)

- **.NET 10.0** - Web API Framework
- **Entity Framework Core 10.0** - ORM with SQL Server
- **SQL Server Vector Search** - Vector embeddings storage
- **Microsoft.Extensions.AI** - AI service abstractions
- **Swagger/OpenAPI** - API Documentation

### Frontend (Angular UI)

- **Angular 21** - Frontend Framework
- **TypeScript 5.9** - Programming Language
- **Bootstrap 5.3** - CSS Framework
- **RxJS** - Reactive Programming
- **Highlight.js** - Code Syntax Highlighting
- **Markdown-it** - Markdown Rendering

### AI Service Integrations

- **Ollama** - Local AI models
- **OpenAI** - GPT models
- **Azure AI Foundry** - Azure OpenAI Service
- **Anthropic** - Claude models

## 📋 Prerequisites

### Required Software

- **.NET 10.0 SDK** - [Download here](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Node.js 18+** - [Download here](https://nodejs.org/)
- **SQL Server** - Express, Developer, or Full edition
- **Angular CLI 21+** - Install via `npm install -g @angular/cli`

### Optional (for local AI)

- **Ollama** - [Install here](https://ollama.ai/) for local AI models

### AI Service API Keys (at least one required)

- **OpenAI API Key** - For GPT models
- **Azure AI Foundry** - Endpoint URL and API Key
- **Anthropic API Key** - For Claude models

## 🚀 Quick Start

> **New to the project?** Check out our [Quick Start Guide](QUICK_START.md) for the fastest way to get running!

### 1. Clone the Repository

```bash
git clone https://github.com/RorroRojas3/ai-chat.git
cd ai-chat
```

### 2. Setup the Database

```bash
# Create database (replace connection string as needed)
# Default: Server=localhost;Database=aichat;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;
```

### 3. Configure API Environment Variables

Create user secrets for the API project:

```bash
cd enterprise-gpt-api/Enterprise.Gpt.Api
dotnet user-secrets init
```

Add your AI service configurations:

```bash
# For Azure AI Foundry
dotnet user-secrets set "AzureAIFoundry:Url" "https://your-endpoint.openai.azure.com/"
dotnet user-secrets set "AzureAIFoundry:ApiKey" "your-azure-api-key"
dotnet user-secrets set "AzureAIFoundry:DefaultModel" "your-chat-deployment-name"
dotnet user-secrets set "AzureAIFoundry:EmbeddingModel" "text-embedding-ada-002"

# For Amazon Bedrock (optional; leave AmazonBedrock:Enabled false to skip it entirely)
dotnet user-secrets set "AmazonBedrock:Enabled" "true"
dotnet user-secrets set "AmazonBedrock:Region" "us-east-1"
dotnet user-secrets set "AmazonBedrock:ApiKey" "your-bedrock-api-key"
dotnet user-secrets set "AmazonBedrock:DefaultModelId" "us.anthropic.claude-sonnet-4-5-20250929-v1:0"

# For Anthropic, first-party (optional; leave Anthropic:Enabled false to skip it entirely)
dotnet user-secrets set "Anthropic:Enabled" "true"
dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-your-api-key"
dotnet user-secrets set "Anthropic:DefaultModelId" "claude-opus-5"
```

Model ids differ between the two Claude providers: Anthropic takes a **bare** id (`claude-opus-5`),
Bedrock takes the `anthropic.`-prefixed inference-profile id. Startup validation rejects the prefixed
form in the `Anthropic` section.

Enabling Bedrock also needs two database changes that are not applied automatically and fail
silently if skipped — see [docs/models/amazon-bedrock.md](docs/models/amazon-bedrock.md). Enabling
Anthropic needs one: its `Core.Ref.Provider` row must be inserted by hand — see
[docs/models/anthropic.md](docs/models/anthropic.md).

### 4. Run the API

```bash
cd enterprise-gpt-api
dotnet restore
dotnet build
dotnet run --project Enterprise.Gpt.Api
```

The API will start at `https://localhost:7045` (HTTPS) and `http://localhost:5045` (HTTP).

### 5. Run the Frontend

```bash
cd enterprise-ui
npm install
npm start
```

The frontend will start at `http://localhost:4200`.

### 6. (Optional) Enable Angular MCP Tools in VS Code

Use the Angular CLI MCP server to supercharge AI-assisted Angular workflows.

Create `.vscode/mcp.json` in the repo root with one of the following configurations:

```jsonc
{
  "servers": {
    "angular-cli": {
      "command": "npx",
      "args": ["-y", "@angular/cli", "mcp"]
    }
  }
}
```

### 6. Access the Application

- **Frontend**: http://localhost:4200
- **API Documentation**: https://localhost:7045/swagger

## 🔧 Configuration

### API Configuration (`appsettings.json`)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "CorsOrigins": ["http://localhost:4200"],
  "OllamaUrl": "http://localhost:11434/",
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=aichat;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;"
  }
}
```

### Frontend Configuration (`src/environments/environment.ts`)

```typescript
export const environment = {
  production: false,
  apiUrl: "https://localhost:7045/api/",
};
```

### Environment Variables Reference

| Variable                              | Description                                                   | Required | Default                |
| ------------------------------------- | ------------------------------------------------------------- | -------- | ---------------------- |
| `AzureAIFoundry:Url`                  | Azure OpenAI endpoint URL                                     | Yes      | -                      |
| `AzureAIFoundry:ApiKey`               | Azure OpenAI API key                                          | Yes      | -                      |
| `AzureAIFoundry:DefaultModel`         | Chat deployment used when a request names none                | Yes      | -                      |
| `AzureAIFoundry:EmbeddingModel`       | Embedding model name                                          | Yes      | -                      |
| `AmazonBedrock:Enabled`               | Registers the Bedrock chat client                             | No       | false                  |
| `AmazonBedrock:Region`                | AWS region system name, e.g. `us-east-1`                      | If enabled | -                    |
| `AmazonBedrock:ApiKey`                | Bedrock API key, sent as an HTTP bearer token                 | If enabled | -                    |
| `AmazonBedrock:DefaultModelId`        | Bedrock model or inference profile id used when none is named | If enabled | -                    |
| `Anthropic:Enabled`                   | Registers the Anthropic chat client                           | No       | false                  |
| `Anthropic:ApiKey`                    | Anthropic API key (secrets store only)                        | If enabled | -                    |
| `Anthropic:DefaultModelId`            | Bare Anthropic model id used when none is named               | If enabled | `claude-opus-5`      |
| `Anthropic:DefaultMaxOutputTokens`    | Output token ceiling applied to every Anthropic turn          | No       | 32768                  |
| `Anthropic:Thinking`                  | `adaptive` or `disabled`                                      | No       | `adaptive`             |
| `Anthropic:Effort`                    | `low`, `medium`, `high`, `xhigh`, or `max`                    | No       | `high`                 |
| `Anthropic:Timeout`                   | Per-request timeout                                           | No       | 10 minutes             |
| `Anthropic:MaxRetries`                | SDK retries per failed request                                | No       | 2                      |
| `ConnectionStrings:DefaultConnection` | SQL Server connection string                                  | Yes      | See above              |

Azure AI Foundry is required — it also backs document-embedding generation. Amazon Bedrock is optional
and off by default; when `AmazonBedrock:Enabled` is `true` the remaining Bedrock settings are validated
at startup and the app refuses to boot if any is missing or the region is unknown. Anthropic is optional
and off by default too; when `Anthropic:Enabled` is `true` every other `Anthropic:*` setting is validated
at startup, including the rule that `disabled` thinking is not accepted above `high` effort.

`Anthropic:DefaultMaxOutputTokens` is the ceiling on **every** Anthropic turn, not a fallback — the
catalog model's `maxOutputTokens` is never sent — and with thinking on it covers reasoning and answer
text together. Size it generously; see [docs/models/anthropic.md](docs/models/anthropic.md).

## 🧪 Database Setup

The application uses Entity Framework migrations. To set up the database:

1. **Update Connection String**: Modify the connection string in `appsettings.json` or set via user secrets:

   ```bash
   dotnet user-secrets set "ConnectionStrings:DefaultConnection" "your-connection-string"
   ```

2. **Run Migrations** (when available):
   ```bash
   cd enterprise-gpt-api
   dotnet ef database update --project Enterprise.Gpt.Api
   ```

## 📚 API Endpoints

### Chat Endpoints

- `POST /api/chats/sessions/{sessionId}/stream` - Stream chat responses
- `POST /api/chats/sessions/{sessionId}/completion` - Get chat completion

### Session Management

- `GET /api/sessions` - Get all sessions
- `POST /api/sessions` - Create new session
- `GET /api/sessions/{id}` - Get session by ID
- `DELETE /api/sessions/{id}` - Delete session

### Document Management

- `POST /api/documents` - Upload document
- `GET /api/documents` - Get all documents
- `POST /api/documents/search` - Search documents

### Models

- `GET /api/models` - Get available AI models

## 📝 Examples

### Example 1: Starting a Chat Session

**Backend (C# API Call)**:

```csharp
// Create a new chat session
var session = new SessionDto
{
    Name = "My AI Conversation",
    CreatedAt = DateTime.UtcNow
};

// POST to /api/sessions
var response = await httpClient.PostAsJsonAsync("api/sessions", session);
var createdSession = await response.Content.ReadFromJsonAsync<SessionDto>();
```

**Frontend (TypeScript/Angular)**:

```typescript
// Using the SessionService
this.sessionService.createSession("My AI Conversation").subscribe((session) => {
  console.log("Session created:", session.id);
  this.currentSessionId = session.id;
});
```

### Example 2: Sending a Chat Message and Streaming Response

**Backend (C# Controller)**:

```csharp
[HttpPost("sessions/{sessionId}/stream")]
public async IAsyncEnumerable<string> StreamChatCompletion(
    Guid sessionId,
    [FromBody] ChatCompletionDto request,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    await foreach (var chunk in _chatService.StreamCompletionAsync(
        sessionId,
        request,
        cancellationToken))
    {
        yield return chunk;
    }
}
```

**Frontend (TypeScript/Angular with SSE)**:

```typescript
// Stream chat response
sendMessage(sessionId: string, message: string, model: string) {
  const request = {
    prompt: message,
    model: model,
    systemPrompt: 'You are a helpful assistant.'
  };

  this.chatService.streamCompletion(sessionId, request).subscribe({
    next: (chunk) => {
      // Append chunk to message display
      this.currentMessage += chunk;
    },
    complete: () => {
      console.log('Streaming completed');
    }
  });
}
```

### Example 3: Document Upload and Vector Search

**Upload a Document**:

```csharp
// C# Example
var formData = new MultipartFormDataContent();
formData.Add(new StreamContent(fileStream), "file", fileName);

var response = await httpClient.PostAsync("api/documents", formData);
var document = await response.Content.ReadFromJsonAsync<DocumentDto>();
```

**Search Documents**:

```csharp
// C# Example - Vector search with AI embeddings
var searchRequest = new DocumentSearchDto
{
    Query = "What are the system requirements?",
    TopK = 5
};

var response = await httpClient.PostAsJsonAsync("api/documents/search", searchRequest);
var results = await response.Content.ReadFromJsonAsync<List<DocumentDto>>();
```

### Example 4: Using Different AI Providers

**OpenAI (GPT-4)**:

```typescript
const request = {
  prompt: "Explain quantum computing",
  model: "gpt-4",
  systemPrompt: "You are a physics expert.",
};

this.chatService.getCompletion(sessionId, request).subscribe((response) => {
  console.log(response.content);
});
```

**Ollama (Local Model)**:

```typescript
const request = {
  prompt: "Write a haiku about coding",
  model: "llama3.2:latest",
  systemPrompt: "You are a creative poet.",
};

this.chatService.getCompletion(sessionId, request).subscribe((response) => {
  console.log(response.content);
});
```

**Anthropic (Claude)**:

```typescript
const request = {
  prompt: "Help me debug this code",
  model: "claude-opus-5",
  systemPrompt: "You are an expert programmer.",
};

this.chatService.getCompletion(sessionId, request).subscribe((response) => {
  console.log(response.content);
});
```

### Example 5: Session Management

**List All Sessions**:

```typescript
// Get all chat sessions
this.sessionService.getSessions().subscribe((sessions) => {
  sessions.forEach((session) => {
    console.log(`${session.name} - Created: ${session.createdAt}`);
  });
});
```

**Delete a Session**:

```typescript
// Delete a specific session
this.sessionService.deleteSession(sessionId).subscribe(() => {
  console.log("Session deleted successfully");
});
```

### Example 6: Configuration with User Secrets

**Setting up OpenAI**:

```bash
cd enterprise-gpt-api/Enterprise.Gpt.Api
dotnet user-secrets set "OpenAI:ApiKey" "sk-proj-xxxxxxxxxxxxx"
```

**Setting up Azure AI Foundry**:

```bash
dotnet user-secrets set "AzureAIFoundry:Url" "https://my-resource.openai.azure.com/"
dotnet user-secrets set "AzureAIFoundry:ApiKey" "your-azure-key"
dotnet user-secrets set "AzureAIFoundry:EmbeddingModel" "text-embedding-ada-002"
```

**Setting up Anthropic**:

```bash
dotnet user-secrets set "Anthropic:Enabled" "true"
dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-xxxxxxxxxxxxx"
dotnet user-secrets set "Anthropic:DefaultModelId" "claude-opus-5"
```

The key alone does nothing — `Anthropic:Enabled` is what registers the chat client.

## 🔧 Development

> **Contributing?** See our [Development Guide](DEVELOPMENT.md) for detailed development tips and best practices.

### Running Tests

**Backend Tests**:

```bash
cd enterprise-gpt-api
dotnet test
```

**Frontend Tests**:

```bash
cd enterprise-ui
npm test
```

### Building for Production

**Backend**:

```bash
cd enterprise-gpt-api
dotnet publish -c Release -o ./publish
```

**Frontend**:

```bash
cd enterprise-ui
npm run build
```

## 🧪 Testing

### Test Framework

- **Backend**: The project uses .NET testing frameworks. Test projects can be added following the pattern `[ProjectName].Tests`
- **Frontend**: Angular uses Jasmine and Karma for unit testing

### Running Backend Tests

```bash
cd enterprise-gpt-api
dotnet test --verbosity normal
```

### Running Frontend Tests

```bash
cd enterprise-ui
npm test
```

For continuous test watching during development:

```bash
npm test -- --watch
```

### Code Coverage

To generate code coverage reports:

**Backend (using dotnet-coverage)**:

```bash
dotnet tool install -g dotnet-coverage
cd enterprise-gpt-api
dotnet-coverage collect -f cobertura -o coverage.cobertura.xml dotnet test
```

**Frontend**:

```bash
cd enterprise-ui
npm test -- --code-coverage
```

Coverage reports will be generated in the `coverage/` directory.

## 🐛 Troubleshooting

### Common Issues

#### 1. .NET SDK Version Error

**Error**: `The current .NET SDK does not support targeting .NET 10.0`

**Solution**: Install .NET 10.0 SDK from [Microsoft's download page](https://dotnet.microsoft.com/download/dotnet/10.0).

#### 2. Database Connection Issues

**Error**: Cannot connect to SQL Server

**Solutions**:

- Ensure SQL Server is running
- Verify connection string in `appsettings.json`
- Check if Windows Authentication is enabled (for Integrated Security)
- For Docker SQL Server, ensure proper port mapping

#### 3. CORS Errors

**Error**: CORS policy blocking requests from frontend

**Solutions**:

- Verify `CorsOrigins` in `appsettings.json` includes your frontend URL
- Ensure the API is running on the expected port
- Check if HTTPS redirects are causing issues

#### 4. AI Service Errors

**Error**: API key authentication failed

**Solutions**:

- Verify API keys are correctly set in user secrets
- Check if the AI service endpoint URLs are correct
- Ensure at least one AI service is properly configured

#### 5. Vector Search Issues

**Error**: Vector search operations failing

**Solutions**:

- Ensure SQL Server supports Vector Search (SQL Server 2022+)
- Verify EFCore.SqlServer.VectorSearch package is installed
- Check if embedding model is properly configured

#### 6. Node.js/Angular Issues

**Error**: Node.js version compatibility

**Solutions**:

- Use Node.js 18+ (recommended: LTS version)
- Clear npm cache: `npm cache clean --force`
- Delete `node_modules` and run `npm install` again

#### 8. Angular MCP CLI Error

**Error**: `Error: Unknown arguments: read-only, mcp`

**Cause**: An older Angular CLI (e.g., v19) is being resolved by `npx`.

**Solutions**:

- Configure VS Code MCP to use the workspace-local CLI binary (Windows example):

  - File: `.vscode/mcp.json`
  - Snippet:

    ```jsonc
    {
      "servers": {
        "angular-cli": {
          "type": "stdio",
          "command": "enterprise-ui/node_modules/.bin/ng.cmd",
          "args": ["mcp", "--read-only"]
        }
      }
    }
    ```

- Or pin CLI v21 when using `npx`:

  ```bash
  npx -y @angular/cli@21 mcp --read-only
  ```

Docs: https://angular.dev/ai/mcp

#### 7. Port Conflicts

**Error**: Port already in use

**Solutions**:

- API: Modify `launchSettings.json` to use different ports
- Frontend: Use `ng serve --port 4201` to specify different port

### Logs and Debugging

- **API Logs**: Check console output when running `dotnet run`
- **Frontend Logs**: Open browser developer tools (F12)
- **Database**: Use SQL Server Management Studio or Azure Data Studio

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Code Style Guidelines

- **Backend (.NET)**: Follow standard C# conventions and SOLID principles
- **Frontend (Angular)**: Follow Angular style guide and use TypeScript strict mode
- Ensure all tests pass before submitting PR
- Add tests for new features
- Update documentation as needed

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

**Copyright (c) 2025 Rodrigo Ignacio Rojas Garcia**

## 👥 Authors

**Rodrigo Ignacio Rojas Garcia** - _Creator and Maintainer_

- GitHub: [@RorroRojas3](https://github.com/RorroRojas3)

## 🙏 Acknowledgments

### AI Service Providers

- [OpenAI](https://openai.com/) - GPT models and embeddings
- [Anthropic](https://www.anthropic.com/) - Claude AI models
- [Microsoft Azure AI](https://azure.microsoft.com/en-us/products/ai-services) - Azure OpenAI Service
- [Ollama](https://ollama.ai/) - Local AI model runtime

### Key Technologies & Libraries

**Backend (.NET)**

- [ASP.NET Core](https://dotnet.microsoft.com/apps/aspnet) - Web framework
- [Entity Framework Core](https://docs.microsoft.com/ef/core/) - ORM and database access
- [Microsoft.Extensions.AI](https://devblogs.microsoft.com/dotnet/introducing-microsoft-extensions-ai-preview/) - AI service abstractions
- [OllamaSharp](https://github.com/awaescher/OllamaSharp) - Ollama .NET client
- [Anthropic](https://github.com/anthropics/anthropic-sdk-csharp) - Official Anthropic .NET SDK
- [Swashbuckle](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) - OpenAPI/Swagger documentation

**Frontend (Angular)**

- [Angular](https://angular.dev/) - Frontend framework
- [Bootstrap](https://getbootstrap.com/) - UI component library
- [Bootstrap Icons](https://icons.getbootstrap.com/) - Icon library
- [highlight.js](https://highlightjs.org/) - Syntax highlighting
- [markdown-it](https://github.com/markdown-it/markdown-it) - Markdown parser and renderer
- [RxJS](https://rxjs.dev/) - Reactive programming library
- [MSAL Angular](https://github.com/AzureAD/microsoft-authentication-library-for-js) - Microsoft Authentication Library

**Database & Search**

- [SQL Server](https://www.microsoft.com/sql-server) - Database engine
- [EFCore.SqlServer.VectorSearch](https://github.com/Giorgi/EFCore.SqlServer.VectorSearch) - Vector search capabilities

**Development Tools**

- [Visual Studio Code](https://code.visualstudio.com/) - Code editor
- [.NET SDK](https://dotnet.microsoft.com/download) - Development framework
- [Node.js](https://nodejs.org/) - JavaScript runtime
- [Angular CLI](https://angular.dev/tools/cli) - Angular development tools

### Inspiration

This project combines modern AI capabilities with traditional web development practices to create a flexible, multi-provider chat interface suitable for various AI use cases.

## 🆘 Support

If you encounter any issues or have questions:

1. Check the [Troubleshooting](#-troubleshooting) section
2. Review the [API documentation](https://localhost:7045/swagger) when the API is running
3. Create an issue in the GitHub repository

## 🔗 Useful Links

- [.NET 10 Documentation](https://learn.microsoft.com/dotnet/)
- [Angular Documentation](https://angular.dev/)
- [Entity Framework Core](https://docs.microsoft.com/ef/core/)
- [OpenAI API Reference](https://platform.openai.com/docs/)
- [Azure OpenAI Service](https://azure.microsoft.com/services/cognitive-services/openai-service/)
- [Anthropic API](https://docs.anthropic.com/)
- [Ollama Documentation](https://ollama.ai/docs)
