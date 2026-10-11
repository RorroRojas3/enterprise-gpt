using Enterprise.Gpt.Service.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Enterprise.Gpt.Service.Extensions;

/// <summary>
/// Saves a unit of work, reporting a lost row-version race as a <see cref="ConflictException"/>.
/// </summary>
internal static class DbContextConflictExtensions
{
    #region Public static methods

    /// <summary>
    /// Saves the context, translating a <see cref="DbUpdateConcurrencyException"/> into a 409 whose
    /// <paramref name="conflictMessage"/> reaches the client, so it must name nothing sensitive.
    /// </summary>
    /// <exception cref="ConflictException">Another request changed a row this save updates.</exception>
    internal static async Task SaveChangesOrConflictAsync(
        this DbContext ctx, string conflictMessage, CancellationToken cancellationToken)
    {
        try
        {
            await ctx.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConflictException(conflictMessage, ex);
        }
    }

    #endregion
}
