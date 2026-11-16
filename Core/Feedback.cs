using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CentrePC.Core;

/// <summary>Un retour d'expérience en attente (tel qu'il serait envoyé, lisible par l'utilisateur).</summary>
public sealed class FeedbackEvent
{
    /// <summary>« erreur », « classement »…</summary>
    public string Type { get; set; }
    /// <summary>Jour seulement (pas l'heure).</summary>
    public string Jour { get; set; }
    public string Version { get; set; }
    public string Windows { get; set; }
    public Dictionary<string, string> Donnees { get; set; } = new();
    /// <summary>Empreinte du contenu : sert à ne pas enregistrer deux fois la même chose le même jour.</summary>
    public string Empreinte { get; set; }
}

/// <summary>
/// Retours d'expérience anonymes, sur accord explicite uniquement (désactivé par défaut ; refuser ne retire aucune fonction).
/// <list type="bullet">
/// <item>Rien n'est enregistré sans l'accord de l'utilisateur ; retirer l'accord vide la file.</item>
/// <item>Chaque texte est nettoyé avant d'entrer dans la file : dossier personnel, nom d'utilisateur, nom du PC, adresses e-mail.</item>
/// <item>La file est un fichier lisible (retours.json) : l'utilisateur voit exactement ce qui partirait, et peut tout effacer.</item>
/// <item>Ni nom de fichier, ni contenu de document, ni adresse IP conservée côté serveur ; identifiant d'installation aléatoire, renouvelé chaque mois.</item>
/// </list>
/// Envoi groupé une fois par jour au plus vers <see cref="Endpoint"/> ; en cas d'échec, la file reste sur le PC pour la fois suivante.
/// </summary>
public static class Feedback
{
    /// <summary>Point d'entrée HTTPS (Cloudflare Workers, code dans Serveur/). Vide : aucun envoi.</summary>
    public static string Endpoint { get; internal set; } = "https://centre-pc-retours.centre-pc.workers.dev/v1/retours";

    private const int MaxEvents = 200, MaxText = 600;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string QueuePath => Path.Combine(Settings.DataDir, "retours.json");
    public static bool Enabled => Settings.Current?.FeedbackConsent == true;
    public static bool CanSend => !string.IsNullOrEmpty(Endpoint) && Endpoint.StartsWith("https://", StringComparison.Ordinal);

    /// <summary>Donne ou retire l'accord. Le retirer efface la file et l'identifiant.</summary>
    public static void SetConsent(bool on)
    {
        Settings.Current.FeedbackConsent = on;
        if (!on) { Settings.Current.FeedbackInstallId = null; Settings.Current.FeedbackIdMonth = null; Settings.Current.FeedbackReported?.Clear(); Clear(); }
        Settings.Save();
    }

    // ------------------------------------------------------------------ nettoyage
    /// <summary>
    /// Retire d'un texte ce qui identifie la personne ou la machine : chemins de fichiers, nom d'utilisateur, nom du PC,
    /// adresses e-mail ; le texte est ensuite limité en longueur.
    /// </summary>
    public static string Clean(string text) => Clean(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

    public static string Clean(string text, string profile, string user, string machine)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = Regex.Replace(text, @"[\w.+-]+@[\w-]+(\.[\w-]+)+", "%EMAIL%");
        // Chemins : jamais de nom de fichier ni de dossier personnel. Seuls les dossiers de Windows et des programmes sont gardés
        // (ils ne disent rien de la personne et aident à comprendre l'erreur) ; tout le reste est réduit à son point de départ.
        t = Regex.Replace(t, @"(?i)(?:[a-z]:\\|\\\\)[^""'«»\r\n]*", m =>
        {
            var path = m.Value;
            if (!string.IsNullOrEmpty(profile) && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)) return @"%PROFIL%\…";
            if (path.StartsWith(@"\\")) return @"\\…";
            var rest = path[3..];
            if (Regex.IsMatch(rest, @"(?i)^(Users|Utilisateurs|Documents and Settings)\\")) return path[..3] + @"Users\%UTILISATEUR%\…";
            if (Regex.IsMatch(rest, @"(?i)^(Windows|Program Files|Program Files \(x86\)|ProgramData)(\\|$)")) return path;
            return path[..3] + "…";
        });
        if (!string.IsNullOrEmpty(user) && user.Length >= 3) t = Regex.Replace(t, @"(?<![\p{L}\p{N}])" + Regex.Escape(user) + @"(?![\p{L}\p{N}])", "%UTILISATEUR%", RegexOptions.IgnoreCase);
        if (!string.IsNullOrEmpty(machine) && machine.Length >= 3) t = Regex.Replace(t, Regex.Escape(machine), "%PC%", RegexOptions.IgnoreCase);
        return t.Length > MaxText ? t[..MaxText] + "…" : t;
    }

    /// <summary>Pile d'appels réduite aux méthodes de Centre PC (sans chemins de fichiers ni numéros de ligne).</summary>
    public static string ShortStack(Exception ex)
    {
        if (ex?.StackTrace == null) return "";
        var frames = ex.StackTrace.Split('\n')
            .Select(l => Regex.Match(l, @"(CentrePC\.[\w.<>`]+)\(").Groups[1].Value)
            .Where(f => f.Length > 0).Take(6);
        return string.Join(" < ", frames);
    }

    // ------------------------------------------------------------------ enregistrement
    public static void RecordError(string context, Exception ex)
    {
        if (!Enabled || ex == null || ex is OperationCanceledException) return;
        Record("erreur", new()
        {
            ["etape"] = Clean(context),
            ["type"] = ex.GetType().Name,
            ["message"] = Clean(ex.Message),
            ["pile"] = ShortStack(ex)
        });
    }

    /// <summary>Catégorie choisie à la main pour une application (sert à améliorer le classement automatique de tous).</summary>
    public static void RecordClassification(string app, string category)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(app) || category == null) return;
        Record("classement", new() { ["application"] = Clean(app), ["categorie"] = category });
    }

    /// <summary>
    /// Applications installées restées sans catégorie : leur nom et leur éditeur aident à les reconnaître chez tout le monde.
    /// Seuls les vrais logiciels installés sont concernés (dossiers de programmes ou Microsoft Store) : jamais un script,
    /// un document, un raccourci vers un site ou un programme rangé dans un dossier personnel. Chaque application n'est
    /// signalée qu'une fois.
    /// </summary>
    public static void RecordUnknownApps(IEnumerable<(string name, string publisher, string target, bool packaged)> apps)
    {
        if (!Enabled) return;
        try
        {
            var reported = Settings.Current.FeedbackReported ??= new();
            bool changed = false;
            foreach (var (name, publisher, target, packaged) in apps)
            {
                if (string.IsNullOrWhiteSpace(name) || !(packaged || IsInstalledProgram(target))) continue;
                var clean = Clean(name);
                var print = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("non-reconnue|" + clean.ToLowerInvariant())))[..16];
                if (reported.Contains(print)) continue;
                reported.Add(print);
                changed = true;
                Record("non-reconnue", new() { ["application"] = clean, ["editeur"] = Clean(publisher) });
            }
            if (reported.Count > 3000) reported.RemoveRange(0, reported.Count - 3000);
            if (changed) Settings.Save();
        }
        catch { }
    }

    /// <summary>Programme (.exe) installé dans un dossier de programmes de Windows, et non dans un dossier personnel.</summary>
    public static bool IsInstalledProgram(string target)
    {
        if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                     Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) })
            if (!string.IsNullOrEmpty(root) && target.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static void Record(string type, Dictionary<string, string> data)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                var day = DateTime.Now.ToString("yyyy-MM-dd");
                var print = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(type + "|" + string.Join("|", data.OrderBy(d => d.Key).Select(d => d.Key + "=" + d.Value)))))[..16];
                var queue = LoadLocked();
                if (queue.Any(e => e.Empreinte == print && e.Jour == day)) return;   // déjà noté aujourd'hui
                queue.Add(new FeedbackEvent
                {
                    Type = type, Jour = day, Empreinte = print, Donnees = data,
                    Version = AppUpdater.Current?.ToString(3) ?? "",
                    Windows = Environment.OSVersion.Version.ToString()
                });
                if (queue.Count > MaxEvents) queue.RemoveRange(0, queue.Count - MaxEvents);
                File.WriteAllText(QueuePath, JsonSerializer.Serialize(queue, Json));
            }
        }
        catch { /* les retours ne doivent jamais gêner l'application */ }
    }

    private static List<FeedbackEvent> LoadLocked()
    {
        try { if (File.Exists(QueuePath)) return JsonSerializer.Deserialize<List<FeedbackEvent>>(File.ReadAllText(QueuePath)) ?? new(); } catch { }
        return new();
    }

    public static List<FeedbackEvent> Queue() { lock (Gate) return LoadLocked(); }

    public static void Clear()
    {
        lock (Gate) { try { File.Delete(QueuePath); } catch { } }
    }

    /// <summary>Identifiant d'installation aléatoire, renouvelé chaque mois (dédoublonnage et demande d'effacement, rien d'autre).</summary>
    public static string InstallId(DateTime now)
    {
        var month = now.ToString("yyyy-MM");
        if (Settings.Current.FeedbackInstallId == null || Settings.Current.FeedbackIdMonth != month)
        {
            Settings.Current.FeedbackInstallId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            Settings.Current.FeedbackIdMonth = month;
            Settings.Save();
        }
        return Settings.Current.FeedbackInstallId;
    }

    // ------------------------------------------------------------------ envoi
    /// <summary>Résultat du dernier essai d'envoi (affiché dans Réglages), null s'il n'y en a pas eu.</summary>
    public static string LastResult { get; private set; }

    /// <summary>
    /// Envoi groupé, seulement avec l'accord et si un point d'entrée est configuré ; une fois par jour au plus, sauf
    /// <paramref name="now"/> forcé par l'utilisateur (« Envoyer maintenant »). En cas d'échec la file est gardée pour la
    /// fois suivante. Renvoie le nombre d'éléments envoyés.
    /// </summary>
    public static async Task<int> SendAsync(DateTime now, bool force = false)
    {
        if (!Enabled || !CanSend || RemoteCatalog.IsDisabled("retours")) return 0;
        if (!force && Settings.Current.FeedbackLastSent is DateTime last && (now - last).TotalHours < 24) return 0;
        List<FeedbackEvent> queue;
        lock (Gate) queue = LoadLocked();
        if (queue.Count == 0) return 0;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var body = JsonSerializer.Serialize(new { installation = InstallId(now), evenements = queue });
            using var rs = await http.PostAsync(Endpoint, new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"));
            if (!rs.IsSuccessStatusCode)
            {
                // Journal seulement (Log.Error créerait lui-même un retour d'erreur)
                LastResult = $"Échec de l'envoi (réponse {(int)rs.StatusCode}) : nouvel essai plus tard.";
                Log.Info("Retours : " + LastResult);
                return 0;
            }
            lock (Gate)
            {
                // Ne retirer que ce qui vient d'être envoyé (d'autres retours ont pu s'ajouter entre-temps)
                var sent = queue.Select(e => e.Empreinte + e.Jour).ToHashSet();
                var rest = LoadLocked().Where(e => !sent.Contains(e.Empreinte + e.Jour)).ToList();
                if (rest.Count == 0) File.Delete(QueuePath); else File.WriteAllText(QueuePath, JsonSerializer.Serialize(rest, Json));
            }
            Settings.Current.FeedbackLastSent = now;
            Settings.Save();
            LastResult = $"{queue.Count} retour(s) envoyé(s) le {now:d MMMM à HH:mm}.";
            Log.Info("Retours : " + LastResult);
            return queue.Count;
        }
        catch (Exception ex)
        {
            LastResult = "Envoi impossible pour l'instant (connexion ?) : nouvel essai plus tard.";
            Log.Info("Retours : envoi impossible : " + ex.GetType().Name + " " + ex.Message);
            return 0;
        }
    }
}
