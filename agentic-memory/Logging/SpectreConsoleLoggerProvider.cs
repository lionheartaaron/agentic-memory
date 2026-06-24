using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Collections.Concurrent;

namespace AgenticMemory.Logging;

[ProviderAlias("SpectreConsole")]
public sealed class SpectreConsoleLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, SpectreConsoleLogger> _loggers =
        new(StringComparer.OrdinalIgnoreCase);

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, static name => new SpectreConsoleLogger(name));

    public void Dispose() => _loggers.Clear();
}

internal sealed class SpectreConsoleLogger : ILogger
{
    private readonly string _category;

    internal SpectreConsoleLogger(string category) => _category = category;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception == null) return;

        var (style, prefix) = logLevel switch
        {
            LogLevel.Trace =>       ("grey dim", "trc"),
            LogLevel.Debug =>       ("grey",     "dbg"),
            LogLevel.Information => ("blue dim", "inf"),
            LogLevel.Warning =>     ("yellow",   "wrn"),
            LogLevel.Error =>       ("red",      "err"),
            LogLevel.Critical =>    ("bold red", "crt"),
            _ =>                    ("white",    "???"),
        };

        AnsiConsole.MarkupLine(
            $"[{style}]{prefix}[/] [grey dim]{Markup.Escape(ShortCategory(_category))}[/] {Markup.Escape(message)}");

        if (exception != null)
            AnsiConsole.MarkupLine($"  [red dim]{Markup.Escape(exception.ToString())}[/]");
    }

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }
}
