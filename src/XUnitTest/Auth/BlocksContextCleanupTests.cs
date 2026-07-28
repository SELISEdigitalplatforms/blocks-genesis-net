using Blocks.Genesis;

namespace XUnitTest.Auth;

public class BlocksContextCleanupTests
{
    [Fact]
    public void Cleanup_ShouldReleaseThreadLocalStorage_AndLeaveTestModeUsable()
    {
        var original = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.Cleanup();

            // The thread-local is recreated, so the property remains usable
            // and reports its default value afterwards.
            Assert.False(BlocksContext.IsTestMode);
            BlocksContext.IsTestMode = true;
            Assert.True(BlocksContext.IsTestMode);
        }
        finally
        {
            BlocksContext.IsTestMode = original;
        }
    }
}
