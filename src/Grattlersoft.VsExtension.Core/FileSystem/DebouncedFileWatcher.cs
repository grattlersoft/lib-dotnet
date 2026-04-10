using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Grattlersoft.VsExtension.Core.FileSystem;

/// <summary>
/// Gebuendelter FileSystemWatcher — mehrere Events innerhalb der Debounce-Periode
/// werden zu genau einem <see cref="Triggered"/>-Event zusammengefasst.
///
/// Ersetzt handgerollte Paare aus <see cref="FileSystemWatcher"/> +
/// <see cref="Timer"/>.<see cref="Timer.Change(int, int)"/>. Fuer Konsumenten gelten
/// drei Garantien, die sonst leicht vergessen werden:
///
/// <list type="bullet">
/// <item>Scheduling ist lock-geschuetzt (keine race zwischen Dispose und FSW-Thread).</item>
/// <item>Dispose-Reihenfolge stoppt erst den Watcher, dann den Timer —
///       sonst kann der Timer ein bereits disposed-Objekt erwischen.</item>
/// <item>Das Triggered-Event wird nach Dispose nicht mehr gefeuert.</item>
/// </list>
///
/// <example>
/// <code>
/// using var watcher = new DebouncedFileWatcher(solutionDir, TimeSpan.FromMilliseconds(300))
/// {
///     Filter = "*.json",
///     NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
/// };
/// watcher.Triggered += (_, _) => ReloadConfig();
/// watcher.EnableRaisingEvents = true;
/// </code>
/// </example>
/// </summary>
public sealed class DebouncedFileWatcher : IDisposable
{
    private readonly string _directory;
    private readonly TimeSpan _debounce;
    private readonly object _lock = new();

    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private bool _disposed;
    private int _ignorePathFailureLogged;

    public DebouncedFileWatcher(string directory, TimeSpan debounce)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Verzeichnis darf nicht leer sein", nameof(directory));
        if (debounce <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce), "Debounce muss > 0 sein");

        _directory = directory;
        _debounce = debounce;
    }

    /// <summary>
    /// FileSystemWatcher-Filter (Datei-Maske wie "*.json"). Default: "*.*".
    /// Muss vor <see cref="EnableRaisingEvents"/>=true gesetzt werden.
    /// </summary>
    public string Filter { get; set; } = "*.*";

    /// <summary>
    /// Unterverzeichnisse mitbeobachten. Default: false.
    /// </summary>
    public bool IncludeSubdirectories { get; set; }

    /// <summary>
    /// NotifyFilters (Change-Typen). Default: LastWrite | CreationTime | FileName.
    /// </summary>
    public NotifyFilters NotifyFilter { get; set; } =
        NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName;

    /// <summary>
    /// Optionaler Filter: bekommt den FullPath, gibt <c>true</c> zurueck
    /// wenn das Event unterdrueckt werden soll (z.B. fuer .git, bin, obj).
    /// Ausnahmen im Predikat werden geloggt und als "nicht ignorieren" gewertet.
    /// </summary>
    public Func<string, bool>? IgnorePath { get; set; }

    /// <summary>
    /// Schaltet die Ueberwachung an/aus. Erst nach dem Setzen auf <c>true</c>
    /// werden Dateisystem-Events beobachtet.
    /// </summary>
    public bool EnableRaisingEvents
    {
        get
        {
            lock (_lock)
            {
                return _watcher?.EnableRaisingEvents ?? false;
            }
        }
        set
        {
            lock (_lock)
            {
                if (_disposed) return;

                if (value)
                {
                    EnsureStartedLocked();
                    if (_watcher != null) _watcher.EnableRaisingEvents = true;
                }
                else if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                }
            }
        }
    }

    /// <summary>
    /// Gebuendelter Event — feuert einmal pro Debounce-Periode ohne weitere Events.
    /// </summary>
    public event EventHandler? Triggered;

    private void EnsureStartedLocked()
    {
        if (_watcher != null) return;

        _timer = new Timer(OnDebounce, null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(_directory)
        {
            Filter = Filter,
            IncludeSubdirectories = IncludeSubdirectories,
            NotifyFilter = NotifyFilter,
        };

        _watcher.Created += OnFsEvent;
        _watcher.Changed += OnFsEvent;
        _watcher.Deleted += OnFsEvent;
        _watcher.Renamed += OnFsRenamed;
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => OnEvent(e.FullPath);

    private void OnFsRenamed(object sender, RenamedEventArgs e)
    {
        // Bei Rename sind beide Pfade relevant — Altname kann der Ignore-Filter
        // bereits kennen, Neuname noch nicht. Reagieren wenn mindestens einer
        // nicht-ignoriert ist.
        if (!ShouldIgnore(e.FullPath) || !ShouldIgnore(e.OldFullPath))
            Schedule();
    }

    private void OnEvent(string fullPath)
    {
        if (ShouldIgnore(fullPath)) return;
        Schedule();
    }

    private bool ShouldIgnore(string fullPath)
    {
        var predicate = IgnorePath;
        if (predicate == null) return false;

        try
        {
            return predicate(fullPath);
        }
        catch (Exception ex)
        {
            // Nur einmal pro Instanz loggen — LogError traversiert den StackTrace und
            // wuerde den FSW-Event-Thread blockieren wenn jedes Event einen Log erzeugt.
            if (Interlocked.Exchange(ref _ignorePathFailureLogged, 1) == 0)
            {
                // Log-Call selbst absichern: in Tests ohne initialisiertes Log-Target
                // kann die StackTrace-Traversal werfen. Der Watcher darf daran nicht sterben.
                try { LogError($"DebouncedFileWatcher.IgnorePath warf — Event nicht unterdrueckt ({fullPath}). Weitere Fehler werden nicht geloggt.", ex); }
                catch { /* Log-Fehler darf den Watcher nicht lahmlegen */ }
            }
            return false;
        }
    }

    private void Schedule()
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                _timer?.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Timer wurde gleichzeitig disposed — harmlos
            }
            catch (Exception ex)
            {
                LogError("DebouncedFileWatcher.Schedule fehlgeschlagen", ex);
            }
        }
    }

    private void OnDebounce(object? _)
    {
        EventHandler? handler;
        lock (_lock)
        {
            if (_disposed) return;
            handler = Triggered;
        }

        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogError("DebouncedFileWatcher.Triggered-Handler warf", ex);
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        Timer? timer;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            watcher = _watcher;
            timer = _timer;
            _watcher = null;
            _timer = null;
        }

        // Erst Watcher stoppen (keine neuen Events), dann Timer (keine
        // pending-Callbacks mehr die auf disposed Watcher zugreifen).
        try
        {
            if (watcher != null)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
        }
        catch (Exception ex)
        {
            LogError("DebouncedFileWatcher.Dispose: Watcher-Dispose fehlgeschlagen", ex);
        }

        try
        {
            timer?.Dispose();
        }
        catch (Exception ex)
        {
            LogError("DebouncedFileWatcher.Dispose: Timer-Dispose fehlgeschlagen", ex);
        }
    }

    /// <summary>
    /// Interner Error-Logger. Trace statt <see cref="Diagnostics.Log"/> weil der Watcher
    /// auch in Test- und CLI-Kontexten laufen soll, wo das VS-Output-Window-Target
    /// nicht initialisiert ist. <see cref="Diagnostics.Log"/> hoert auf Trace mit —
    /// wenn in einer VS-Extension eine <see cref="TraceListener"/> registriert ist,
    /// landet die Meldung trotzdem im Output-Window.
    /// </summary>
    private static void LogTrace(string message, Exception? ex = null)
    {
        try
        {
            var full = ex == null ? message : $"{message}: {ex}";
            Trace.TraceError("[DebouncedFileWatcher] " + full);
        }
        catch
        {
            // Trace-Fehler darf den Watcher nicht lahmlegen
        }
    }

    private static void LogError(string message, Exception ex) => LogTrace(message, ex);
}
