using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Verifies that <see cref="InMemoryLoggerService"/> is safe to call from many
/// concurrent background threads/tasks without data corruption or lost updates,
/// and that the <see cref="ILoggerService.Entries"/> snapshot and
/// <see cref="ILoggerService.EntryAdded"/> event stay consistent. No display
/// device is required, so this runs in headless CI.
/// </summary>
public class LoggerThreadSafetyTests
{
    [Fact]
    public async Task ManyBackgroundThreads_CanLog_And_AllEntriesAreCaptured()
    {
        // Arrange: total (1200) is kept below the 2000-entry capacity so the
        // ring buffer does not truncate, letting us assert every message survives.
        var logger = new InMemoryLoggerService();
        const int threads = 6;
        const int perThread = 200;
        const int expected = threads * perThread;
        var barrier = new Barrier(threads);
        var tasks = new List<Task>(threads);

        // Act: start all threads together to maximize contention on the lock.
        for (int t = 0; t < threads; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < perThread; i++)
                {
                    logger.Info($"t{threadId} m{i}");
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert: no lost updates and no corruption.
        IReadOnlyList<LogEntry> entries = logger.Entries;
        Assert.Equal(expected, entries.Count);
        Assert.All(entries, e => Assert.Equal(LogLevel.Info, e.Level));

        // Each (thread, index) pair is unique, so a distinct-message count equal
        // to the total proves no message was dropped or duplicated.
        int distinct = entries.Select(e => e.Message).Distinct().Count();
        Assert.Equal(expected, distinct);
    }

    [Fact]
    public async Task EntryAdded_Raised_OnceForEachLog_FromBackgroundThreads()
    {
        var logger = new InMemoryLoggerService();
        int raised = 0;
        var sync = new object();
        logger.EntryAdded += (_, _) =>
        {
            lock (sync)
            {
                raised++;
            }
        };

        const int threads = 4;
        const int perThread = 250;
        const int expected = threads * perThread;

        Task[] tasks = Enumerable.Range(0, threads)
            .Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    logger.Warning("x");
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // EntryAdded is invoked synchronously inside Log on the calling thread,
        // so by the time WhenAll returns every raise has already completed.
        Assert.Equal(expected, raised);
    }

    [Fact]
    public async Task Capacity_IsRespected_UnderConcurrentLoad()
    {
        // Arrange: log well past the 2000-entry cap from many threads.
        var logger = new InMemoryLoggerService();
        const int threads = 8;
        const int perThread = 500; // 4000 total >> capacity
        var tasks = new List<Task>(threads);

        // Act
        for (int t = 0; t < threads; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    logger.Debug("load");
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert: no exception, and the ring buffer capped the store exactly.
        Assert.Equal(2000, logger.Entries.Count);
    }
}
