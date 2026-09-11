using System.Text.Json.Nodes;

namespace TanssLogWatcher.Storage.Config;

/// <summary>
/// Findet, lädt und schreibt <c>config.json</c>.
/// </summary>
/// <remarks>
/// Als Schnittstelle geführt, damit der Einrichtungsassistent und die Tests gegen dieselbe
/// Zusage arbeiten wie der Dienst — und damit eine spätere Ablage in der Registrierung oder
/// per Gruppenrichtlinie nichts oberhalb dieser Schicht ändert.
/// </remarks>
public interface IConfigStore
{
    /// <summary>Pfad der Konfigurationsdatei.</summary>
    string Path { get; }

    /// <summary>Pfad des Zwischenstands des Einrichtungsassistenten.</summary>
    string PartialPath { get; }

    /// <summary>Liegt bereits eine Konfiguration vor?</summary>
    bool Exists();

    /// <summary>Lädt und prüft die Konfiguration.</summary>
    /// <exception cref="ConfigException">Datei fehlt oder ist kein gültiges JSON.</exception>
    /// <exception cref="ConfigValidationException">Die Datei verletzt mindestens eine Regel.</exception>
    AppConfig Load();

    /// <summary>
    /// Prüft und schreibt die Konfiguration atomar.
    /// </summary>
    /// <remarks>
    /// Geprüft wird <b>vor</b> dem Schreiben. Eine fehlerhafte Konfiguration auf die Platte
    /// zu legen hieße, den Dienst beim nächsten Start mit einer Datei zu starten, die er
    /// selbst geschrieben hat und nicht laden kann.
    /// </remarks>
    /// <exception cref="ConfigValidationException">Die Konfiguration verletzt mindestens eine Regel.</exception>
    void Save(AppConfig config);

    /// <summary>
    /// Legt den Zwischenstand des Einrichtungsassistenten ab.
    /// </summary>
    /// <remarks>
    /// Bewusst ungeprüft und als freies JSON-Objekt: Ein Zwischenstand ist per Definition
    /// unvollständig. Sein Zweck ist, dass ein Abbruch mit Strg-C die bereits beantworteten
    /// Fragen nicht verliert.
    /// </remarks>
    void SavePartial(JsonObject draft);

    /// <summary>Liest den Zwischenstand. <c>null</c>, wenn keiner vorliegt oder er unlesbar ist.</summary>
    JsonObject? LoadPartial();

    /// <summary>Entfernt den Zwischenstand. Nach erfolgreichem <see cref="Save"/> aufzurufen.</summary>
    void ClearPartial();
}
