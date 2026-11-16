namespace CentrePC.Core;

/// <summary>Un dossier et la place qu'il occupe (sous-dossiers compris).</summary>
public sealed class UsageNode
{
    public string Name { get; set; }
    public string Path { get; set; }
    public long Size { get; set; }
    public long Files { get; set; }
    /// <summary>Fichiers placés directement dans ce dossier (sans ses sous-dossiers).</summary>
    public long OwnSize { get; set; }
    /// <summary>Sous-dossiers détaillés (vide au-delà de la profondeur retenue : la taille reste comptée).</summary>
    public List<UsageNode> Children { get; } = new();
    public UsageNode Parent { get; set; }
    /// <summary>Contenu illisible sans droits administrateur (taille partielle).</summary>
    public bool Partial { get; set; }
}

public sealed class UsageResult
{
    public UsageNode Root { get; set; }
    /// <summary>Plus gros fichiers rencontrés, du plus gros au plus petit.</summary>
    public List<(string Path, long Size)> BiggestFiles { get; set; } = new();
    public long Folders { get; set; }
}

/// <summary>
/// « Ce qui prend de la place » : taille de chaque dossier d'un lecteur ou d'un dossier, et plus gros fichiers. Lecture seule :
/// rien n'est modifié ni supprimé (le ménage se fait dans l'Explorateur, où la Corbeille permet de revenir en arrière).
/// Les liens et jonctions ne sont pas suivis (pas de double comptage) ; ce qui est illisible sans droits administrateur est signalé.
/// </summary>
public static class DiskUsage
{
    public const int Depth = 5;
    private const int KeepFiles = 40;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0, ReturnSpecialDirectories = false
    };

    public static UsageResult Scan(string root, IProgress<string> progress, CancellationToken ct)
    {
        var res = new UsageResult();
        var top = new PriorityQueue<string, long>();
        long lastReport = Environment.TickCount64;

        UsageNode Visit(DirectoryInfo dir, int depth, UsageNode parent)
        {
            ct.ThrowIfCancellationRequested();
            var node = new UsageNode { Name = depth == 0 ? dir.FullName : dir.Name, Path = dir.FullName, Parent = parent };
            res.Folders++;
            if (Environment.TickCount64 - lastReport > 250) { lastReport = Environment.TickCount64; progress?.Report(dir.FullName); }
            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos("*", Options))
                {
                    // Liens symboliques et jonctions : leur contenu est ailleurs, il serait compté deux fois
                    // (les dossiers synchronisés avec le nuage sont aussi des « points d'analyse » : seuls les vrais liens sont écartés)
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget != null) continue;
                    if (entry is FileInfo f)
                    {
                        // Fichier gardé seulement dans le nuage (OneDrive…) : il n'occupe pas le disque
                        if ((entry.Attributes & (FileAttributes.Offline | (FileAttributes)0x400000 | (FileAttributes)0x40000)) != 0) continue;
                        long len = f.Length;
                        node.OwnSize += len; node.Size += len; node.Files++;
                        if (top.Count < KeepFiles) top.Enqueue(f.FullName, len);
                        else if (top.TryPeek(out _, out var min) && len > min) { top.Dequeue(); top.Enqueue(f.FullName, len); }
                    }
                    else if (entry is DirectoryInfo d)
                    {
                        var child = Visit(d, depth + 1, node);
                        node.Size += child.Size; node.Files += child.Files;
                        if (child.Partial) node.Partial = true;
                        if (depth < Depth) node.Children.Add(child);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException) { node.Partial = true; }
            catch (IOException) { node.Partial = true; }
            catch (System.Security.SecurityException) { node.Partial = true; }
            node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
            return node;
        }

        res.Root = Visit(new DirectoryInfo(root), 0, null);
        while (top.TryDequeue(out var path, out var size)) res.BiggestFiles.Add((path, size));
        res.BiggestFiles.Reverse();
        return res;
    }

    public static string Size(long bytes) =>
        bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.##} To"
        : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} Go"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} Mo"
        : bytes >= 1L << 10 ? $"{bytes / 1024.0:0} Ko"
        : $"{bytes} octets";
}
