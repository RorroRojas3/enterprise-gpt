using FluentValidation;
using FluentValidation.Results;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Project;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Mappers;
using Enterprise.Gpt.Service.Sorting;

namespace Enterprise.Gpt.Service;

/// <summary>
/// Reads and writes the caller's projects: the containers that group conversations and own a shared
/// document set and standing instructions.
/// </summary>
public interface IProjectService
{
    /// <summary>
    /// Searches the caller's active projects by name, most recently created first unless an order is
    /// requested.
    /// </summary>
    /// <remarks>
    /// Returns summaries rather than full projects: a listing never renders the instructions, and
    /// a page of them would ship far more text than the caller asked for. Paging arguments are
    /// clamped rather than rejected — <paramref name="skip"/> to zero or above and
    /// <paramref name="take"/> to 1..100. The ordering arguments are the opposite: an unrecognised
    /// value is rejected, because a page in the wrong order looks exactly like a page in the right one.
    /// </remarks>
    /// <param name="name">An optional case-insensitive substring to filter on.</param>
    /// <param name="skip">The number of projects to skip.</param>
    /// <param name="take">The maximum number of projects to return.</param>
    /// <param name="isFavorite">
    /// Optionally narrows the page to the caller's favourites, or to everything but them. Omitting
    /// it applies no filter at all — it is a <see cref="bool"/>?, not a defaulted <see cref="bool"/>.
    /// </param>
    /// <param name="sort">
    /// An optional field to order by — one of <see cref="Sorting.ProjectSortKeys.Supported"/>.
    /// </param>
    /// <param name="dir">An optional direction, <c>asc</c> or <c>desc</c>.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>A page of the caller's projects.</returns>
    /// <exception cref="ValidationException">
    /// <paramref name="sort"/> or <paramref name="dir"/> names something this listing does not accept.
    /// </exception>
    Task<PaginatedResponseDto<ProjectSummaryDto>> SearchProjectsAsync(string? name, int skip = 0, int take = 20, bool? isFavorite = null, string? sort = null, string? dir = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets one of the caller's active projects by id.
    /// </summary>
    /// <param name="id">The project id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The matching project.</returns>
    /// <exception cref="NotFoundException">No active project with <paramref name="id"/> belongs to the caller.</exception>
    Task<ProjectDto> GetProjectAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a project owned by the caller.
    /// </summary>
    /// <param name="request">The creation request.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The created project.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ValidationException">The request fails validation, or the caller already has an active project of that name.</exception>
    Task<ProjectDto> CreateProjectAsync(CreateProjectActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates one of the caller's active projects.
    /// </summary>
    /// <param name="id">The id of the project to update.</param>
    /// <param name="request">The update request.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The updated project.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ValidationException">The request fails validation, or the caller already has another active project of that name.</exception>
    /// <exception cref="NotFoundException">No active project with <paramref name="id"/> belongs to the caller.</exception>
    Task<ProjectDto> UpdateProjectAsync(Guid id, UpdateProjectActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks one of the caller's projects as a favourite, or clears the mark.
    /// </summary>
    /// <remarks>
    /// Its own operation rather than a field on <see cref="UpdateProjectAsync"/>, which replaces the
    /// whole representation: a rename that forgot to echo the flag back would silently clear it.
    /// <c>DateModified</c> is deliberately left alone — starring a project does not modify it.
    /// </remarks>
    /// <param name="id">The id of the project to mark.</param>
    /// <param name="request">The state being asked for.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotFoundException">No active project with <paramref name="id"/> belongs to the caller.</exception>
    Task SetProjectFavoriteAsync(Guid id, SetProjectFavoriteActionDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes a project together with its documents and their chunks, and releases its
    /// conversations.
    /// </summary>
    /// <remarks>
    /// Conversations are deliberately kept: their <c>ProjectId</c> is cleared so they survive as
    /// standalone conversations, keeping their own documents and transcripts, and simply lose access
    /// to the project's documents and instructions.
    /// </remarks>
    /// <param name="id">The id of the project to deactivate.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <exception cref="NotFoundException">No active project with <paramref name="id"/> belongs to the caller.</exception>
    Task DeactivateProjectAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the active documents of one of the caller's projects, most recently uploaded first.
    /// </summary>
    /// <param name="projectId">The owning project id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>The project's documents.</returns>
    /// <exception cref="NotFoundException">No active project with <paramref name="projectId"/> belongs to the caller.</exception>
    Task<List<ProjectDocumentDto>> GetProjectDocumentsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes one document of one of the caller's projects, together with its chunks.
    /// </summary>
    /// <param name="projectId">The owning project id.</param>
    /// <param name="documentId">The document id.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <exception cref="NotFoundException">The project or the document does not exist, is deactivated, or does not belong to the caller.</exception>
    Task DeactivateProjectDocumentAsync(Guid projectId, Guid documentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the project aggregate: the project row, its document listing, and the soft-delete cascade
/// across documents and chunks.
/// </summary>
/// <remarks>
/// Every read and write is scoped to the caller's object id, and a project belonging to someone else
/// is reported as <see cref="NotFoundException"/> rather than <see cref="ForbiddenException"/> — the
/// same reasoning as the document upload path, so the API cannot be used to probe for project ids.
/// </remarks>
public class ProjectService(ILogger<ProjectService> logger,
    ITokenService tokenService,
    EnterpriseGptDbContext ctx,
    IValidator<CreateProjectActionDto> createValidator,
    IValidator<UpdateProjectActionDto> updateValidator) : IProjectService
{
    /// <summary>
    /// The wire-visible key for a name failure in the problem details <c>errors</c> dictionary.
    /// Identical on the create and update DTOs.
    /// </summary>
    private const string NameProperty = nameof(CreateProjectActionDto.Name);

    private readonly ILogger<ProjectService> _logger = logger;
    private readonly ITokenService _tokenService = tokenService;
    private readonly EnterpriseGptDbContext _ctx = ctx;
    private readonly IValidator<CreateProjectActionDto> _createValidator = createValidator;
    private readonly IValidator<UpdateProjectActionDto> _updateValidator = updateValidator;

    /// <inheritdoc />
    public async Task<PaginatedResponseDto<ProjectSummaryDto>> SearchProjectsAsync(string? name, int skip = 0, int take = 20, bool? isFavorite = null, string? sort = null, string? dir = null, CancellationToken cancellationToken = default)
    {
        // Resolved before any database work, so a misspelled sort field costs a round trip to
        // nothing rather than a page the caller then has to distrust.
        SortSelection<ProjectSortKey>? order = SortParameters.IsDefaultOrder(sort, dir)
            ? null
            : ProjectSortKeys.Resolve(sort, dir);

        // Clamped rather than validated: the paging arguments come straight off the query
        // string, and take = 0 would divide by zero when computing CurrentPage below.
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, 100);

        var userId = _tokenService.GetOid();

        var query = _ctx.Projects
            .AsNoTracking()
            .Where(x => x.UserId == userId && !x.DateDeactivated.HasValue);

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

    /// <remarks>
    /// The string comparison is the database collation's, not the CLR's — case-insensitive on a SQL
    /// Server default and ordinal on the SQLite the unit tests run against. A client must not re-sort
    /// a page with <c>localeCompare</c> and expect to agree with it.
    /// </remarks>
    private static IOrderedQueryable<Project> OrderProjects(
        IQueryable<Project> query,
        SortSelection<ProjectSortKey>? order)
    {
        if (order is not { } requested)
        {
            // Left exactly as it was before this listing accepted an order, because that is what
            // clients written against it are promised. Id breaks ties: DateCreated alone is not a
            // total order, so two projects created in the same tick could straddle a page boundary
            // and be duplicated or skipped.
            return query
                .OrderByDescending(x => x.DateCreated)
                .ThenByDescending(x => x.Id);
        }

        var ordered = requested.Key switch
        {
            ProjectSortKey.Updated => requested.IsAscending
                ? query.OrderBy(x => x.DateModified)
                : query.OrderByDescending(x => x.DateModified),
            ProjectSortKey.Name => requested.IsAscending
                ? query.OrderBy(x => x.Name)
                : query.OrderByDescending(x => x.Name),
            // A two-value flag leaves almost every row tied, so the name carries the order inside
            // each group rather than leaving it to whatever the engine returns. It stays ascending in
            // both directions on purpose: a name reads A-Z whichever end of the list its group is at.
            ProjectSortKey.Favorite => requested.IsAscending
                ? query.OrderBy(x => x.IsFavorite).ThenBy(x => x.Name)
                : query.OrderByDescending(x => x.IsFavorite).ThenBy(x => x.Name),
            _ => requested.IsAscending
                ? query.OrderBy(x => x.DateCreated)
                : query.OrderByDescending(x => x.DateCreated)
        };

        // Every requested order gets the same tie-break, for the reason the default one has it: a
        // client draining pages needs the row at a page boundary to be the same row every time. It
        // follows the requested direction, or a listing whose rows are all tied on the primary key
        // would return the same page for both directions and the control would look inert.
        return requested.IsAscending
            ? ordered.ThenBy(x => x.Id)
            : ordered.ThenByDescending(x => x.Id);
    }

    /// <inheritdoc />
    public async Task<ProjectDto> GetProjectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = _tokenService.GetOid();

        return await _ctx.Projects
            .AsNoTracking()
            .Where(x => x.Id == id && x.UserId == userId && !x.DateDeactivated.HasValue)
            .Select(ProjectMapper.MapToProjectDtoExpression)
            .FirstOrDefaultAsync(cancellationToken) ?? throw LogAndCreateNotFound(id, userId);
    }

    /// <inheritdoc />
    public async Task<ProjectDto> CreateProjectAsync(CreateProjectActionDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var userId = _tokenService.GetOid();
        await EnsureNameIsAvailableAsync(userId, request.Name, excludingProjectId: null, cancellationToken);

        var date = DateTimeOffset.UtcNow;
        var project = request.FromCreateProjectActionDtoToProject();
        project.Id = Guid.NewGuid();
        project.UserId = userId;
        project.DateCreated = date;
        project.DateModified = date;

        _ctx.Add(project);
        await SaveTranslatingDuplicateNameAsync(request.Name, cancellationToken);

        _logger.LogInformation("Project {ProjectId} created by user {UserId}", project.Id, userId);

        return project.MapToProjectDto();
    }

    /// <inheritdoc />
    public async Task<ProjectDto> UpdateProjectAsync(Guid id, UpdateProjectActionDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var userId = _tokenService.GetOid();

        var project = await _ctx.Projects
            .Where(x => x.Id == id && x.UserId == userId && !x.DateDeactivated.HasValue)
            .FirstOrDefaultAsync(cancellationToken) ?? throw LogAndCreateNotFound(id, userId);

        await EnsureNameIsAvailableAsync(userId, request.Name, excludingProjectId: id, cancellationToken);

        request.FromUpdateProjectActionDtoToProject(project);
        project.DateModified = DateTimeOffset.UtcNow;

        await SaveTranslatingDuplicateNameAsync(request.Name, cancellationToken);

        _logger.LogInformation("Project {ProjectId} updated by user {UserId}", id, userId);

        return project.MapToProjectDto();
    }

    /// <inheritdoc />
    public async Task SetProjectFavoriteAsync(Guid id, SetProjectFavoriteActionDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = _tokenService.GetOid();

        // A set-based update rather than the tracked load-and-save UpdateProjectAsync uses, because
        // DateModified must not move: starring a project does not modify it, and bumping the
        // timestamp would put every client's copy out of step at the next fetch over a write that
        // changes nothing a listing renders.
        var rows = await _ctx.Projects
            .Where(x => x.Id == id && x.UserId == userId && !x.DateDeactivated.HasValue)
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
            .AnyAsync(x => x.Id == id && x.UserId == userId && !x.DateDeactivated.HasValue, cancellationToken);

        if (!exists)
        {
            throw LogAndCreateNotFound(id, userId);
        }

        var date = DateTimeOffset.UtcNow;

        // Five separate ExecuteUpdate statements, so they need a transaction to be all-or-nothing:
        // a partial failure would otherwise leave conversations pointing at a deleted project, or
        // chunks alive under a deleted document.
        await using var transaction = await _ctx.Database.BeginTransactionAsync(cancellationToken);

        // Ahead of the chunks and the document itself, so a summary never outlives what it
        // summarizes.
        await _ctx.ProjectDocumentSummaries
            .Where(s => s.ProjectDocument.ProjectId == id && !s.DateDeactivated.HasValue)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        await _ctx.ProjectDocumentChunks
            .Where(c => c.ProjectDocument.ProjectId == id && !c.DateDeactivated.HasValue)
            .ExecuteUpdateAsync(c => c
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        await _ctx.ProjectDocuments
            .Where(d => d.ProjectId == id && !d.DateDeactivated.HasValue)
            .ExecuteUpdateAsync(d => d
                .SetProperty(x => x.DateDeactivated, date),
                cancellationToken);

        // Released, not deleted: the conversations keep their own documents and transcripts and
        // simply become standalone. Deactivated ones are cleared too, so a later restore cannot
        // resurrect a link to a deleted project.
        await _ctx.Conversations
            .Where(s => s.ProjectId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ProjectId, (Guid?)null)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        await _ctx.Projects
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(p => p
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Project {ProjectId} deactivated by user {UserId}", id, userId);
    }

    /// <inheritdoc />
    public async Task<List<ProjectDocumentDto>> GetProjectDocumentsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var userId = _tokenService.GetOid();
        await EnsureProjectExistsAsync(projectId, userId, cancellationToken);

        return await _ctx.ProjectDocuments
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && !x.DateDeactivated.HasValue)
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
            .AnyAsync(x => x.Id == documentId && x.ProjectId == projectId && !x.DateDeactivated.HasValue, cancellationToken);

        if (!exists)
        {
            _logger.LogWarning("Document {DocumentId} not found in project {ProjectId} for user {UserId}", documentId, projectId, userId);
            throw new NotFoundException($"Document with id '{documentId}' was not found.");
        }

        var date = DateTimeOffset.UtcNow;

        await using var transaction = await _ctx.Database.BeginTransactionAsync(cancellationToken);

        await _ctx.ProjectDocumentSummaries
            .Where(s => s.ProjectDocumentId == documentId && !s.DateDeactivated.HasValue)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DateDeactivated, date)
                .SetProperty(x => x.DateModified, date),
                cancellationToken);

        await _ctx.ProjectDocumentChunks
            .Where(c => c.ProjectDocumentId == documentId && !c.DateDeactivated.HasValue)
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

        // The blob is deliberately left in storage: it is the original the user uploaded, the row is
        // only soft-deleted, and reclaiming it belongs to a retention job that can also handle the
        // blobs orphaned by failed ingestions.
        _logger.LogInformation("Project document {DocumentId} deactivated by user {UserId}", documentId, userId);
    }

    /// <summary>
    /// Throws when the project does not exist, is deactivated, or belongs to another user.
    /// </summary>
    private async Task EnsureProjectExistsAsync(Guid projectId, Guid userId, CancellationToken cancellationToken)
    {
        var exists = await _ctx.Projects
            .AsNoTracking()
            .AnyAsync(x => x.Id == projectId && x.UserId == userId && !x.DateDeactivated.HasValue, cancellationToken);

        if (!exists)
        {
            throw LogAndCreateNotFound(projectId, userId);
        }
    }

    /// <summary>
    /// Rejects a name already taken by another of the caller's active projects.
    /// </summary>
    /// <remarks>
    /// The unique filtered index is the real guard; this check exists so the caller gets a 400 with a
    /// usable <c>errors</c> dictionary instead of an opaque 500 from a unique-index violation. Two
    /// requests racing both pass this check and the loser lands on the index, which
    /// <see cref="SaveTranslatingDuplicateNameAsync"/> turns into the same 400.
    /// </remarks>
    private async Task EnsureNameIsAvailableAsync(Guid userId, string name, Guid? excludingProjectId, CancellationToken cancellationToken)
    {
        var taken = await _ctx.Projects
            .AsNoTracking()
            .AnyAsync(x => x.UserId == userId
                && x.Name == name
                && !x.DateDeactivated.HasValue
                && (!excludingProjectId.HasValue || x.Id != excludingProjectId.Value), cancellationToken);

        if (taken)
        {
            throw CreateDuplicateNameException(name);
        }
    }

    /// <summary>
    /// Saves the pending changes, reporting a lost race for a project name the way the up-front
    /// check does rather than as an unhandled database error.
    /// </summary>
    /// <remarks>
    /// Narrowed to the duplicate-key case on purpose: a timeout, a deadlock or a broken foreign key
    /// are real faults and must not be reported to the caller as a naming problem.
    /// </remarks>
    /// <param name="name">The name being written, for the error message.</param>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <exception cref="ValidationException">The unique index rejected the name.</exception>
    private async Task SaveTranslatingDuplicateNameAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await _ctx.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Detached so the rejected row cannot be re-attempted by a later save. The DbContext is
            // request-scoped and this exception ends the request, so nothing does that today — but
            // leaving a failed insert tracked makes that an accident waiting to happen.
            // Fully qualified: Enterprise.Gpt.Dto.Actions.Project is a namespace in scope here.
            foreach (var entry in _ctx.ChangeTracker.Entries<Enterprise.Gpt.Entity.Project>().ToList())
            {
                entry.State = EntityState.Detached;
            }

            _logger.LogInformation(ex, "A concurrent request already claimed a project name for this user");

            throw CreateDuplicateNameException(name);
        }
    }

    /// <summary>
    /// Builds the validation failure both the pre-check and the unique index report, so the two
    /// orderings are indistinguishable to a client.
    /// </summary>
    /// <remarks>
    /// The property name is the wire-visible key in the problem details <c>errors</c> dictionary and
    /// is identical on the create and update DTOs, so one literal serves both paths.
    /// </remarks>
    private static ValidationException CreateDuplicateNameException(string name)
    {
        return new ValidationException(
        [
            new ValidationFailure(NameProperty, $"A project named '{name}' already exists.")
        ]);
    }

    private NotFoundException LogAndCreateNotFound(Guid projectId, Guid userId)
    {
        // Deliberately indistinguishable from "does not exist" so the endpoints cannot be used to
        // probe for project ids belonging to other users.
        _logger.LogWarning("Project {ProjectId} not found, deactivated, or not owned by user {UserId}", projectId, userId);

        return new NotFoundException($"Project with id '{projectId}' was not found.");
    }
}
