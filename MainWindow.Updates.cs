using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using CentrePC.Core;

namespace CentrePC;

/// <summary>Page Mises à jour : recherche, applications, pilotes, installation, avancement et rapport.</summary>
public partial class MainWindow
{
    // =====================================================================================
    //  Page Mises à jour
    // =====================================================================================
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        _online = await Network.IsOnlineAsync();
        ApplyOnlineState();
        if (!_online) { if (sender != this) ShowToast("Pas de connexion Internet : recherche impossible pour le moment."); return; }
        ScanButton.IsEnabled = false;
        ScanButtonText.Text = "Recherche en cours…";
        SumApps.Text = SumWu.Text = SumDrivers.Text = "…";
        SumAppsSub.Text = SumWuSub.Text = SumDriversSub.Text = "recherche…";
        DriversEmpty.Text = "Recherche des pilotes…";
        DriversEmpty.Visibility = Visibility.Visible;
        _drivers.Clear();
        WingetEmpty.Text = "Recherche des applications à mettre à jour…";
        WuEmpty.Text = "Interrogation de Windows Update (peut prendre une minute)…";
        WingetEmpty.Visibility = WuEmpty.Visibility = Visibility.Visible;
        _winget.Clear(); _wu.Clear();

        // Le matériel d'abord : il indique quel pilote graphique est géré par le logiciel du fabricant
        try { _hw = await Updates.ScanHardwareAsync(); ShowHardware(); } catch { }
        var tWinget = Updates.ScanWingetAsync();
        var tWu = Updates.ScanWindowsUpdateAsync(_hw);

        try
        {
            foreach (var u in await tWinget) _winget.Add(u);
            _wingetScanned = true;
            ShowWingetSummary();
            WingetEmpty.Text = "Toutes les applications sont à jour.";
        }
        catch (Exception ex) { SumApps.Text = "!"; SumAppsSub.Text = "winget indisponible"; WingetEmpty.Text = ex.Message; }
        WingetEmpty.Visibility = _winget.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AllAppsCheck.IsChecked = true;

        try
        {
            var scan = await tWu;
            foreach (var u in scan.System) _wu.Add(u);
            SumWu.Text = _wu.Count.ToString();
            SumWuSub.Text = _wu.Count == 0 ? "Windows est à jour" : "mise(s) à jour système";
            WuEmpty.Text = "Windows est à jour.";

            foreach (var d in scan.Drivers) _drivers.Add(d);
            int real = _drivers.Count(d => d.CanSelect);
            SumDrivers.Text = real.ToString();
            var managed = _drivers.FirstOrDefault(d => !d.CanSelect)?.ManagedBy;
            SumDriversSub.Text = real == 0 ? (managed != null ? $"graphique : via {managed}" : "tout est à jour")
                               : managed != null ? $"+ graphique via {managed}" : "mise(s) à jour disponible(s)";
            DriversEmpty.Text = "Tous les pilotes proposés par Windows Update sont installés.";
            AllDriversCheck.IsChecked = true;
        }
        catch (Exception ex)
        {
            SumWu.Text = SumDrivers.Text = "!"; SumWuSub.Text = SumDriversSub.Text = "erreur";
            WuEmpty.Text = DriversEmpty.Text = "Windows Update : " + ex.Message;
        }
        WuEmpty.Visibility = _wu.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DriversEmpty.Visibility = _drivers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Settings.Current.LastUpdateCheck = DateTime.Now;
        Settings.Save();
        UpdateLastCheck();
        UpdateBadge();
        ScanButton.IsEnabled = true;
        ScanButtonText.Text = "Rechercher à nouveau";
    }

    private void ShowWingetSummary()
    {
        SumApps.Text = _winget.Count.ToString();
        SumAppsSub.Text = _winget.Count == 0 ? "tout est à jour" : "mise(s) à jour disponible(s)";
    }

    private void UpdateBadge()
    {
        int n = _winget.Count + _wu.Count(u => !u.Ignored) + _drivers.Count(d => d.CanSelect);
        UpdatesBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesBadgeText.Text = n.ToString();
    }

    private void UpdateLastCheck()
    {
        var d = Settings.Current.LastUpdateCheck;
        LastCheckText.Text = d == null ? "Cliquez sur « Rechercher » pour analyser votre PC." : $"Dernière vérification : {d:dddd d MMMM à HH:mm}";
    }

    private void AllApps_Changed(object sender, RoutedEventArgs e)
    {
        foreach (var u in _winget) u.Selected = AllAppsCheck.IsChecked == true;
    }

    private void AllDrivers_Changed(object sender, RoutedEventArgs e)
    {
        // Les firmwares restent à cocher un par un
        foreach (var d in _drivers.Where(d => !d.IsFirmware)) d.Selected = AllDriversCheck.IsChecked == true;
    }

    private void ExcludeDriver_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DriverUpdate d || string.IsNullOrEmpty(d.HardwareId)) return;
        var cfg = Settings.Current;
        if (!cfg.DriverExclusions.Contains(d.HardwareId, StringComparer.OrdinalIgnoreCase)) cfg.DriverExclusions.Add(d.HardwareId);
        cfg.DriverExclusionNames[d.HardwareId] = d.Name;
        Settings.Save();
        _drivers.Remove(d);
        SumDrivers.Text = _drivers.Count(x => x.CanSelect).ToString();
        DriversEmpty.Visibility = _drivers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBadge();
        RefreshExclusions();
        ShowToast($"{d.Name} : pilote masqué (réversible dans Réglages).");
    }

    /// <summary>Liste des applications et pilotes exclus des mises à jour (page Réglages).</summary>
    private void RefreshExclusions()
    {
        var cfg = Settings.Current;
        var rows = cfg.WingetExclusions.Select(id => new ExclusionRow { Kind = "app", Key = id, Name = id, Label = "Application (winget)" })
            .Concat(cfg.DriverExclusions.Select(h => new ExclusionRow
            {
                Kind = "driver", Key = h, Label = "Pilote · " + h,
                Name = cfg.DriverExclusionNames.TryGetValue(h, out var n) ? n : h
            }))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        ExclusionsList.ItemsSource = rows;
        ExclusionsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestoreExclusion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ExclusionRow r) return;
        var cfg = Settings.Current;
        if (r.Kind == "app") cfg.WingetExclusions.RemoveAll(x => x.Equals(r.Key, StringComparison.OrdinalIgnoreCase));
        else { cfg.DriverExclusions.RemoveAll(x => x.Equals(r.Key, StringComparison.OrdinalIgnoreCase)); cfg.DriverExclusionNames.Remove(r.Key); }
        Settings.Save();
        RefreshExclusions();
        ShowToast($"{r.Name} sera de nouveau proposé à la prochaine recherche.");
    }

    private void ShowHardware()
    {
        if (_hw == null) return;
        GpuInfo.Children.Clear(); BoardInfo.Children.Clear(); PeriphInfo.Children.Clear();

        // ---- Carte graphique
        int? gpuAge = _hw.GpuDriverDate is DateTime gd ? (int)(DateTime.Now - gd).TotalDays : null;
        AddInfo(GpuInfo, _hw.GpuName ?? "Non détectée", true);
        if (_hw.GpuDriver != null)
            AddInfo(GpuInfo, $"Pilote {_hw.GpuDriver}" + (_hw.GpuDriverDate is DateTime d1 ? $" du {d1:dd/MM/yyyy}" : ""));
        if (_hw.GpuToolName != null)
            AddInfo(GpuInfo, _hw.GpuToolVersion != null ? $"{_hw.GpuToolName} {_hw.GpuToolVersion}" : $"{_hw.GpuToolName} : non installé");
        foreach (var g in _hw.OtherGpus) AddInfo(GpuInfo, "Aussi : " + g);
        GpuToolButton.Content = _hw.GpuToolName == null ? "Pilotes graphiques"
            : _hw.GpuToolPath != null ? $"Ouvrir {_hw.GpuToolName}" : $"Télécharger {_hw.GpuToolName}";
        GpuToolButton.Visibility = _hw.GpuToolName == null && _hw.GpuName == null ? Visibility.Collapsed : Visibility.Visible;
        SumGpu.Text = gpuAge == null ? "?" : gpuAge <= 90 ? "À jour" : "À vérifier";
        SumGpuSub.Text = gpuAge == null ? (_hw.GpuName ?? "") : $"{_hw.GpuVendor} · pilote de {gpuAge} jours";

        // ---- PC / carte mère / BIOS / chipset
        int? biosAge = _hw.BiosDate is DateTime bd ? (int)(DateTime.Now - bd).TotalDays : null;
        BoardTitle.Text = _hw.IsLaptop ? "PC PORTABLE ET BIOS" : "CARTE MÈRE ET BIOS";
        bool branded = _hw.IsLaptop || (_hw.SystemMaker != null && !Regex.IsMatch(_hw.SystemMaker, "System manufacturer|To be filled|Default string|O.E.M", RegexOptions.IgnoreCase)
                                        && !string.Equals(_hw.SystemMaker, _hw.BoardMaker, StringComparison.OrdinalIgnoreCase));
        AddInfo(BoardInfo, (branded ? $"{_hw.SystemMaker} {_hw.SystemModel}" : $"{_hw.BoardMaker} {_hw.BoardModel}").Trim(), true);
        if (branded && !string.IsNullOrEmpty(_hw.BoardModel)) AddInfo(BoardInfo, $"Carte mère {_hw.BoardMaker} {_hw.BoardModel}");
        AddInfo(BoardInfo, $"BIOS {_hw.BiosVersion}" + (_hw.BiosDate is DateTime d2 ? $" du {d2:dd/MM/yyyy}" : ""));
        if (_hw.CpuName != null) AddInfo(BoardInfo, _hw.CpuName);
        // Microcode du processeur (livré par le BIOS) : défaut de dégradation des Intel 13e / 14e génération
        var micro = CpuMicrocode.Assess(_hw.CpuName);
        if (micro.text != null) AddInfo(BoardInfo, (micro.level >= DeviceStatus.Warning ? "⚠ " : "") + micro.text, micro.level >= DeviceStatus.Warning);
        if (_hw.ChipsetName != null)
            AddInfo(BoardInfo, _hw.ChipsetVersion != null ? $"{_hw.ChipsetName} {_hw.ChipsetVersion}" : $"{_hw.ChipsetName} : non détecté");
        // Page du modèle exact quand elle est connue (carte mère d'un PC monté, numéro de série d'un PC de marque)
        var exact = Updates.ModelSupportUrl(_hw) != null;
        var board = System.Text.RegularExpressions.Regex.Replace(_hw.BoardModel ?? "", @"\s*\((MS|rev)[^)]*\)\s*$", "").Trim();
        BiosButton.Content = exact && !_hw.IsLaptop && board.Length > 0 ? $"Page {_hw.SupportName} de la {board} (BIOS, pilotes)"
                           : exact ? $"Page {_hw.SupportName} de ce PC (BIOS, pilotes)"
                           : _hw.SupportName != null ? $"Support {_hw.SupportName} (BIOS, pilotes)" : "Chercher une mise à jour du BIOS";
        SumBios.Text = micro.level == DeviceStatus.Problem ? "À mettre à jour" : biosAge == null ? "?" : biosAge <= 365 && micro.level == DeviceStatus.Ok ? "Récent" : "À vérifier";
        SumBiosSub.Text = biosAge == null ? "" : $"{_hw.BiosVersion} · {biosAge / 30} mois";

        // ---- Périphériques et outils du constructeur
        foreach (var p in _hw.MakerTools) AddInfo(PeriphInfo, "• " + p + " (outil constructeur)");
        foreach (var p in _hw.PeripheralApps) AddInfo(PeriphInfo, "• " + p);
        if (_hw.PeripheralApps.Count == 0 && _hw.MakerTools.Count == 0)
            AddInfo(PeriphInfo, "Aucun logiciel de périphérique détecté : les pilotes passent par Windows Update.");
    }

    private static void AddInfo(Panel panel, string text, bool strong = false)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
            FontSize = strong ? 14.5 : 13, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)Application.Current.FindResource(strong ? "Text" : "Text2")
        });
    }

    private void OpenGpuTool_Click(object sender, RoutedEventArgs e)
    {
        if (_hw?.GpuToolPath != null) Updates.Open(_hw.GpuToolPath);
        else if (_hw?.GpuToolUrl != null) Updates.Open(_hw.GpuToolUrl);
        else Updates.Open("ms-settings:windowsupdate");
    }

    private void Bios_Click(object sender, RoutedEventArgs e)
    {
        if (_hw?.SupportUrl != null) { Updates.Open(_hw.SupportUrl); return; }
        var model = _hw?.IsLaptop == true ? $"{_hw.SystemMaker} {_hw.SystemModel}" : $"{_hw?.BoardMaker} {_hw?.BoardModel}";
        Updates.Open("https://www.google.com/search?q=" + Uri.EscapeDataString($"{model} BIOS support".Trim()));
    }

    // ---------- Installation ----------
    private DispatcherTimer _tail;
    private long _logPos;
    private Process _installProc;
    private string _logPath;
    private readonly StringBuilder _partial = new();
    private ShortcutSnapshot _snapshot;
    private readonly List<Updates.ManualAction> _manual = new();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        _online = await Network.IsOnlineAsync();
        ApplyOnlineState();
        if (!_online) { ShowToast("Pas de connexion Internet : installation impossible pour le moment."); return; }
        var opt = new Updates.InstallOptions
        {
            GpuFilter = _hw?.GpuDriverFilter ?? "",
            RestorePoint = OptRestore.IsChecked == true,
            Apps = OptApps.IsChecked == true,
            WindowsUpdate = OptWu.IsChecked == true,
            Drivers = OptDrivers.IsChecked == true,
            Store = OptStore.IsChecked == true,
            Defender = OptDefender.IsChecked == true,
        };
        InstallButton.IsEnabled = false;   // pas de double clic pendant la préparation
        if (opt.Drivers)
            opt.DriverUpdates = _drivers.Where(d => d.Selected && d.CanSelect && d.UpdateId != null)
                .Select(d => new Updates.PackageRef { Id = d.UpdateId, Name = d.Name }).ToList();
        if (opt.Drivers && opt.DriverUpdates.Count == 0) opt.Drivers = false;
        if (opt.Apps)
        {
            // Chaque application est traitée une par une (vérifications avant / après) : il faut donc la liste
            IEnumerable<WingetUpdate> selected = _winget.Where(u => u.Selected);
            if (!_wingetScanned)
            {
                ShowToast("Recherche des applications à mettre à jour…");
                try { selected = await Updates.ScanWingetAsync(); } catch { selected = Enumerable.Empty<WingetUpdate>(); }
            }
            opt.Packages = selected.Select(u => new Updates.PackageRef { Id = u.Id, Name = u.Name }).ToList();
        }

        _steps.Clear();
        if (opt.RestorePoint) _steps.Add(new StepItem { Key = "restore", Title = "Point de restauration" });
        if (opt.Apps) _steps.Add(new StepItem { Key = "apps", Title = "Applications" });
        if (opt.WindowsUpdate) _steps.Add(new StepItem { Key = "wu", Title = "Windows Update" });
        if (opt.Drivers) _steps.Add(new StepItem { Key = "drivers", Title = "Pilotes" });
        if (opt.Store) _steps.Add(new StepItem { Key = "store", Title = "Microsoft Store" });
        if (opt.Defender) _steps.Add(new StepItem { Key = "defender", Title = "Antivirus Defender" });
        if (_steps.Count == 0) { InstallButton.IsEnabled = true; ShowToast("Sélectionnez au moins une option."); return; }

        // Photo du bureau avant : les installateurs y remettent des icônes ou cassent les raccourcis rangés
        var snap = await Task.Run(() => ShortcutGuard.Take());
        opt.TidyPublicShortcuts = snap.StoredCount > 0;
        _logPath = Updates.StartInstall(opt, out _installProc);
        if (_logPath == null) { InstallButton.IsEnabled = true; ShowToast("Installation annulée (autorisation administrateur refusée)."); return; }
        snap.LogPath = _logPath;
        _snapshot = snap;
        ShortcutGuard.Save(snap);
        _manual.Clear();
        _report.Clear();
        _appSteps.Clear();
        var names = _winget.GroupBy(u => u.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var pk in opt.Packages)
            _appSteps.Add(new AppStep
            {
                Key = pk.Id, Title = pk.Name ?? pk.Id,
                Version = names.TryGetValue(pk.Id, out var wu) ? wu.VersionText : null,
                Detail = names.TryGetValue(pk.Id, out var wu2) ? wu2.VersionText : ""
            });
        AppsProgressBox.Visibility = _appSteps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _driverSteps.Clear();
        foreach (var dr in opt.DriverUpdates.Where(_ => opt.Drivers))
        {
            var src = _drivers.FirstOrDefault(x => x.UpdateId == dr.Id);
            _driverSteps.Add(new AppStep { Key = dr.Id, Title = dr.Name, Version = src?.VersionText, Detail = src?.VersionText ?? "" });
        }
        DriversProgressBox.Visibility = _driverSteps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReportCard.Visibility = Visibility.Collapsed;
        InstallHeadline.Text = "Préparation…";
        UpdateInstallProgress();

        LogBox.Clear(); _logPos = 0; _partial.Clear();
        InstallPanel.Visibility = Visibility.Visible;
        RebootBanner.Visibility = Visibility.Collapsed;
        InstallProgress.Visibility = Visibility.Visible;
        InstallButton.IsEnabled = false;
        _installRunning = true;
        _tail = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _tail.Tick += (_, _) => TailLog();
        _tail.Start();
        InstallCard.BringIntoView();
    }

    private void TailLog()
    {
        try
        {
            if (File.Exists(_logPath))
            {
                using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > _logPos)
                {
                    fs.Seek(_logPos, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - _logPos];
                    int read = fs.Read(buf, 0, buf.Length);
                    int lastNl = Array.LastIndexOf(buf, (byte)'\n', read - 1);
                    if (lastNl >= 0)
                    {
                        _logPos += lastNl + 1;
                        var text = Encoding.UTF8.GetString(buf, 0, lastNl + 1).Replace("﻿", "");
                        foreach (var line in text.Split('\n')) HandleLogLine(line.TrimEnd('\r'));
                    }
                }
            }
            bool exited = false;
            try { exited = _installProc != null && _installProc.HasExited; } catch { }
            if (exited && _tail != null && _tail.IsEnabled)
            {
                // Le script s'est arrêté sans écrire de fin : on lit une dernière fois puis on clôt
                _installProc = null;
                Dispatcher.InvokeAsync(() => { if (_tail?.IsEnabled == true) FinishInstall(false, interrupted: true); }, DispatcherPriority.Background);
            }
        }
        catch { }
    }

    private void HandleLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.StartsWith("##ETAPE|"))
        {
            var p = line.Split('|');
            var step = _steps.FirstOrDefault(s => s.Key == p.ElementAtOrDefault(1));
            if (step != null)
            {
                step.Status = p.ElementAtOrDefault(2) ?? "";
                step.Detail = p.ElementAtOrDefault(3) ?? "";
            }
            UpdateInstallProgress();
            return;
        }
        if (line.StartsWith("##PILOTE|"))
        {
            var p = line.Split('|');
            var drv = _driverSteps.FirstOrDefault(a => a.Key.Equals(p.ElementAtOrDefault(1) ?? "", StringComparison.OrdinalIgnoreCase));
            if (drv != null)
            {
                drv.Status = p.ElementAtOrDefault(2) ?? "";
                var d = p.ElementAtOrDefault(3);
                if (!string.IsNullOrEmpty(d)) drv.Detail = d;
            }
            UpdateInstallProgress();
            return;
        }
        if (line.StartsWith("##APP|"))
        {
            var p = line.Split('|');
            var app = _appSteps.FirstOrDefault(a => a.Key.Equals(p.ElementAtOrDefault(1) ?? "", StringComparison.OrdinalIgnoreCase));
            if (app != null)
            {
                app.Status = p.ElementAtOrDefault(2) ?? "";
                if (app.Status == "À jour")
                    Updates.RememberInstalled(_winget.FirstOrDefault(w => w.Id.Equals(app.Key, StringComparison.OrdinalIgnoreCase)));
                var d = p.ElementAtOrDefault(3);
                app.Detail = string.IsNullOrEmpty(d) ? (app.Status == "À jour" ? app.Version ?? "" : app.Detail) : d;
            }
            UpdateInstallProgress();
            return;
        }
        if (line.StartsWith("##FIN|"))
        {
            FinishInstall(line.EndsWith("1"));
            return;
        }
        if (line.StartsWith("##MANUEL|"))
        {
            var m = Updates.ManualAction.Parse(line);
            if (m != null) _manual.Add(m);
            return;
        }
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private async void FinishInstall(bool reboot, bool interrupted = false)
    {
        _tail?.Stop();
        InstallButton.IsEnabled = true;
        _installRunning = false;
        foreach (var s in _steps.Where(s => s.Status is "En attente" or "En cours"))
            s.Status = interrupted ? "ERREUR" : "IGNORÉ";
        foreach (var a in _appSteps.Concat(_driverSteps).Where(a => !a.IsDone))
        {
            a.Status = interrupted ? "Échec" : "Reporté";
            a.Detail = interrupted ? "interrompue" : "non traitée";
        }
        UpdateInstallProgress();
        RebootBanner.Visibility = reboot ? Visibility.Visible : Visibility.Collapsed;
        InstallHeadline.Text = "Remise en ordre du bureau…";

        // Bureau et raccourcis rangés : retour à l'état d'avant la mise à jour
        var snap = _snapshot; _snapshot = null;
        string desktopText = null;
        if (snap != null)
        {
            try { desktopText = await AfterUpdateShortcutsAsync(snap, reload: true); }
            catch (Exception ex) { desktopText = $"Remise en ordre du bureau impossible : {ex.Message}"; Log.Error("Remise en ordre du bureau", ex); }
        }
        ShowReport(reboot, interrupted, desktopText);
        // Pilotes installés : retirés de la liste
        foreach (var st in _driverSteps.Where(a => a.Status == "À jour"))
            foreach (var d in _drivers.Where(d => d.UpdateId == st.Key).ToList()) _drivers.Remove(d);
        SumDrivers.Text = _drivers.Count(d => d.CanSelect).ToString();
        DriversEmpty.Visibility = _drivers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBadge();
        // Rafraîchit la liste des applications à jour
        if (_wingetScanned)
        {
            try
            {
                var fresh = await Updates.ScanWingetAsync();
                _winget.Clear(); foreach (var u in fresh) _winget.Add(u);
                ShowWingetSummary();
                WingetEmpty.Text = "Toutes les applications sont à jour.";
                WingetEmpty.Visibility = _winget.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdateBadge();
            }
            catch { }
        }
    }

    /// <summary>Retire les icônes remises sur le bureau par les installateurs et remplace les raccourcis cassés.</summary>
    /// <returns>Phrase pour le rapport, ou null si rien n'a changé.</returns>
    private async Task<string> AfterUpdateShortcutsAsync(ShortcutSnapshot snap, bool reload)
    {
        var r = await Task.Run(() => ShortcutGuard.Reconcile(snap));
        // Doublons plus anciens encore présents sur le bureau
        var d = await Task.Run(() => ShortcutGuard.CleanDuplicates());
        if (r.ToStyle.Count + d.ToStyle.Count > 0) await IconStyler.ApplyAsync(r.ToStyle.Concat(d.ToStyle).ToList());
        var orphans = await Task.Run(() => ShortcutGuard.RemoveOrphans());
        int replaced = r.Replaced + r.Repaired + d.Replaced + d.Repaired, removed = r.Removed + d.Removed, stored = r.Stored;
        if (replaced + removed + stored + orphans.Count == 0) return null;
        var parts = new List<string>();
        if (orphans.Count > 0) parts.Add($"raccourci(s) d'application(s) désinstallée(s) retiré(s) : {string.Join(", ", orphans)}");
        if (removed > 0) parts.Add($"{removed} icône(s) remise(s) sur le bureau par les installateurs retirée(s)");
        if (replaced > 0) parts.Add($"{replaced} raccourci(s) rangé(s) mis à jour vers la nouvelle version");
        if (stored > 0) parts.Add($"{stored} nouvelle(s) icône(s) rangée(s)");
        var text = "Bureau remis comme avant : " + string.Join(", ", parts) + ".";
        LogBox.AppendText(text + Environment.NewLine);
        if (reload) await AfterTidyAsync();
        return text;
    }

    /// <summary>Avancement global : barre, compteur et phrase « ce qui se passe maintenant ».</summary>
    private void UpdateInstallProgress()
    {
        var others = _steps.Where(s => s.Key is not ("apps" or "drivers")).ToList();
        int total = others.Count + Math.Max(_appSteps.Count, _steps.Any(s => s.Key == "apps") ? 1 : 0)
                    + Math.Max(_driverSteps.Count, _steps.Any(s => s.Key == "drivers") ? 1 : 0);
        bool appsStepDone = _steps.FirstOrDefault(s => s.Key == "apps")?.Status is { } st && st is not ("En attente" or "En cours");
        bool driversStepDone = _steps.FirstOrDefault(s => s.Key == "drivers")?.Status is { } st2 && st2 is not ("En attente" or "En cours");
        int done = others.Count(s => s.Status is not ("En attente" or "En cours"))
                   + (_appSteps.Count > 0 ? _appSteps.Count(a => a.IsDone) : appsStepDone ? 1 : 0)
                   + (_driverSteps.Count > 0 ? _driverSteps.Count(a => a.IsDone) : driversStepDone ? 1 : 0);
        InstallProgress.Maximum = Math.Max(1, total);
        InstallProgress.Value = done;

        var app = _appSteps.FirstOrDefault(a => a.Status == "En cours");
        var drvNow = _driverSteps.FirstOrDefault(a => a.Status == "En cours");
        var step = _steps.FirstOrDefault(s => s.Status == "En cours");
        if (app != null)
        {
            int i = _appSteps.IndexOf(app) + 1;
            InstallHeadline.Text = $"Mise à jour de {app.Title}…";
            InstallCounter.Text = $"Application {i} sur {_appSteps.Count}";
        }
        else if (drvNow != null)
        {
            int i = _driverSteps.IndexOf(drvNow) + 1;
            InstallHeadline.Text = $"Pilote : {drvNow.Title} ({drvNow.Detail})";
            InstallCounter.Text = $"Pilote {i} sur {_driverSteps.Count}";
        }
        else if (step != null)
        {
            InstallHeadline.Text = step.Key switch
            {
                "restore" => "Création d'un point de restauration…",
                "wu" => "Windows Update : recherche et installation (peut être long)…",
                "store" => "Microsoft Store…",
                "defender" => "Mise à jour de l'antivirus…",
                _ => step.Title + "…"
            };
            InstallCounter.Text = $"{done} / {total}";
        }
        else if (_installRunning) InstallCounter.Text = $"{done} / {total}";
    }

    /// <summary>Rapport de fin : résultat global, compteurs, ce qui reste à faire, bureau.</summary>
    private void ShowReport(bool reboot, bool interrupted, string desktopText)
    {
        int ok = _appSteps.Count(a => a.Status == "À jour"), todo = _appSteps.Count(a => a.Status == "À faire"),
            later = _appSteps.Count(a => a.Status == "Reporté"), failed = _appSteps.Count(a => a.Status == "Échec");
        int dOk = _driverSteps.Count(a => a.Status == "À jour"), dFailed = _driverSteps.Count(a => a.Status == "Échec"),
            dLater = _driverSteps.Count(a => a.Status == "Reporté");
        failed += dFailed;
        int stepErrors = _steps.Count(s => s.Key is not ("apps" or "drivers") && s.Status is "ERREUR" or "PARTIEL");

        _report.Clear();
        foreach (var m in _manual.GroupBy(m => m.Id).Select(g => g.Last()))
            _report.Add(new ReportAction
            {
                Id = m.Id, Name = m.Name, Message = m.Message,
                Installer = m.Installer != null && File.Exists(m.Installer) ? m.Installer : null
            });
        foreach (var a in _driverSteps.Where(a => a.Status == "Échec"))
            _report.Add(new ReportAction { Id = null, Name = a.Title, Message = $"Le pilote n'a pas pu être installé ({a.Detail}). Le périphérique conserve son pilote actuel et reste fonctionnel. Windows Update le proposera de nouveau ; en cas de nouvel échec, installez-le depuis le site du fabricant." });
        foreach (var a in _appSteps.Where(a => a.Status == "Échec" && !_report.Any(r => r.Id == a.Key)))
            _report.Add(new ReportAction { Id = a.Key, Name = a.Title, Message = $"La mise à jour a échoué ({a.Detail}). Elle sera retentée lors de la prochaine mise à jour ; si l'échec persiste, mettez l'application à jour depuis l'application elle-même." });

        bool clean = !interrupted && todo + failed + stepErrors == 0;
        ReportGlyph.Text = clean ? "\uE73E" : interrupted || failed + stepErrors > 0 ? "\uE783" : "\uE7BA";
        ReportGlyph.Foreground = clean ? Brushes.MediumSeaGreen : interrupted || failed + stepErrors > 0 ? Brushes.IndianRed : Brushes.Goldenrod;
        ReportTitle.Text = interrupted ? "Mise à jour interrompue"
            : clean ? "Tout est à jour"
            : _report.Count == 1 ? "Mises à jour terminées — 1 action requise"
            : $"Mises à jour terminées — {_report.Count} actions requises";
        var when = DateTime.Now.ToString("HH:mm");
        ReportSummary.Text = interrupted
            ? "Le programme de mise à jour s'est arrêté avant la fin. Les opérations effectuées sont conservées ; relancez la mise à jour pour terminer."
            : (clean ? $"Terminé à {when}. Aucune action requise." : $"Terminé à {when}. Les autres opérations se sont déroulées correctement.")
              + (reboot ? " Un redémarrage est nécessaire pour finaliser." : "");

        ReportCounts.Children.Clear();
        void Chip(string text, Brush color)
        {
            var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 6),
                                 Background = (Brush)FindResource("Surface"), BorderBrush = color, BorderThickness = new Thickness(1) };
            b.Child = new TextBlock { Text = text, Foreground = color, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
            ReportCounts.Children.Add(b);
        }
        if (_appSteps.Count > 0)
        {
            Chip($"{ok} application(s) à jour", Brushes.MediumSeaGreen);
            if (todo > 0) Chip($"{todo} action(s) requise(s)", Brushes.Goldenrod);
            if (later > 0) Chip($"{later} reportée(s)", Brushes.Gray);
            if (failed - dFailed > 0) Chip($"{failed - dFailed} application(s) en échec", Brushes.IndianRed);
        }
        if (_driverSteps.Count > 0)
        {
            Chip($"{dOk} pilote(s) installé(s)", Brushes.MediumSeaGreen);
            if (dLater > 0) Chip($"{dLater} pilote(s) reporté(s)", Brushes.Gray);
            if (dFailed > 0) Chip($"{dFailed} pilote(s) en échec", Brushes.IndianRed);
        }
        foreach (var s in _steps.Where(s => s.Key is not ("apps" or "drivers")))
            Chip($"{s.Title} : {(s.Status == "OK" ? (string.IsNullOrEmpty(s.Detail) ? "OK" : s.Detail) : s.Status.ToLowerInvariant())}", s.StatusBrush);

        ReportActionsBox.Visibility = _report.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReportDesktop.Text = desktopText ?? "";
        ReportDesktop.Visibility = desktopText == null ? Visibility.Collapsed : Visibility.Visible;
        ReportCard.Visibility = Visibility.Visible;
        InstallHeadline.Text = interrupted ? "Mise à jour interrompue" : "Mises à jour terminées";
        InstallCounter.Text = "";
        InstallProgress.Value = InstallProgress.Maximum;
        InstallCard.BringIntoView();
        ShowToast(ReportTitle.Text);
    }

    private void ReportLaunch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path && File.Exists(path)) Updates.Open(path);
        else ShowToast("L'installateur n'est plus disponible : relancez la mise à jour.");
    }

    private void ReportExclude_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id || string.IsNullOrEmpty(id)) return;
        if (!Settings.Current.WingetExclusions.Contains(id, StringComparer.OrdinalIgnoreCase)) Settings.Current.WingetExclusions.Add(id);
        Settings.Save();
        var item = _report.FirstOrDefault(r => r.Id == id);
        if (item != null) _report.Remove(item);
        ReportActionsBox.Visibility = _report.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var u in _winget.Where(u => u.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList()) _winget.Remove(u);
        ShowToast($"{item?.Name ?? id} ne sera plus proposé (réversible dans Réglages).");
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Log.FilePath)) DocumentLibrary.ShowInExplorer(Log.FilePath);
        else ShowToast("Aucune erreur enregistrée pour l'instant.");
    }

    private void Reboot_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Redémarrer le PC maintenant ? Pensez à enregistrer votre travail.", "Centre PC",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false });
    }
}
