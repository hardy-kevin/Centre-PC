using System.Runtime.CompilerServices;
using CentrePC.Core;
using Xunit;

// Les tests partagent la configuration de Centre PC (classes statiques) : ils s'exécutent l'un après l'autre.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CentrePC.Tests;

/// <summary>
/// Faux PC pour les tests : bureau, bureau public, Documents, Images, menu Démarrer et dossier de Centre PC
/// sont redirigés vers un dossier temporaire (variable CENTREPC_TEST_ROOT, voir Core\Paths.cs).
/// Les tests ne touchent jamais au vrai bureau ni à la vraie configuration.
/// </summary>
public static class Sandbox
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "CentrePC-tests-" + Guid.NewGuid().ToString("N")[..8]);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("CENTREPC_TEST_ROOT", Root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Root, true); } catch { } };
    }

    public static string Desktop => AppScanner.UserDesktop;
    public static string Documents => DocumentLibrary.Roots[0].Path;
    public static string Programs => Path.Combine(Root, "Programmes");

    /// <summary>Repart d'un PC vide : dossiers vidés, configuration par défaut, historique vide.</summary>
    public static void Reset()
    {
        Settings.ReadOnly = false;
        Settings.Flush();
        foreach (var dir in Directory.GetDirectories(Root))
            for (int i = 0; i < 5; i++)
            {
                try { Directory.Delete(dir, true); break; }
                catch { Thread.Sleep(100); }
            }
        Settings.Load();
        History.ResetCache();
        Directory.CreateDirectory(Desktop);
        Directory.CreateDirectory(Programs);
    }

    /// <summary>Raccourci Internet (.url) : un simple fichier texte.</summary>
    public static string Url(string dir, string name, string url = "https://example.org")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".url");
        File.WriteAllText(path, $"[InternetShortcut]\r\nURL={url}\r\n");
        return path;
    }

    /// <summary>Faux programme installé (fichier .exe vide dans « Programmes\dossier »).</summary>
    public static string FakeExe(string folder, string exe = "app.exe")
    {
        var dir = Path.Combine(Programs, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, exe);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    /// <summary>Vrai raccourci Windows (.lnk) créé avec WScript.Shell.</summary>
    public static string Lnk(string dir, string name, string target)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".lnk");
        dynamic wsh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic l = wsh.CreateShortcut(path);
        l.TargetPath = target;
        l.Save();
        return path;
    }

    public static string File_(string dir, string name, string content = "test")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }
}

/// <summary>Garde-fou : si la redirection ne fonctionnait pas, tous les autres tests risqueraient de toucher au vrai PC.</summary>
public class SandboxTests
{
    [Fact]
    public void Les_tests_travaillent_dans_un_dossier_temporaire()
    {
        Sandbox.Reset();
        Assert.StartsWith(Sandbox.Root, Settings.DataDir, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Sandbox.Root, AppScanner.UserDesktop, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Sandbox.Root, AppScanner.PublicDesktop, StringComparison.OrdinalIgnoreCase);
        Assert.All(DocumentLibrary.Roots, r => Assert.StartsWith(Sandbox.Root, r.Path, StringComparison.OrdinalIgnoreCase));
    }
}
