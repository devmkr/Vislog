using Sejil.Configuration;
using Sejil.Models;
using Sejil.Sqlite.Data;
using Sejil.Sqlite.Data.Query;
using Serilog;
using Serilog.Events;

namespace Sejil.Sqlite.Test;

public class SqliteSejilRepositoryTests
{
    [Fact]
    public async Task AllTests()
    {
        var connStr = $"DataSource={Guid.NewGuid()}";
        var repository = new SqliteSejilRepository(new SejilSettings("/sejil", default) { CodeGeneratorType = typeof(SqliteCodeGenerator) }, connStr);

        await repository.InsertEventsAsync(GetTestEvents());

        await repository.SaveQueryAsync(new LogQuery { Name = "TestName", Query = "TestQuery" });
        var savedQuery = Assert.Single(await repository.GetSavedQueriesAsync());
        Assert.Equal("TestName", savedQuery.Name);
        Assert.Equal("TestQuery", savedQuery.Query);

        await AssertFiltersByLevel(repository);
        await AssertFiltersByExceptionOnly(repository);
        await AssertFiltersByDate(repository);
        await AssertFiltersByDateRange(repository);
        await AssertFiltersByQuery(repository);
    }

    private static async Task AssertFiltersByLevel(SqliteSejilRepository repository)
    {
        var e = Assert.Single(await repository.GetEventsPageAsync(1, null, new LogQueryFilter { LevelFilter = "Debug" }));
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2017, 8, 3, 11, 5, 5, 5, DateTimeKind.Local)), e.Timestamp);
        Assert.Equal("Debug", e.Level);
        Assert.Equal("Object is \"{ Id = 5, Name = Test Object }\"", e.Message);
        Assert.Null(e.Exception);
    }

    private static async Task AssertFiltersByDate(SqliteSejilRepository repository)
        => Assert.Empty(await repository.GetEventsPageAsync(1, null, new LogQueryFilter { DateFilter = "5m" }));

    private static async Task AssertFiltersByDateRange(SqliteSejilRepository repository)
    {
        var start = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2017, 8, 3, 12, 0, 0, DateTimeKind.Local));
        var end = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2017, 8, 3, 12, 10, 0, DateTimeKind.Local));

        var e = Assert.Single(await repository.GetEventsPageAsync(1, null, new LogQueryFilter { DateRangeFilter = new List<DateTime> { start, end } }));
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2017, 8, 3, 12, 5, 5, 5, DateTimeKind.Local)), e.Timestamp);
        Assert.Equal("Warning", e.Level);
        Assert.Equal("This is a warning with value: null", e.Message);
        Assert.Null(e.Exception);
    }

    private static async Task AssertFiltersByExceptionOnly(SqliteSejilRepository repository)
    {
        var e = Assert.Single(await repository.GetEventsPageAsync(1, null, new LogQueryFilter { ExceptionsOnly = true }));
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2017, 8, 3, 13, 5, 5, 5, DateTimeKind.Local)), e.Timestamp);
        Assert.Equal("Error", e.Level);
        Assert.Equal("This is an exception", e.Message);
        Assert.Equal("System.Exception: Test exception", e.Exception);
    }

    private static async Task AssertFiltersByQuery(SqliteSejilRepository repository)
    {
        var e = Assert.Single(await repository.GetEventsPageAsync(1, null, new LogQueryFilter { QueryText = "name = 'test name'" }));
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2017, 8, 3, 10, 5, 5, 5, DateTimeKind.Local)), e.Timestamp);
        Assert.Equal("Information", e.Level);
        Assert.Equal("Name is \"Test name\" and Value is \"Test value\"", e.Message);
        Assert.Null(e.Exception);
    }

    private static IEnumerable<LogEvent> GetTestEvents() => new[]
    {
        BuildLogEvent(new DateTime(2017, 8, 3, 10, 5, 5, 5, DateTimeKind.Local), LogEventLevel.Information, null, "Name is {Name} and Value is {Value}", "Test name", "Test value"),
        BuildLogEvent(new DateTime(2017, 8, 3, 11, 5, 5, 5, DateTimeKind.Local), LogEventLevel.Debug, null, "Object is {Object}", new { Id = 5, Name = "Test Object" }),
        BuildLogEvent(new DateTime(2017, 8, 3, 12, 5, 5, 5, DateTimeKind.Local), LogEventLevel.Warning, null, "This is a warning with value: {Value}", (string)null),
        BuildLogEvent(new DateTime(2017, 8, 3, 13, 5, 5, 5, DateTimeKind.Local), LogEventLevel.Error, new Exception("Test exception"), "This is an exception"),
    };

    private static LogEvent BuildLogEvent(DateTime timestamp, LogEventLevel level, Exception ex, string messageTemplate, params object[] propertyValues)
    {
        var logger = new LoggerConfiguration().CreateLogger();
        logger.BindMessageTemplate(messageTemplate, propertyValues, out var parsedTemplate, out var boundProperties);
        return new LogEvent(timestamp, level, ex, parsedTemplate, boundProperties);
    }
}