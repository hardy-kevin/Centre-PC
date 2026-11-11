namespace CentrePC.Core;

/// <summary>
/// Journal local des erreurs et des événements importants (Journaux\centrepc.log). Rien n'est envoyé :
/// le fichier sert à comprendre un problème (« Ouvrir le journal » dans Réglages). Taille limitée à ~1 Mo.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(Settings.DataDir, "Journaux", "centrepc.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string context, Exception ex) =>
        Write("ERREUR", $"{context} : {ex?.GetType().Name} {ex?.Message}{(ex?.InnerException != null ? " → " + ex.InnerException.Message : "")}");

    /// <summary>Erreur détaillée (pile d'appels) : pour les plantages.</summary>
    public static void Crash(string context, Exception ex) => Write("PLANTAGE", $"{context}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    var old = path + ".1";
                    File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* le journal ne doit jamais faire planter l'appli */ }
    }
}
