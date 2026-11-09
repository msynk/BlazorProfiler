using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using BlazorProfiler.Collectors;
using BlazorProfiler.Correlation;
using BlazorProfiler.Events;

namespace BlazorProfiler.Tests;

// The Entity Framework Core listener is process-wide, so these tests must not run in parallel with each other.
[Collection(nameof(DatabaseCollectorTests))]
public class DatabaseCollectorTests
{
    [Theory]
    [InlineData("SELECT * FROM [Orders] WHERE [Id] = 42", "SELECT * FROM [Orders] WHERE [Id] = ?")]
    [InlineData("SELECT  *\n  FROM t0 WHERE Name = N'O''Brien' AND x > 1.5e3", "SELECT * FROM t0 WHERE Name = ? AND x > ?")]
    [InlineData("SELECT TOP(@__p_0) [p].[Id1] FROM \"Table 2\" AS p WHERE p.Code = 'A1'", "SELECT TOP(@__p_0) [p].[Id1] FROM \"Table 2\" AS p WHERE p.Code = ?")]
    [InlineData("INSERT INTO T (A) VALUES (0x1F, -7)", "INSERT INTO T (A) VALUES (?, -?)")]
    [InlineData("", "")]
    public void NormalizesLiteralsButKeepsIdentifiers(string sql, string expected)
    {
        Assert.Equal(expected, DatabaseCollector.Normalize(sql));
    }

    [Fact]
    public void TruncatesLongCommandText()
    {
        Assert.Equal(100, DatabaseCollector.Normalize(new string('a', 500), 100).Length);
    }

    [Fact]
    public async Task RecordsEntityFrameworkCommandsWithTimingCorrelationAndHashes()
    {
        await using var f = ProfilerFixture.Create();
        using var listener = new DiagnosticListener(DatabaseCollector.ListenerName);
        var context = new FakeDbContext();

        var previous = ProfilerContext.Push(ProfilerContext.CreateComponentFrame("App.Orders", 5, LifecycleMethod.OnInitializedAsync, 0));
        try
        {
            RunReader(listener, context, "SELECT * FROM Lines WHERE OrderId = @p0", 1, rows: 3);
            RunReader(listener, context, "SELECT * FROM Lines WHERE OrderId = @p0", 2, rows: 4);
            RunReader(listener, context, "SELECT * FROM Lines WHERE OrderId = @p0", 2, rows: 4);
        }
        finally
        {
            ProfilerContext.Restore(previous);
        }

        var command = new FakeDbCommand { CommandText = "UPDATE T SET A = 1" };
        var id = Guid.NewGuid();
        listener.Write("Microsoft.EntityFrameworkCore.Database.Command.CommandExecuting", new CommandPayload(command, id, "ExecuteNonQuery", false, context));
        listener.Write("Microsoft.EntityFrameworkCore.Database.Command.CommandError", new ErrorPayload(command, id, new InvalidOperationException("boom"), context));
        await f.Host.FlushAsync();

        var commands = f.All<DatabaseCommandEvent>();
        Assert.Equal(4, commands.Count);
        var reads = commands.Where(c => c.ExecuteMethod == "Reader").ToList();
        Assert.Equal(3, reads.Count);
        Assert.All(reads, r => Assert.Equal("App.Orders", r.OriginComponentType));
        Assert.All(reads, r => Assert.Equal(LifecycleMethod.OnInitializedAsync, r.OriginLifecycleMethod));
        Assert.All(reads, r => Assert.True(r.IsAsync));
        Assert.Single(reads.Select(r => r.CommandHash).Distinct());
        Assert.Equal(2, reads.Select(r => r.ParameterHash).Distinct().Count());
        Assert.Equal([3, 4, 4], reads.Select(r => r.RowsRead ?? -1));
        Assert.All(commands, c => Assert.Equal(typeof(FakeDbContext).FullName, c.ContextType));
        Assert.Single(commands.Select(c => c.ContextInstanceId).Distinct());
        Assert.Equal("FakeDbConnection", reads[0].Provider);

        var failed = Assert.Single(commands, c => c.Outcome == ProfilerOutcome.Error);
        Assert.Equal(typeof(InvalidOperationException).FullName, failed.ErrorType);
        Assert.Equal("UPDATE T SET A = ?", failed.CommandText);

        var summary = f.Session.GetSummary();
        var query = Assert.Single(summary.Queries, q => q.CommandText!.StartsWith("SELECT", StringComparison.Ordinal));
        Assert.Equal(3, query.Durations.Count);
        Assert.Equal(11, query.RowsRead);
    }

    [Fact]
    public async Task IgnoresOtherListenersAndStopsWhenCollectionStops()
    {
        await using var f = ProfilerFixture.Create(o => o.Collectors.Database = false);
        using var listener = new DiagnosticListener(DatabaseCollector.ListenerName);
        RunReader(listener, new FakeDbContext(), "SELECT 1", 1, rows: 1);
        await f.Host.FlushAsync();
        Assert.Empty(f.All<DatabaseCommandEvent>());
    }

    private static void RunReader(DiagnosticListener listener, object context, string sql, int argument, int rows)
    {
        var command = new FakeDbCommand { CommandText = sql };
        command.Parameters.Add(new FakeDbParameter { ParameterName = "@p0", Value = argument });
        var id = Guid.NewGuid();
        listener.Write("Microsoft.EntityFrameworkCore.Database.Command.CommandExecuting", new CommandPayload(command, id, "ExecuteReader", true, context));
        listener.Write("Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted", new ExecutedPayload(command, id, "ExecuteReader", true, context, new FakeReader()));
        listener.Write("Microsoft.EntityFrameworkCore.Database.Command.DataReaderDisposing", new ReaderDisposingPayload(command, id, rows, context));
    }

    // Payload shapes mirror Entity Framework Core's event data types (properties are read by name).
    internal sealed record CommandPayload(DbCommand Command, Guid CommandId, string ExecuteMethod, bool IsAsync, object Context);

    internal sealed record ExecutedPayload(DbCommand Command, Guid CommandId, string ExecuteMethod, bool IsAsync, object Context, object Result);

    internal sealed record ReaderDisposingPayload(DbCommand Command, Guid CommandId, int ReadCount, object Context);

    internal sealed record ErrorPayload(DbCommand Command, Guid CommandId, Exception Exception, object Context);

    internal sealed class FakeDbContext;

    internal sealed class FakeDbConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "test";

        public override string DataSource => "test";

        public override string ServerVersion => "1";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new FakeDbCommand();
    }

    internal sealed class FakeDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; }

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        public override object? Value { get; set; }

        public override bool SourceColumnNullMapping { get; set; }

        public override int Size { get; set; }

        public override void ResetDbType()
        {
        }
    }

    internal sealed class FakeParameters : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];

        public override int Count => _items.Count;

        public override object SyncRoot => _items;

        public override int Add(object value)
        {
            _items.Add((DbParameter)value);
            return _items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
            {
                Add(value!);
            }
        }

        public override void Clear() => _items.Clear();

        public override bool Contains(object value) => _items.Contains((DbParameter)value);

        public override bool Contains(string value) => _items.Exists(p => p.ParameterName == value);

        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);

        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();

        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);

        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);

        public override void Remove(object value) => _items.Remove((DbParameter)value);

        public override void RemoveAt(int index) => _items.RemoveAt(index);

        public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));

        protected override DbParameter GetParameter(int index) => _items[index];

        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
    }

    internal sealed class FakeDbCommand : DbCommand
    {
        private readonly FakeParameters _parameters = new();

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; } = new FakeDbConnection();

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() => 0;

        public override object? ExecuteScalar() => null;

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => new FakeDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new FakeReader();
    }

    internal sealed class FakeReader : DbDataReader
    {
        public override int Depth => 0;

        public override int FieldCount => 0;

        public override bool HasRows => false;

        public override bool IsClosed => false;

        public override int RecordsAffected => 0;

        public override object this[int ordinal] => throw new NotSupportedException();

        public override object this[string name] => throw new NotSupportedException();

        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();

        public override byte GetByte(int ordinal) => throw new NotSupportedException();

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

        public override char GetChar(int ordinal) => throw new NotSupportedException();

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

        public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();

        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();

        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();

        public override double GetDouble(int ordinal) => throw new NotSupportedException();

        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();

        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
        public override Type GetFieldType(int ordinal) => throw new NotSupportedException();

        public override float GetFloat(int ordinal) => throw new NotSupportedException();

        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

        public override short GetInt16(int ordinal) => throw new NotSupportedException();

        public override int GetInt32(int ordinal) => throw new NotSupportedException();

        public override long GetInt64(int ordinal) => throw new NotSupportedException();

        public override string GetName(int ordinal) => throw new NotSupportedException();

        public override int GetOrdinal(string name) => throw new NotSupportedException();

        public override string GetString(int ordinal) => throw new NotSupportedException();

        public override object GetValue(int ordinal) => throw new NotSupportedException();

        public override int GetValues(object[] values) => 0;

        public override bool IsDBNull(int ordinal) => false;

        public override bool NextResult() => false;

        public override bool Read() => false;
    }
}
