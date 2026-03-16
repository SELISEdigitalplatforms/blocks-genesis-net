using Azure.Messaging.ServiceBus;
using Blocks.Genesis;
using System.Runtime.CompilerServices;

namespace XUnitTest.Message;

public class AutoRenewalEventArgsTests
{
    [Fact]
    public void AutoRenewalEventArgs_ShouldHaveExpectedDefaults()
    {
        var args = new AutoRenewalEventArgs();

        Assert.Null(args.Args);
        Assert.Equal(default, args.Token);
        Assert.Null(args.CancellationTokenSource);
    }

    [Fact]
    public void AutoRenewalEventArgs_ShouldAllowSettingAllProperties()
    {
        var messageArgs = (ProcessMessageEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(ProcessMessageEventArgs));
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        var args = new AutoRenewalEventArgs
        {
            Args = messageArgs,
            Token = token,
            CancellationTokenSource = cts
        };

        Assert.Same(messageArgs, args.Args);
        Assert.Equal(token, args.Token);
        Assert.Same(cts, args.CancellationTokenSource);
    }
}