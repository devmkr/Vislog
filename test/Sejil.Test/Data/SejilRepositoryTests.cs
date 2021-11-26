// Copyright (C) 2017 Alaa Masoud
// See the LICENSE file in the project root for more information.

using Sejil.Data;
using Sejil.Models;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;

namespace Sejil.Test.Data;

public partial class SejilRepositoryTests
{
    [Fact]
    public void Ctor_throws_when_null_settings()
    {
        var ex = Assert.ThrowsAny<Exception>(() => new Mock<SejilRepository>(null, "").Object);
        var ane = Assert.IsType<ArgumentNullException>(ex.InnerException);
        Assert.Equal("settings", ane.ParamName);
    }

    [Fact]
    public void Ctor_throws_when_null_connectionString()
    {
        var ex = Assert.ThrowsAny<Exception>(() => new Mock<SejilRepository>(Mocks.GetTestSettings(), null).Object);
        var ane = Assert.IsType<ArgumentNullException>(ex.InnerException);
        Assert.Equal("connectionString", ane.ParamName);
    }

    [Fact]
    public async Task SaveQueryAsync_saves_query()
    {
        // Arrange
        var repository = new SejilRepositoryMoq(Mocks.GetTestSettings());

        var logQuery = new LogQuery
        {
            Id = 1,
            Name = "Test",
            Query = "q"
        };

        // Act
        var result = await repository.SaveQueryAsync(logQuery);

        // Assert
        Assert.True(result);
        var savedQueries = await repository.GetSavedQueriesAsync();
        Assert.Single(savedQueries);
        Assert.Equal(1, savedQueries.First().Id);
        Assert.Equal("Test", savedQueries.First().Name);
        Assert.Equal("q", savedQueries.First().Query);
    }

    [Fact]
    public async Task GetSavedQueriesAsync_returns_saved_queries()
    {
        // Arrange
        var repository = new SejilRepositoryMoq(Mocks.GetTestSettings());
        await repository.SaveQueryAsync(new LogQuery { Name = "Test1", Query = "q1" });
        await repository.SaveQueryAsync(new LogQuery { Name = "Test2", Query = "q2" });

        // Act
        var queries = await repository.GetSavedQueriesAsync();

        // Assert
        Assert.Equal(2, queries.Count());
        Assert.Equal(1, queries.First().Id);
        Assert.Equal("Test1", queries.First().Name);
        Assert.Equal("q1", queries.First().Query);
        Assert.Equal(2, queries.Skip(1).First().Id);
        Assert.Equal("Test2", queries.Skip(1).First().Name);
        Assert.Equal("q2", queries.Skip(1).First().Query);
    }

    [Fact]
    public async Task DeleteQueryAsync_deletes_specified_query()
    {
        // Arrange
        var repository = new SejilRepositoryMoq(Mocks.GetTestSettings());
        await repository.SaveQueryAsync(new LogQuery { Name = "Test1", Query = "q1" });

        // Act
        var result = await repository.DeleteQueryAsync("Test1");

        // Assert
        Assert.True(result);
        var queries = await repository.GetSavedQueriesAsync();
        Assert.Empty(queries);
    }

    [Fact]
    public async Task DeleteQueryAsync_returns_false_when_specified_query_does_not_exist()
    {
        // Arrange
        var repository = new SejilRepositoryMoq(Mocks.GetTestSettings());
        await repository.SaveQueryAsync(new LogQuery { Name = "Test1", Query = "q1" });

        // Act
        var result = await repository.DeleteQueryAsync("Test2");

        // Assert
        Assert.False(result);
        var queries = await repository.GetSavedQueriesAsync();
        Assert.Single(queries);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task GetEventsPageAsync_throws_when_page_arg_is_zero(int page)
    {
        // Arrange
        var repository = new SejilRepositoryMoq(Mocks.GetTestSettings());

        // Act & assert
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.GetEventsPageAsync(page, null, new LogQueryFilter()));
        Assert.Equal($"Argument must be greater than zero. (Parameter 'page')", ex.Message);
    }

    [Fact]
    public async Task GetEventsPageAsync_no_props_returns_events_page()
    {
        // Arrange
        var events = new List<LogEvent>();
        for (var i = 0; i < 10; i++)
        {
            var msgTemplate = new MessageTemplate(new[] { new TextToken($"{i}", 0) });
            events.Add(new LogEvent(DateTime.UtcNow.AddHours(i), LogEventLevel.Information, null, msgTemplate, Enumerable.Empty<LogEventProperty>()));
        }

        var repository = new SejilRepositoryMoq(Mocks.GetTestSettings(3));
        await repository.InsertEventsAsync(events);

        // Act
        var logs = await repository.GetEventsPageAsync(2, null, new LogQueryFilter());

        // Assert
        Assert.Equal(3, logs.Count());
        Assert.Equal("6", logs.ElementAt(0).Message);
        Assert.Empty(logs.ElementAt(0).Properties);
        Assert.Equal("5", logs.ElementAt(1).Message);
        Assert.Empty(logs.ElementAt(1).Properties);
        Assert.Equal("4", logs.ElementAt(2).Message);
        Assert.Empty(logs.ElementAt(2).Properties);
    }

    [Fact]
    public async Task GetEventsPageAsync_returns_events_page()
    {
        // Arrange
        var events = new List<LogEvent>();
        for (var i = 0; i < 10; i++)
        {
            var msgTemplate = new MessageTemplate(new[] { new PropertyToken("p1", "{p1}"), new PropertyToken("p2", "{p2}"), });
            var properties = new List<LogEventProperty>
            {
                new LogEventProperty("p1", new ScalarValue($"{i}_0")),
                new LogEventProperty("p2", new ScalarValue($"{i}_1")),
            };
            events.Add(new LogEvent(DateTime.UtcNow.AddHours(i), LogEventLevel.Information, null, msgTemplate, properties));
        }

        var repository = new SejilRepositoryMoq(Mocks.GetTestSettings(3));
        await repository.InsertEventsAsync(events);

        // Act
        var logs = await repository.GetEventsPageAsync(4, null, new LogQueryFilter());

        // Assert
        var log = Assert.Single(logs);
        Assert.Equal("\"0_0\"\"0_1\"", log.Message);
        Assert.NotNull(log.Properties);
        Assert.Equal(2, log.Properties.Count);
        Assert.Equal("0_0", log.Properties[0].Value);
        Assert.Equal("0_1", log.Properties[1].Value);
    }

    [Fact]
    public async Task InsertEventsAsync_inserts_events_to_database()
    {
        // Arrange
        var repository = new SejilRepositoryMoq(Mocks.GetTestSettings());

        // Hello, {name}. Your # is {number}
        var tokens = new List<MessageTemplateToken>
        {
            new TextToken("Hello, ", 0),
            new PropertyToken("name", "{name}"),
            new TextToken(". Your # is ", 13),
            new PropertyToken("number", "{number}"),
        };

        var properties = new List<LogEventProperty>
        {
            new LogEventProperty("name", new ScalarValue("world")),
            new LogEventProperty("number", new ScalarValue(null))
        };

        var messageTemplate = new MessageTemplate(tokens);

        var timestamp1 = new DateTime(2017, 8, 3, 11, 44, 15, 542, DateTimeKind.Local);
        var timestamp2 = new DateTime(2017, 9, 3, 11, 44, 15, 542, DateTimeKind.Local);

        var events = new List<LogEvent>
        {
            new LogEvent(timestamp1, LogEventLevel.Information, null, messageTemplate, properties),
            new LogEvent(timestamp2, LogEventLevel.Debug, new Exception("error"), messageTemplate, properties),
        };

        // Act
        await repository.InsertEventsAsync(events);

        // Assert
        var logEvents = await repository.GetEventsPageAsync(1, null, new LogQueryFilter());
        Assert.Equal(2, logEvents.Count());

        var logEvent1 = logEvents.FirstOrDefault(p => p.Level == "Information");
        Assert.Equal("Hello, \"world\". Your # is null", logEvent1.Message);
        Assert.Equal("Hello, {name}. Your # is {number}", logEvent1.MessageTemplate);
        Assert.Equal("Information", logEvent1.Level);
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(timestamp1), logEvent1.Timestamp);
        Assert.Null(logEvent1.Exception);
        Assert.Equal(2, logEvent1.Properties.Count);
        Assert.Equal(logEvent1.Id, logEvent1.Properties.ElementAt(0).LogId);
        Assert.Equal("name", logEvent1.Properties.ElementAt(0).Name);
        Assert.Equal("world", logEvent1.Properties.ElementAt(0).Value);
        Assert.Equal(logEvent1.Id, logEvent1.Properties.ElementAt(1).LogId);
        Assert.Equal("number", logEvent1.Properties.ElementAt(1).Name);
        Assert.Equal("null", logEvent1.Properties.ElementAt(1).Value);

        var logEvent2 = logEvents.FirstOrDefault(p => p.Level == "Debug");
        Assert.Equal("Hello, \"world\". Your # is null", logEvent2.Message);
        Assert.Equal("Hello, {name}. Your # is {number}", logEvent2.MessageTemplate);
        Assert.Equal("Debug", logEvent2.Level);
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(timestamp2), logEvent2.Timestamp);
        Assert.Equal("System.Exception: error", logEvent2.Exception);
        Assert.Equal(2, logEvent2.Properties.Count);
        Assert.Equal(logEvent2.Id, logEvent2.Properties.ElementAt(0).LogId);
        Assert.Equal("name", logEvent2.Properties.ElementAt(0).Name);
        Assert.Equal("world", logEvent2.Properties.ElementAt(0).Value);
        Assert.Equal(logEvent2.Id, logEvent2.Properties.ElementAt(1).LogId);
        Assert.Equal("number", logEvent2.Properties.ElementAt(1).Name);
        Assert.Equal("null", logEvent2.Properties.ElementAt(1).Value);
    }

    [Theory]
    [InlineData("RequestPath")]
    [InlineData("Path")]
    public async Task InsertEventsAsync_ignores_events_with_sejil_url_in_RequestPath_or_Path_properties(string propertyName)
    {
        // Arrange
        var repository = new SejilRepositoryMoq(Mocks.GetTestSettings());

        var tokens = new List<MessageTemplateToken>
        {
            new PropertyToken(propertyName, "{" + propertyName + "}"),
        };

        var properties = new List<LogEventProperty>
        {
            new LogEventProperty(propertyName, new ScalarValue("/sejil/events")),
        };

        var messageTemplate = new MessageTemplate(tokens);

        var events = new List<LogEvent>
        {
            new LogEvent(DateTime.Now, LogEventLevel.Information, null, messageTemplate, properties),
        };

        // Act
        await repository.InsertEventsAsync(events);

        // Assert
        var logEvents = await repository.GetEventsPageAsync(1, null, new LogQueryFilter());
        Assert.Empty(logEvents);
    }

    [Fact]
    public async Task Multiple_queries_no_conflict()
    {
        // Arrange
        var repository = new SejilRepositoryMoq(Mocks.GetTestSettings());
        await repository.InsertEventsAsync(new[]
        {
            BuildLogEvent(DateTime.UtcNow, LogEventLevel.Verbose, null, "Verbose"),
        });
        await repository.GetEventsPageAsync(1, null, new LogQueryFilter { QueryText = "@level='Verbose'" });

        // Act & assert no exception is thrown
        Assert.Single(await repository.GetEventsPageAsync(1, null, new LogQueryFilter { QueryText = "@level='Verbose'" }));
    }

    [Fact]
    public async Task CleanupAsync_test()
    {
        // Arrange
        var settings = Mocks.GetTestSettings()
            .AddRetentionPolicy(TimeSpan.FromHours(5), LogEventLevel.Verbose, LogEventLevel.Debug)
            .AddRetentionPolicy(TimeSpan.FromDays(10), LogEventLevel.Information)
            .AddRetentionPolicy(TimeSpan.FromDays(75));
        var repository = new SejilRepositoryMoq(settings);

        var now = DateTime.UtcNow;
        await repository.InsertEventsAsync(new[]
        {
            BuildLogEvent(now.AddHours(-5.1), LogEventLevel.Verbose, null, "Verbose #{Num}", 1),
            BuildLogEvent(now.AddHours(-5.1), LogEventLevel.Debug, null, "Debug #{Num}", 1),
            BuildLogEvent(now, LogEventLevel.Verbose, null, "Verbose #{Num}", 2),
            BuildLogEvent(now, LogEventLevel.Debug, null, "Debug #{Num}", 2),

            BuildLogEvent(now.AddDays(-10.1), LogEventLevel.Information, null, "Information #{Num}", 1),
            BuildLogEvent(now, LogEventLevel.Information, null, "Information #{Num}", 2),

            BuildLogEvent(now.AddDays(-75.1), LogEventLevel.Warning, null, "Warning #{Num}", 1),
            BuildLogEvent(now.AddDays(-75.1), LogEventLevel.Error, null, "Error #{Num}", 1),
            BuildLogEvent(now.AddDays(-75.1), LogEventLevel.Fatal, null, "Fatal #{Num}", 1),
            BuildLogEvent(now, LogEventLevel.Warning, null, "Warning #{Num}", 2),
            BuildLogEvent(now, LogEventLevel.Error, null, "Error #{Num}", 2),
            BuildLogEvent(now, LogEventLevel.Fatal, null, "Fatal #{Num}", 2),
        });

        // Act
        Assert.Equal(6, (await repository.GetEventsPageAsync(1, null, new LogQueryFilter { QueryText = "Num=1" })).Count());
        await repository.CleanupAsync();

        // Assert
        Assert.Empty(await repository.GetEventsPageAsync(1, null, new LogQueryFilter { QueryText = "Num=1" }));
        Assert.Equal(6, (await repository.GetEventsPageAsync(1, null, new LogQueryFilter { QueryText = "Num=2" })).Count());
    }

    private static LogEvent BuildLogEvent(DateTime timestamp, LogEventLevel level, Exception ex, string messageTemplate, params object[] propertyValues)
    {
        var logger = new LoggerConfiguration().CreateLogger();
        logger.BindMessageTemplate(messageTemplate, propertyValues, out var parsedTemplate, out var boundProperties);
        return new LogEvent(timestamp, level, ex, parsedTemplate, boundProperties);
    }
}
