using System;
using System.Collections.Generic;

namespace Grattlersoft.VsExtension.Core.Options;

/// <summary>
/// Basisklasse fuer Extension-Optionen mit automatischer Change-Detection.
/// Feuert Changed-Event nur wenn sich ein Wert tatsaechlich aendert.
/// </summary>
public abstract class ObservableOptions
{
    public event EventHandler? Changed;

    /// <summary>
    /// Setzt einen Wert und feuert Changed wenn er sich unterscheidet.
    /// </summary>
    protected bool SetValue<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Feuert Changed-Event manuell (z.B. wenn ein komplexes Objekt ersetzt wird).
    /// </summary>
    protected void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
