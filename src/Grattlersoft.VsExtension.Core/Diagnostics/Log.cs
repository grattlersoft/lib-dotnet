using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Grattlersoft.VsExtension.Core.Diagnostics;

/// <summary>
/// Zentraler Logger fuer alle Grattlersoft VS Extensions.
/// Unterstuetzt mehrere Extensions gleichzeitig — jede bekommt eigene
/// Log-Datei (%TEMP%) und eigene Output-Window-Pane.
///
/// Initialize() kann mehrfach aufgerufen werden (einmal pro Extension).
/// Info/Warn/Error schreiben in ALLE registrierten Extensions.
/// </summary>
public static class Log
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, LogTarget> _targets = new();
    private static readonly Dictionary<Assembly, string> _assemblyTargets = new();
    private static readonly AsyncLocal<string?> _currentExtension = new();

    /// <summary>
    /// Registriert eine Extension als Log-Ziel.
    /// Kann mehrfach fuer verschiedene Extensions aufgerufen werden.
    /// Jede Extension bekommt eigene Log-Datei und Output-Window-Pane.
    /// </summary>
    public static void Initialize(IServiceProvider serviceProvider, string extensionName, Assembly extensionAssembly)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var logFilePath = Path.Combine(Path.GetTempPath(), $"{extensionName}.log");

        try
        {
            File.WriteAllText(logFilePath,
                $"=== {extensionName} Log gestartet {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[{extensionName}] Log-Datei Fehler: {ex.Message}");
        }

        IVsOutputWindowPane? outputPane = null;
        try
        {
            var outputWindow = (IVsOutputWindow?)serviceProvider.GetService(typeof(SVsOutputWindow));
            if (outputWindow != null)
            {
                var paneGuid = GenerateGuidFromName(extensionName);
                outputWindow.CreatePane(ref paneGuid, extensionName, 1, 1);
                outputWindow.GetPane(ref paneGuid, out outputPane);
            }
        }
        catch (Exception ex)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [WARN] Output Window Pane Fehler: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(line);
            WriteToFile(logFilePath, line);
        }

        lock (_lock)
        {
            _targets[extensionName] = new LogTarget(extensionName, logFilePath, outputPane);
            _assemblyTargets[extensionAssembly] = extensionName;
        }

        using (UseExtension(extensionName))
        {
            Info($"[{extensionName}] Log initialisiert");
            Info($"[{extensionName}] Log-Datei: file:///{logFilePath.Replace('\\', '/')}");
        }
    }

    private static Guid GenerateGuidFromName(string name)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("Grattlersoft.VsExtension." + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    public static void Debug(string message)
    {
#if DEBUG
        Write("DEBUG", message);
#endif
    }

#pragma warning disable VSTHRD010 // OutputStringThreadSafe ist per Design thread-safe
    private static void Write(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] {message}";

        System.Diagnostics.Debug.WriteLine(line);

        lock (_lock)
        {
            var target = ResolveTarget();
            if (target != null)
            {
                try { target.OutputPane?.OutputStringThreadSafe(line + Environment.NewLine); }
                catch { /* Output-Pane kann disposed sein */ }

                WriteToFile(target.LogFilePath, line);
                return;
            }

            foreach (var fallbackTarget in _targets.Values)
            {
                try { fallbackTarget.OutputPane?.OutputStringThreadSafe(line + Environment.NewLine); }
                catch { /* Output-Pane kann disposed sein */ }

                WriteToFile(fallbackTarget.LogFilePath, line);
            }

            if (_targets.Count == 0)
                WriteToFile(Path.Combine(Path.GetTempPath(), "Grattlersoft.VsExtension.log"), line);
        }
    }
#pragma warning restore VSTHRD010

    public static IDisposable UseExtension(string extensionName)
    {
        var previous = _currentExtension.Value;
        _currentExtension.Value = extensionName;
        return new ExtensionScope(previous);
    }

    private static LogTarget? ResolveTarget()
    {
        var current = _currentExtension.Value;
        if (current != null && _targets.TryGetValue(current, out var currentTarget))
        {
            return currentTarget;
        }

        var callingAssembly = GetCallingAssembly();
        if (callingAssembly != null
            && _assemblyTargets.TryGetValue(callingAssembly, out var extensionName)
            && _targets.TryGetValue(extensionName, out var assemblyTarget))
        {
            return assemblyTarget;
        }

        return null;
    }

    private static Assembly? GetCallingAssembly()
    {
        var logAssembly = typeof(Log).Assembly;
        var stackTrace = new StackTrace();
        foreach (var frame in stackTrace.GetFrames() ?? Array.Empty<StackFrame>())
        {
            var method = frame.GetMethod();
            var assembly = method?.DeclaringType?.Assembly;
            if (assembly == null || assembly == logAssembly)
            {
                continue;
            }

            return assembly;
        }

        return null;
    }

    private static void WriteToFile(string path, string message)
    {
        try { File.AppendAllText(path, message + Environment.NewLine); }
        catch { /* Datei-Schreibfehler still ignorieren */ }
    }

    /// <summary>
    /// Pfad zur Log-Datei einer bestimmten Extension.
    /// Gibt den Default-Pfad zurueck wenn die Extension nicht registriert ist.
    /// </summary>
    public static string GetLogFilePath(string extensionName)
    {
        lock (_lock)
        {
            return _targets.TryGetValue(extensionName, out var target)
                ? target.LogFilePath
                : Path.Combine(Path.GetTempPath(), $"{extensionName}.log");
        }
    }

    private sealed class ExtensionScope : IDisposable
    {
        private readonly string? _previous;

        public ExtensionScope(string? previous) => _previous = previous;

        public void Dispose() => _currentExtension.Value = _previous;
    }

    private sealed class LogTarget
    {
        public string ExtensionName { get; }
        public string LogFilePath { get; }
        public IVsOutputWindowPane? OutputPane { get; }

        public LogTarget(string extensionName, string logFilePath, IVsOutputWindowPane? outputPane)
        {
            ExtensionName = extensionName;
            LogFilePath = logFilePath;
            OutputPane = outputPane;
        }
    }
}
