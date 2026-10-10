using System.Text.RegularExpressions;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>A file the File Agent may read, and its bytes.</summary>
/// <param name="Name">The file name as the user knows it, which is also the name in the sandbox.</param>
public sealed record FileAgentSourceFile(Guid Id, string Name, string MimeType, byte[] Content);
