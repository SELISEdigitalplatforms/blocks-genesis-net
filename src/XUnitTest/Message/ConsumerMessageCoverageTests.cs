using Blocks.Genesis;

namespace XUnitTest.Message;

public class ConsumerMessageCoverageTests
{
    [Fact]
    public void SccheduledEnqueueTimeUtc_Getter_ShouldReturnScheduledEnqueueTimeUtc()
    {
        var scheduledAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var message = new ConsumerMessage<string>
        {
            ConsumerName = "consumer",
            Payload = "payload",
            ScheduledEnqueueTimeUtc = scheduledAt
        };

#pragma warning disable CS0618
        Assert.Equal(scheduledAt, message.SccheduledEnqueueTimeUtc);
#pragma warning restore CS0618
    }
}
