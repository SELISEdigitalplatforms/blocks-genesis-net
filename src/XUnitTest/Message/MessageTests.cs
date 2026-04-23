using Blocks.Genesis;
using System.Text.Json;

namespace XUnitTest.Message;

public class MessageTests
{
    [Fact]
    public void Message_ShouldInitializeRequiredProperties()
    {
        var message = new global::Blocks.Genesis.Message
        {
            Body = "{\"value\":1}",
            Type = "MyPayload"
        };

        Assert.Equal("{\"value\":1}", message.Body);
        Assert.Equal("MyPayload", message.Type);
    }

    [Fact]
    public void Message_RecordEquality_ShouldCompareByValue()
    {
        var first = new global::Blocks.Genesis.Message { Body = "b", Type = "t" };
        var second = new global::Blocks.Genesis.Message { Body = "b", Type = "t" };
        var different = new global::Blocks.Genesis.Message { Body = "b", Type = "other" };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, different);
    }

    [Fact]
    public void Message_WithExpression_ShouldReturnNewInstanceWithOverriddenValues()
    {
        var original = new global::Blocks.Genesis.Message { Body = "b1", Type = "t1" };

        var mutated = original with { Body = "b2" };

        Assert.Equal("b1", original.Body);
        Assert.Equal("b2", mutated.Body);
        Assert.Equal("t1", mutated.Type);
        Assert.NotSame(original, mutated);
    }

    [Fact]
    public void Message_ShouldRoundTripThroughJson()
    {
        var original = new global::Blocks.Genesis.Message { Body = "payload", Type = "SomeType" };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<global::Blocks.Genesis.Message>(json);

        Assert.NotNull(restored);
        Assert.Equal(original, restored);
    }
}
