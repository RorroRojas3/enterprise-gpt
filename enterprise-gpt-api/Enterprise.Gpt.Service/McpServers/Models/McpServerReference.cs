using System.Net.Sockets;
using System.Net;
using Andes.Extensions.AI;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Common.Validation;
using Enterprise.Gpt.Entity.McpServers;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Caching;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Security;
using Enterprise.Gpt.Service.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using ModelContextProtocol.Client;
using ModelContextProtocol;

namespace Enterprise.Gpt.Service.McpServers.Models;

/// <summary>
/// Identifies an MCP server whose tools were leased for a request, captured as the server
/// stood at that moment.
/// </summary>
/// <param name="Id">The server's unique identifier.</param>
/// <param name="Name">The server's name at lease time.</param>
public readonly record struct McpServerReference(Guid Id, string Name);
