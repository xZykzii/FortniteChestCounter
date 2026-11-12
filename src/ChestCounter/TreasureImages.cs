namespace ChestCounter;

/// <summary>
/// Pictures of the treasure collection, downloaded once into the data folder ("images"): the report shows the local
/// copies, so opening or refreshing it never calls the internet. They come from the project's GitHub repository
/// (assets/treasures, always available), or from fortnite.bot for a picture not added to the repository yet.
/// Downloaded even with the updates turned off: the report needs them, and they are only pictures.
/// </summary>
public static class TreasureImages
{
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Folder of the local copies (data folder\images), set at startup.</summary>
    public static string? Folder { get; set; }

    /// <summary>Copy kept in the repository (same file name as the local copy).</summary>
    private static string RepoUrl(string url) => $"https://raw.githubusercontent.com/{AppInfo.Repo}/main/assets/treasures/{LocalName(url)}";

    /// <summary>File name of the local copy: the picture's name, plus its version when the address has one ("?v=3").</summary>
    public static string LocalName(string url)
    {
        var uri = new Uri(url);
        var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        var ext = Path.GetExtension(uri.AbsolutePath);
        var query = new string(uri.Query.TrimStart('?').Where(char.IsAsciiLetterOrDigit).ToArray());
        return query.Length > 0 ? $"{name}-{query}{ext}" : name + ext;
    }

    /// <summary>
    /// What the report's &lt;img src&gt; points to: the local copy (path relative to the report, which sits in the data
    /// folder), or null when there is none yet (no picture rather than a call to fortnite.bot).
    /// </summary>
    public static string? Src(string? url)
    {
        if (url == null || Folder == null)
        {
            return null;
        }
        var name = LocalName(url);
        using var embedded = typeof(TreasureImages).Assembly.GetManifestResourceStream("ChestCounter.Treasures." + name);
        if (embedded != null)
        {
            using var copy = new MemoryStream();
            embedded.CopyTo(copy);
            return "data:image/webp;base64," + Convert.ToBase64String(copy.ToArray());
        }
        return File.Exists(Path.Combine(Folder, name)) ? "images/" + name : null;
    }

    // The pictures named by the current content.json (a new week brings its own).
    private static IEnumerable<string> Urls() =>
        TreasureCatalog.Weeks.SelectMany(w => w.Prizes).Prepend(TreasureCatalog.PirateShopAccess)
            .Select(p => p.Image).OfType<string>().Distinct();

    /// <summary>True when a picture of the catalog has no local copy yet.</summary>
    public static bool Missing() => Folder != null && Urls().Any(u => Src(u) == null);

    /// <summary>Downloads the pictures that have no local copy yet. Returns how many were added.</summary>
    public static async Task<int> DownloadMissingAsync()
    {
        if (Folder == null)
        {
            return 0;
        }
        var added = 0;
        try
        {
            Directory.CreateDirectory(Folder);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ChestCounter/{AppInfo.VersionText} (+{AppInfo.RepoUrl})");
            foreach (var url in Urls())
            {
                var path = Path.Combine(Folder, LocalName(url));
                if (Src(url) != null)
                {
                    continue;
                }
                foreach (var source in new[] { RepoUrl(url), url })
                {
                    if (await TryDownload(http, source, path))
                    {
                        added++;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("No se pudieron preparar las imágenes del catálogo", ex);
            // no connection: the collection is shown without pictures
        }
        return added;
    }

    /// <summary>One picture into <paramref name="path"/>; false (and nothing written) when it is not a picture or cannot be had.</summary>
    private static async Task<bool> TryDownload(HttpClient http, string url, string path)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
            {
                return false;
            }
            using var input = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[MaxBytes + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await input.ReadAsync(buffer.AsMemory(count));
                if (read == 0) break;
                count += read;
            }
            if (count > MaxBytes) return false;
            var bytes = buffer.AsSpan(0, count).ToArray();
            // raw.githubusercontent.com serves pictures as application/octet-stream: check the WebP / PNG / JPEG signature.
            var webp = bytes.Length > 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P';
            var png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G';
            var jpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;
            if (!(webp || png || jpeg) || bytes.Length > MaxBytes)
            {
                return false;
            }
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("No se pudo descargar una imagen del catálogo", ex);
            return false; // tried again at the next start
        }
    }
}
