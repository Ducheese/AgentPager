using System.IO;
using AgentPager.Bridge;

var failures = new List<string>();
var testLogRoot = Path.Combine(Path.GetTempPath(), "ap-bridge-tests-" + Guid.NewGuid().ToString("N"));

void Check(bool condition, string message)
{
    if (condition)
    {
        Console.WriteLine($"PASS  {message}");
        return;
    }
    failures.Add(message);
    Console.WriteLine($"FAIL  {message}");
}

void CheckDelays(string name, Func<int, TimeSpan> delayFor)
{
    var expected = new[] { 5, 10, 20, 40, 60, 60 };
    for (var attempt = 1; attempt <= expected.Length; attempt++)
        Check(delayFor(attempt) == TimeSpan.FromSeconds(expected[attempt - 1]),
            $"{name}: retry delay attempt {attempt} is {expected[attempt - 1]}s");
}

CheckDelays("ngrok", NgrokTunnelService.RetryDelayForTest);
CheckDelays("cloudflared", CloudflaredTunnelService.RetryDelayForTest);

await TestRetryLoop(
    "ngrok",
    onStatus => new NgrokTunnelService(new DailyFileLog(testLogRoot), _ => { }, onStatus),
    (exe, runOnce, delay) =>
    {
        NgrokTunnelService.ExecutableOverride = exe;
        NgrokTunnelService.RunOnceOverride = runOnce;
        NgrokTunnelService.RetryDelayOverride = delay;
    },
    () =>
    {
        NgrokTunnelService.ExecutableOverride = null;
        NgrokTunnelService.RunOnceOverride = null;
        NgrokTunnelService.RetryDelayOverride = null;
    });

await TestRetryLoop(
    "cloudflared",
    onStatus => new CloudflaredTunnelService(new DailyFileLog(testLogRoot), _ => { }, onStatus),
    (exe, runOnce, delay) =>
    {
        CloudflaredTunnelService.ExecutableOverride = exe;
        CloudflaredTunnelService.RunOnceOverride = runOnce;
        CloudflaredTunnelService.RetryDelayOverride = delay;
    },
    () =>
    {
        CloudflaredTunnelService.ExecutableOverride = null;
        CloudflaredTunnelService.RunOnceOverride = null;
        CloudflaredTunnelService.RetryDelayOverride = null;
    });

await TestBackoffResets(
    "ngrok",
    onStatus => new NgrokTunnelService(new DailyFileLog(testLogRoot), _ => { }, onStatus),
    service => service.MarkReachedRunningForTest(),
    (exe, runOnce, delay) =>
    {
        NgrokTunnelService.ExecutableOverride = exe;
        NgrokTunnelService.RunOnceOverride = runOnce;
        NgrokTunnelService.RetryDelayOverride = delay;
    },
    () =>
    {
        NgrokTunnelService.ExecutableOverride = null;
        NgrokTunnelService.RunOnceOverride = null;
        NgrokTunnelService.RetryDelayOverride = null;
    });

await TestBackoffResets(
    "cloudflared",
    onStatus => new CloudflaredTunnelService(new DailyFileLog(testLogRoot), _ => { }, onStatus),
    service => service.MarkReachedRunningForTest(),
    (exe, runOnce, delay) =>
    {
        CloudflaredTunnelService.ExecutableOverride = exe;
        CloudflaredTunnelService.RunOnceOverride = runOnce;
        CloudflaredTunnelService.RetryDelayOverride = delay;
    },
    () =>
    {
        CloudflaredTunnelService.ExecutableOverride = null;
        CloudflaredTunnelService.RunOnceOverride = null;
        CloudflaredTunnelService.RetryDelayOverride = null;
    });

await TestStaleSupervisor(
    "ngrok",
    onStatus => new NgrokTunnelService(new DailyFileLog(testLogRoot), _ => { }, onStatus),
    service => service.CurrentOwnerForTest,
    (service, status, message, owner) => service.SetStatusForTest(status, message, owner),
    (exe, runOnce, delay) =>
    {
        NgrokTunnelService.ExecutableOverride = exe;
        NgrokTunnelService.RunOnceOverride = runOnce;
        NgrokTunnelService.RetryDelayOverride = delay;
    },
    () =>
    {
        NgrokTunnelService.ExecutableOverride = null;
        NgrokTunnelService.RunOnceOverride = null;
        NgrokTunnelService.RetryDelayOverride = null;
    });

await TestStaleSupervisor(
    "cloudflared",
    onStatus => new CloudflaredTunnelService(new DailyFileLog(testLogRoot), _ => { }, onStatus),
    service => service.CurrentOwnerForTest,
    (service, status, message, owner) => service.SetStatusForTest(status, message, owner),
    (exe, runOnce, delay) =>
    {
        CloudflaredTunnelService.ExecutableOverride = exe;
        CloudflaredTunnelService.RunOnceOverride = runOnce;
        CloudflaredTunnelService.RetryDelayOverride = delay;
    },
    () =>
    {
        CloudflaredTunnelService.ExecutableOverride = null;
        CloudflaredTunnelService.RunOnceOverride = null;
        CloudflaredTunnelService.RetryDelayOverride = null;
    });

try { Directory.Delete(testLogRoot, recursive: true); } catch (DirectoryNotFoundException) { } catch (IOException) { }

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"{failures.Count} FAILED");
    return 1;
}

Console.WriteLine();
Console.WriteLine("ALL CHECKS PASSED");
return 0;

async Task TestRetryLoop(
    string name,
    Func<Action<TunnelStatus, string?>, ITunnelService> create,
    Action<
        Func<string?>,
        Func<string, bool, CancellationToken, Task<string?>>,
        Func<TimeSpan, CancellationToken, Task>> install,
    Action reset)
{
    var attempts = new int[1];
    var reachedThird = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var statuses = new List<TunnelStatus>();

    install(
        () => "fake-tunnel",
        async (_, _, token) =>
        {
            var attempt = Interlocked.Increment(ref attempts[0]);
            if (attempt >= 3)
            {
                reachedThird.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { }
                return null;
            }
            return "boom";
        },
        (_, _) => Task.CompletedTask);

    var service = create((status, _) =>
    {
        lock (statuses) statuses.Add(status);
    });
    try
    {
        await service.StartAsync();
        await reachedThird.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Check(attempts[0] >= 3, $"{name}: retry loop reaches attempt 3 (got {attempts[0]})");
        lock (statuses)
            Check(statuses.Contains(TunnelStatus.Retrying), $"{name}: failures surface as Retrying");

        await service.StopAsync();
        Check(service.Status == TunnelStatus.Stopped, $"{name}: Stop after retrying ends Stopped");
    }
    finally
    {
        await service.DisposeAsync();
        reset();
    }
}

async Task TestBackoffResets<T>(
    string name,
    Func<Action<TunnelStatus, string?>, T> create,
    Action<T> markReached,
    Action<
        Func<string?>,
        Func<string, bool, CancellationToken, Task<string?>>,
        Func<TimeSpan, CancellationToken, Task>> install,
    Action reset)
    where T : ITunnelService
{
    var delays = new List<TimeSpan>();
    var secondDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var attempts = 0;
    T service = default!;

    install(
        () => "fake-tunnel",
        async (_, _, token) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1) return "first failure";
            if (attempt == 2)
            {
                markReached(service);
                return "ran, then exited";
            }
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            return null;
        },
        (delay, _) =>
        {
            lock (delays)
            {
                delays.Add(delay);
                if (delays.Count == 2) secondDelay.TrySetResult();
            }
            return Task.CompletedTask;
        });

    service = create((_, _) => { });
    try
    {
        await service.StartAsync();
        await secondDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));

        TimeSpan[] recorded;
        lock (delays) recorded = delays.ToArray();
        Check(recorded.Length == 2 && recorded[0] == TimeSpan.FromSeconds(5) &&
              recorded[1] == TimeSpan.FromSeconds(5),
            $"{name}: a healthy run resets backoff (got {string.Join(", ", recorded)})");

        await service.StopAsync();
    }
    finally
    {
        await service.DisposeAsync();
        reset();
    }
}

async Task TestStaleSupervisor<T>(
    string name,
    Func<Action<TunnelStatus, string?>, T> create,
    Func<T, CancellationTokenSource?> ownerOf,
    Action<T, TunnelStatus, string, CancellationTokenSource> setStatus,
    Action<
        Func<string?>,
        Func<string, bool, CancellationToken, Task<string?>>,
        Func<TimeSpan, CancellationToken, Task>> install,
    Action reset)
    where T : ITunnelService
{
    var callbacks = 0;
    install(
        () => "fake-tunnel",
        async (_, _, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            return null;
        },
        (_, _) => Task.CompletedTask);

    var service = create((_, _) => Interlocked.Increment(ref callbacks));
    try
    {
        await service.StartAsync();
        var oldOwner = ownerOf(service);
        Check(oldOwner is not null, $"{name}: start owns a supervisor CTS");

        await service.StopAsync();
        callbacks = 0;
        setStatus(service, TunnelStatus.Retrying, "stale supervisor", oldOwner!);

        Check(service.Status == TunnelStatus.Stopped, $"{name}: stale SetStatus cannot leave Stopped");
        Check(callbacks == 0, $"{name}: stale SetStatus publishes no status callback");

        await service.StartAsync();
        Check(service.Status != TunnelStatus.Stopped, $"{name}: Start works after Stop + stale callback");
        await service.StopAsync();
    }
    finally
    {
        await service.DisposeAsync();
        reset();
    }
}
