using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Service.Conversations;
using Enterprise.Gpt.Service.Projects;
using Enterprise.Gpt.Service.Sorting;
using Enterprise.Gpt.Service.Users;
using FluentValidation;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Sorting;

/// <summary>
/// Each listing's sort keys are written down three times over — as an enumeration, as the advertised
/// <c>Supported</c> set, and as the private parse lookup — and nothing but this keeps them in step.
/// Drift fails silently and client-facing in both directions: a token in the lookup but not in
/// <c>Supported</c> vanishes from the rejection message a client learns the supported set from, and a
/// token in <c>Supported</c> but not in the lookup is advertised and then refused by a message that
/// lists it.
/// </summary>
public sealed class SortKeysTests
{
    public static TheoryData<string, IReadOnlyList<string>, int> Listings =>
        new()
        {
            { "conversations", ConversationSortKeyNames.Supported, Enum.GetValues<ConversationSortKeys>().Length },
            { "projects", ProjectSortKeyNames.Supported, Enum.GetValues<ProjectSortKeys>().Length },
            { "users", UserSortKeyNames.Supported, Enum.GetValues<UserSortKeys>().Length }
        };

    [Theory]
    [MemberData(nameof(Listings))]
    public void Supported_NamesEveryKeyExactlyOnce(string listing, IReadOnlyList<string> supported, int keyCount)
    {
        Assert.Equal(keyCount, supported.Count);
        Assert.Equal(supported.Count, supported.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(supported, token => Assert.False(string.IsNullOrWhiteSpace(token), listing));
    }

    [Fact]
    public void ConversationSortKeys_EverySupportedToken_ResolvesToADistinctKey()
    {
        var resolved = ConversationSortKeyNames.Supported
            .Select(token => ConversationSortKeyNames.Resolve(token, null).Key)
            .ToList();

        Assert.Equal(ConversationSortKeyNames.Supported.Count, resolved.Distinct().Count());
    }

    [Fact]
    public void ProjectSortKeys_EverySupportedToken_ResolvesToADistinctKey()
    {
        var resolved = ProjectSortKeyNames.Supported
            .Select(token => ProjectSortKeyNames.Resolve(token, null).Key)
            .ToList();

        Assert.Equal(ProjectSortKeyNames.Supported.Count, resolved.Distinct().Count());
    }

    [Fact]
    public void UserSortKeys_EverySupportedToken_ResolvesToADistinctKey()
    {
        var resolved = UserSortKeyNames.Supported
            .Select(token => UserSortKeyNames.Resolve(token, null).Key)
            .ToList();

        Assert.Equal(UserSortKeyNames.Supported.Count, resolved.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Listings))]
    public void SupportedList_QuotesEveryTokenItAdvertises(string listing, IReadOnlyList<string> supported, int keyCount)
    {
        _ = keyCount;
        var rendered = listing switch
        {
            "conversations" => ConversationSortKeyNames.SupportedList,
            "projects" => ProjectSortKeyNames.SupportedList,
            _ => UserSortKeyNames.SupportedList
        };

        Assert.All(supported, token => Assert.Contains($"'{token}'", rendered));
    }

    [Theory]
    [InlineData("conversations")]
    [InlineData("projects")]
    [InlineData("users")]
    public void Resolve_TokenInAnotherListingsSet_IsRejected(string listing)
    {
        // The three sets overlap — every listing has a `name` — so a token being valid *somewhere*
        // must not make it valid here. `updated` is projects-only; `email` is users-only.
        var foreign = listing == "projects" ? "email" : "updated";

        Assert.Throws<ValidationException>(() => _ = listing switch
        {
            "conversations" => ConversationSortKeyNames.Resolve(foreign, null).Key.ToString(),
            "projects" => ProjectSortKeyNames.Resolve(foreign, null).Key.ToString(),
            _ => UserSortKeyNames.Resolve(foreign, null).Key.ToString()
        });
    }

    [Fact]
    public void Resolve_BlankField_FallsBackWithoutRejecting()
    {
        // A caller who sends only `?dir=` has named no field, and the listing's own default is the
        // one that must answer — not a 400 about a parameter they left out.
        Assert.Equal(ProjectSortKeys.Created, ProjectSortKeyNames.Resolve(null, "asc").Key);
        Assert.Equal(ConversationSortKeys.Created, ConversationSortKeyNames.Resolve("   ", null).Key);
        Assert.Equal(UserSortKeys.Name, UserSortKeyNames.Resolve(null, null).Key);
    }

    [Fact]
    public void Resolve_NaturalDirection_DiffersByField()
    {
        // A name reads A-Z and a date reads newest first, so an omitted direction cannot resolve to
        // one constant across every field.
        Assert.Equal(SortDirections.Ascending, ProjectSortKeyNames.Resolve("name", null).Direction);
        Assert.Equal(SortDirections.Descending, ProjectSortKeyNames.Resolve("created", null).Direction);
        Assert.Equal(SortDirections.Ascending, UserSortKeyNames.Resolve("name", null).Direction);
        Assert.Equal(SortDirections.Descending, UserSortKeyNames.Resolve("created", null).Direction);
    }

    [Fact]
    public void IsDefaultOrder_OnlyWhenNeitherParameterWasSupplied()
    {
        Assert.True(SortParameters.IsDefaultOrder(null, null));
        Assert.True(SortParameters.IsDefaultOrder("  ", ""));
        Assert.False(SortParameters.IsDefaultOrder("name", null));
        // A direction alone is still a request: it applies to the listing's default field.
        Assert.False(SortParameters.IsDefaultOrder(null, "asc"));
    }

    [Theory]
    [InlineData("asc", SortDirections.Ascending)]
    [InlineData("DESC", SortDirections.Descending)]
    public void ResolveDirection_IsCaseInsensitive(string dir, SortDirections expected)
    {
        Assert.Equal(expected, SortParameters.ResolveDirection(dir, SortDirections.Ascending));
    }

    [Fact]
    public void ResolveDirection_UnknownValue_NamesBothAcceptedTokens()
    {
        var failure = Assert.Throws<ValidationException>(
            () => SortParameters.ResolveDirection("sideways", SortDirections.Ascending));

        var error = Assert.Single(failure.Errors);
        Assert.Equal(SortParameters.DirectionKey, error.PropertyName);
        Assert.Contains("'asc'", error.ErrorMessage);
        Assert.Contains("'desc'", error.ErrorMessage);
    }
}
