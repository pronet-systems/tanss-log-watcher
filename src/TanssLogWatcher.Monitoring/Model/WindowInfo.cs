namespace TanssLogWatcher.Monitoring.Model;

/// <summary>
/// Ein sichtbares Fenster der obersten Ebene mit nicht leerem Titel.
/// </summary>
/// <param name="Title">
/// Der Fenstertitel in UTF-16, so wie Windows ihn führt.
/// <para><b>Datenschutz:</b> Titel tragen Kundennamen, Rechnernamen und E-Mail-Betreffe. Sie
/// gehören niemals in ein Protokoll unterhalb der Stufe „Trace“ und in kein Fehlerbild, das den
/// Rechner verlässt.</para>
/// </param>
/// <param name="ProcessId">Die Kennung des besitzenden Prozesses.</param>
/// <param name="WindowHandle">Das Fensterhandle. Dient allein der Zuordnung bei Titeländerungen.</param>
/// <param name="IsOwned">
/// Wahr, wenn das Fenster einem anderen Fenster gehört — also ein Dialog oder ein frei schwebendes
/// Werkzeugfenster ist (<c>GetWindow</c> mit <c>GW_OWNER</c> liefert einen Besitzer).
/// <para>Nur besitzerlose Fenster kommen als Hauptfenster eines Prozesses in Frage. Ohne diese
/// Unterscheidung würde jeder gerade offene Dialog das vermeintliche Hauptfenster verschieben und
/// bei <see cref="MonitoringProfile.MonitorOnlyMainWindowHandle"/> das echte Hauptfenster
/// unterdrücken.</para>
/// </param>
public readonly record struct WindowInfo(string Title, int ProcessId, nint WindowHandle, bool IsOwned = false);
