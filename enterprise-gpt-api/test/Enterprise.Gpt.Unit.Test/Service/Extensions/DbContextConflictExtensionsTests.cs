using Enterprise.Gpt.Service.Exceptions;
using Enterprise.Gpt.Service.Extensions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Extensions;

public class DbContextConflictExtensionsTests
{
    private sealed class ThrowingContext(Exception exception) : DbContext
    {
        private readonly Exception _exception = exception;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<int>(_exception);
    }

    [Fact]
    public async Task SaveChangesOrConflictAsync_LostRowVersionRace_ThrowsConflictWithTheMessage()
    {
        var race = new DbUpdateConcurrencyException("lost", []);
        using var ctx = new ThrowingContext(race);

        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => ctx.SaveChangesOrConflictAsync("Model was changed by another request.", TestContext.Current.CancellationToken));

        Assert.Equal("Model was changed by another request.", exception.Message);
        Assert.Same(race, exception.InnerException);
    }

    [Fact]
    public async Task SaveChangesOrConflictAsync_OtherUpdateFailure_PropagatesUnchanged()
    {
        var failure = new DbUpdateException("constraint");
        using var ctx = new ThrowingContext(failure);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => ctx.SaveChangesOrConflictAsync("unused", TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
    }
}
