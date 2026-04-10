using System;

namespace Grattlersoft.VsExtension.Core.Diagnostics;

/// <summary>
/// Hilfs-Konstrukt fuer VS-Extensions: kapselt das allgegenwaertige
/// <c>try { ... } catch (Exception ex) { Log.Error(context, ex); }</c>-Muster.
///
/// Hintergrund: In VS-Extensions (MEF) werden geworfene Exceptions still
/// geschluckt. Deshalb muss jede fehlbare Methode ihre Exception *selbst*
/// fangen und loggen — ein erneutes <c>throw</c> ergibt keinen Sinn.
///
/// <example>
/// <code>
/// Safe.Run("VirtualRootNode.Initialize", () =>
/// {
///     ThreadHelper.ThrowIfNotOnUIThread();
///     RefreshChildren(force: true);
/// });
///
/// var config = Safe.Run("ConfigLoader.Load", fallback: null, () =>
/// {
///     return JsonConvert.DeserializeObject&lt;Config&gt;(json);
/// });
/// </code>
/// </example>
///
/// Nicht verwenden in: Konsolen-/Service-Code (dort soll <c>throw</c> greifen),
/// oder Hot-Path-Schleifen, in denen die Delegate-Allocation teuer wird.
/// </summary>
public static class Safe
{
    /// <summary>
    /// Fuehrt <paramref name="action"/> aus und loggt eine geworfene Exception
    /// unter dem angegebenen <paramref name="context"/>. Wirft nicht weiter.
    /// </summary>
    public static void Run(string context, Action action)
    {
        if (action == null) return;

        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error(context, ex);
        }
    }

    /// <summary>
    /// Fuehrt <paramref name="func"/> aus und gibt deren Ergebnis zurueck.
    /// Bei Exception wird <paramref name="fallback"/> zurueckgegeben und unter
    /// <paramref name="context"/> geloggt.
    /// </summary>
    public static T Run<T>(string context, T fallback, Func<T> func)
    {
        if (func == null) return fallback;

        try
        {
            return func();
        }
        catch (Exception ex)
        {
            Log.Error(context, ex);
            return fallback;
        }
    }
}
