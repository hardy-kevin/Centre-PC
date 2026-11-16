using System.Diagnostics;

namespace CentrePC.Core;

/// <summary>
/// Rapport en PDF sans passer par la boîte d'impression : Microsoft Edge (présent sur Windows 10 et 11) convertit la page
/// en arrière-plan, sans fenêtre. Rien n'est envoyé : le fichier est lu et écrit sur ce PC.
/// </summary>
public static class ReportPdf
{
    public static string EdgePath()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            var p = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Convertit le rapport HTML en PDF (même dossier, même nom). Renvoie le chemin du PDF, ou null si impossible.</summary>
    public static async Task<string> ExportAsync(string htmlPath)
    {
        var edge = EdgePath();
        if (edge == null || !File.Exists(htmlPath)) return null;
        var pdf = Path.ChangeExtension(htmlPath, ".pdf");
        try
        {
            if (File.Exists(pdf)) File.Delete(pdf);
            // Profil temporaire : Edge déjà ouvert ne reprend pas la commande à son compte
            var profile = Path.Combine(Path.GetTempPath(), "CentrePC-pdf-" + Guid.NewGuid().ToString("N"));
            var psi = new ProcessStartInfo(edge) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "--headless", "--disable-gpu", "--no-first-run", "--disable-extensions", "--user-data-dir=" + profile,
                                      "--no-pdf-header-footer", "--print-to-pdf=" + pdf, new Uri(htmlPath).AbsoluteUri })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
            try { Directory.Delete(profile, true); } catch { }
            return File.Exists(pdf) && new FileInfo(pdf).Length > 1000 ? pdf : null;
        }
        catch (Exception ex) { Log.Error("PDF du rapport", ex); return null; }
    }
}
