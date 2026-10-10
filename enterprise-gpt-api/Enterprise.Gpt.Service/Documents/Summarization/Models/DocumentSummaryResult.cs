using System.Diagnostics;
using System.Text;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity.Base;
using Enterprise.Gpt.Entity.Conversations;
using Enterprise.Gpt.Entity.Documents;
using Enterprise.Gpt.Entity.Projects;
using Enterprise.Gpt.Entity.Usage;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Observability;
using Enterprise.Gpt.Service.Prompts;
using Enterprise.Gpt.Service.Settings;
using Enterprise.Gpt.Service.Tool;
using FluentValidation.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Documents.Summarization.Models;

/// <summary>
/// One document's summary, and whether producing it cost anything.
/// </summary>
/// <param name="Text">The summary text.</param>
/// <param name="FromCache">
/// <see langword="true"/> when the stored summary was reused, in which case no model call was made
/// and no usage was billed.
/// </param>
/// <param name="ModelCallCount">Model calls this request made: <c>0</c> on a cache hit.</param>
public sealed record DocumentSummaryResult(string Text, bool FromCache, int ModelCallCount);
