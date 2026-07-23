using Blocks.Genesis;

namespace XUnitTest.Exceptions;

/// <summary>Tests for the Blocks exception hierarchy (message/inner-exception plumbing and validation errors).</summary>
public class BlocksExceptionsTests
{
    [Fact]
    public void BlocksException_ShouldExposeMessageAndInnerException()
    {
        Assert.NotNull(new BlocksException().Message);

        var withMessage = new BlocksException("boom");
        Assert.Equal("boom", withMessage.Message);

        var inner = new InvalidOperationException("cause");
        var withInner = new BlocksException("boom", inner);
        Assert.Equal("boom", withInner.Message);
        Assert.Same(inner, withInner.InnerException);
    }

    [Theory]
    [MemberData(nameof(SubtypeFactories))]
    public void Subtypes_ShouldCarryMessageAndInnerException(Func<string, BlocksException> withMessage, Func<string, Exception, BlocksException> withInner)
    {
        var ex = withMessage("nope");
        Assert.Equal("nope", ex.Message);
        Assert.IsAssignableFrom<BlocksException>(ex);

        var inner = new Exception("cause");
        var chained = withInner("nope", inner);
        Assert.Same(inner, chained.InnerException);
    }

    public static IEnumerable<object[]> SubtypeFactories()
    {
        yield return [(Func<string, BlocksException>)(m => new BlocksAuthenticationException(m)), (Func<string, Exception, BlocksException>)((m, e) => new BlocksAuthenticationException(m, e))];
        yield return [(Func<string, BlocksException>)(m => new BlocksNotFoundException(m)), (Func<string, Exception, BlocksException>)((m, e) => new BlocksNotFoundException(m, e))];
        yield return [(Func<string, BlocksException>)(m => new BlocksRateLimitException(m)), (Func<string, Exception, BlocksException>)((m, e) => new BlocksRateLimitException(m, e))];
    }

    [Fact]
    public void BlocksValidationException_ShouldExposeErrors_AndDefaultToEmpty()
    {
        var errors = new Dictionary<string, string[]> { ["Name"] = ["required"] };
        var ex = new BlocksValidationException("invalid", errors);
        Assert.Equal("invalid", ex.Message);
        Assert.Same(errors, ex.Errors);

        var inner = new Exception("cause");
        var chained = new BlocksValidationException("invalid", null!, inner);
        Assert.Same(inner, chained.InnerException);
        Assert.Empty(chained.Errors);
    }
}
