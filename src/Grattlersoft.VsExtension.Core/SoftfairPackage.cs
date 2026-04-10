using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Grattlersoft.VsExtension.Core.Diagnostics;
using Task = System.Threading.Tasks.Task;

namespace Grattlersoft.VsExtension.Core;

/// <summary>
/// Basisklasse fuer alle Softfair VS Extension Packages.
/// Kuemmert sich automatisch um:
/// - Boot-Log (vor Log.Initialize, ueberlebt alle Fehler)
/// - Log.Initialize mit eigenem Output-Window-Pane
/// - try/catch um InitializeAsync mit vollstaendigem Error-Logging
/// - [ProvideBindingPath] ist bereits gesetzt
/// - Dependency-Check: Prueft ob alle DLLs im Extension-Ordner vorhanden sind
/// - MEF-Health-Check: Warnt wenn erwartete MEF-Exports nicht geladen wurden
///
/// Abgeleitete Klassen implementieren nur noch InitializeExtensionAsync().
/// Optional: ExpectedMefExports fuer den Health-Check ueberschreiben.
/// </summary>
[ProvideBindingPath]
public abstract class SoftfairPackage : AsyncPackage
{
    private string _bootLogPath = null!;
    private readonly List<string> _bootMessages = new();
    private bool _bootLogFlushed;

    /// <summary>
    /// Name der Extension — wird fuer Log-Datei und Output-Window-Pane verwendet.
    /// </summary>
    protected abstract string ExtensionName { get; }

    /// <summary>
    /// Hier die Extension-spezifische Initialisierung implementieren.
    /// Wird automatisch auf dem UI-Thread aufgerufen, mit try/catch und Logging.
    /// </summary>
    protected abstract Task InitializeExtensionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Liste der MEF-Export-Typen die diese Extension erwartet.
    /// Wird nach der Initialisierung geprueft — fehlende Exports werden als ERROR geloggt.
    /// </summary>
    protected virtual IReadOnlyList<Type> ExpectedMefExports => Array.Empty<Type>();

    /// <summary>
    /// Liste der DLL-Dateinamen die im Extension-Ordner vorhanden sein muessen.
    /// Wird beim Start geprueft — fehlende DLLs werden als ERROR geloggt.
    /// </summary>
    protected virtual IReadOnlyList<string> RequiredDependencies => Array.Empty<string>();

    protected sealed override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        _bootLogPath = Path.Combine(Path.GetTempPath(), $"{ExtensionName}.boot.log");

        WriteBootLog("InitializeAsync ENTRY");

        try
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            WriteBootLog("SwitchedToMainThread");

            await base.InitializeAsync(cancellationToken, progress);

            Log.Initialize(this, ExtensionName, GetType().Assembly);
            using var logScope = Log.UseExtension(ExtensionName);
            FlushBootLogToLogger();
            Log.Info($"{GetType().Name} InitializeAsync gestartet");

            CheckDependencies();
            await InitializeExtensionAsync(cancellationToken);
            CheckMefExports();

            Log.Info($"{GetType().Name} InitializeAsync abgeschlossen");
            WriteBootLog("InitializeAsync OK");
        }
        catch (Exception ex)
        {
            WriteBootLog($"FATAL: {ex}");
            try { Log.Error("InitializeAsync fehlgeschlagen", ex); }
            catch { /* Log selbst ist kaputt — Boot-Log hat den Fehler */ }
        }
    }

    /// <summary>
    /// Prueft ob alle benoetigten DLLs im Extension-Verzeichnis vorhanden sind.
    /// Fehlende DLLs verursachen stille MEF-Fehler (ReflectionTypeLoadException).
    /// </summary>
    private void CheckDependencies()
    {
        try
        {
            var extensionDir = Path.GetDirectoryName(GetType().Assembly.Location);
            if (extensionDir == null) return;

            Log.Info($"Extension-Verzeichnis: {extensionDir}");
            var existingDlls = Directory.GetFiles(extensionDir, "*.dll")
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Log.Info($"  {existingDlls.Count} DLLs im Verzeichnis");

            foreach (var dep in RequiredDependencies)
            {
                if (existingDlls.Contains(dep))
                    Log.Info($"  OK: {dep}");
                else
                    Log.Error($"  FEHLT: {dep} — MEF-Exports werden NICHT geladen! VSIX-Packaging pruefen.");
            }

            // Zusaetzlich: Alle referenzierten Assemblies pruefen
            var assembly = GetType().Assembly;
            foreach (var refName in assembly.GetReferencedAssemblies())
            {
                try { Assembly.Load(refName); }
                catch (FileNotFoundException)
                {
                    Log.Error($"  Assembly nicht ladbar: {refName.Name} — wird zur Laufzeit fehlen!");
                }
                catch { /* Andere Fehler ignorieren */ }
            }
        }
        catch (Exception ex)
        {
            Log.Error("CheckDependencies fehlgeschlagen", ex);
        }
    }

    /// <summary>
    /// Prueft ob die erwarteten MEF-Exports tatsaechlich im MEF-Container registriert sind.
    /// Wenn nicht: ERROR-Log mit konkretem Hinweis auf die Ursache.
    /// </summary>
    private void CheckMefExports()
    {
        try
        {
            if (ExpectedMefExports.Count == 0) return;

            var componentModel = (IComponentModel?)GetService(typeof(SComponentModel));
            if (componentModel == null)
            {
                Log.Error("IComponentModel nicht verfuegbar — kann MEF-Exports nicht pruefen");
                return;
            }

            foreach (var exportType in ExpectedMefExports)
            {
                try
                {
                    var exports = GetMefExports(componentModel, exportType)
                        .Where(e => exportType.IsAssignableFrom(e.GetType())
                                 && e.GetType().Assembly == GetType().Assembly)
                        .ToList();

                    if (exports.Count > 0)
                        Log.Info($"  MEF OK: {exportType.Name} ({exports.Count} Exports aus unserer Assembly)");
                    else
                        Log.Error($"  MEF FEHLT: {exportType.Name} — Kein Export aus {GetType().Assembly.GetName().Name} gefunden! " +
                                  "Pruefe: 1) Alle DLLs im VSIX? 2) ComponentModelCache loeschen 3) vsixmanifest MefComponent Asset?");
                }
                catch (Exception ex)
                {
                    Log.Error($"  MEF-Pruefung fuer {exportType.Name} fehlgeschlagen", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("CheckMefExports fehlgeschlagen", ex);
        }
    }

    private static IEnumerable<object> GetMefExports(IComponentModel componentModel, Type exportType)
    {
        try
        {
            var method = typeof(IComponentModel).GetMethod("GetExtensions", Type.EmptyTypes);
            if (method == null) return Array.Empty<object>();

            var generic = method.MakeGenericMethod(exportType);
            if (generic.Invoke(componentModel, null) is System.Collections.IEnumerable result)
            {
                return result.Cast<object>().ToList();
            }
        }
        catch
        {
            // Ignorieren: MEF-Check darf nie den Extension-Start blockieren
        }

        return Array.Empty<object>();
    }

    private void WriteBootLog(string message)
    {
        try
        {
            _bootMessages.Add(message);
            File.AppendAllText(_bootLogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine(
                $"[{ExtensionName}] Boot-Log Schreibfehler: {message}");
        }
    }

    private void FlushBootLogToLogger()
    {
        if (_bootLogFlushed || _bootMessages.Count == 0)
        {
            _bootLogFlushed = true;
            return;
        }

        foreach (var message in _bootMessages)
        {
            Log.Info(message);
        }

        _bootMessages.Clear();
        _bootLogFlushed = true;
    }
}
