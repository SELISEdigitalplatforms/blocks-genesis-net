#pragma warning disable CA1707, CA2007
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Lookups;
using Blocks.Genesis.Entities.Data;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace XUnitTest.DataAccess.Lookups;

/// <summary>Schemas, policies, a recording runner and an event-recording logger for the lookup resolver tests.</summary>
internal static class AccessLookupFixtures
{
    public static SchemaDefinitionExtended Employee(params DataAccessPolicy[] policies)
    {
        var schema = DataAccessTestSupport.Schema("Employee", read: SchemaAccessLevel.Custom, fields:
        [
            DataAccessTestSupport.Field("UserId"),
            DataAccessTestSupport.Field("DepartmentId"),
            DataAccessTestSupport.Field("Name"),
            DataAccessTestSupport.Field("Age", "Int"),
        ], policies: policies.ToList());
        schema.ItemId = "schema-employee";
        return schema;
    }

    public static SchemaDefinitionExtended Department()
    {
        var schema = DataAccessTestSupport.Schema("Department", fields:
        [
            DataAccessTestSupport.Field("Name"),
            DataAccessTestSupport.Field("Code", "Int"),
            DataAccessTestSupport.Field("MemberUserIds", isArray: true),
        ]);
        schema.ItemId = "schema-department";
        return schema;
    }

    public static SchemaDefinitionExtended SystemSchema(string name, params string[] fields)
    {
        var schema = DataAccessTestSupport.Schema(name, fields: fields.Select(f => DataAccessTestSupport.Field(f)).ToList());
        schema.ItemId = "schema-" + name;
        schema.IsSystemEntity = true;
        return schema;
    }

    public static SchemaDefinitionExtended User(params DataAccessPolicy[] policies)
    {
        var schema = SystemSchema("User", "Email", "FirstName", "Password");
        schema.Policies = policies.ToList();
        return schema;
    }

    public static PolicyRule WhereAuth(string field, string auth) =>
        DataAccessTestSupport.Rule(ConditionSource.SCHEMA_FIELD, field, PolicyOperator.EQUAL, ConditionSource.AUTH, auth);

    public static PolicyRule WhereStatic(string field, object value) =>
        DataAccessTestSupport.Rule(ConditionSource.SCHEMA_FIELD, field, PolicyOperator.EQUAL, ConditionSource.STATIC_VALUE, staticValue: value);

    public static PolicyRule Lookup(string left, string schemaName, string fieldName, PolicyOperator op = PolicyOperator.EXISTS_IN, params PolicyRule[] where) => new()
    {
        LeftSource = ConditionSource.SCHEMA_FIELD,
        LeftOperand = left,
        Operator = op,
        Reference = new PolicyReference
        {
            SchemaName = schemaName,
            FieldName = fieldName,
            Filter = where.Length == 0 ? null : DataAccessTestSupport.Group(PolicyLogicalOperator.AND, where),
        },
    };

    /// <summary>Spec section 1: DepartmentId EXISTS_IN Employee.DepartmentId WHERE UserId = Auth.UserId.</summary>
    public static PolicyRule MyDepartment(PolicyOperator op = PolicyOperator.EXISTS_IN) =>
        Lookup("DepartmentId", "Employee", "DepartmentId", op, WhereAuth("UserId", "UserId"));

    public static DataAccessPolicy Rls(PolicyOperation operation, params PolicyRule[] rules)
    {
        var policy = DataAccessTestSupport.Policy(PolicyType.RLS, operation, [], DataAccessTestSupport.Group(PolicyLogicalOperator.AND, rules));
        policy.ItemId = Guid.NewGuid().ToString();
        return policy;
    }

    public static DataAccessPolicy RoleRead(string role)
    {
        var policy = DataAccessTestSupport.RolePolicy(PolicyOperation.READ, role);
        policy.ItemId = Guid.NewGuid().ToString();
        return policy;
    }
}

/// <summary>An <see cref="IAccessLookupRunner"/> that records every query and answers from a function.</summary>
internal sealed class RecordingRunner(Func<AccessLookupQuery, IReadOnlyList<BsonValue>>? answer = null) : IAccessLookupRunner
{
    public List<AccessLookupQuery> Calls { get; } = [];

    public RecordingRunner(params BsonValue[] values) : this(_ => values) { }

    public Task<IReadOnlyList<BsonValue>> RunAsync(AccessLookupQuery query, CancellationToken cancellationToken)
    {
        Calls.Add(query);
        return Task.FromResult(answer?.Invoke(query) ?? []);
    }
}

internal sealed record EventEntry(LogLevel Level, EventId EventId, string Message);

/// <summary>Captures the event id with each log call (the shared RecordingLogger doesn't).</summary>
internal sealed class EventLogger<T> : ILogger<T>
{
    public List<EventEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add(new EventEntry(logLevel, eventId, formatter(state, exception)));

    public IReadOnlyList<EventEntry> Named(string name) => Entries.Where(e => e.EventId.Name == name).ToList();

    public string AllText() => string.Join("\n", Entries.Select(e => e.Message));
}
