using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace CentrePC.Core;

/// <summary>Connexion internet : vérification légère, mise en cache une minute.</summary>
public static class Network
{
    private static DateTime _last = DateTime.MinValue;
    private static bool _online;
    public static event Action Changed;

    static Network()
    {
        NetworkChange.NetworkAvailabilityChanged += (_, _) => { _last = DateTime.MinValue; Changed?.Invoke(); };
    }

    public static bool LastKnown => _online;

    public static async Task<bool> IsOnlineAsync(bool force = false)
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) { _last = DateTime.Now; return _online = false; }
        if (!force && DateTime.Now - _last < TimeSpan.FromSeconds(60)) return _online;
        try
        {
            // Adresse utilisée par Windows lui-même pour tester la connexion
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var txt = await c.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt");
            _online = txt.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch { _online = false; }
        _last = DateTime.Now;
        return _online;
    }
}

public class ReleaseInfo
{
    public Version Version { get; set; }
    public string Tag { get; set; }
    public string Notes { get; set; }
    public string ExeUrl { get; set; }
    public string ShaUrl { get; set; }
    public string PageUrl { get; set; }
}

/// <summary>
/// Mise à jour de Centre PC depuis les « Releases » du dépôt GitHub.
/// La nouvelle version est téléchargée, son empreinte SHA-256 vérifiée, puis elle remplace l'exe et redémarre.
/// </summary>
public static class AppUpdater
{
    public const string Owner = "hardy-kevin";
    public const string Repo = "Centre-PC";
    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"CentrePC/{Current}");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>Dernière version publiée, ou null (hors connexion, aucune version publiée, erreur).</summary>
    public static async Task<ReleaseInfo> GetLatestAsync()
    {
        if (!await Network.IsOnlineAsync()) return null;
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            var r = new ReleaseInfo
            {
                Version = new Version(v.Major, v.Minor, Math.Max(0, v.Build)),
                Tag = tag,
                Notes = root.TryGetProperty("body", out var b) ? b.GetString() : "",
                PageUrl = root.TryGetProperty("html_url", out var u) ? u.GetString() : RepoUrl + "/releases"
            };
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString();
                if (name.Equals("CentrePC.exe", StringComparison.OrdinalIgnoreCase)) r.ExeUrl = url;
                else if (name.Equals("CentrePC.exe.sha256", StringComparison.OrdinalIgnoreCase)) r.ShaUrl = url;
            }
            return r.ExeUrl == null ? null : r;
        }
        catch { return null; }
    }

    public static bool IsNewer(ReleaseInfo r) => r != null && r.Version > Current;

    /// <summary>Télécharge, vérifie et installe la nouvelle version, puis la lance. L'appli actuelle doit ensuite se fermer.</summary>
    public static async Task InstallAsync(ReleaseInfo r, IProgress<double> progress)
    {
        var exe = Environment.ProcessPath;
        var dir = Path.GetDirectoryName(exe);
        var tmp = Path.Combine(dir, "CentrePC.new.exe");
        var old = Path.Combine(dir, "CentrePC.old.exe");

        // 1) Téléchargement
        using (var resp = await Http.GetAsync(r.ExeUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(tmp);
            var buf = new byte[81920];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        // 2) Vérification de l'empreinte (protège contre un fichier abîmé ou modifié)
        if (r.ShaUrl != null)
        {
            var expected = (await Http.GetStringAsync(r.ShaUrl)).Trim().Split(' ', '\t', '\r', '\n')[0];
            string actual;
            await using (var fs = File.OpenRead(tmp)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new InvalidDataException("Le fichier téléchargé est incomplet ou abîmé. Réessaie plus tard.");
            }
        }

        // 3) Remplacement : Windows autorise à renommer un exe en cours d'utilisation
        try { if (File.Exists(old)) File.Delete(old); } catch { }
        File.Move(exe, old);
        try { File.Move(tmp, exe); }
        catch { File.Move(old, exe); throw; }   // retour arrière si le remplacement échoue

        // 4) Lancement de la nouvelle version (elle attend que celle-ci se ferme)
        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true });
    }

    /// <summary>Supprime les restes d'une mise à jour précédente.</summary>
    public static void CleanupOld()
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath);
            foreach (var f in new[] { "CentrePC.old.exe", "CentrePC.new.exe" })
            {
                var p = Path.Combine(dir, f);
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { }
    }
}
