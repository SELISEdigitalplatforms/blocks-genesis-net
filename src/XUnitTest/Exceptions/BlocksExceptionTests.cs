using Blocks.Genesis;

namespace XUnitTest.Exceptions;

public class BlocksExceptionTests
{
    [Fact]
    public void Ctor_Default_ShouldCreateInstanceWithFrameworkMessage()
    {
        var ex = new BlocksException();

        Assert.IsAssignableFrom<Exception>(ex);
        Assert.Null(ex.InnerException);
        Assert.NotNull(ex.Message); // default .NET message
    }

    [Fact]
    public void Ctor_WithMessage_ShouldSetMessage()
    {
        var ex = new BlocksException("boom");

        Assert.Equal("boom", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Ctor_WithMessageAndInner_ShouldSetBoth()
    {
        var inner = new InvalidOperationException("inner");

        var ex = new BlocksException("outer", inner);

        Assert.Equal("outer", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void CanBeThrownAndCaughtAsException()
    {
        var thrown = Assert.Throws<BlocksException>((Action)(() => throw new BlocksException("x")));
        Assert.Equal("x", thrown.Message);
    }
}

public class BlocksAuthenticationExceptionTests
{
    [Fact]
    public void Ctor_WithMessage_ShouldSetMessageAndInheritBase()
    {
        var ex = new BlocksAuthenticationException("auth failed");

        Assert.IsAssignableFrom<BlocksException>(ex);
        Assert.Equal("auth failed", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Ctor_WithMessageAndInner_ShouldSetInner()
    {
        var inner = new Exception("root");

        var ex = new BlocksAuthenticationException("auth failed", inner);

        Assert.Equal("auth failed", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }
}

public class BlocksNotFoundExceptionTests
{
    [Fact]
    public void Ctor_WithMessage_ShouldSetMessageAndInheritBase()
    {
        var ex = new BlocksNotFoundException("missing");

        Assert.IsAssignableFrom<BlocksException>(ex);
        Assert.Equal("missing", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Ctor_WithMessageAndInner_ShouldSetInner()
    {
        var inner = new Exception("root");

        var ex = new BlocksNotFoundException("missing", inner);

        Assert.Equal("missing", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }
}

public class BlocksRateLimitExceptionTests
{
    [Fact]
    public void Ctor_WithMessage_ShouldSetMessageAndInheritBase()
    {
        var ex = new BlocksRateLimitException("too many");

        Assert.IsAssignableFrom<BlocksException>(ex);
        Assert.Equal("too many", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Ctor_WithMessageAndInner_ShouldSetInner()
    {
        var inner = new Exception("root");

        var ex = new BlocksRateLimitException("too many", inner);

        Assert.Equal("too many", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }
}

public class BlocksValidationExceptionTests
{
    [Fact]
    public void Ctor_WithErrors_ShouldRetainProvidedDictionary()
    {
        var errors = new Dictionary<string, string[]>
        {
            ["email"] = new[] { "invalid" },
            ["name"]  = new[] { "required", "too short" }
        };

        var ex = new BlocksValidationException("validation failed", errors);

        Assert.IsAssignableFrom<BlocksException>(ex);
        Assert.Equal("validation failed", ex.Message);
        Assert.Same(errors, ex.Errors);
        Assert.Equal(2, ex.Errors.Count);
        Assert.Equal(new[] { "invalid" }, ex.Errors["email"]);
    }

    [Fact]
    public void Ctor_WithNullErrors_ShouldFallBackToEmptyOrdinalIgnoreCaseDictionary()
    {
        var ex = new BlocksValidationException("validation failed", null!);

        Assert.NotNull(ex.Errors);
        Assert.Empty(ex.Errors);

        // Verify OrdinalIgnoreCase behaviour on the fallback dictionary
        ex.Errors["Field"] = new[] { "bad" };
        Assert.True(ex.Errors.ContainsKey("field"));
        Assert.True(ex.Errors.ContainsKey("FIELD"));
    }

    [Fact]
    public void Ctor_WithErrorsAndInner_ShouldSetInnerAndRetainErrors()
    {
        var inner = new InvalidOperationException("root");
        var errors = new Dictionary<string, string[]> { ["f"] = new[] { "bad" } };

        var ex = new BlocksValidationException("invalid", errors, inner);

        Assert.Equal("invalid", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Same(errors, ex.Errors);
    }

    [Fact]
    public void Ctor_WithNullErrorsAndInner_ShouldFallBackToEmptyOrdinalIgnoreCaseDictionary()
    {
        var inner = new Exception("root");

        var ex = new BlocksValidationException("invalid", null!, inner);

        Assert.Same(inner, ex.InnerException);
        Assert.NotNull(ex.Errors);
        Assert.Empty(ex.Errors);

        ex.Errors["Key"] = new[] { "v" };
        Assert.True(ex.Errors.ContainsKey("KEY"));
    }
}
