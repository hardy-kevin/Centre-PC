namespace CentrePC.Core;

/// <summary>
/// Dossiers spéciaux de Windows (bureau, documents, menu Démarrer, AppData).
/// Pour les tests automatiques : si la variable d'environnement CENTREPC_TEST_ROOT est définie, tous ces dossiers
/// sont remplacés par des sous-dossiers de ce répertoire, afin que les tests ne touchent jamais au vrai PC.
/// </summary>
public static class Paths
{
    private static readonly string TestRoot = Environment.GetEnvironmentVariable("CENTREPC_TEST_ROOT");
    public static bool IsTest => !string.IsNullOrEmpty(TestRoot);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id,
                                                   uint flags, IntPtr token, out IntPtr path);

    /// <summary>Dossier Téléchargements (pas dans Environment.SpecialFolder).</summary>
    public static string Downloads
    {
        get
        {
            if (IsTest)
            {
                var t = Path.Combine(TestRoot, "Telechargements");
                Directory.CreateDirectory(t);
                return t;
            }
            try
            {
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
                {
                    var s = System.Runtime.InteropServices.Marshal.PtrToStringUni(p);
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    public static string Get(Environment.SpecialFolder folder)
    {
        if (!IsTest) return Environment.GetFolderPath(folder);
        var name = folder switch
        {
            Environment.SpecialFolder.DesktopDirectory => "Bureau",
            Environment.SpecialFolder.CommonDesktopDirectory => "BureauPublic",
            Environment.SpecialFolder.MyDocuments => "Documents",
            Environment.SpecialFolder.MyPictures => "Images",
            Environment.SpecialFolder.MyMusic => "Musique",
            Environment.SpecialFolder.MyVideos => "Videos",
            Environment.SpecialFolder.Programs => "MenuDemarrer",
            Environment.SpecialFolder.CommonPrograms => "MenuDemarrerCommun",
            Environment.SpecialFolder.LocalApplicationData => "AppData",
            _ => null
        };
        if (name == null) return Environment.GetFolderPath(folder);
        var path = Path.Combine(TestRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
