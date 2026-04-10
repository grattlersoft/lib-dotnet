using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Grattlersoft.VsExtension.Core.FileSystem;
using Xunit;

namespace Grattlersoft.VsExtension.Core.Tests.FileSystem;

/// <summary>
/// Tests fuer <see cref="DebouncedFileWatcher"/>. Arbeiten mit einem echten
/// TempDir — schnelle IO, kein Mock. Timing-Toleranzen grosszuegig gewaehlt,
/// damit die Tests auf lahmen CI-Runnern nicht flaky werden.
/// </summary>
public sealed class DebouncedFileWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    private readonly string _tempDir;

    public DebouncedFileWatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DebouncedFileWatcherTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task WhenMultipleEventsWithinDebounce_FiresOnceAfterQuietPeriod()
    {
        using var watcher = new DebouncedFileWatcher(_tempDir, Debounce);
        var count = 0;
        watcher.Triggered += (_, _) => Interlocked.Increment(ref count);
        watcher.EnableRaisingEvents = true;

        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_tempDir, $"file{i}.txt"), "x");
            await Task.Delay(20);
        }

        await Task.Delay(Quiet);

        count.Should().Be(1, "alle Events innerhalb der Debounce-Periode sollen zu einem Trigger gebuendelt werden");
    }

    [Fact]
    public async Task WhenIgnorePathReturnsTrue_EventIsSuppressed()
    {
        using var watcher = new DebouncedFileWatcher(_tempDir, Debounce)
        {
            IgnorePath = path => path.EndsWith(".ignore", StringComparison.OrdinalIgnoreCase),
        };
        var fired = false;
        watcher.Triggered += (_, _) => fired = true;
        watcher.EnableRaisingEvents = true;

        File.WriteAllText(Path.Combine(_tempDir, "skip.ignore"), "x");
        await Task.Delay(Quiet);

        fired.Should().BeFalse("Pfade fuer die IgnorePath true liefert sollen kein Trigger-Event ausloesen");
    }

    [Fact]
    public async Task WhenIgnorePathReturnsFalse_EventFires()
    {
        using var watcher = new DebouncedFileWatcher(_tempDir, Debounce)
        {
            IgnorePath = path => path.EndsWith(".ignore", StringComparison.OrdinalIgnoreCase),
        };
        var fired = false;
        watcher.Triggered += (_, _) => fired = true;
        watcher.EnableRaisingEvents = true;

        File.WriteAllText(Path.Combine(_tempDir, "keep.txt"), "x");
        await Task.Delay(Quiet);

        fired.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_StopsFurtherEvents()
    {
        var watcher = new DebouncedFileWatcher(_tempDir, Debounce);
        var count = 0;
        watcher.Triggered += (_, _) => Interlocked.Increment(ref count);
        watcher.EnableRaisingEvents = true;

        File.WriteAllText(Path.Combine(_tempDir, "before.txt"), "x");
        await Task.Delay(Quiet);
        count.Should().Be(1);

        watcher.Dispose();

        File.WriteAllText(Path.Combine(_tempDir, "after.txt"), "x");
        await Task.Delay(Quiet);

        count.Should().Be(1, "nach Dispose duerfen keine weiteren Events mehr gefeuert werden");
    }

    [Fact]
    public async Task Filter_AppliesToFileSystemWatcher()
    {
        using var watcher = new DebouncedFileWatcher(_tempDir, Debounce)
        {
            Filter = "*.json",
        };
        var fired = false;
        watcher.Triggered += (_, _) => fired = true;
        watcher.EnableRaisingEvents = true;

        File.WriteAllText(Path.Combine(_tempDir, "ignored.txt"), "x");
        await Task.Delay(Quiet);
        fired.Should().BeFalse("Dateien ausserhalb des Filters sollen keine Events ausloesen");

        File.WriteAllText(Path.Combine(_tempDir, "matched.json"), "x");
        await Task.Delay(Quiet);
        fired.Should().BeTrue("Dateien die zum Filter passen sollen ein Event ausloesen");
    }

    [Fact]
    public async Task IncludeSubdirectories_WatchesChildFolders()
    {
        var sub = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(sub);

        using var watcher = new DebouncedFileWatcher(_tempDir, Debounce)
        {
            IncludeSubdirectories = true,
        };
        var fired = false;
        watcher.Triggered += (_, _) => fired = true;
        watcher.EnableRaisingEvents = true;

        File.WriteAllText(Path.Combine(sub, "deep.txt"), "x");
        await Task.Delay(Quiet);

        fired.Should().BeTrue();
    }

    [Fact]
    public void Ctor_ThrowsOnEmptyDirectory()
    {
        Action act = () => _ = new DebouncedFileWatcher("", Debounce);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ctor_ThrowsOnNonPositiveDebounce()
    {
        Action act = () => _ = new DebouncedFileWatcher(_tempDir, TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task EnableRaisingEventsFalse_SuppressesEvents()
    {
        using var watcher = new DebouncedFileWatcher(_tempDir, Debounce);
        var fired = false;
        watcher.Triggered += (_, _) => fired = true;
        // bewusst NICHT aktiviert

        File.WriteAllText(Path.Combine(_tempDir, "file.txt"), "x");
        await Task.Delay(Quiet);

        fired.Should().BeFalse("ohne EnableRaisingEvents duerfen keine Events gefeuert werden");
    }

    [Fact]
    public async Task IgnorePathThrowing_DoesNotSuppressEvent()
    {
        // Wenn das Predikat wirft, ist das "fail-open": Event durchlassen statt
        // schlucken. Sonst kann ein Bug im Filter stumm den ganzen Watcher lahmlegen.
        using var watcher = new DebouncedFileWatcher(_tempDir, Debounce)
        {
            IgnorePath = _ => throw new InvalidOperationException("boom"),
        };
        var fired = false;
        watcher.Triggered += (_, _) => fired = true;
        watcher.EnableRaisingEvents = true;

        File.WriteAllText(Path.Combine(_tempDir, "file.txt"), "x");
        await Task.Delay(Quiet);

        fired.Should().BeTrue();
    }
}
