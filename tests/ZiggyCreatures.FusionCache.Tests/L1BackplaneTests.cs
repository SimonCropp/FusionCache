﻿using System;
using System.Threading;
using System.Threading.Tasks;
using FusionCacheTests.Stuff;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane;
using ZiggyCreatures.Caching.Fusion.Backplane.Memory;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.Chaos;
using ZiggyCreatures.Caching.Fusion.DangerZone;
using ZiggyCreatures.Caching.Fusion.Internals;

namespace FusionCacheTests;

public class L1BackplaneTests
	: AbstractTests
{
	public L1BackplaneTests(ITestOutputHelper output)
		: base(output, "MyCache:")
	{
		if (UseRedis)
			InitialBackplaneDelay = TimeSpan.FromSeconds(5).PlusALittleBit();
	}

	private FusionCacheOptions CreateFusionCacheOptions()
	{
		var res = new FusionCacheOptions
		{
			WaitForInitialBackplaneSubscribe = true,
			CacheKeyPrefix = TestingCacheKeyPrefix,
			IncludeTagsInLogs = true,
		};

		return res;
	}

	private static readonly bool UseRedis = false;
	private static readonly string RedisConnection = "127.0.0.1:6379,ssl=False,abortConnect=false,connectTimeout=1000,syncTimeout=1000";

	private readonly TimeSpan InitialBackplaneDelay = TimeSpan.FromMilliseconds(300);
	private readonly TimeSpan MultiNodeOperationsDelay = TimeSpan.FromMilliseconds(300);

	private IFusionCacheBackplane CreateBackplane(string connectionId, ILogger? logger = null)
	{
		if (UseRedis)
			return new RedisBackplane(new RedisBackplaneOptions { Configuration = RedisConnection }, logger: (logger as ILogger<RedisBackplane>) ?? CreateXUnitLogger<RedisBackplane>());

		return new MemoryBackplane(new MemoryBackplaneOptions() { ConnectionId = connectionId }, logger: (logger as ILogger<MemoryBackplane>) ?? CreateXUnitLogger<MemoryBackplane>());
	}

	private FusionCache CreateFusionCache(string? cacheName, IFusionCacheBackplane? backplane, Action<FusionCacheOptions>? setupAction = null, IMemoryCache? memoryCache = null, string? cacheInstanceId = null, ILogger<FusionCache>? logger = null)
	{
		var options = CreateFusionCacheOptions();

		if (string.IsNullOrWhiteSpace(cacheInstanceId) == false)
			options.SetInstanceId(cacheInstanceId!);

		if (string.IsNullOrWhiteSpace(cacheName) == false)
			options.CacheName = cacheName;

		options.EnableSyncEventHandlersExecution = true;

		setupAction?.Invoke(options);
		var fusionCache = new FusionCache(options, memoryCache, logger: logger ?? CreateXUnitLogger<FusionCache>());
		fusionCache.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
		if (backplane is not null)
			fusionCache.SetupBackplane(backplane);

		return fusionCache;
	}

	[Fact]
	public async Task WorksWithDifferentCachesAsync()
	{
		var backplaneConnectionId = Guid.NewGuid().ToString("N");

		var key = Guid.NewGuid().ToString("N");
		using var cache1 = CreateFusionCache("C1", CreateBackplane(backplaneConnectionId), cacheInstanceId: "C1");
		using var cache2 = CreateFusionCache("C2", CreateBackplane(backplaneConnectionId), cacheInstanceId: "C2-01");
		using var cache2bis = CreateFusionCache("C2", CreateBackplane(backplaneConnectionId), cacheInstanceId: "C2-02");

		await Task.Delay(InitialBackplaneDelay);

		await cache1.GetOrSetAsync(key, async _ => 1, TimeSpan.FromMinutes(10));
		await cache2.GetOrSetAsync(key, async _ => 2, TimeSpan.FromMinutes(10));
		await Task.Delay(MultiNodeOperationsDelay);
		await cache2bis.GetOrSetAsync(key, async _ => 2, TimeSpan.FromMinutes(10));
		await Task.Delay(MultiNodeOperationsDelay);

		Assert.Equal(1, await cache1.GetOrDefaultAsync<int>(key));
		Assert.Equal(0, await cache2.GetOrDefaultAsync<int>(key));
		Assert.Equal(2, await cache2bis.GetOrDefaultAsync<int>(key));

		await cache1.SetAsync(key, 21);
		await cache2.SetAsync(key, 42);

		await Task.Delay(MultiNodeOperationsDelay);

		Assert.Equal(21, await cache1.GetOrSetAsync(key, async _ => 78, TimeSpan.FromMinutes(10)));
		Assert.Equal(42, await cache2.GetOrSetAsync(key, async _ => 78, TimeSpan.FromMinutes(10)));
		await Task.Delay(MultiNodeOperationsDelay);
		Assert.Equal(78, await cache2bis.GetOrSetAsync(key, async _ => 78, TimeSpan.FromMinutes(10)));
		await Task.Delay(MultiNodeOperationsDelay);
		Assert.Equal(88, await cache2.GetOrSetAsync(key, async _ => 88, TimeSpan.FromMinutes(10)));
	}

	[Fact]
	public void WorksWithDifferentCaches()
	{
		var backplaneConnectionId = Guid.NewGuid().ToString("N");

		var key = Guid.NewGuid().ToString("N");
		using var cache1 = CreateFusionCache("C1", CreateBackplane(backplaneConnectionId), cacheInstanceId: "C1");
		using var cache2 = CreateFusionCache("C2", CreateBackplane(backplaneConnectionId), cacheInstanceId: "C2-01");
		using var cache2bis = CreateFusionCache("C2", CreateBackplane(backplaneConnectionId), cacheInstanceId: "C2-02");

		Thread.Sleep(InitialBackplaneDelay);

		cache1.GetOrSetAsync(key, async _ => 1, TimeSpan.FromMinutes(10));
		cache2.GetOrSetAsync(key, async _ => 2, TimeSpan.FromMinutes(10));
		Thread.Sleep(MultiNodeOperationsDelay);
		cache2bis.GetOrSet(key, _ => 2, TimeSpan.FromMinutes(10));
		Thread.Sleep(MultiNodeOperationsDelay);

		Assert.Equal(1, cache1.GetOrDefault<int>(key));
		Assert.Equal(0, cache2.GetOrDefault<int>(key));
		Assert.Equal(2, cache2bis.GetOrDefault<int>(key));

		cache1.Set(key, 21);
		cache2.Set(key, 42);

		Thread.Sleep(MultiNodeOperationsDelay);

		Assert.Equal(21, cache1.GetOrSet(key, _ => 78, TimeSpan.FromMinutes(10)));
		Assert.Equal(42, cache2.GetOrSet(key, _ => 78, TimeSpan.FromMinutes(10)));
		Thread.Sleep(MultiNodeOperationsDelay);
		Assert.Equal(78, cache2bis.GetOrSet(key, _ => 78, TimeSpan.FromMinutes(10)));
		Thread.Sleep(MultiNodeOperationsDelay);
		Assert.Equal(88, cache2.GetOrSet(key, _ => 88, TimeSpan.FromMinutes(10)));
	}

	[Fact]
	public async Task CanSkipNotificationsAsync()
	{
		var backplaneConnectionId = Guid.NewGuid().ToString("N");

		var key = Guid.NewGuid().ToString("N");
		using var cache1 = CreateFusionCache(null, CreateBackplane(backplaneConnectionId));
		using var cache2 = CreateFusionCache(null, CreateBackplane(backplaneConnectionId));
		using var cache3 = CreateFusionCache(null, CreateBackplane(backplaneConnectionId));

		cache1.DefaultEntryOptions.SkipBackplaneNotifications = true;
		cache2.DefaultEntryOptions.SkipBackplaneNotifications = true;
		cache3.DefaultEntryOptions.SkipBackplaneNotifications = true;

		cache1.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
		cache2.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
		cache3.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;

		await Task.Delay(InitialBackplaneDelay);

		await cache1.SetAsync(key, 1, TimeSpan.FromMinutes(10));
		await Task.Delay(MultiNodeOperationsDelay);

		await cache2.SetAsync(key, 2, TimeSpan.FromMinutes(10));
		await Task.Delay(MultiNodeOperationsDelay);

		await cache3.SetAsync(key, 3, TimeSpan.FromMinutes(10));
		await Task.Delay(MultiNodeOperationsDelay);

		Assert.Equal(1, await cache1.GetOrDefaultAsync<int>(key));
		Assert.Equal(2, await cache2.GetOrDefaultAsync<int>(key));
		Assert.Equal(3, await cache3.GetOrDefaultAsync<int>(key));
	}

	[Fact]
	public void CanSkipNotifications()
	{
		var backplaneConnectionId = Guid.NewGuid().ToString("N");

		var key = Guid.NewGuid().ToString("N");
		using var cache1 = CreateFusionCache(null, CreateBackplane(backplaneConnectionId));
		using var cache2 = CreateFusionCache(null, CreateBackplane(backplaneConnectionId));
		using var cache3 = CreateFusionCache(null, CreateBackplane(backplaneConnectionId));

		cache1.DefaultEntryOptions.SkipBackplaneNotifications = true;
		cache2.DefaultEntryOptions.SkipBackplaneNotifications = true;
		cache3.DefaultEntryOptions.SkipBackplaneNotifications = true;

		cache1.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
		cache2.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
		cache3.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;

		Thread.Sleep(InitialBackplaneDelay);

		cache1.Set(key, 1, TimeSpan.FromMinutes(10));
		Thread.Sleep(MultiNodeOperationsDelay);

		cache2.Set(key, 2, TimeSpan.FromMinutes(10));
		Thread.Sleep(MultiNodeOperationsDelay);

		cache3.Set(key, 3, TimeSpan.FromMinutes(10));
		Thread.Sleep(MultiNodeOperationsDelay);

		Assert.Equal(1, cache1.GetOrDefault<int>(key));
		Assert.Equal(2, cache2.GetOrDefault<int>(key));
		Assert.Equal(3, cache3.GetOrDefault<int>(key));
	}

	[Fact]
	public async Task ReThrowsBackplaneExceptionsAsync()
	{
		var backplane = new MemoryBackplane(Options.Create(new MemoryBackplaneOptions()));
		var chaosBackplane = new ChaosBackplane(backplane);

		chaosBackplane.SetAlwaysThrow();
		using var fusionCache = new FusionCache(CreateFusionCacheOptions());
		fusionCache.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
		fusionCache.DefaultEntryOptions.ReThrowBackplaneExceptions = true;

		fusionCache.SetupBackplane(chaosBackplane);

		await Task.Delay(InitialBackplaneDelay);

		await Assert.ThrowsAsync<FusionCacheBackplaneException>(async () =>
		{
			await fusionCache.SetAsync<int>("foo", 42);
		});
	}

	[Fact]
	public void ReThrowsBackplaneExceptions()
	{
		var backplane = new MemoryBackplane(Options.Create(new MemoryBackplaneOptions()));
		var chaosBackplane = new ChaosBackplane(backplane);

		chaosBackplane.SetAlwaysThrow();
		using var fusionCache = new FusionCache(CreateFusionCacheOptions());
		fusionCache.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
		fusionCache.DefaultEntryOptions.ReThrowBackplaneExceptions = true;

		fusionCache.SetupBackplane(chaosBackplane);

		Thread.Sleep(InitialBackplaneDelay);

		Assert.Throws<FusionCacheBackplaneException>(() =>
		{
			fusionCache.Set<int>("foo", 42);
		});
	}

	[Fact]
	public async Task CanClearAsync()
	{
		var logger = CreateXUnitLogger<FusionCache>();

		var backplaneConnectionId = Guid.NewGuid().ToString("N");
		var key = Guid.NewGuid().ToString("N");

		using var cache1 = CreateFusionCache(null, CreateBackplane(backplaneConnectionId));
		cache1.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
		cache1.DefaultEntryOptions.SkipBackplaneNotifications = true;

		using var cache2 = CreateFusionCache(null, CreateBackplane(backplaneConnectionId));
		cache2.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
		cache2.DefaultEntryOptions.SkipBackplaneNotifications = true;

		await Task.Delay(InitialBackplaneDelay);

		logger.LogInformation("STEP 1");

		await cache1.SetAsync<int>("foo", 1, options => options.SetDuration(TimeSpan.FromMinutes(10)));
		await cache1.SetAsync<int>("bar", 1, options => options.SetDuration(TimeSpan.FromMinutes(10)));
		await cache1.SetAsync<int>("baz", 1, options => options.SetDuration(TimeSpan.FromMinutes(10)));

		logger.LogInformation("STEP 2");

		var foo1_1 = await cache1.GetOrSetAsync<int>("foo", async _ => 2, options => options.SetDuration(TimeSpan.FromMinutes(10)));
		var bar1_1 = await cache1.GetOrSetAsync<int>("bar", async _ => 2, options => options.SetDuration(TimeSpan.FromMinutes(10)));
		var baz1_1 = await cache1.GetOrSetAsync<int>("baz", async _ => 2, options => options.SetDuration(TimeSpan.FromMinutes(10)));

		Assert.Equal(1, foo1_1);
		Assert.Equal(1, bar1_1);
		Assert.Equal(1, baz1_1);

		logger.LogInformation("STEP 3");

		var foo2_1 = await cache2.GetOrSetAsync<int>("foo", async _ => 3, options => options.SetDuration(TimeSpan.FromMinutes(10)));
		var bar2_1 = await cache2.GetOrSetAsync<int>("bar", async _ => 3, options => options.SetDuration(TimeSpan.FromMinutes(10)));
		var baz2_1 = await cache2.GetOrSetAsync<int>("baz", async _ => 3, options => options.SetDuration(TimeSpan.FromMinutes(10)));

		Assert.Equal(3, foo2_1);
		Assert.Equal(3, bar2_1);
		Assert.Equal(3, baz2_1);

		logger.LogInformation("CLEAR");

		await cache1.ClearAsync();

		logger.LogInformation("STEP 4");

		var foo1_2 = await cache1.GetOrDefaultAsync<int>("foo");
		var bar1_2 = await cache1.GetOrDefaultAsync<int>("bar");
		var baz1_2 = await cache1.GetOrDefaultAsync<int>("baz");

		Assert.Equal(0, foo1_2);
		Assert.Equal(0, bar1_2);
		Assert.Equal(0, baz1_2);

		await Task.Delay(MultiNodeOperationsDelay);

		logger.LogInformation("STEP 5");

		var foo2_2 = await cache2.GetOrDefaultAsync<int>("foo");
		var bar2_2 = await cache2.GetOrDefaultAsync<int>("bar");
		var baz2_2 = await cache2.GetOrDefaultAsync<int>("baz");

		Assert.Equal(0, foo2_2);
		Assert.Equal(0, bar2_2);
		Assert.Equal(0, baz2_2);

		logger.LogInformation("STEP 6");

		var foo2_3 = await cache2.GetOrDefaultAsync<int>("foo");
		var bar2_3 = await cache2.GetOrDefaultAsync<int>("bar");
		var baz2_3 = await cache2.GetOrDefaultAsync<int>("baz");

		Assert.Equal(0, foo2_3);
		Assert.Equal(0, bar2_3);
		Assert.Equal(0, baz2_3);
	}

	[Fact]
	public void CanClear()
	{
		var logger = CreateXUnitLogger<FusionCache>();

		var backplaneConnectionId = Guid.NewGuid().ToString("N");
		var key = Guid.NewGuid().ToString("N");

		using var cache1 = CreateFusionCache(null, CreateBackplane(backplaneConnectionId));
		cache1.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
		cache1.DefaultEntryOptions.SkipBackplaneNotifications = true;

		using var cache2 = CreateFusionCache(null, CreateBackplane(backplaneConnectionId));
		cache2.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
		cache2.DefaultEntryOptions.SkipBackplaneNotifications = true;

		Thread.Sleep(InitialBackplaneDelay);

		logger.LogInformation("STEP 1");

		cache1.Set<int>("foo", 1, options => options.SetDuration(TimeSpan.FromMinutes(10)));
		cache1.Set<int>("bar", 1, options => options.SetDuration(TimeSpan.FromMinutes(10)));
		cache1.Set<int>("baz", 1, options => options.SetDuration(TimeSpan.FromMinutes(10)));

		logger.LogInformation("STEP 2");

		var foo1_1 = cache1.GetOrSet<int>("foo", _ => 2, options => options.SetDuration(TimeSpan.FromMinutes(10)));
		var bar1_1 = cache1.GetOrSet<int>("bar", _ => 2, options => options.SetDuration(TimeSpan.FromMinutes(10)));
		var baz1_1 = cache1.GetOrSet<int>("baz", _ => 2, options => options.SetDuration(TimeSpan.FromMinutes(10)));

		Assert.Equal(1, foo1_1);
		Assert.Equal(1, bar1_1);
		Assert.Equal(1, baz1_1);

		logger.LogInformation("STEP 3");

		var foo2_1 = cache2.GetOrSet<int>("foo", _ => 3, options => options.SetDuration(TimeSpan.FromMinutes(10)));
		var bar2_1 = cache2.GetOrSet<int>("bar", _ => 3, options => options.SetDuration(TimeSpan.FromMinutes(10)));
		var baz2_1 = cache2.GetOrSet<int>("baz", _ => 3, options => options.SetDuration(TimeSpan.FromMinutes(10)));

		Assert.Equal(3, foo2_1);
		Assert.Equal(3, bar2_1);
		Assert.Equal(3, baz2_1);

		logger.LogInformation("CLEAR");

		cache1.Clear();

		logger.LogInformation("STEP 4");

		var foo1_2 = cache1.GetOrDefault<int>("foo");
		var bar1_2 = cache1.GetOrDefault<int>("bar");
		var baz1_2 = cache1.GetOrDefault<int>("baz");

		Assert.Equal(0, foo1_2);
		Assert.Equal(0, bar1_2);
		Assert.Equal(0, baz1_2);

		Thread.Sleep(MultiNodeOperationsDelay);

		logger.LogInformation("STEP 5");

		var foo2_2 = cache2.GetOrDefault<int>("foo");
		var bar2_2 = cache2.GetOrDefault<int>("bar");
		var baz2_2 = cache2.GetOrDefault<int>("baz");

		Assert.Equal(0, foo2_2);
		Assert.Equal(0, bar2_2);
		Assert.Equal(0, baz2_2);

		logger.LogInformation("STEP 6");

		var foo2_3 = cache2.GetOrDefault<int>("foo");
		var bar2_3 = cache2.GetOrDefault<int>("bar");
		var baz2_3 = cache2.GetOrDefault<int>("baz");

		Assert.Equal(0, foo2_3);
		Assert.Equal(0, bar2_3);
		Assert.Equal(0, baz2_3);
	}

	[Fact]
	public async Task CanRemoveByTagAsync()
	{
		var logger = CreateXUnitLogger<FusionCache>();

		var cacheName = FusionCacheInternalUtils.GenerateOperationId();

		var backplaneConnectionId = Guid.NewGuid().ToString("N");

		using var cache1 = CreateFusionCache(
			cacheName,
			CreateBackplane(backplaneConnectionId),
			opt =>
			{
				opt.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
				opt.DefaultEntryOptions.SkipBackplaneNotifications = true;
			},
			cacheInstanceId: "C1",
			logger: logger
		);

		using var cache2 = CreateFusionCache(
			cacheName,
			CreateBackplane(backplaneConnectionId),
			opt =>
			{
				opt.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
				opt.DefaultEntryOptions.SkipBackplaneNotifications = true;
			},
			cacheInstanceId: "C2",
			logger: logger
		);

		await Task.Delay(InitialBackplaneDelay);

		logger.LogInformation("STEP 1");

		var foo = 1;
		var bar = 2;
		var baz = 3;

		await cache1.SetAsync<int>("foo", foo, tags: ["x", "y"]);
		await cache1.SetAsync<int>("bar", bar, tags: ["y", "z"]);
		await cache1.GetOrSetAsync<int>("baz", async _ => baz, tags: ["x", "z"]);

		var cache1_foo_1 = await cache1.GetOrDefaultAsync<int>("foo");
		var cache1_bar_1 = await cache1.GetOrDefaultAsync<int>("bar");
		var cache1_baz_1 = await cache1.GetOrDefaultAsync<int>("baz");

		Assert.Equal(1, cache1_foo_1);
		Assert.Equal(2, cache1_bar_1);
		Assert.Equal(3, cache1_baz_1);

		var cache2_foo_1 = await cache2.GetOrSetAsync<int>("foo", async _ => foo, tags: ["x", "y"]);
		var cache2_bar_1 = await cache2.GetOrSetAsync<int>("bar", async _ => bar, tags: ["y", "z"]);
		var cache2_baz_1 = await cache2.GetOrSetAsync<int>("baz", async _ => baz, tags: ["x", "z"]);

		Assert.Equal(1, cache2_foo_1);
		Assert.Equal(2, cache2_bar_1);
		Assert.Equal(3, cache2_baz_1);

		logger.LogInformation("STEP 2");

		// REMOVE BY TAG ("x") ON CACHE 1
		await cache1.RemoveByTagAsync("x");
		await Task.Delay(MultiNodeOperationsDelay);

		logger.LogInformation("STEP 3");

		foo = 11;
		bar = 22;
		baz = 33;

		var cache1_foo_3 = await cache1.GetOrSetAsync<int>("foo", async _ => foo, tags: ["x", "y"]);
		var cache1_bar_3 = await cache1.GetOrSetAsync<int>("bar", async _ => bar, tags: ["y", "z"]);
		var cache1_baz_3 = await cache1.GetOrSetAsync<int>("baz", async _ => baz, tags: ["x", "z"]);

		Assert.Equal(foo, cache1_foo_3);
		Assert.Equal(2, cache1_bar_3);
		Assert.Equal(baz, cache1_baz_3);

		var cache2_foo_3 = await cache2.GetOrSetAsync<int>("foo", async _ => foo, tags: ["x", "y"]);
		var cache2_bar_3 = await cache2.GetOrSetAsync<int>("bar", async _ => bar, tags: ["y", "z"]);
		var cache2_baz_3 = await cache2.GetOrSetAsync<int>("baz", async _ => baz, tags: ["x", "z"]);

		Assert.Equal(foo, cache2_foo_3);
		Assert.Equal(2, cache2_bar_3);
		Assert.Equal(baz, cache2_baz_3);

		logger.LogInformation("STEP 4");

		// REMOVE BY TAG ("y") ON CACHE 2
		await cache2.RemoveByTagAsync("y");
		await Task.Delay(MultiNodeOperationsDelay);

		logger.LogInformation("STEP 5");

		foo = 111;
		bar = 222;
		baz = 333;

		var cache1_foo_4 = await cache1.GetOrSetAsync<int>("foo", async _ => foo, tags: ["x", "y"]);
		var cache1_bar_4 = await cache1.GetOrSetAsync<int>("bar", async _ => bar, tags: ["y", "z"]);
		var cache1_baz_4 = await cache1.GetOrSetAsync<int>("baz", async _ => baz, tags: ["x", "z"]);

		Assert.Equal(foo, cache1_foo_4);
		Assert.Equal(bar, cache1_bar_4);
		Assert.Equal(33, cache1_baz_4);

		var cache2_foo_4 = await cache2.GetOrSetAsync<int>("foo", async _ => foo, tags: ["x", "y"]);
		var cache2_bar_4 = await cache2.GetOrSetAsync<int>("bar", async _ => bar, tags: ["y", "z"]);
		var cache2_baz_4 = await cache2.GetOrSetAsync<int>("baz", async _ => baz, tags: ["x", "z"]);

		Assert.Equal(foo, cache2_foo_4);
		Assert.Equal(bar, cache2_bar_4);
		Assert.Equal(33, cache2_baz_4);
	}

	[Fact]
	public void CanRemoveByTag()
	{
		var logger = CreateXUnitLogger<FusionCache>();

		var cacheName = FusionCacheInternalUtils.GenerateOperationId();

		var backplaneConnectionId = Guid.NewGuid().ToString("N");

		using var cache1 = CreateFusionCache(
			cacheName,
			CreateBackplane(backplaneConnectionId),
			opt =>
			{
				opt.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
				opt.DefaultEntryOptions.SkipBackplaneNotifications = true;
			},
			cacheInstanceId: "C1",
			logger: logger
		);

		using var cache2 = CreateFusionCache(
			cacheName,
			CreateBackplane(backplaneConnectionId),
			opt =>
			{
				opt.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
				opt.DefaultEntryOptions.SkipBackplaneNotifications = true;
			},
			cacheInstanceId: "C2",
			logger: logger
		);

		Thread.Sleep(InitialBackplaneDelay);

		logger.LogInformation("STEP 1");

		var foo = 1;
		var bar = 2;
		var baz = 3;

		cache1.Set<int>("foo", foo, tags: ["x", "y"]);
		cache1.Set<int>("bar", bar, tags: ["y", "z"]);
		cache1.GetOrSet<int>("baz", _ => baz, tags: ["x", "z"]);

		var cache1_foo_1 = cache1.GetOrDefault<int>("foo");
		var cache1_bar_1 = cache1.GetOrDefault<int>("bar");
		var cache1_baz_1 = cache1.GetOrDefault<int>("baz");

		Assert.Equal(1, cache1_foo_1);
		Assert.Equal(2, cache1_bar_1);
		Assert.Equal(3, cache1_baz_1);

		var cache2_foo_1 = cache2.GetOrSet<int>("foo", _ => foo, tags: ["x", "y"]);
		var cache2_bar_1 = cache2.GetOrSet<int>("bar", _ => bar, tags: ["y", "z"]);
		var cache2_baz_1 = cache2.GetOrSet<int>("baz", _ => baz, tags: ["x", "z"]);

		Assert.Equal(1, cache2_foo_1);
		Assert.Equal(2, cache2_bar_1);
		Assert.Equal(3, cache2_baz_1);

		logger.LogInformation("STEP 2");

		// REMOVE BY TAG ("x") ON CACHE 1
		cache1.RemoveByTag("x");
		Thread.Sleep(MultiNodeOperationsDelay);

		logger.LogInformation("STEP 3");

		foo = 11;
		bar = 22;
		baz = 33;

		var cache1_foo_3 = cache1.GetOrSet<int>("foo", _ => foo, tags: ["x", "y"]);
		var cache1_bar_3 = cache1.GetOrSet<int>("bar", _ => bar, tags: ["y", "z"]);
		var cache1_baz_3 = cache1.GetOrSet<int>("baz", _ => baz, tags: ["x", "z"]);

		Assert.Equal(foo, cache1_foo_3);
		Assert.Equal(2, cache1_bar_3);
		Assert.Equal(baz, cache1_baz_3);

		var cache2_foo_3 = cache2.GetOrSet<int>("foo", _ => foo, tags: ["x", "y"]);
		var cache2_bar_3 = cache2.GetOrSet<int>("bar", _ => bar, tags: ["y", "z"]);
		var cache2_baz_3 = cache2.GetOrSet<int>("baz", _ => baz, tags: ["x", "z"]);

		Assert.Equal(foo, cache2_foo_3);
		Assert.Equal(2, cache2_bar_3);
		Assert.Equal(baz, cache2_baz_3);

		logger.LogInformation("STEP 4");

		// REMOVE BY TAG ("y") ON CACHE 2
		cache2.RemoveByTag("y");
		Thread.Sleep(MultiNodeOperationsDelay);

		logger.LogInformation("STEP 5");

		foo = 111;
		bar = 222;
		baz = 333;

		var cache1_foo_4 = cache1.GetOrSet<int>("foo", _ => foo, tags: ["x", "y"]);
		var cache1_bar_4 = cache1.GetOrSet<int>("bar", _ => bar, tags: ["y", "z"]);
		var cache1_baz_4 = cache1.GetOrSet<int>("baz", _ => baz, tags: ["x", "z"]);

		Assert.Equal(foo, cache1_foo_4);
		Assert.Equal(bar, cache1_bar_4);
		Assert.Equal(33, cache1_baz_4);

		var cache2_foo_4 = cache2.GetOrSet<int>("foo", _ => foo, tags: ["x", "y"]);
		var cache2_bar_4 = cache2.GetOrSet<int>("bar", _ => bar, tags: ["y", "z"]);
		var cache2_baz_4 = cache2.GetOrSet<int>("baz", _ => baz, tags: ["x", "z"]);

		Assert.Equal(foo, cache2_foo_4);
		Assert.Equal(bar, cache2_bar_4);
		Assert.Equal(33, cache2_baz_4);
	}
}
