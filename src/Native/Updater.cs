using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace CHelper.Native;

/// <summary>A newer release on GitHub with its installer and checksum.</summary>
public sealed record UpdateInfo(Version Version, string SetupUrl, string SetupName, string ShaUrl, long Size);

/// <summary>
/// Checks GitHub Releases for a newer C-Helper and installs it: downloads CHelper-Setup-x.y.z.exe, checks it
/// against the .sha256 asset the release workflow publishes, then runs it silently. The installer closes
/// C-Helper cleanly and starts it again afterwards (its /UPDATE switch).
/// </summary>
public static class Updater
{
    private const string LatestUrl = "https://api.github.com/repos/th4rv4/CHelper/releases/latest";
    public const string ReleasesPage = "https://github.com/th4rv4/CHelper/releases";

    public static Version Current { get; } = ParseVersion(Application.ProductVersion) ?? new Version(0, 0, 0);

    public static string CurrentText => $"v{Current.ToString(3)}";

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("C-Helper", Current.ToString(3)));
        return http;
    }

    /// <summary>"v0.9.1", "0.9.1" or "0.9.1+abc" → 0.9.1.</summary>
    public static Version ParseVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        if (!Version.TryParse(text, out var v)) return null;
        // Compare as major.minor.build so "0.9.1" and "0.9.1.0" are the same version.
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>
    /// Returns the latest release if it is newer than this copy and has an installer with a checksum, null if
    /// this copy is up to date. Throws when GitHub can't be reached (offline, private repo, rate limit).
    /// </summary>
    public static async Task<UpdateInfo> CheckAsync()
    {
        using var http = NewClient(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        var root = doc.RootElement;

        var latest = ParseVersion(root.GetProperty("tag_name").GetString());
        if (latest == null || latest <= Current) return null;

        string setupName = $"CHelper-Setup-{latest.ToString(3)}.exe";
        string setupUrl = null, shaUrl = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString();
            string url = asset.GetProperty("browser_download_url").GetString();
            if (string.Equals(name, setupName, StringComparison.OrdinalIgnoreCase))
            {
                setupUrl = url;
                size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            }
            else if (string.Equals(name, setupName + ".sha256", StringComparison.OrdinalIgnoreCase))
            {
                shaUrl = url;
            }
        }

        if (setupUrl == null || shaUrl == null)
        {
            Log.Info($"Update {latest.ToString(3)} found but its installer or checksum is missing; skipped");
            return null;
        }
        return new UpdateInfo(latest, setupUrl, setupName, shaUrl, size);
    }

    /// <summary>
    /// Downloads and verifies the installer, reporting 0-100 %. Returns its path; throws if the download fails
    /// or the checksum doesn't match (the file is deleted then).
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int> progress)
    {
        string dir = Path.Combine(Path.GetTempPath(), "CHelper-Update");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, info.SetupName);

        using var http = NewClient(TimeSpan.FromMinutes(10));

        // "<hash>  CHelper-Setup-x.y.z.exe"
        string shaText = await WithRetries("checksum", info.ShaUrl, () => http.GetStringAsync(info.ShaUrl)).ConfigureAwait(false);
        string expected = shaText.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[0];
        if (expected.Length != 64) throw new InvalidDataException("The release checksum file is not valid.");

        using (var resp = await WithRetries("installer", info.SetupUrl, async () =>
               {
                   var r = await http.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                   if (!r.IsSuccessStatusCode)
                   {
                       var code = r.StatusCode;
                       string host = r.RequestMessage?.RequestUri?.Host ?? "?";
                       r.Dispose();
                       throw new HttpRequestException($"{(int)code} {code} from {host}", null, code);
                   }
                   return r;
               }).ConfigureAwait(false))
        {
            long total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long done = 0;
            int lastPct = -1, read;
            while ((read = await src.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                done += read;
                if (total > 0)
                {
                    int pct = (int)Math.Min(100, done * 100 / total);
                    if (pct != lastPct) progress?.Report(lastPct = pct);
                }
            }
        }

        string actual;
        await using (var fs = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs).ConfigureAwait(false));

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new InvalidDataException($"Checksum mismatch (expected {expected}, got {actual}).");
        }

        Log.Info($"Update {info.Version.ToString(3)} downloaded and verified ({actual})");
        return path;
    }

    /// <summary>
    /// GitHub's download servers sometimes answer 5xx for a few seconds: try 3 times (2 s, then 5 s apart) and log
    /// each failure with the URL, so a lasting problem can be told apart from a hiccup.
    /// </summary>
    private static async Task<T> WithRetries<T>(string what, string url, Func<Task<T>> attempt)
    {
        int[] waits = { 2000, 5000 };
        for (int i = 0; ; i++)
        {
            try
            {
                return await attempt().ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && i < waits.Length)
            {
                Log.Info($"Update {what} download attempt {i + 1} failed ({ex.Message}) for {url}; retrying");
                await Task.Delay(waits[i]).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Starts the installer silently. C-Helper must quit right after; the installer closes any copy still
    /// running and starts the new one when it's done.
    /// </summary>
    public static void RunInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            // Already elevated, so this doesn't prompt; keeps Setup elevated either way.
            Verb = "runas",
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE",
        });
        Log.Info($"Started installer {Path.GetFileName(path)}");
    }

    /// <summary>Removes installers left over from an earlier update.</summary>
    public static void CleanUp()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "CHelper-Update");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // The installer may still be finishing; try again next start.
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
