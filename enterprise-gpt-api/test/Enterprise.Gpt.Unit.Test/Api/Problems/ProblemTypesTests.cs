using System.Reflection;
using Enterprise.Gpt.Api.Problems.Constants;
using Enterprise.Gpt.Common.Constants;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Api.Problems;

public class ProblemTypesTests
{
    public static TheoryData<string> EveryProblemCode =>
    [
        .. typeof(ProblemCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(constant => constant.IsLiteral)
            .Select(constant => (string)constant.GetRawConstantValue()!)
    ];

    [Theory]
    [MemberData(nameof(EveryProblemCode))]
    public void ForCode_EveryProblemCode_ResolvesToItsRelativeUri(string problemCode)
    {
        var problemType = ProblemTypes.ForCode(problemCode);

        Assert.NotNull(problemType);
        Assert.Equal($"/problems/{problemCode}", problemType.Value.Type);
        Assert.False(string.IsNullOrWhiteSpace(problemType.Value.Title));
    }

    [Fact]
    public void ForCode_UnknownCode_ReturnsNull()
    {
        Assert.Null(ProblemTypes.ForCode("not-a-problem"));
    }
}
