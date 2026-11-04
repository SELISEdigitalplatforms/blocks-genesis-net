using Blocks.Genesis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using XUnitTest.TestDoubles;

namespace XUnitTest.Actions;

[Collection("BlocksAuthStaticState")]
public sealed class ActionPublisherTests : IDisposable
{
    private const string Tenant = "T1";
    private const string ActionName = "iam-user-created";
    private const string Marker = "DATA-MARKER-5b1e";
    private static readonly string[] Roles = ["admin"];

    private readonly Mock<IKeyValueStore> _store = new();
    private readonly Mock<IMessageClient> _client = new();
    private readonly List<ConsumerMessage<BlocksAction>> _sent = [];
    private readonly ManualClock _clock = new();
    private readonly RecordingLogger<ActionPublisher> _logger = new();
    private readonly ActivitySource _activitySource = new("action-publisher-tests-" + Guid.NewGuid().ToString("N"));

    public ActionPublisherTests()
    {
        _client
            .Setup(c => c.SendToConsumerAsync(It.IsAny<ConsumerMessage<BlocksAction>>()))
            .Callback<ConsumerMessage<BlocksAction>>(_sent.Add)
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _activitySource.Dispose();

    private static ActionDefinition Definition(bool workflow = false, bool function = false, bool webhook = false) => new()
    {
        Name = ActionName,
        Label = "User created",
        Service = "iam",
        Payload = new Dictionary<string, string> { ["userId"] = "string", ["roles"] = "string[]", ["Self"] = "object" },
        Workflow = workflow,
        Function = function,
        Webhook = webhook
    };

    private void StoreReturns(params ActionDefinition[] definitions) =>
        _store
            .Setup(s => s.GetAsync<List<ActionDefinition>>(ActionConstants.ActionsKey, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(definitions.ToList());

    private static BlocksContext Context(string tenantId = Tenant, string organizationId = "ctx-org") =>
        BlocksContext.Create(tenantId, [], "user-1", true, "", organizationId, DateTime.MinValue, "", [], "", "", "", "", tenantId);

    private ActionPublisher Publisher() => new(_store.Object, _client.Object, _activitySource, _clock, _logger);

    private async Task Publish(BlocksContext? context, object? data, string? organizationId = null, string action = ActionName)
    {
        BlocksContext.SetContext(context);
        try
        {
            await Publisher().PublishAsync(action, data, organizationId);
        }
        finally
        {
            BlocksContext.SetContext(null);
        }
    }

    private BlocksAction SingleSent() => Assert.Single(_sent).Payload;

    [Fact]
    public async Task NoTenant_SendsNothing_AndDoesNotReadTheList()
    {
        StoreReturns(Definition(workflow: true));

        await Publish(null, new { userId = "u" });

        Assert.Empty(_sent);
        _store.VerifyNoOtherCalls();
        Assert.Single(_logger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task ListReadFailure_SendsNothing_AndLogsTheTypeOnly()
    {
        _store
            .Setup(s => s.GetAsync<List<ActionDefinition>>(ActionConstants.ActionsKey, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("read broke " + Marker));

        await Publish(Context(), new { userId = "u" });

        Assert.Empty(_sent);
        var warning = Assert.Single(_logger.At(LogLevel.Warning));
        Assert.Contains(nameof(InvalidOperationException), warning.Message);
        Assert.DoesNotContain(Marker, _logger.AllText());
    }

    [Fact]
    public async Task ListReadTimeout_SendsNothing_AfterTwoSeconds()
    {
        var neverEnds = new TaskCompletionSource<List<ActionDefinition>?>();
        _store
            .Setup(s => s.GetAsync<List<ActionDefinition>>(ActionConstants.ActionsKey, true, It.IsAny<CancellationToken>()))
            .Returns(neverEnds.Task);

        BlocksContext.SetContext(Context());
        Task publish;
        try
        {
            publish = Publisher().PublishAsync(ActionName, new { userId = "u" });
        }
        finally
        {
            BlocksContext.SetContext(null);
        }

        Assert.False(publish.IsCompleted);
        _clock.Advance(ActionPublisher.ReadTimeout);
        await publish.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(_sent);
        Assert.Contains(nameof(TimeoutException), Assert.Single(_logger.At(LogLevel.Warning)).Message);
    }

    [Fact]
    public async Task UnknownAction_SendsNothing()
    {
        StoreReturns(Definition(workflow: true));

        await Publish(Context(), new { userId = "u" }, action: "iam-user-deleted");

        Assert.Empty(_sent);
        Assert.Single(_logger.At(LogLevel.Information));
    }

    [Fact]
    public async Task ActionNames_AreMatchedCaseSensitively()
    {
        StoreReturns(Definition(workflow: true));

        await Publish(Context(), new { userId = "u" }, action: ActionName.ToUpperInvariant());

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task AllTargetsOff_SendsNothing()
    {
        StoreReturns(Definition());

        await Publish(Context(), new { userId = "u" });

        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData(true, false, false, ActionConstants.WorkflowQueue)]
    [InlineData(false, true, false, ActionConstants.FunctionQueue)]
    [InlineData(false, false, true, ActionConstants.WebhookQueue)]
    public async Task EachTarget_RoutesToItsQueue(bool workflow, bool function, bool webhook, string queue)
    {
        StoreReturns(Definition(workflow, function, webhook));

        await Publish(Context(), new { userId = "u" });

        Assert.Equal(queue, Assert.Single(_sent).ConsumerName);
    }

    [Fact]
    public async Task Body_CarriesIdNameTimeAndOrganization()
    {
        StoreReturns(Definition(workflow: true));

        await Publish(Context(), new { userId = "u" });

        var body = SingleSent();
        Assert.Matches("^evt_[0-9a-f]{32}$", body.Id);
        Assert.Equal(ActionName, body.Action);
        Assert.Equal(_clock.Now.UtcDateTime, body.Time);
        Assert.Equal("ctx-org", body.OrganizationId);
    }

    [Fact]
    public async Task TwoTargets_SendTwoMessages_WithOneEventId()
    {
        StoreReturns(Definition(workflow: true, webhook: true));

        await Publish(Context(), new { userId = "u" });

        Assert.Equal([ActionConstants.WorkflowQueue, ActionConstants.WebhookQueue], _sent.Select(m => m.ConsumerName));
        Assert.Same(_sent[0].Payload, _sent[1].Payload);
    }

    [Fact]
    public async Task DataFieldsNotInThePayload_AreDropped()
    {
        StoreReturns(Definition(workflow: true));

        var data = new Dictionary<string, object>
        {
            ["userId"] = "u",
            ["roles"] = Roles,
            ["password"] = Marker,
            ["Userid"] = "wrong-case"
        };

        await Publish(Context(), data);

        Assert.Equal("""{"userId":"u","roles":["admin"]}""", SingleSent().DataJson);
    }

    [Fact]
    public async Task TypedData_IsMatchedByCamelCaseNames()
    {
        StoreReturns(Definition(workflow: true));

        await Publish(Context(), new TypedUser { UserId = "u", Roles = Roles, Password = Marker });

        Assert.Equal("""{"userId":"u","roles":["admin"]}""", SingleSent().DataJson);
    }

    [Fact]
    public async Task NoMessageBus_SendsNothing_AndDoesNotReadTheList()
    {
        StoreReturns(Definition(workflow: true));
        BlocksContext.SetContext(Context());
        try
        {
            await new ActionPublisher(_store.Object, null, _activitySource, _clock, _logger).PublishAsync(ActionName, new { userId = "u" });
        }
        finally
        {
            BlocksContext.SetContext(null);
        }

        _store.Verify(s => s.GetAsync<List<ActionDefinition>>(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NullData_SendsAnEmptyObject()
    {
        StoreReturns(Definition(workflow: true));

        await Publish(Context(), null);

        Assert.Equal("{}", SingleSent().DataJson);
    }

    [Theory]
    [InlineData("ctx-org", null, "ctx-org")]
    [InlineData("ctx-org", "body-org", "body-org")]
    [InlineData("", null, null)]
    public async Task Organization_ComesFromTheContextUnlessGiven(string contextOrg, string? givenOrg, string? expected)
    {
        StoreReturns(Definition(workflow: true));

        await Publish(Context(organizationId: contextOrg), new { userId = "u" }, givenOrg);

        Assert.Equal(expected, SingleSent().OrganizationId);
    }

    [Fact]
    public async Task OversizeAction_IsDropped()
    {
        StoreReturns(Definition(workflow: true));

        await Publish(Context(), new { userId = new string('x', ActionConstants.MaxMessageBytes) });

        Assert.Empty(_sent);
        Assert.Single(_logger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task FailedSend_DoesNotStopTheNextTarget()
    {
        StoreReturns(Definition(workflow: true, function: true));
        _client
            .Setup(c => c.SendToConsumerAsync(It.Is<ConsumerMessage<BlocksAction>>(m => m.ConsumerName == ActionConstants.WorkflowQueue)))
            .ThrowsAsync(new InvalidOperationException("bus down " + Marker));

        await Publish(Context(), new { userId = Marker });

        Assert.Equal(ActionConstants.FunctionQueue, Assert.Single(_sent).ConsumerName);
        Assert.Single(_logger.At(LogLevel.Warning));
        Assert.DoesNotContain(Marker, _logger.AllText());
    }

    [Fact]
    public async Task DataThatCannotBeSerialized_IsNotPublished_AndDoesNotThrow()
    {
        StoreReturns(Definition(workflow: true));

        var loop = new SelfReference();
        loop.Self = loop;

        await Publish(Context(), loop);

        Assert.Empty(_sent);
        Assert.DoesNotContain(Marker, _logger.AllText());
    }

    [Fact]
    public async Task Trace_TagsTheOutcome_WithoutData()
    {
        StoreReturns(Definition(workflow: true));
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == _activitySource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);

        await Publish(Context(), new { userId = Marker });

        var activity = Assert.Single(stopped);
        Assert.Equal(ActionPublisher.ActivityName, activity.OperationName);
        Assert.Equal(ActionName, activity.GetTagItem(ActionPublisher.NameTag));
        Assert.Equal(SingleSent().Id, activity.GetTagItem(ActionPublisher.EventIdTag));
        Assert.Equal(ActionPublisher.OutcomeSent, activity.GetTagItem(ActionPublisher.OutcomeTag));
        Assert.DoesNotContain(activity.TagObjects, t => t.Value?.ToString()?.Contains(Marker) == true);
    }

    [Fact]
    public void AddActions_RegistersThePublisher()
    {
        var services = new ServiceCollection()
            .AddSingleton(_store.Object)
            .AddSingleton(_client.Object)
            .AddSingleton(_activitySource)
            .AddLogging()
            .AddActions();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<ActionPublisher>(provider.GetRequiredService<IActionPublisher>());
    }

    [Fact]
    public void AddActions_WorksInAHostWithoutAMessageBus()
    {
        var services = new ServiceCollection()
            .AddSingleton(_store.Object)
            .AddSingleton(_activitySource)
            .AddLogging()
            .AddActions();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<ActionPublisher>(provider.GetRequiredService<IActionPublisher>());
    }

    private sealed class TypedUser
    {
        public string UserId { get; set; } = "";
        public string[] Roles { get; set; } = [];
        public string Password { get; set; } = "";
    }

    // System.Text.Json refuses a reference cycle.
    private sealed class SelfReference
    {
        public SelfReference? Self { get; set; }
        public string Note { get; set; } = Marker;
    }
}
