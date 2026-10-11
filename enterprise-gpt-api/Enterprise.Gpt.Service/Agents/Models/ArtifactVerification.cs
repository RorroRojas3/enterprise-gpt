namespace Enterprise.Gpt.Service.Agents.Models;

/// <summary>What re-opening a produced artifact found.</summary>
/// <param name="Passed">Whether the file opened and carries something.</param>
/// <param name="Shape">What was measured (sheets, slides, pages, header row), for the answer to state.</param>
/// <param name="Reason">Why it failed, in a sentence safe to relay. <see langword="null"/> on a pass.</param>
public sealed record ArtifactVerification(bool Passed, string Shape, string? Reason)
{
    #region Public static methods

    /// <summary>A file that opened, with what was measured in it.</summary>
    public static ArtifactVerification Pass(string shape) => new(true, shape, null);

    /// <summary>A file that did not.</summary>
    /// <param name="reason">A clause that completes "… could not be saved because …".</param>
    public static ArtifactVerification Fail(string reason) => new(false, string.Empty, reason);

    #endregion
}
