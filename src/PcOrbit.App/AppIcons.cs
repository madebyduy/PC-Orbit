using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcOrbit.Core.Apps;

namespace PcOrbit.App;

/// <summary>
/// A product's real logo for the catalogue, from the two places it can honestly come from.
/// </summary>
/// <remarks>
/// <para>
/// An installed product has its icon on disk already: the uninstall entry names the file Windows
/// draws it from, and the shell extracts it the same way Add or Remove Programs does. That is the
/// first choice, and it involves no network.
/// </para>
/// <para>
/// A product that is not installed has nothing on disk. Its publisher's website does, as the site
/// icon, and that is the product's own logo as the publisher themselves publish it — which is what
/// makes showing it defensible in a way that bundling two dozen trademarks in the repository is
/// not. Fetched once, kept under <c>%LOCALAPPDATA%\PC Orbit\icons</c>, and never fetched again for
/// that product. The request carries the publisher's domain and nothing about the user or the
/// machine; the resolver is DuckDuckGo's icon service, chosen because it does not log lookups.
/// </para>
/// <para>
/// Null when neither source has anything, or the network is off. The card then shows the category
/// glyph, which is a truthful answer and not a broken image.
/// </para>
/// </remarks>
internal static class AppIcons
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly Dictionary<string, ImageSource?> Memory = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lock Gate = new();

    private static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PC Orbit",
        "icons");

    /// <summary>The logo for a catalogue entry, or null.</summary>
    /// <param name="installedIconPath">
    /// The file Windows draws this product's icon from, when it is installed and the uninstall
    /// entry says. Wins over the website, because it is the exact build the user has.
    /// </param>
    public static async Task<ImageSource?> ForAsync(CatalogApp app, string? installedIconPath)
    {
        ArgumentNullException.ThrowIfNull(app);

        lock (Gate)
        {
            if (Memory.TryGetValue(app.Id, out ImageSource? remembered))
            {
                return remembered;
            }
        }

        ImageSource? icon = FromDisk(installedIconPath) ?? await FromSiteAsync(app).ConfigureAwait(false);

        lock (Gate)
        {
            Memory[app.Id] = icon;
        }

        return icon;
    }

    /// <summary>Forget what was remembered for one product, so a fresh install gets a fresh look.</summary>
    public static void Forget(string id)
    {
        lock (Gate)
        {
            Memory.Remove(id);
        }
    }

    private static ImageSource? FromDisk(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? ShellIcons.For(path, large: true) : null;

    private static async Task<ImageSource?> FromSiteAsync(CatalogApp app)
    {
        if (app.Homepage is null || !Uri.TryCreate(app.Homepage, UriKind.Absolute, out Uri? home))
        {
            return null;
        }

        string file = Path.Combine(CacheDirectory, Safe(app.Id) + ".ico");

        try
        {
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(CacheDirectory);

                byte[] bytes = await Http
                    .GetByteArrayAsync(new Uri($"https://icons.duckduckgo.com/ip3/{home.Host}.ico"))
                    .ConfigureAwait(false);

                // The service answers a tiny placeholder rather than 404 for a site it does not
                // know. Under a few hundred bytes is that placeholder, and a placeholder cached is a
                // placeholder shown forever — so it is not written.
                if (bytes.Length < 400)
                {
                    return null;
                }

                await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
            }

            return Decode(file);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            // Offline, blocked, or a disk that would not take the file. The glyph is the answer.
            return null;
        }
    }

    private static ImageSource? Decode(string file)
    {
        try
        {
            using FileStream stream = File.OpenRead(file);

            // The decoder is chosen by the bytes, not the extension: the service hands back ICO or
            // PNG depending on what the site published, and both arrive under the same name.
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            // An ICO can carry several sizes; the largest is the one that scales down cleanly.
            BitmapFrame frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();

            frame.Freeze();

            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or InvalidOperationException)
        {
            // Something the decoder could not read was cached. Remove it so the next run retries.
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A file we cannot delete is not worth failing the page over.
            }

            return null;
        }
    }

    private static string Safe(string id) =>
        string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
}
