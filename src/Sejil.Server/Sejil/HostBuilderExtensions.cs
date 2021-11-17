// Copyright (C) 2017 Alaa Masoud
// See the LICENSE file in the project root for more information.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sejil.Configuration.Internal;
using Sejil.Data.Internal;
using Sejil.Logging;
using Sejil.Routing.Internal;
using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace Microsoft.AspNetCore.Hosting;

public static partial class IHostBuilderExtensions
{
    /// <summary>
    /// Configures Sejil logging.
    /// </summary>
    /// <param name="builder">The host builder to configure.</param>
    /// <param name="url">The URL at which Sejil should be available. Defaults to '/sejil'.</param>
    /// <param name="minLogLevel">The minimum log level.</param>
    /// <param name="writeToProviders">
    /// By default, Serilog does not write events to Microsoft.Extensions.Logging.ILoggerProviders
    /// registered through the Microsoft.Extensions.Logging API. Specify true to write events to
    /// all providers.
    /// </param>
    /// <param name="sinks">
    /// Configures additional sinks that log events will be emitted to. Use this to enable other serilog providers:
    /// <code>
    /// UseSejil(sinks: sinks =>
    ///     {
    ///         sinks.Console();
    ///         sinks.ElmahIo(new ElmahIoSinkOptions("API_KEY", new Guid("LOG_ID")));
    ///         // ...
    ///     });
    /// </code>
    /// </param>
    /// <returns>The host builder.</returns>
    public static IHostBuilder UseSejil(
        this IHostBuilder builder,
        string url = "/sejil",
        LogLevel minLogLevel = LogLevel.Information,
        bool writeToProviders = false,
        Action<LoggerSinkConfiguration>? sinks = null,
        Action<ISejilSettings>? setupAction = null) => UseSejil(
            builder,
            new Uri(url, UriKind.Relative),
            minLogLevel,
            writeToProviders,
            sinks,
            setupAction);

    private static IHostBuilder UseSejil(
        this IHostBuilder builder,
        Uri uri,
        LogLevel minLogLevel = LogLevel.Information,
        bool writeToProviders = false,
        Action<LoggerSinkConfiguration>? sinks = null,
        Action<ISejilSettings>? setupAction = null)
    {
        var settings = new SejilSettings(uri, MapSerilogLogLevel(minLogLevel));
        setupAction?.Invoke(settings);

        return builder
            .UseSerilog((context, cfg) =>
            {
                cfg
                    .Enrich.FromLogContext()
                    .ReadFrom.Configuration(context.Configuration)
                    .MinimumLevel.ControlledBy(settings.LoggingLevelSwitch)
                    .WriteTo.Sejil(settings);
                sinks?.Invoke(cfg.WriteTo);
            }, writeToProviders: writeToProviders)
            .ConfigureServices((_, services) =>
            {
                services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
                services.AddSingleton<ISejilSettings>(settings);
                services.AddScoped<ISejilRepository, SejilRepository>();
                services.AddScoped<ISejilSqlProvider, SejilSqlProvider>();
                services.AddScoped<ISejilController, SejilController>();
            });
    }

    internal static LogEventLevel MapSerilogLogLevel(LogLevel logLevel)
    {
        if (logLevel == LogLevel.None)
        {
            throw new InvalidOperationException("Minimum log level cannot be set to None.");
        }

        return (LogEventLevel)(int)logLevel;
    }
}
