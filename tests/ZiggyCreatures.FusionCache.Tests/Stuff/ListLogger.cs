﻿using Microsoft.Extensions.Logging;

namespace FusionCacheTests.Stuff;

public class ListLogger<T>
	: ILogger<T>
{
	internal class Scope : IDisposable
	{
		public void Dispose()
		{
			// EMPTY
		}
	}

	private readonly LogLevel _minLogLevel;
	public readonly List<(LogLevel LogLevel, string Message)> Items = new();

	public ListLogger(LogLevel minLogLevel)
	{
		_minLogLevel = minLogLevel;
	}

	public ListLogger()
		: this(LogLevel.Trace)
	{
		// EMPTY
	}

	public IDisposable BeginScope<TState>(TState state)
		where TState : notnull
	{
		return new Scope();
	}

	public bool IsEnabled(LogLevel logLevel)
	{
		return logLevel >= _minLogLevel;
	}

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		Items.Add((logLevel, formatter(state, exception)));
	}
}
