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
    /// <summary>Signature de l'empreinte par la clé hors ligne de l'auteur (Catalogue/versions/X.Y.Z.sig), null si absente.</summary>
    public string Signature { get; set; }
    public string PageUrl { get; set; }
}

/// <summary>
/// Mise à jour de Centre PC depuis les « Releases » du dépôt GitHub.
/// La nouvelle version est téléchargée, son empreinte SHA-256 vérifiée, puis elle remplace l'exe et redémarre.
/// L'empreinte elle-même doit être signée par la clé de l'auteur (la même que le catalogue, gardée hors de GitHub) :
/// même si le compte GitHub était piraté, une version fabriquée par quelqu'un d'autre ne serait jamais installée.
/// </summary>
public static class AppUpdater
{
    public const string Owner = "hardy-kevin";
    public const string Repo = "Centre-PC";
    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}/releases";

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
                PageUrl = root.TryGetProperty("html_url", out var u) ? u.GetString() : RepoUrl
            };
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString();
                if (name.Equals("CentrePC.exe", StringComparison.OrdinalIgnoreCase)) r.ExeUrl = url;
                else if (name.Equals("CentrePC.exe.sha256", StringComparison.OrdinalIgnoreCase)) r.ShaUrl = url;
            }
            if (r.ExeUrl == null || r.ShaUrl == null) return null;
            // Version pas encore signée par l'auteur (quelques minutes après sa publication) : pas proposée pour l'instant
            try { r.Signature = (await Http.GetStringAsync(SignatureUrl(r.Version))).Trim(); } catch { return null; }
            return r;
        }
        catch { return null; }
    }

    public static bool IsNewer(ReleaseInfo r) => r != null && r.Version > Current;

    /// <summary>Emplacement de la signature d'une version : déposée dans le dépôt par Outils\signer-version.ps1.</summary>
    public static string SignatureUrl(Version v) =>
        $"https://raw.githubusercontent.com/{Owner}/{Repo}/main/Catalogue/versions/{v.Major}.{v.Minor}.{v.Build}.sig";

    /// <summary>Texte signé pour une version : son numéro et l'empreinte de CentrePC.exe (une signature ne vaut que pour eux).</summary>
    public static byte[] SignedText(Version v, string sha256) =>
        System.Text.Encoding.UTF8.GetBytes($"CentrePC {v.Major}.{v.Minor}.{v.Build} {sha256.Trim().ToLowerInvariant()}");

    /// <summary>L'empreinte annoncée pour cette version est-elle signée par une clé de l'auteur ?</summary>
    public static bool VerifyRelease(Version v, string sha256, string signature, IEnumerable<string> keys) =>
        !string.IsNullOrWhiteSpace(sha256) && !string.IsNullOrWhiteSpace(signature) && RemoteCatalog.Verify(SignedText(v, sha256), signature, keys);

    /// <summary>Télécharge, vérifie et installe la nouvelle version, puis la lance. L'application actuelle doit ensuite se fermer.</summary>
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
        if (r.ShaUrl == null)
        {
            File.Delete(tmp);
            throw new InvalidDataException("Cette version ne fournit pas d'empreinte de vérification : elle n'est pas installée automatiquement. Téléchargez-la depuis la page des versions.");
        }
        {
            var expected = (await Http.GetStringAsync(r.ShaUrl)).Trim().Split(' ', '\t', '\r', '\n')[0];
            if (!VerifyRelease(r.Version, expected, r.Signature, RemoteCatalog.PublicKeys))
            {
                File.Delete(tmp);
                throw new InvalidDataException("Cette version n'est pas signée par l'auteur de Centre PC : elle n'est pas installée. Réessayez plus tard.");
            }
            string actual;
            await using (var fs = File.OpenRead(tmp)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new InvalidDataException("Le fichier téléchargé est incomplet ou endommagé. Veuillez réessayer ultérieurement.");
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
