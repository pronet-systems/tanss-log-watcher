using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Storage;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Was im Fenster „Über“ steht.
/// </summary>
/// <remarks>
/// <para><b>Es ist kein Schaufenster, sondern die erste Seite jeder Fehlermeldung.</b> Wer
/// anruft, weil etwas nicht geht, wird als Erstes nach der Fassung gefragt — und danach nach
/// dem Ort der Konfiguration, weil dort die Antwort steht. Beides hier zu finden ist schneller
/// als beides zu erklären.</para>
///
/// <para><b>Alles gelesen, nichts geschrieben.</b> Die Fassung kommt aus dem Erzeugnis selbst
/// und nicht aus einer zweiten Stelle, die man zu pflegen vergisst.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class AboutViewModel : ObservableObject
{
    /// <summary>Baut die Angaben aus dem laufenden Erzeugnis.</summary>
    public AboutViewModel()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();

        Product = Read<AssemblyProductAttribute>(assembly, a => a.Product) ?? "TANSS Log-Watcher";
        Company = Read<AssemblyCompanyAttribute>(assembly, a => a.Company) ?? string.Empty;
        Copyright = Read<AssemblyCopyrightAttribute>(assembly, a => a.Copyright) ?? string.Empty;

        // Aus InformationalVersion und nicht aus Version: Letztere traegt eine vierte Stelle,
        // die nichts aussagt, und verliert einen Vorabzusatz wie "-beta.1".
        Version = Read<AssemblyInformationalVersionAttribute>(assembly, a => a.InformationalVersion)
            ?? assembly.GetName().Version?.ToString()
            ?? "unbekannt";

        // Der Zusatz, den der Uebersetzer an die Fassung haengt (+Commit), gehoert nicht in
        // die Anzeige: Er ist fuer einen Menschen keine Fassungsnummer.
        int plus = Version.IndexOf('+', StringComparison.Ordinal);
        Version = plus >= 0 ? Version[..plus] : Version;

        Runtime = RuntimeInformation.FrameworkDescription;
        OperatingSystemName = RuntimeInformation.OSDescription;
        ConfigPath = StoragePaths.ConfigFile;
        StatePath = StoragePaths.StateDirectory;
    }

    /// <summary>Der Name des Erzeugnisses.</summary>
    public string Product { get; }

    /// <summary>Das Haus dahinter; im Fenster als „Entwicklung“ beschriftet.</summary>
    public string Company { get; }

    /// <summary>Der Urhebervermerk.</summary>
    public string Copyright { get; }

    /// <summary>Die Fassung, ohne den angehängten Baustand.</summary>
    public string Version { get; }

    /// <summary>Die Laufzeitumgebung, etwa <c>.NET 10.0.0</c>.</summary>
    public string Runtime { get; }

    /// <summary>Das Betriebssystem, wie es sich selbst nennt.</summary>
    public string OperatingSystemName { get; }

    /// <summary>Wo die Konfiguration liegt.</summary>
    public string ConfigPath { get; }

    /// <summary>Wo der Laufzeitzustand liegt.</summary>
    public string StatePath { get; }

    /// <summary>Die Anschrift des Hauses im Netz.</summary>
    public static string Website => "https://www.pronet-systems.de";

    /// <summary>Die Rückmeldung der letzten Handlung.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    /// <summary>Gibt es eine Rückmeldung?</summary>
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Öffnet die Anschrift im eingestellten Browser.</summary>
    [RelayCommand]
    private void OpenWebsite()
    {
        try
        {
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Website,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = "Die Seite liess sich nicht öffnen: " + Redaction.Scrub(ex.Message);
        }
    }

    /// <summary>Öffnet den Ordner mit der Konfiguration.</summary>
    [RelayCommand]
    private void OpenConfigFolder()
    {
        try
        {
            string folder = Path.GetDirectoryName(ConfigPath) ?? StoragePaths.ConfigDirectory;

            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = "Der Ordner liess sich nicht öffnen: " + Redaction.Scrub(ex.Message);
        }
    }

    private static string? Read<T>(Assembly assembly, Func<T, string?> pick)
        where T : Attribute =>
        assembly.GetCustomAttribute<T>() is { } attribute ? pick(attribute) : null;
}
