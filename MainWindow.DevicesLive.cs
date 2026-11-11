using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CentrePC.Core;

namespace CentrePC;

/// <summary>
/// Page Périphériques en direct : aucune carte ne reste sur un ancien état.
/// <list type="bullet">
/// <item>Notifications de Windows : périphérique branché ou retiré, réglage système modifié, écran, alimentation, réseau.</item>
/// <item>Empreintes instantanées (touches Verr. Maj / Verr. Num, réglages du clavier et de la souris, volume, place libre, mémoire,
/// batterie, fréquence d'écran…) comparées toutes les 1,5 seconde quand la page est affichée, toutes les 15 secondes sinon
/// (pour la pastille du menu).</item>
/// </list>
/// Seule la carte concernée est réanalysée, une fois les changements terminés.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] Fingerprinted = { "input", "audio", "storage", "memory", "battery", "display", "network" };
    private readonly Dictionary<string, string> _fingerprints = new();
    private readonly HashSet<string> _liveDirty = new(), _liveBusy = new();
    private DispatcherTimer _liveTimer, _liveDebounce, _liveFast;
    private string _inputFingerprint;
    private bool _liveChecking, _deviceMenuOpen;

    private void InitDevicesLive()
    {
        // Verr. Maj / Verr. Num lus depuis le thread de la fenêtre (seul fiable), même pendant une analyse en arrière-plan
        InputChecks.ToggleReader = vk => Dispatcher.CheckAccess() ? Toggle(vk) : Dispatcher.Invoke(() => Toggle(vk));

        _liveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _liveDebounce.Tick += (_, _) => { _liveDebounce.Stop(); FlushLive(); };
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _liveTimer.Tick += async (_, _) => await CheckFingerprintsAsync();
        _liveTimer.Start();
        // Clavier et souris (Verr. Num, Verr. Maj, disposition, réglages) : lecture de quelques microsecondes, vérifiée 4 fois par seconde
        _liveFast = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _liveFast.Tick += (_, _) =>
        {
            if (!IsActive || !DevicesPageShown || _devicesScanning || !_deviceCards.ContainsKey("input")) return;
            try
            {
                var v = InputChecks.KeyboardMouseFingerprint(Toggle(0x14), Toggle(0x90));
                if (_inputFingerprint != null && v != _inputFingerprint) { _liveDirty.Add("input"); FlushLive(); }
                _inputFingerprint = v;
                _fingerprints["input"] = v;
            }
            catch (Exception ex) { Log.Error("Périphériques en direct", ex); _liveFast.Stop(); }
        };
        _liveFast.Start();
        // Pas de mise à jour pendant qu'un menu (clic droit sur un lecteur…) est ouvert : il se refermerait
        DevicesPanel.ContextMenuOpening += (_, _) => _deviceMenuOpen = true;
        DevicesPanel.ContextMenuClosing += (_, _) => { _deviceMenuOpen = false; if (_liveDirty.Count > 0) MarkLive(); };

        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(DevicesWndProc);
        NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.InvokeAsync(() => MarkLive("network"));
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Dispatcher.InvokeAsync(() => MarkLive("network"));
        IsVisibleChanged += (_, _) => UpdateLiveInterval();
        Activated += (_, _) => { UpdateLiveInterval(); _ = CheckFingerprintsAsync(); };
        Deactivated += (_, _) => UpdateLiveInterval();
    }

    private bool _capsOn, _numOn;

    /// <summary>
    /// Verr. Maj / Verr. Num : lus seulement quand Centre PC est la fenêtre active (état exact à ce moment-là) ;
    /// sinon, le dernier état connu. Aucune interaction avec les autres fenêtres : le jeu ou la saisie en cours ne perdent jamais le focus.
    /// </summary>
    private bool Toggle(int vk)
    {
        if (IsActive) { _capsOn = InputChecks.ReadToggle(0x14); _numOn = InputChecks.ReadToggle(0x90); }
        return vk == 0x14 ? _capsOn : _numOn;
    }

    private bool DevicesPageShown => IsVisible && WindowState != WindowState.Minimized && PageDevices.Visibility == Visibility.Visible;

    /// <summary>Rapide quand la page est affichée, espacé sinon (la pastille du menu reste juste).</summary>
    private void UpdateLiveInterval()
    {
        if (_liveTimer == null) return;
        _liveTimer.Interval = DevicesPageShown ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(15);
    }

    /// <summary>Messages de Windows : chaque type de changement réveille les cartes qu'il concerne.</summary>
    private IntPtr DevicesWndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        switch (msg)
        {
            case 0x0219: // WM_DEVICECHANGE : périphérique branché, retiré ou modifié
                DeviceHealth.InvalidateHardware();
                MarkLive("problems", "audio", "storage", "memory", "printers", "bluetooth", "camera", "input", "display");
                break;
            case 0x001A: // WM_SETTINGCHANGE : réglage de Windows modifié (accessibilité, souris, clavier, langue…)
                MarkLive("input", "audio");
                break;
            case 0x0051: // WM_INPUTLANGCHANGE : disposition du clavier changée
                MarkLive("input");
                break;
            case 0x007E: // WM_DISPLAYCHANGE
                MarkLive("display");
                break;
            case 0x0218: // WM_POWERBROADCAST : secteur / batterie
                MarkLive("battery");
                break;
        }
        return IntPtr.Zero;
    }

    private void MarkLive(params string[] keys)
    {
        foreach (var k in keys) _liveDirty.Add(k);
        _liveDebounce?.Stop();
        _liveDebounce?.Start();
    }

    /// <summary>Compare les empreintes à celles de la dernière analyse ; les cartes qui ont changé sont réanalysées.</summary>
    private async Task CheckFingerprintsAsync()
    {
        if (_liveChecking || _devicesScanning || _deviceCards.Count == 0) return;
        _liveChecking = true;
        try
        {
            bool caps = Toggle(0x14), num = Toggle(0x90);
            var now = await Task.Run(() => Fingerprinted.ToDictionary(k => k, k => k == "input" ? InputChecks.KeyboardMouseFingerprint(caps, num) : DeviceHealth.Fingerprint(k)));
            foreach (var (k, v) in now)
            {
                if (v == null) continue;
                if (k == "input")
                {
                    if (_inputFingerprint != null && _inputFingerprint != v) _liveDirty.Add(k);
                    _inputFingerprint = v;
                    _fingerprints[k] = v;
                    continue;
                }
                if (_fingerprints.TryGetValue(k, out var old) && old != v) _liveDirty.Add(k);
                _fingerprints[k] = v;
            }
            if (_liveDirty.Count > 0) FlushLive();
        }
        catch (Exception ex) { Log.Error("Périphériques en direct", ex); }
        finally { _liveChecking = false; }
    }

    /// <summary>Réanalyse les cartes marquées (une seule analyse à la fois par carte ; un changement pendant l'analyse en relance une).</summary>
    private void FlushLive()
    {
        if (_devicesScanning) return;   // l'analyse complète en cours verra l'état à jour
        if (_deviceMenuOpen) return;    // reprise à la fermeture du menu
        foreach (var k in _liveDirty.ToList())
        {
            if (!DeviceHealth.Order.Contains(k)) { _liveDirty.Remove(k); continue; }
            if (_liveBusy.Contains(k)) continue;
            _liveDirty.Remove(k);
            _ = LiveRescanAsync(k);
        }
    }

    private async Task LiveRescanAsync(string key)
    {
        _liveBusy.Add(key);
        try { await RescanDeviceAsync(key); }
        finally
        {
            _liveBusy.Remove(key);
            if (_liveDirty.Contains(key)) FlushLive();
        }
    }

    /// <summary>Après une analyse complète : les empreintes de référence sont celles de l'état analysé.</summary>
    private async Task ResetFingerprintsAsync()
    {
        bool caps = Toggle(0x14), num = Toggle(0x90);
        var now = await Task.Run(() => Fingerprinted.ToDictionary(k => k, k => k == "input" ? InputChecks.KeyboardMouseFingerprint(caps, num) : DeviceHealth.Fingerprint(k)));
        foreach (var (k, v) in now) if (v != null) _fingerprints[k] = v;
        _inputFingerprint = now["input"];
    }
}
