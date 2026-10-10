using System.Collections.Frozen;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Conversations;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Settings;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Documents.Models;

/// <summary>An artifact to store, as it came out of the sandbox.</summary>
/// <param name="FileName">The name the sandbox gave it, used verbatim for the download.</param>
/// <param name="Content">The bytes. Already bounded by the caller's own artifact ceiling.</param>
public sealed record GeneratedDocumentRequest(Guid ConversationId, Guid UserId, string FileName, byte[] Content);
