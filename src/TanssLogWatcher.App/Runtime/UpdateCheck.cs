using System.Globalization;
using System.Text.Json.Serialization;

namespace TanssLogWatcher.App.Runtime;

/// <summary>
/// Eine verfügbare neue Fassung, so wie die Veröffentlichung sie beschreibt.
/// </summary>
/// <param name="Version">Die Fassung ohne das führende <c>v</c> der Marke.</param>
/// <param name="Tag">Die Marke, etwa <c>v0.2.0</c>.</param>
/// <param name="SetupUrl">Die Adresse des Setups.</param>
/// <param name="SetupName">Der Dateiname des Setups.</param>
/// <param name="ChecksumUrl">Die Adresse der Prüfsummendatei, oder <c>null</c>.</param>
/// <param name="Bytes">Die Größe des Setups in Bytes.</param>
/// <param name="ReleaseUrl">Die Seite der Veröffentlichung, für „Was ist neu“.</param>
public sealed record AvailableUpdate(Version Version, string Tag, Uri SetupUrl, string SetupName,
                                     Uri? ChecksumUrl, long Bytes, Uri ReleaseUrl)
{
    /// <summary>Die Größe in Megabyte, für die Anzeige.</summary>
    public string SizeText => string.Create(CultureInfo.CurrentCulture, $"{Bytes / 1024d / 1024d:N1} MB");
}

/// <summary>Wie eine Prüfung auf Aktualisierungen ausgegangen ist.</summary>
public enum UpdateCheckState
{
    /// <summary>Noch nicht gefragt.</summary>
    Unknown,

    /// <summary>Wird gerade gefragt.</summary>
    Checking,

    /// <summary>Die laufende Fassung ist die neueste.</summary>
    UpToDate,

    /// <summary>Es liegt eine neuere Fassung vor.</summary>
    UpdateAvailable,

    /// <summary>Die Prüfung selbst ist fehlgeschlagen.</summary>
    Failed,
}

/// <summary>
/// Die Teile einer GitHub-Veröffentlichung, die hier gebraucht werden.
/// </summary>
/// <remarks>
/// Bewusst nur diese vier Felder. Die Antwort von GitHub trägt über hundert; sie alle
/// abzubilden hiesse, ein fremdes Schema zu pflegen, das sich jederzeit ändern darf.
/// </remarks>
internal sealed record GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; init; } = string.Empty;

    [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }

    [JsonPropertyName("draft")] public bool Draft { get; init; }

    [JsonPropertyName("html_url")] public string HtmlUrl { get; init; } = string.Empty;

    [JsonPropertyName("assets")] public IReadOnlyList<GitHubAsset> Assets { get; init; } = [];
}

/// <summary>Eine angehängte Datei einer Veröffentlichung.</summary>
internal sealed record GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; init; } = string.Empty;

    [JsonPropertyName("size")] public long Size { get; init; }
}
