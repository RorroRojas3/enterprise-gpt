using FluentValidation;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Enterprise.Gpt.Dto;
using Enterprise.Gpt.Dto.Actions.Project;
using Enterprise.Gpt.Entity;
using Enterprise.Gpt.Service;
using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Unit.Test.TestInfrastructure;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Services;

public sealed class ProjectServiceTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly ProjectService _service;

    public ProjectServiceTests()
    {
        _tokenService.GetOid().Returns(KnownIds.SeedUserId);
        _service = new ProjectService(
            NullLogger<ProjectService>.Instance,
            _tokenService,
            _fixture.Context,
            new CreateProjectActionDtoValidator(),
            new UpdateProjectActionDtoValidator());
    }

    public void Dispose() => _fixture.Dispose();

    #region Helpers
    private async Task<Project> AddProjectAsync(
        string name = "Research", Guid? userId = null, DateTimeOffset? deactivated = null,
        string? instructions = null, bool isFavorite = false)
    {
        var date = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? KnownIds.SeedUserId,
            Name = name,
            Description = $"{name} description",
            Instructions = instructions,
            IsFavorite = isFavorite,
            DateCreated = date,
            DateModified = date,
            DateDeactivated = deactivated
        };

        using var ctx = _fixture.CreateContext();
        ctx.Projects.Add(project);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return project;
    }

    private async Task<ProjectDocument> AddProjectDocumentAsync(Guid projectId, int chunkCount = 2, Guid? userId = null)
    {
        var date = DateTimeOffset.UtcNow;
        var document = new ProjectDocument
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            UserId = userId ?? KnownIds.SeedUserId,
            Name = "spec.pdf",
            Extension = ".pdf",
            MimeType = "application/pdf",
            Size = 1024,
            Path = $"{userId ?? KnownIds.SeedUserId}/projects/{projectId}/doc.pdf",
            DateCreated = date,
            DateModified = date,
            Chunks = [.. Enumerable.Range(0, chunkCount).Select(i => new ProjectDocumentChunk
            {
                Id = Guid.NewGuid(),
                Index = i,
                SourceNumber = i + 1,
                TokenCount = 10,
                Text = $"chunk {i}",
                Embedding = new SqlVector<float>(new float[] { 0.1f, 0.2f }),
                DateCreated = date,
                DateModified = date
            })]
        };

        using var ctx = _fixture.CreateContext();
        ctx.ProjectDocuments.Add(document);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return document;
    }

    private async Task<Conversation> AddConversationAsync(Guid? projectId = null, Guid? userId = null)
    {
        var date = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? KnownIds.SeedUserId,
            ProjectId = projectId,
            Name = "Test Conversation",
            DateCreated = date,
            DateModified = date
        };

        using var ctx = _fixture.CreateContext();
        ctx.Conversations.Add(conversation);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return conversation;
    }

    private async Task<User> AddUserAsync()
    {
        var date = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            FirstName = "Other",
            LastName = "User",
            Email = "other.user@example.com",
            DateCreated = date,
            DateModified = date
        };

        using var ctx = _fixture.CreateContext();
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user;
    }

    private static CreateProjectActionDto CreateRequest(string name = "New Project", string? instructions = null)
    {
        return new CreateProjectActionDto
        {
            Name = name,
            Description = "A description.",
            Instructions = instructions
        };
    }

    private static UpdateProjectActionDto UpdateRequest(string name = "Renamed Project", string? instructions = null)
    {
        return new UpdateProjectActionDto
        {
            Name = name,
            Description = "An updated description.",
            Instructions = instructions
        };
    }
    #endregion

    #region SearchProjectsAsync
    [Fact]
    public async Task SearchProjectsAsync_NoFilter_ReturnsOnlyTheCallersActiveProjects()
    {
        var otherUser = await AddUserAsync();
        var mine = await AddProjectAsync("Mine");
        await AddProjectAsync("Theirs", userId: otherUser.Id);
        await AddProjectAsync("Deleted", deactivated: DateTimeOffset.UtcNow);

        var result = await _service.SearchProjectsAsync(null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(mine.Id, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task SearchProjectsAsync_NameFilter_MatchesOnSubstring()
    {
        await AddProjectAsync("Quarterly Planning");
        await AddProjectAsync("Hiring");

        var result = await _service.SearchProjectsAsync("plan", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Quarterly Planning", Assert.Single(result.Items).Name);
    }

    [Fact]
    public async Task SearchProjectsAsync_MoreProjectsThanThePage_PagesAndReportsTheTotal()
    {
        for (var i = 0; i < 5; i++)
        {
            await AddProjectAsync($"Project {i}");
        }

        var result = await _service.SearchProjectsAsync(null, skip: 2, take: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5, result.TotalCount);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.CurrentPage);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, -1)]
    [InlineData(0, 100_000)]
    public async Task SearchProjectsAsync_OutOfRangePagingArguments_AreClampedRatherThanFaulting(int skip, int take)
    {
        // These arrive straight off the query string. take = 0 used to divide by zero when computing
        // CurrentPage, turning a trivially reachable request into a 500.
        await AddProjectAsync("Research");

        var result = await _service.SearchProjectsAsync(
            null, skip, take, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalCount);
        Assert.InRange(result.PageSize, 1, 100);
        Assert.True(result.CurrentPage >= 1);
    }

    [Fact]
    public async Task SearchProjectsAsync_Listing_OmitsInstructions()
    {
        // A page of up to 100 projects would otherwise carry 100 instruction bodies that no listing
        // renders.
        await AddProjectAsync("Research", instructions: "Answer in British English.");

        var result = await _service.SearchProjectsAsync(null, cancellationToken: TestContext.Current.CancellationToken);

        var summary = Assert.Single(result.Items);
        Assert.Equal("Research", summary.Name);
        Assert.IsNotType<ProjectDto>(summary);
    }

    [Fact]
    public async Task SearchProjectsAsync_FavoritesRequested_ReturnsOnlyFavorites()
    {
        var starred = await AddProjectAsync("Starred", isFavorite: true);
        await AddProjectAsync("Plain");

        var result = await _service.SearchProjectsAsync(
            null, isFavorite: true, cancellationToken: TestContext.Current.CancellationToken);

        // The count travels with the filter: a total describing the unfiltered listing would send
        // every client paging past the end of the result set.
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(starred.Id, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task SearchProjectsAsync_NonFavoritesRequested_ExcludesFavorites()
    {
        await AddProjectAsync("Starred", isFavorite: true);
        var plain = await AddProjectAsync("Plain");

        var result = await _service.SearchProjectsAsync(
            null, isFavorite: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(plain.Id, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task SearchProjectsAsync_FavoriteFilterOmitted_ReturnsBoth()
    {
        // A bool?, not a defaulted bool: omitting it applies no filter at all.
        await AddProjectAsync("Starred", isFavorite: true);
        await AddProjectAsync("Plain");

        var result = await _service.SearchProjectsAsync(null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public async Task SearchProjectsAsync_NameAndFavoriteFilters_BothApply()
    {
        await AddProjectAsync("Quarterly Planning", isFavorite: true);
        await AddProjectAsync("Hiring Planning");
        await AddProjectAsync("Starred Research", isFavorite: true);

        var result = await _service.SearchProjectsAsync(
            "plan", isFavorite: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Quarterly Planning", Assert.Single(result.Items).Name);
    }
    #endregion

    #region GetProjectAsync
    [Fact]
    public async Task GetProjectAsync_OwnedProject_ReturnsIt()
    {
        var project = await AddProjectAsync(instructions: "Answer in British English.");

        var result = await _service.GetProjectAsync(project.Id, TestContext.Current.CancellationToken);

        Assert.Equal(project.Id, result.Id);
        Assert.Equal("Answer in British English.", result.Instructions);
    }

    [Fact]
    public async Task GetProjectAsync_ProjectOwnedByAnotherUser_ThrowsNotFound()
    {
        // Indistinguishable from "does not exist" so the endpoint cannot be used to probe for other
        // users' project ids.
        var otherUser = await AddUserAsync();
        var project = await AddProjectAsync(userId: otherUser.Id);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetProjectAsync(project.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetProjectAsync_DeactivatedProject_ThrowsNotFound()
    {
        var project = await AddProjectAsync(deactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetProjectAsync(project.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetProjectAsync_UnknownProject_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetProjectAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
    #endregion

    #region CreateProjectAsync
    [Fact]
    public async Task CreateProjectAsync_ValidRequest_PersistsItAgainstTheCaller()
    {
        var result = await _service.CreateProjectAsync(
            CreateRequest(instructions: "Cite sources."), TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var project = await ctx.Projects.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(result.Id, project.Id);
        Assert.Equal(KnownIds.SeedUserId, project.UserId);
        Assert.Equal("New Project", project.Name);
        Assert.Equal("Cite sources.", project.Instructions);
        Assert.Null(project.DateDeactivated);
    }

    [Fact]
    public async Task CreateProjectAsync_NameAlreadyUsedByAnActiveProject_ThrowsValidationException()
    {
        await AddProjectAsync("Research");

        var exception = await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateProjectAsync(CreateRequest("Research"), TestContext.Current.CancellationToken));

        Assert.Equal(nameof(CreateProjectActionDto.Name), Assert.Single(exception.Errors).PropertyName);
    }

    [Fact]
    public async Task CreateProjectAsync_NameOnlyUsedByADeletedProject_Succeeds()
    {
        // The unique index is filtered on DateDeactivated, so a deleted project must not permanently
        // reserve its name.
        await AddProjectAsync("Research", deactivated: DateTimeOffset.UtcNow);

        var result = await _service.CreateProjectAsync(CreateRequest("Research"), TestContext.Current.CancellationToken);

        Assert.Equal("Research", result.Name);
    }

    [Fact]
    public async Task CreateProjectAsync_NameUsedByAnotherUser_Succeeds()
    {
        var otherUser = await AddUserAsync();
        await AddProjectAsync("Research", userId: otherUser.Id);

        var result = await _service.CreateProjectAsync(CreateRequest("Research"), TestContext.Current.CancellationToken);

        Assert.Equal("Research", result.Name);
    }

    [Fact]
    public async Task CreateProjectAsync_EmptyName_ThrowsValidationException()
    {
        await Assert.ThrowsAsync<ValidationException>(
            () => _service.CreateProjectAsync(CreateRequest(string.Empty), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateProjectAsync_NullRequest_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.CreateProjectAsync(null!, TestContext.Current.CancellationToken));
    }
    #endregion

    #region UpdateProjectAsync
    [Fact]
    public async Task UpdateProjectAsync_OwnedProject_WritesEveryEditableField()
    {
        var project = await AddProjectAsync("Research", instructions: "Old instructions.");

        var result = await _service.UpdateProjectAsync(
            project.Id, UpdateRequest(instructions: "New instructions."), TestContext.Current.CancellationToken);

        Assert.Equal("Renamed Project", result.Name);

        using var ctx = _fixture.CreateContext();
        var stored = await ctx.Projects.AsNoTracking().SingleAsync(x => x.Id == project.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Renamed Project", stored.Name);
        Assert.Equal("An updated description.", stored.Description);
        Assert.Equal("New instructions.", stored.Instructions);
    }

    [Fact]
    public async Task UpdateProjectAsync_KeepingItsOwnName_Succeeds()
    {
        // The uniqueness check has to exclude the project being updated, or renaming nothing but the
        // description would fail.
        var project = await AddProjectAsync("Research");

        var result = await _service.UpdateProjectAsync(
            project.Id, UpdateRequest("Research"), TestContext.Current.CancellationToken);

        Assert.Equal("Research", result.Name);
    }

    [Fact]
    public async Task UpdateProjectAsync_NameAlreadyUsedByAnotherProject_ThrowsValidationException()
    {
        await AddProjectAsync("Hiring");
        var project = await AddProjectAsync("Research");

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.UpdateProjectAsync(project.Id, UpdateRequest("Hiring"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateProjectAsync_ProjectOwnedByAnotherUser_ThrowsNotFound()
    {
        var otherUser = await AddUserAsync();
        var project = await AddProjectAsync(userId: otherUser.Id);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.UpdateProjectAsync(project.Id, UpdateRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateProjectAsync_NullRequest_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.UpdateProjectAsync(Guid.NewGuid(), null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateProjectAsync_ProjectIsFavorited_LeavesTheFlagAlone()
    {
        // The update DTO carries no flag and this PUT is a full representation, so a rename that
        // wrote IsFavorite would silently clear the star.
        var project = await AddProjectAsync("Research", isFavorite: true);

        await _service.UpdateProjectAsync(project.Id, UpdateRequest(), TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var stored = await ctx.Projects.AsNoTracking().SingleAsync(x => x.Id == project.Id, TestContext.Current.CancellationToken);
        Assert.True(stored.IsFavorite);
    }
    #endregion

    #region SetProjectFavoriteAsync
    [Fact]
    public async Task SetProjectFavoriteAsync_WhenFavorited_PersistsTheFlag()
    {
        var project = await AddProjectAsync();

        await _service.SetProjectFavoriteAsync(
            project.Id, new SetProjectFavoriteActionDto { IsFavorite = true }, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var stored = await ctx.Projects.AsNoTracking().SingleAsync(x => x.Id == project.Id, TestContext.Current.CancellationToken);
        Assert.True(stored.IsFavorite);
    }

    [Fact]
    public async Task SetProjectFavoriteAsync_WhenCleared_PersistsTheFlag()
    {
        // A set, not a toggle: a retried or duplicated request cannot land on the opposite value.
        var project = await AddProjectAsync(isFavorite: true);

        await _service.SetProjectFavoriteAsync(
            project.Id, new SetProjectFavoriteActionDto { IsFavorite = false }, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var stored = await ctx.Projects.AsNoTracking().SingleAsync(x => x.Id == project.Id, TestContext.Current.CancellationToken);
        Assert.False(stored.IsFavorite);
    }

    [Fact]
    public async Task SetProjectFavoriteAsync_WhenSet_DoesNotBumpDateModified()
    {
        // Starring a project does not modify it, and every client keeps its own copy of the row on
        // the strength of that: a bump here would desynchronise them at their next fetch.
        var project = await AddProjectAsync();
        var before = project.DateModified;

        await _service.SetProjectFavoriteAsync(
            project.Id, new SetProjectFavoriteActionDto { IsFavorite = true }, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var stored = await ctx.Projects.AsNoTracking().SingleAsync(x => x.Id == project.Id, TestContext.Current.CancellationToken);
        Assert.Equal(before, stored.DateModified);
    }

    [Fact]
    public async Task SetProjectFavoriteAsync_ProjectOwnedByAnotherUser_ThrowsNotFoundAndChangesNothing()
    {
        var otherUser = await AddUserAsync();
        var project = await AddProjectAsync(userId: otherUser.Id);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.SetProjectFavoriteAsync(
                project.Id, new SetProjectFavoriteActionDto { IsFavorite = true }, TestContext.Current.CancellationToken));

        using var ctx = _fixture.CreateContext();
        var stored = await ctx.Projects.AsNoTracking().SingleAsync(x => x.Id == project.Id, TestContext.Current.CancellationToken);
        Assert.False(stored.IsFavorite);
    }

    [Fact]
    public async Task SetProjectFavoriteAsync_DeactivatedProject_ThrowsNotFound()
    {
        var project = await AddProjectAsync(deactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.SetProjectFavoriteAsync(
                project.Id, new SetProjectFavoriteActionDto { IsFavorite = true }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetProjectFavoriteAsync_UnknownProject_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.SetProjectFavoriteAsync(
                Guid.NewGuid(), new SetProjectFavoriteActionDto { IsFavorite = true }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetProjectFavoriteAsync_NullRequest_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.SetProjectFavoriteAsync(Guid.NewGuid(), null!, TestContext.Current.CancellationToken));
    }
    #endregion

    #region DeactivateProjectAsync
    [Fact]
    public async Task DeactivateProjectAsync_ProjectWithDocuments_DeactivatesTheDocumentsAndTheirChunks()
    {
        var project = await AddProjectAsync();
        var document = await AddProjectDocumentAsync(project.Id, chunkCount: 3);

        await _service.DeactivateProjectAsync(project.Id, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        Assert.NotNull((await ctx.Projects.AsNoTracking().SingleAsync(x => x.Id == project.Id, TestContext.Current.CancellationToken)).DateDeactivated);
        Assert.NotNull((await ctx.ProjectDocuments.AsNoTracking().SingleAsync(x => x.Id == document.Id, TestContext.Current.CancellationToken)).DateDeactivated);
        Assert.All(
            await ctx.ProjectDocumentChunks.AsNoTracking().Where(x => x.ProjectDocumentId == document.Id).ToListAsync(TestContext.Current.CancellationToken),
            chunk => Assert.NotNull(chunk.DateDeactivated));
    }

    [Fact]
    public async Task DeactivateProjectAsync_ProjectWithConversations_KeepsThemAndClearsTheirProjectId()
    {
        // Deleting a project must not delete the user's chat history: the conversations survive as
        // standalone ones and simply lose access to the project's documents and instructions.
        var project = await AddProjectAsync();
        var conversation = await AddConversationAsync(projectId: project.Id);

        await _service.DeactivateProjectAsync(project.Id, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        var stored = await ctx.Conversations.AsNoTracking().SingleAsync(x => x.Id == conversation.Id, TestContext.Current.CancellationToken);
        Assert.Null(stored.DateDeactivated);
        Assert.Null(stored.ProjectId);
    }

    [Fact]
    public async Task DeactivateProjectAsync_OtherProjects_AreLeftAlone()
    {
        var project = await AddProjectAsync("Research");
        var untouched = await AddProjectAsync("Hiring");
        var untouchedDocument = await AddProjectDocumentAsync(untouched.Id);
        var untouchedConversation = await AddConversationAsync(projectId: untouched.Id);

        await _service.DeactivateProjectAsync(project.Id, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        Assert.Null((await ctx.Projects.AsNoTracking().SingleAsync(x => x.Id == untouched.Id, TestContext.Current.CancellationToken)).DateDeactivated);
        Assert.Null((await ctx.ProjectDocuments.AsNoTracking().SingleAsync(x => x.Id == untouchedDocument.Id, TestContext.Current.CancellationToken)).DateDeactivated);
        Assert.Equal(untouched.Id, (await ctx.Conversations.AsNoTracking().SingleAsync(x => x.Id == untouchedConversation.Id, TestContext.Current.CancellationToken)).ProjectId);
    }

    [Fact]
    public async Task DeactivateProjectAsync_ProjectOwnedByAnotherUser_ThrowsNotFoundAndChangesNothing()
    {
        var otherUser = await AddUserAsync();
        var project = await AddProjectAsync(userId: otherUser.Id);
        await AddProjectDocumentAsync(project.Id, userId: otherUser.Id);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.DeactivateProjectAsync(project.Id, TestContext.Current.CancellationToken));

        using var ctx = _fixture.CreateContext();
        Assert.Null((await ctx.Projects.AsNoTracking().SingleAsync(x => x.Id == project.Id, TestContext.Current.CancellationToken)).DateDeactivated);
        Assert.False(await ctx.ProjectDocuments.AsNoTracking().AnyAsync(x => x.DateDeactivated.HasValue, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeactivateProjectAsync_AlreadyDeactivatedProject_ThrowsNotFound()
    {
        var project = await AddProjectAsync(deactivated: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.DeactivateProjectAsync(project.Id, TestContext.Current.CancellationToken));
    }
    #endregion

    #region GetProjectDocumentsAsync
    [Fact]
    public async Task GetProjectDocumentsAsync_OwnedProject_ReturnsOnlyItsActiveDocuments()
    {
        var project = await AddProjectAsync("Research");
        var otherProject = await AddProjectAsync("Hiring");
        var document = await AddProjectDocumentAsync(project.Id);
        await AddProjectDocumentAsync(otherProject.Id);

        var result = await _service.GetProjectDocumentsAsync(project.Id, TestContext.Current.CancellationToken);

        var only = Assert.Single(result);
        Assert.Equal(document.Id, only.Id);
        Assert.Equal(document.Id, only.DocumentId);
        Assert.Equal(project.Id, only.ProjectId);
        Assert.Equal(".pdf", only.Extension);
    }

    [Fact]
    public async Task GetProjectDocumentsAsync_DeactivatedDocument_IsExcluded()
    {
        var project = await AddProjectAsync();
        var document = await AddProjectDocumentAsync(project.Id);
        await _service.DeactivateProjectDocumentAsync(project.Id, document.Id, TestContext.Current.CancellationToken);

        Assert.Empty(await _service.GetProjectDocumentsAsync(project.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetProjectDocumentsAsync_ProjectOwnedByAnotherUser_ThrowsNotFound()
    {
        var otherUser = await AddUserAsync();
        var project = await AddProjectAsync(userId: otherUser.Id);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GetProjectDocumentsAsync(project.Id, TestContext.Current.CancellationToken));
    }
    #endregion

    #region DeactivateProjectDocumentAsync
    [Fact]
    public async Task DeactivateProjectDocumentAsync_OwnedDocument_DeactivatesItAndItsChunks()
    {
        var project = await AddProjectAsync();
        var document = await AddProjectDocumentAsync(project.Id, chunkCount: 2);

        await _service.DeactivateProjectDocumentAsync(project.Id, document.Id, TestContext.Current.CancellationToken);

        using var ctx = _fixture.CreateContext();
        Assert.NotNull((await ctx.ProjectDocuments.AsNoTracking().SingleAsync(x => x.Id == document.Id, TestContext.Current.CancellationToken)).DateDeactivated);
        Assert.All(
            await ctx.ProjectDocumentChunks.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken),
            chunk => Assert.NotNull(chunk.DateDeactivated));
        // The project itself is untouched.
        Assert.Null((await ctx.Projects.AsNoTracking().SingleAsync(x => x.Id == project.Id, TestContext.Current.CancellationToken)).DateDeactivated);
    }

    [Fact]
    public async Task DeactivateProjectDocumentAsync_DocumentBelongingToAnotherProject_ThrowsNotFound()
    {
        var project = await AddProjectAsync("Research");
        var otherProject = await AddProjectAsync("Hiring");
        var document = await AddProjectDocumentAsync(otherProject.Id);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.DeactivateProjectDocumentAsync(project.Id, document.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeactivateProjectDocumentAsync_ProjectOwnedByAnotherUser_ThrowsNotFound()
    {
        var otherUser = await AddUserAsync();
        var project = await AddProjectAsync(userId: otherUser.Id);
        var document = await AddProjectDocumentAsync(project.Id, userId: otherUser.Id);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.DeactivateProjectDocumentAsync(project.Id, document.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeactivateProjectDocumentAsync_UnknownDocument_ThrowsNotFound()
    {
        var project = await AddProjectAsync();

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.DeactivateProjectDocumentAsync(project.Id, Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
    #endregion
}
