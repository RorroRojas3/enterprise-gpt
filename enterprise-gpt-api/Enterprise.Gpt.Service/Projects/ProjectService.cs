using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Dto.Actions.Projects;
using Enterprise.Gpt.Dto.Documents;
using Enterprise.Gpt.Dto.Pagination;
using Enterprise.Gpt.Dto.Projects;
using Enterprise.Gpt.Entity.Projects;
using Enterprise.Gpt.Repository.Sql.Constants;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Service.Documents;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Security;
using Enterprise.Gpt.Service.Sorting;
using Enterprise.Gpt.Service.Sorting.Models;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Enterprise.Gpt.Service.Projects;

public interface IProjectService
{
    /// <summary>
    /// Searches the caller's active projects by name, newest first unless <paramref name="sort"/> names one
    /// of <see cref="ProjectSortKeyNames.Supported"/>. Paging is clamped (<paramref name="take"/> to 1..100),
    /// but an unknown order is rejected, since a misordered page looks like a correct one.
    /// </summary>
    /// <exception cref="ValidationException"><paramref name="sort"/> or <paramref name="dir"/> is unrecognised.</exception>
    Task<PaginatedResponseDto<ProjectSummaryDto>> SearchProjectsAsync(string? name, int skip = 0, int take = 20, bool? isFavorite = null, string? sort = null, string? dir = null, CancellationToken cancellationToken = default);

    /// <exception cref="NotFoundException">No active project with <paramref name="id"/> belongs to the caller.</exception>
    Task<ProjectDto> GetProjectAsync(Guid id, CancellationToken cancellationToken = default);

    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ValidationException">The caller already has an active project of that name.</exception>
    Task<ProjectDto> CreateProjectAsync(CreateProjectActionDto request, CancellationToken cancellationToken = default);

    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ValidationException">The caller already has another active project of that name.</exception>
    /// <exception cref="NotFoundException">No active project with <paramref name="id"/> belongs to the caller.</exception>
    Task<ProjectDto> UpdateProjectAsync(Guid id, UpdateProjectActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stars or unstars a project, leaving <c>DateModified</c> alone. Separate from
    /// <see cref="UpdateProjectAsync"/>, whose full replacement would clear a flag a rename forgot to echo.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotFoundException">No active project with <paramref name="id"/> belongs to the caller.</exception>
    Task SetProjectFavoriteAsync(Guid id, SetProjectFavoriteActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes a project with its documents and chunks. Its conversations are kept as standalone ones,
    /// losing only the project's documents and instructions.
    /// </summary>
    /// <exception cref="NotFoundException">No active project with <paramref name="id"/> belongs to the caller.</exception>
    Task DeactivateProjectAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lists a project's active documents, most recently uploaded first.</summary>
    /// <exception cref="NotFoundException">No active project with <paramref name="projectId"/> belongs to the caller.</exception>
    Task<List<ProjectDocumentDto>> GetProjectDocumentsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes one project document with its chunks.</summary>
    /// <exception cref="NotFoundException">The project or the document does not exist, is deactivated, or does not belong to the caller.</exception>
    Task DeactivateProjectDocumentAsync(Guid projectId, Guid documentId, CancellationToken cancellationToken = default);
}

public partial class ProjectService(ILogger<ProjectService> logger,
    ITokenService tokenService,
    EnterpriseGptDbContext ctx,
    TimeProvider timeProvider) : IProjectService
{
    // The wire-visible key in the problem details errors; identical on the create and update DTOs.
    private const string NameProperty = nameof(CreateProjectActionDto.Name);

    private readonly ILogger<ProjectService> _logger = logger;
    private readonly ITokenService _tokenService = tokenService;
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    public async Task<PaginatedResponseDto<ProjectSummaryDto>> SearchProjectsAsync(string? name, int skip = 0, int take = 20, bool? isFavorite = null, string? sort = null, string? dir = null, CancellationToken cancellationToken = default)
    {
        // Resolved first, so a misspelled sort field fails before any query runs.
        SortSelection<ProjectSortKeys>? order = SortParameters.IsDefaultOrder(sort, dir)
            ? null
            : ProjectSortKeyNames.Resolve(sort, dir);

        // Clamped, not validated: take = 0 would divide by zero in CurrentPage below.
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, 100);

        var userId = _tokenService.GetOid();

        var query = _ctx.Projects
            .AsNoTracking()
            .Where(x => x.UserId == userId);

        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(x => EF.Functions.Like(x.Name, $"%{name}%"));
        }

        // Before the count, so TotalCount describes the filtered set rather than the whole listing.
        if (isFavorite.HasValue)
        {
            query = query.Where(x => x.IsFavorite == isFavorite.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await OrderProjects(query, order)
            .Skip(skip)
            .Take(take)
            .Select(ProjectMapper.MapToProjectSummaryDtoExpression)
            .ToListAsync(cancellationToken);

        return new PaginatedResponseDto<ProjectSummaryDto>
        {
            Items = items,
            TotalCount = totalCount,
            PageSize = take,
            CurrentPage = (skip / take) + 1
        };
    }

    /// <inheritdoc />
    public async Task<ProjectDto> GetProjectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = _tokenService.GetOid();

        return await _ctx.Projects
            .AsNoTracking()
            .Where(x => x.Id == id && x.UserId == userId)
            .Select(ProjectMapper.MapToProjectDtoExpression)
            .FirstOrDefaultAsync(cancellationToken) ?? throw LogAndCreateNotFound(id, userId);
    }

    /// <inheritdoc />
    public async Task<ProjectDto> CreateProjectAsync(CreateProjectActionDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = _tokenService.GetOid();
        await EnsureNameIsAvailableAsync(userId, request.Name, excludingProjectId: null, cancellationToken);

        var project = request.FromCreateProjectActionDtoToProject();
        project.Id = Guid.NewGuid();
        project.UserId = userId;

        _ctx.Add(project);
        await SaveTranslatingDuplicateNameAsync(request.Name, cancellationToken);

        LogProjectCreated(_logger, project.Id, userId);

        return project.MapToProjectDto();
    }

    /// <inheritdoc />
    public async Task<ProjectDto> UpdateProjectAsync(Guid id, UpdateProjectActionDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = _tokenService.GetOid();

        var project = await _ctx.Projects
            .Where(x => x.Id == id && x.UserId == userId)
            .FirstOrDefaultAsync(cancellationToken) ?? throw LogAndCreateNotFound(id, userId);

        await EnsureNameIsAvailableAsync(userId, request.Name, excludingProjectId: id, cancellationToken);

        request.FromUpdateProjectActionDtoToProject(project);

        await SaveTranslatingDuplicateNameAsync(request.Name, cancellationToken);

        LogProjectUpdated(_logger, id, userId);

        return project.MapToProjectDto();
    }

    /// <inheritdoc />
    public async Task SetProjectFavoriteAsync(Guid id, SetProjectFavoriteActionDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = _tokenService.GetOid();

        // Set-based rather than load-and-save so DateModified stays put: starring does not modify the
        // project, and bumping it would make every client's copy look stale.
        var rows = await _ctx.Projects
            .Where(x => x.Id == id && x.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsFavorite, request.IsFavorite),
                cancellationToken);

        if (rows == 0)
        {
            throw LogAndCreateNotFound(id, userId);
        }
    }

    /// <inheritdoc />
    public async Task DeactivateProjectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = _tokenService.GetOid();

        var exists = await _ctx.Projects
            .AsNoTracking()
            .AnyAsync(x => x.Id == id && x.UserId == userId, cancellationToken);

        if (!exists)
        {
            throw LogAndCreateNotFound(id, userId);
        }

        var date = _timeProvider.GetUtcNow();

        // Separate statements need a transaction, or a partial failure leaves conversations on a deleted
        // project or live chunks under a deleted document.
        await using var transaction = await _ctx.Database.BeginTransactionAsync(cancellationToken);

        // Leaf first, so a summary never outlives what it summarizes.
        await _ctx.ProjectDocumentSummaries
            .Where(s => s.ProjectDocument.ProjectId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        // Leaf first for the same reason: a live row under a dead sheet is still-readable document data.
        await _ctx.ProjectDocumentSheetRows
            .Where(r => r.ProjectDocumentSheet.ProjectDocument.ProjectId == id)
            .ExecuteUpdateAsync(r => r
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ProjectDocumentSheetColumns
            .Where(c => c.ProjectDocumentSheet.ProjectDocument.ProjectId == id)
            .ExecuteUpdateAsync(c => c
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ProjectDocumentSheets
            .Where(s => s.ProjectDocument.ProjectId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ProjectDocumentChunks
            .Where(c => c.ProjectDocument.ProjectId == id)
            .ExecuteUpdateAsync(c => c
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ProjectDocuments
            .Where(d => d.ProjectId == id)
            .ExecuteUpdateAsync(d => d
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        // Released, not deleted: the conversations become standalone.
        await ReleaseConversationsIncludingDeletedAsync(id, date, cancellationToken);

        await _ctx.Projects
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(p => p
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        LogProjectDeactivated(_logger, id, userId);
    }

    /// <inheritdoc />
    public async Task<List<ProjectDocumentDto>> GetProjectDocumentsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var userId = _tokenService.GetOid();
        await EnsureProjectExistsAsync(projectId, userId, cancellationToken);

        return await _ctx.ProjectDocuments
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.DateCreated)
            .Select(ProjectDocumentMapper.MapToProjectDocumentDtoExpression)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeactivateProjectDocumentAsync(Guid projectId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var userId = _tokenService.GetOid();
        await EnsureProjectExistsAsync(projectId, userId, cancellationToken);

        var exists = await _ctx.ProjectDocuments
            .AsNoTracking()
            .AnyAsync(x => x.Id == documentId && x.ProjectId == projectId, cancellationToken);

        if (!exists)
        {
            LogDocumentNotFoundInProject(_logger, documentId, projectId, userId);
            throw new NotFoundException($"Document with id '{documentId}' was not found.");
        }

        var date = _timeProvider.GetUtcNow();

        await using var transaction = await _ctx.Database.BeginTransactionAsync(cancellationToken);

        await _ctx.ProjectDocumentSummaries
            .Where(s => s.ProjectDocumentId == documentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        await _ctx.ProjectDocumentSheetRows
            .Where(r => r.ProjectDocumentSheet.ProjectDocumentId == documentId)
            .ExecuteUpdateAsync(r => r
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ProjectDocumentSheetColumns
            .Where(c => c.ProjectDocumentSheet.ProjectDocumentId == documentId)
            .ExecuteUpdateAsync(c => c
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ProjectDocumentSheets
            .Where(s => s.ProjectDocumentId == documentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ProjectDocumentChunks
            .Where(c => c.ProjectDocumentId == documentId)
            .ExecuteUpdateAsync(c => c
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ProjectDocuments
            .Where(d => d.Id == documentId)
            .ExecuteUpdateAsync(d => d
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // The blob deliberately stays: the row is only soft-deleted, and reclaiming blobs belongs to a
        // retention job that also handles those orphaned by failed ingestions.
        LogProjectDocumentDeactivated(_logger, documentId, userId);
    }

    #region Private methods

    // Name order is the database collation's (case-insensitive on SQL Server, ordinal on the SQLite tests),
    // so a client re-sorting a page with localeCompare will not agree with it.
    private static IOrderedQueryable<Project> OrderProjects(
        IQueryable<Project> query,
        SortSelection<ProjectSortKeys>? order)
    {
        if (order is not { } requested)
        {
            // The order existing clients are promised. Id breaks DateCreated ties so same-tick rows are not
            // duplicated or skipped across a page boundary.
            return query
                .OrderByDescending(x => x.DateCreated)
                .ThenByDescending(x => x.Id);
        }

        var ordered = requested.Key switch
        {
            ProjectSortKeys.Updated => requested.IsAscending
                ? query.OrderBy(x => x.DateModified)
                : query.OrderByDescending(x => x.DateModified),
            ProjectSortKeys.Name => requested.IsAscending
                ? query.OrderBy(x => x.Name)
                : query.OrderByDescending(x => x.Name),
            // Name orders each tied group, ascending in both directions so names always read A-Z.
            ProjectSortKeys.Favorite => requested.IsAscending
                ? query.OrderBy(x => x.IsFavorite).ThenBy(x => x.Name)
                : query.OrderByDescending(x => x.IsFavorite).ThenBy(x => x.Name),
            _ => requested.IsAscending
                ? query.OrderBy(x => x.DateCreated)
                : query.OrderByDescending(x => x.DateCreated)
        };

        // Id tie-break for stable page boundaries. It follows the requested direction, or an all-tied
        // listing would return the same page both ways and the control would look inert.
        return requested.IsAscending
            ? ordered.ThenBy(x => x.Id)
            : ordered.ThenByDescending(x => x.Id);
    }

    private async Task EnsureProjectExistsAsync(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var exists = await _ctx.Projects
            .AsNoTracking()
            .AnyAsync(x => x.Id == projectId && x.UserId == userId, cancellationToken);

        if (!exists)
        {
            throw LogAndCreateNotFound(projectId, userId);
        }
    }

    // The filtered unique index is the real guard; this gives a usable 400. A racing loser hits the index,
    // which SaveTranslatingDuplicateNameAsync turns into the same 400.
    private async Task EnsureNameIsAvailableAsync(Guid userId, string name, Guid? excludingProjectId, CancellationToken cancellationToken)
    {
        var taken = await _ctx.Projects
            .AsNoTracking()
            .AnyAsync(x => x.UserId == userId
                && x.Name == name
                && (!excludingProjectId.HasValue || x.Id != excludingProjectId.Value), cancellationToken);

        if (taken)
        {
            throw CreateDuplicateNameException(name);
        }
    }

    // Reports a lost name race like the up-front check. Duplicate keys only: timeouts, deadlocks and FK
    // failures are real faults, not naming problems.
    private async Task SaveTranslatingDuplicateNameAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await _ctx.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConflictException("This project was changed by another request. Reload it and try again.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Detached so no later save can retry the rejected insert.
            // Fully qualified: Enterprise.Gpt.Dto.Actions.Project is a namespace in scope here.
            foreach (var entry in _ctx.ChangeTracker.Entries<Enterprise.Gpt.Entity.Projects.Project>().ToList())
            {
                entry.State = EntityState.Detached;
            }

            LogDuplicateNameRace(_logger, ex);

            throw CreateDuplicateNameException(name);
        }
    }

    // Shared by the pre-check and the index path so a client cannot tell the two apart.
    private static ValidationException CreateDuplicateNameException(string name)
    {
        return new ValidationException(
        [
            new ValidationFailure(NameProperty, $"A project named '{name}' already exists.")
        ]);
    }

    // Deactivated conversations included, so a later restore cannot resurrect a link to a deleted project.
    private Task<int> ReleaseConversationsIncludingDeletedAsync(Guid projectId, DateTimeOffset date, CancellationToken cancellationToken) =>
        _ctx.Conversations
            .IgnoreQueryFilters([QueryFilterNames.SoftDelete])
            .Where(s => s.ProjectId == projectId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ProjectId, (Guid?)null)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

    private NotFoundException LogAndCreateNotFound(Guid projectId, Guid userId)
    {
        // Deliberately indistinguishable from "does not exist" so other users' project ids cannot be probed.
        LogProjectNotAccessible(_logger, projectId, userId);

        return new NotFoundException($"Project with id '{projectId}' was not found.");
    }

    #endregion

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} created by user {UserId}")]
    private static partial void LogProjectCreated(ILogger logger, Guid projectId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} updated by user {UserId}")]
    private static partial void LogProjectUpdated(ILogger logger, Guid projectId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project {ProjectId} deactivated by user {UserId}")]
    private static partial void LogProjectDeactivated(ILogger logger, Guid projectId, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} not found in project {ProjectId} for user {UserId}")]
    private static partial void LogDocumentNotFoundInProject(
        ILogger logger, Guid documentId, Guid projectId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Project document {DocumentId} deactivated by user {UserId}")]
    private static partial void LogProjectDocumentDeactivated(ILogger logger, Guid documentId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "A concurrent request already claimed a project name for this user")]
    private static partial void LogDuplicateNameRace(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Project {ProjectId} not found, deactivated, or not owned by user {UserId}")]
    private static partial void LogProjectNotAccessible(ILogger logger, Guid projectId, Guid userId);

    #endregion
}
