using System.IO;
using System.Text.Json;

namespace PcOrbit.App;

/// <summary>
/// The handful of things the app remembers between runs.
/// </summary>
/// <remarks>
/// <para>
/// Preferences only — never a reading, never a plan, never anything about the machine. Those live
/// in the store with their evidence. This file is the answer to "why does it open in English again"
/// and "why is the window small again", and to the one preference that changes what the app can
/// do: whether to ask for administrator rights every time it starts.
/// </para>
/// <para>
/// A file under <c>%LOCALAPPDATA%\PC Orbit</c>, written on close and read on open. Unreadable or
/// absent means defaults, silently: a settings file is not something to fail to start over.
/// </para>
/// </remarks>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PC Orbit",
        "settings.json");

    /// <summary>"vi" or "en". Null until the user has chosen, in which case the CLI default applies.</summary>
    public string? Locale { get; set; }

    /// <summary>
    /// Ask for administrator rights on every start.
    /// </summary>
    /// <remarks>
    /// Off by default, because a UAC prompt on every launch is a real cost and the read-only picture
    /// needs no rights at all (spec 21.10). On for someone who lives in the BIOS page, where nothing
    /// can be read or changed without it.
    /// </remarks>
    public bool AlwaysElevate { get; set; }

    /// <summary>The section the rail was on when the window closed, by title key.</summary>
    public string? LastSection { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public bool WindowMaximized { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Defaults. A preferences file that will not parse is not a reason to refuse to open.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to tell the user at the moment they are closing the window.
        }
    }
}
