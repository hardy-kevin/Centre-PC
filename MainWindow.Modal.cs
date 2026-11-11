using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CentrePC;

/// <summary>
/// Fenêtres ouvertes par-dessus l'application (rangement, tri, gestion, éditeur…) : elles seules sont utilisables.
/// Le menu et la page en dessous sont voilés et désactivés (souris et clavier), le menu affiche pourquoi,
/// et un clic à côté fait « vibrer » la fenêtre ouverte pour montrer que c'est elle qui attend une réponse.
/// Les boutons réduire / agrandir / fermer de Windows restent accessibles.
/// </summary>
public partial class MainWindow
{
    private Grid[] _overlays;
    private Grid _modalTop;
    private DateTime _modalHintAt;

    /// <summary>Fenêtres superposées, de la plus basse à la plus haute (ordre du XAML).</summary>
    private Grid[] Overlays => _overlays ??= new[]
    {
        ThemeEditor, SorterOverlay, DocSorterOverlay, ManagerOverlay, DocPlanOverlay, DeviceTestOverlay, PromptOverlay, WelcomeOverlay, QuickOfferOverlay, AppUpdateOverlay
    };

    private FrameworkElement[] Pages => new FrameworkElement[] { PageApps, PageDesktop, PageDocuments, PageUpdates, PageDevices, PageSettings };

    private void InitModal()
    {
        var dpd = DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(Grid));
        foreach (var o in Overlays)
        {
            dpd.AddValueChanged(o, (_, _) => UpdateModalState());
            KeyboardNavigation.SetTabNavigation(o, KeyboardNavigationMode.Cycle);   // Tab reste dans la fenêtre ouverte
            o.MouseLeftButtonDown += (s, e) => { if (ReferenceEquals(e.OriginalSource, s)) { ModalNudge(); e.Handled = true; } };
        }
    }

    private void UpdateModalState()
    {
        var shown = Overlays.Select((o, i) => (o, i)).Where(x => x.o.Visibility == Visibility.Visible)
                            .OrderBy(x => Panel.GetZIndex(x.o)).ThenBy(x => x.i).Select(x => x.o).ToList();
        var top = shown.LastOrDefault();
        bool modal = top != null;

        // Tout ce qui est en dessous : ni souris, ni clavier
        Sidebar.IsEnabled = !modal;
        foreach (var p in Pages) p.IsEnabled = !modal;
        foreach (var o in shown) o.IsEnabled = o == top;
        // Les fenêtres d'accueil et de mise à jour couvrent déjà tout l'écran, menu compris
        SidebarShield.Visibility = modal && top != WelcomeOverlay && top != AppUpdateOverlay && top != QuickOfferOverlay ? Visibility.Visible : Visibility.Collapsed;

        if (top != _modalTop)
        {
            _modalTop = top;
            // Le focus clavier quitte la page du dessous ; chaque fenêtre garde ses propres raccourcis
            if (modal && Keyboard.FocusedElement is DependencyObject f && !IsInside(f, top))
                Dispatcher.InvokeAsync(() => { if (Keyboard.FocusedElement is DependencyObject g && !IsInside(g, top)) Focus(); },
                                       System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void ModalBackdrop_Click(object sender, MouseButtonEventArgs e)
    {
        // Aperçu agrandi ouvert : un clic à l'extérieur (menu voilé compris) le referme simplement
        if (DocZoom.Visibility == Visibility.Visible) HideZoom();
        else ModalNudge();
        e.Handled = true;
    }

    /// <summary>Clic en dehors de la fenêtre ouverte : elle vibre brièvement et un message explique quoi faire.</summary>
    private void ModalNudge()
    {
        var top = _modalTop;
        if (top == null) return;
        var dialog = top.Children.OfType<Border>().FirstOrDefault(b => b.Visibility == Visibility.Visible) ?? (FrameworkElement)top;
        if (dialog.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            dialog.RenderTransform = tt;
        }
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360) };
        double t = 0;
        foreach (var x in new[] { -10.0, 9, -7, 5, -3, 0 })
            shake.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t += 60))));
        tt.BeginAnimation(TranslateTransform.XProperty, shake);

        // Liseré violet le temps de la vibration : « c'est ici que ça se passe »
        if (dialog is Border b)
        {
            var original = b.BorderBrush;
            var flash = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF));
            b.BorderBrush = flash;
            flash.BeginAnimation(SolidColorBrush.OpacityProperty, new DoubleAnimation(1, 0.2, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(250) });
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) => { timer.Stop(); b.BorderBrush = original; };
            timer.Start();
        }
        if ((DateTime.UtcNow - _modalHintAt).TotalSeconds > 3)
        {
            _modalHintAt = DateTime.UtcNow;
            ShowToast("Une fenêtre est ouverte : terminez-la ou fermez-la (Échap) pour revenir au reste de Centre PC.");
        }
    }
}
