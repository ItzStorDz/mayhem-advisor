using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MayhemAdvisor.Core;
using static MayhemAdvisor.Core.Lang;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;

namespace MayhemAdvisor.Overlay;

/// <summary>Una carta de aumento detectada en pantalla, con su puntuación y los objetos que le van bien.</summary>
/// <param name="Anvil">Si es un fragmento de Yunque de Estadísticas: su puntuación y el porqué (no hay % de victorias).</param>
/// <param name="Score">Nota de 0 a 100 (ver <see cref="AugmentScale"/>); <paramref name="Grade"/> es su rango (OP, S, A…).</param>
public sealed record AugmentCard(AugmentHit Hit, Recommendation Rec, IReadOnlyList<ItemAdvice> GoodItems, AnvilRating? Anvil = null,
    AugmentSynergy? Synergy = null, double Score = 50, string Grade = "B")
{
    // Por lo que sube el %, más lo bien que combina con tus aumentos: así se comparan bien aumentos con datos
    // de todos los jugadores y con los tuyos.
    public double SortScore => Anvil?.Score ?? AugmentScale.Evidence(Rec) + (Synergy?.Bonus ?? 0);
}

/// <summary>
/// Ventana transparente a pantalla completa por encima del juego.
/// Los clics la atraviesan, así que nunca estorba al jugar.
/// </summary>
public partial class MainWindow : Window
{
    // Paleta inspirada en la interfaz de LoL: dorado sobre azul muy oscuro.
    static readonly SolidColorBrush Gold = Brush(0xC8, 0xAA, 0x6E), GoldBright = Brush(0xF0, 0xD0, 0x80),
        Text = Brush(0xF0, 0xE6, 0xD2), Muted = Brush(0x8C, 0x96, 0xA0), CardBg = Brush(0x0B, 0x0F, 0x14, 0xF0),
        Green = Brush(0x4C, 0xD9, 0x7B), Red = Brush(0xE8, 0x4C, 0x4C);

    readonly List<UIElement> _cards = [];
    double _gameHeight; // alto del juego en unidades de WPF (en modo ventana es menos que la pantalla)
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    public MainWindow()
    {
        InitializeComponent();
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        _statusTimer.Tick += (_, _) => { StatusBox.Visibility = Visibility.Collapsed; _statusTimer.Stop(); };
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    /// <summary>Muestra un aviso pequeño en la esquina durante unos segundos.</summary>
    public void ShowStatus(string text)
    {
        if (_suppressed) return;
        StatusText.Text = text;
        StatusBox.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // ───────────────────────── Cartas de aumentos ─────────────────────────

    public bool HasCards => _cards.Count > 0;

    public void ClearCards()
    {
        foreach (var c in _cards) Layer.Children.Remove(c);
        _cards.Clear();
    }

    /// <summary>Dibuja una tarjeta encima del nombre de cada aumento. La mejor opción va en dorado.</summary>
    /// <param name="gameHeightPx">Alto de la ventana del juego en píxeles reales (las cartas del juego se escalan con él).</param>
    public void ShowCards(IReadOnlyList<AugmentCard> cards, int gameHeightPx)
    {
        if (_suppressed) return;
        ClearCards();
        if (cards.Count == 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        _gameHeight = gameHeightPx / dpi.DpiScaleY;
        var bestId = cards.MaxBy(c => c.Score)!.Rec.AugmentId;

        foreach (var card in cards)
        {
            bool best = card.Rec.AugmentId == bestId;
            var panel = new StackPanel();

            if (card.Anvil is { } anvil)
            {
                // Yunque: sin % de victorias (no se registra qué fragmento eliges), solo el porqué.
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                if (best) head.Children.Add(Label(L("★ NOTA MÁS ALTA", "★ TOP SCORE"), 12, GoldBright, FontWeights.Bold, new Thickness(0, 0, 8, 0)));
                head.Children.Add(Chip(card.Rec.Tier, TierColor(card.Rec.Tier)));
                head.Children.Add(Label(L("Yunque", "Anvil"), 12, Muted, FontWeights.Normal, new Thickness(8, 3, 0, 0)));
                panel.Children.Add(head);
                panel.Children.Add(Label(anvil.Reason, 12, Text, FontWeights.Normal, new Thickness(0, 4, 0, 0), wrap: true));
                if (card.Hit.Augment.StatList.Length > 0)
                    panel.Children.Add(Label(L("Da: ", "Gives: ") + string.Join(", ", card.Hit.Augment.StatList.Select(Core.Anvil.Label)),
                        11, Muted, FontWeights.Normal, new Thickness(0, 2, 0, 0), wrap: true));
                AddCard(panel, card, best, dpi);
                continue;
            }

            var top = new StackPanel { Orientation = Orientation.Horizontal };
            if (best) top.Children.Add(Label(L("★ NOTA MÁS ALTA", "★ TOP SCORE"), 12, GoldBright, FontWeights.Bold, new Thickness(0, 0, 8, 0)));
            // Nota 0-100 y rango, sin el % de victorias del aumento (Riot no lo permite en overlays).
            top.Children.Add(Chip(card.Grade, TierColor(card.Grade)));
            top.Children.Add(Label($"{card.Score:0.0}", 16, Text, FontWeights.SemiBold, new Thickness(8, 0, 0, 0)));
            top.Children.Add(Label("/100 · " + new string('▮', AugmentScale.Confidence(card.Rec)) + new string('▯', 3 - AugmentScale.Confidence(card.Rec)), 12,
                Muted, FontWeights.Normal, new Thickness(4, 3, 0, 0)));
            panel.Children.Add(top);
            // Todas las partidas que respaldan la nota (mayhemmeta + AramHex/aramkit), como en el overlay de Electron (1.8.1).
            panel.Children.Add(Label(card.Rec.Global ? L($"~{card.Rec.EvidenceGames:N0} partidas en Mayhem (todos)", $"~{card.Rec.EvidenceGames:N0} Mayhem games (everyone)")
                : card.Rec.Games > 0 ? L($"{card.Rec.Games} partidas con tu campeón", $"{card.Rec.Games} games on your champion") : L("sin partidas con tu campeón aún", "no games on your champion yet"),
                11, Muted, FontWeights.Normal, new Thickness(0, 2, 0, 0)));

            if (card.Hit.Augment.StatList.Length > 0)
                panel.Children.Add(Label(L("Potencia: ", "Boosts: ") + string.Join(", ", card.Hit.Augment.StatList.Select(StatTags.Label)),
                    11, Muted, FontWeights.Normal, new Thickness(0, 2, 0, 0)));

            if (card.Synergy is { } syn)
                panel.Children.Add(Label((syn.Bonus >= 0 ? "✓ " : "✗ ") + syn.Reason, 11.5, syn.Bonus >= 0 ? Green : Red,
                    FontWeights.SemiBold, new Thickness(0, 5, 0, 0), wrap: true));

            if (card.GoodItems.Count > 0)
            {
                panel.Children.Add(Label(L("VA BIEN CON", "GOES WELL WITH"), 10, Gold, FontWeights.Bold, new Thickness(0, 6, 0, 2)));
                var icons = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var it in card.GoodItems) icons.Children.Add(ItemIcon(it.Item, 30, new Thickness(0, 0, 5, 0)));
                panel.Children.Add(icons);
                panel.Children.Add(Label(string.Join(" · ", card.GoodItems.Select(i => i.Item.DisplayName)), 10.5, Text,
                    FontWeights.Normal, new Thickness(0, 3, 0, 0), wrap: true));
            }

            AddCard(panel, card, best, dpi);
        }
    }

    /// <summary>Envuelve el contenido en una tarjeta y la coloca encima de la carta del juego.</summary>
    void AddCard(StackPanel panel, AugmentCard card, bool best, DpiScale dpi)
    {
        var border = new Border
        {
            Width = 230,
            Background = CardBg,
            BorderBrush = best ? GoldBright : new SolidColorBrush(TierColor(card.Rec.Tier)),
            BorderThickness = new Thickness(best ? 2.5 : 1.5),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 10),
            Child = panel,
            Effect = new DropShadowEffect { BlurRadius = best ? 22 : 12, ShadowDepth = 0, Color = best ? Color.FromRgb(0xF0, 0xD0, 0x80) : Colors.Black, Opacity = best ? 0.55 : 0.6 },
        };
        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // De píxeles reales a unidades de WPF. La carta del juego empieza ~21 % de pantalla por encima
        // de su nombre; ponemos nuestra tarjeta justo encima, sin tapar la descripción ni los botones.
        double cx = (card.Hit.Bounds.X + card.Hit.Bounds.Width / 2.0) / dpi.DpiScaleX;
        double nameTop = card.Hit.Bounds.Y / dpi.DpiScaleY;
        Canvas.SetLeft(border, cx - border.DesiredSize.Width / 2);
        Canvas.SetTop(border, Math.Max(8, nameTop - _gameHeight * 0.235 - border.DesiredSize.Height));

        Layer.Children.Add(border);
        _cards.Add(border);
    }

    // ───────────────────────── Panel lateral ─────────────────────────

    public void HideSidePanel() => SidePanel.Visibility = Visibility.Collapsed;

    /// <summary>Panel en partida: equipo enemigo, siguiente compra, botas, objetos y tus aumentos.</summary>
    public void ShowAdvice(Advice advice, double gold)
    {
        if (_suppressed) return;
        var c = SideContent.Children;
        c.Clear();

        c.Add(Section(L("EQUIPO ENEMIGO", "ENEMY TEAM"), first: true));
        c.Add(Label(advice.Enemies.Describe(), 13, Text, FontWeights.Normal, new Thickness(0, 0, 0, 0), wrap: true));

        if (advice.Next is { } next)
        {
            c.Add(Section(L("SIGUIENTE COMPRA", "NEXT BUY")));
            var row = new DockPanel();
            var icon = ItemIcon(next.Item, 40, new Thickness(0, 0, 10, 0));
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var info = new StackPanel();
            info.Children.Add(Label(next.Item.DisplayName, 14, GoldBright, FontWeights.SemiBold, new Thickness(0)));
            info.Children.Add(Label(next.GoldMissing == 0 ? L("¡Ya te alcanza!", "You can afford it!") : L($"Te faltan {next.GoldMissing} de oro (cuesta {next.Cost})", $"{next.GoldMissing} gold to go (costs {next.Cost})"),
                11.5, next.GoldMissing == 0 ? Green : Muted, FontWeights.Normal, new Thickness(0, 1, 0, 4)));
            info.Children.Add(new ProgressBar
            {
                Height = 5, Minimum = 0, Maximum = Math.Max(1, next.Cost), Value = Math.Min(gold, next.Cost),
                Foreground = next.GoldMissing == 0 ? Green : Gold, Background = Brush(0x22, 0x2A, 0x33), BorderThickness = new Thickness(0),
            });
            row.Children.Add(info);
            c.Add(row);
        }

        if (advice.Swap is { } swap)
        {
            c.Add(Section(L("VENDER Y CAMBIAR", "SELL AND SWAP")));
            var icons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            icons.Children.Add(ItemIcon(swap.Sell, 32, new Thickness(0)));
            icons.Children.Add(Label("→", 20, GoldBright, FontWeights.Bold, new Thickness(10, 2, 10, 0)));
            icons.Children.Add(ItemIcon(swap.Buy.Item, 32, new Thickness(0)));
            c.Add(icons);
            c.Add(Label(L($"Vende {swap.Sell.DisplayName} (+{swap.SellGold} oro): {swap.SellReason}", $"Sell {swap.Sell.DisplayName} (+{swap.SellGold} gold): {swap.SellReason}"), 11.5, Red, FontWeights.Normal, new Thickness(0, 0, 0, 2), wrap: true));
            c.Add(Label(L($"Compra {swap.Buy.Item.DisplayName}: {swap.Buy.Reason}", $"Buy {swap.Buy.Item.DisplayName}: {swap.Buy.Reason}"), 11.5, Green, FontWeights.Normal, new Thickness(0), wrap: true));
        }

        if (advice.Boots is { } boots)
        {
            c.Add(Section(L("BOTAS", "BOOTS")));
            c.Add(ItemRow(boots, highlight: false));
        }

        // Con el inventario lleno solo tiene sentido terminar lo que ya llevas (o vender: ver arriba).
        c.Add(Section(advice.InventoryFull ? L("TERMINAR LO QUE LLEVAS", "FINISH WHAT YOU HAVE") : L("OBJETOS", "ITEMS")));
        if (advice.Items.Count == 0)
            c.Add(Label(advice.InventoryFull ? L("Inventario lleno: ya no caben más objetos.", "Inventory full: no room for more items.") : L("Aún no hay datos suficientes.", "Not enough data yet."),
                12, Muted, FontWeights.Normal, new Thickness(0), wrap: true));
        for (int i = 0; i < advice.Items.Count; i++) c.Add(ItemRow(advice.Items[i], highlight: i == 0));

        if (advice.MyAugments.Count > 0)
        {
            c.Add(Section(L("TUS AUMENTOS", "YOUR AUGMENTS")));
            var wrap = new WrapPanel();
            foreach (var a in advice.MyAugments)
            {
                var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 4) };
                if (a.Icon != "") chip.Children.Add(Img(a.Icon, 20, new Thickness(0, 0, 4, 0)));
                chip.Children.Add(Label(a.DisplayName, 11.5, Text, FontWeights.Normal, new Thickness(0, 2, 0, 0)));
                wrap.Children.Add(chip);
            }
            c.Add(wrap);
        }

        if (advice.Skills is { } s)
        {
            c.Add(Section(L("HABILIDADES", "SKILLS")));
            c.Add(Label(L($"Maximiza {string.Join(" > ", s.MaxOrder.ToCharArray())}  ·  {s.WinRate:P0} en {s.Games} partidas", $"Max {string.Join(" > ", s.MaxOrder.ToCharArray())}  ·  {s.WinRate:P0} in {s.Games} games"), 12.5, Text, FontWeights.Normal, new Thickness(0)));
        }

        if (advice.FullBuild is { Count: > 0 } build)
        {
            // La meta: 6 objetos según tus aumentos, lo que llevas y lo que lleva el enemigo. ✓ = ya lo tienes.
            c.Add(Section(L("BUILD COMPLETA RECOMENDADA", "RECOMMENDED FULL BUILD")));
            var icons = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var slot in build) icons.Children.Add(BuildIcon(slot));
            c.Add(icons);
            foreach (var slot in build.Where(b => !b.Owned))
                c.Add(Label($"{slot.Item.Item.DisplayName}: {slot.Item.Reason}", 10.5, Text, FontWeights.Normal, new Thickness(0, 1, 0, 1), wrap: true));
            if (build.All(b => b.Owned))
                c.Add(Label(L("Ya tienes la build recomendada.", "You already have the recommended build."), 11, Green, FontWeights.Normal, new Thickness(0)));
        }

        PlaceSidePanel(0.18);
    }

    /// <summary>Panel en selección de campeón: tu campeón y los del banco, con su % de victorias.</summary>
    public void ShowChampSelect(IReadOnlyList<ChampionOption> options)
    {
        if (_suppressed) return;
        var c = SideContent.Children;
        c.Clear();
        c.Add(Section(L("SELECCIÓN DE CAMPEÓN", "CHAMPION SELECT"), first: true));
        bool global = options.Any(o => o.Mayhem is not null);
        c.Add(Label(global ? L("% de victorias en Mayhem (todos los jugadores) · el tuyo debajo", "Mayhem win rate (all players) · yours below") : L("% de victorias en tus partidas de Mayhem", "Win rate in your Mayhem games"),
            11, Muted, FontWeights.Normal, new Thickness(0, 0, 0, 6)));

        // Con datos de todos los jugadores basta con ellos; con los tuyos, hacen falta al menos 3 partidas.
        var best = global ? options.Where(o => o.Mayhem is not null).MaxBy(o => o.BestWinRate)
                          : options.Where(o => o.Games >= 3).MaxBy(o => o.WinRate);
        foreach (var o in options)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var icon = Img(o.Champion.Icon, 34, new Thickness(0, 0, 10, 0));
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);

            var info = new StackPanel();
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(Label(o.Champion.DisplayName, 14, o == best ? GoldBright : Text, FontWeights.SemiBold, new Thickness(0)));
            if (o.IsCurrent) name.Children.Add(Label(L("  (tuyo)", "  (yours)"), 11, Muted, FontWeights.Normal, new Thickness(0, 3, 0, 0)));
            if (o == best && !o.IsCurrent) name.Children.Add(Label(L("  ★ mejor opción", "  ★ best pick"), 11, GoldBright, FontWeights.Bold, new Thickness(0, 3, 0, 0)));
            info.Children.Add(name);
            if (o.Mayhem is { } m)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
                line.Children.Add(Label(m.Tier, 11.5, new SolidColorBrush(TierColor(m.Tier)), FontWeights.Bold, new Thickness(0, 0, 6, 0)));
                line.Children.Add(Label(L($"{o.BestWinRate:P1} de victorias · {m.Games ?? 0:N0} partidas", $"{o.BestWinRate:P1} win rate · {m.Games ?? 0:N0} games"), 11.5, Text, FontWeights.Normal, new Thickness(0)));
                info.Children.Add(line);
            }
            if (o.Spells is { } sp)
            {
                var spells = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                foreach (var ic in sp.Icons.Where(i => i != "")) spells.Children.Add(Img(ic, 18, new Thickness(0, 0, 3, 0)));
                spells.Children.Add(Label($" {string.Join(" + ", sp.Names)} · {sp.WinRate:P1}", 11, Text, FontWeights.SemiBold, new Thickness(0, 1, 0, 0)));
                info.Children.Add(spells);
            }
            info.Children.Add(Label(o.Games > 0 ? L($"{(o.Mayhem is null ? "" : "Tú: ")}{o.WinRate:P0} de victorias · {o.Games} partidas", $"{(o.Mayhem is null ? "" : "You: ")}{o.WinRate:P0} win rate · {o.Games} games") : L("sin partidas tuyas aún", "no games of yours yet"),
                o.Mayhem is null ? 11.5 : 10.5, o.Mayhem is null && o.Games >= 3 ? Text : Muted, FontWeights.Normal, new Thickness(0, 1, 0, 0)));
            row.Children.Add(info);
            c.Add(row);
        }
        if (!global && options.All(o => o.Games < 3))
            c.Add(Label(L("Pocos datos todavía: juega más partidas y esto será más fiable.", "Little data yet: play more games and this gets more reliable."), 11, Muted, FontWeights.Normal, new Thickness(0, 6, 0, 0), wrap: true));

        PlaceSidePanel(0.12);
    }

    void PlaceSidePanel(double topFraction)
    {
        SidePanel.Visibility = Visibility.Visible;
        SidePanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Borde derecho (lejos del minimapa y de la tienda).
        Canvas.SetLeft(SidePanel, Width - SidePanel.DesiredSize.Width - 14);
        Canvas.SetTop(SidePanel, Height * topFraction);
    }

    // ───────────────────────── Piezas de interfaz ─────────────────────────

    static TextBlock Label(string text, double size, Brush color, FontWeight weight, Thickness margin, bool wrap = false) => new()
    {
        Text = text, FontSize = size, Foreground = color, FontWeight = weight, Margin = margin,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    static TextBlock Section(string title, bool first = false) =>
        Label(title, 10.5, Gold, FontWeights.Bold, new Thickness(0, first ? 0 : 12, 0, 4));

    static Border Chip(string text, Color color) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B)),
        BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(7, 0, 7, 1),
        Child = new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(color) },
    };

    static DockPanel ItemRow(ItemAdvice advice, bool highlight)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
        var icon = ItemIcon(advice.Item, 32, new Thickness(0, 0, 10, 0));
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var info = new StackPanel();
        info.Children.Add(Label(advice.Item.DisplayName, 13.5, highlight ? GoldBright : Text, FontWeights.SemiBold, new Thickness(0)));
        info.Children.Add(Label(advice.Reason, 11, Muted, FontWeights.Normal, new Thickness(0, 1, 0, 0), wrap: true));
        row.Children.Add(info);
        return row;
    }

    /// <summary>Icono de la build completa: los que ya tienes llevan un ✓ verde; los que faltan, borde dorado brillante.</summary>
    static FrameworkElement BuildIcon(BuildSlot slot)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 5, 0), ToolTip = slot.Item.Item.DisplayName };
        grid.Children.Add(new Border
        {
            BorderBrush = slot.Owned ? Green : GoldBright, BorderThickness = new Thickness(slot.Owned ? 1 : 2),
            Child = Img(slot.Item.Item.Icon, 34, new Thickness(0)), Opacity = slot.Owned ? 0.75 : 1,
        });
        if (slot.Owned)
            grid.Children.Add(new TextBlock
            {
                Text = "✓", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Green, Background = CardBg,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(2, 0, 2, 0),
            });
        return grid;
    }

    static FrameworkElement ItemIcon(ItemInfo item, double size, Thickness margin) =>
        new Border
        {
            BorderBrush = Gold, BorderThickness = new Thickness(1), Margin = margin, VerticalAlignment = VerticalAlignment.Top,
            Child = Img(item.Icon, size, new Thickness(0)),
        };

    static readonly Dictionary<string, BitmapImage> ImageCache = [];

    static Image Img(string url, double size, Thickness margin)
    {
        var img = new Image { Width = size, Height = size, Margin = margin, VerticalAlignment = VerticalAlignment.Top };
        if (string.IsNullOrEmpty(url)) return img;
        if (!ImageCache.TryGetValue(url, out var bmp))
        {
            bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url);
            bmp.DecodePixelWidth = 64;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            ImageCache[url] = bmp;
        }
        img.Source = bmp;
        return img;
    }

    static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    static Color TierColor(string tier) => tier switch
    {
        "OP" => Color.FromRgb(0xF0, 0xD0, 0x80), // por encima de S en mayhemmeta.com
        "S" => Color.FromRgb(0xFF, 0x6B, 0x6B),
        "A" => Color.FromRgb(0xFF, 0xA9, 0x4D),
        "B" => Color.FromRgb(0x74, 0xC0, 0xFC),
        "C" => Color.FromRgb(0x8C, 0x96, 0xA0),
        _ => Color.FromRgb(0x5C, 0x62, 0x68),
    };

    /// <summary>
    /// El juego se pone por encima al recibir el foco; volvemos a colocarnos arriba del todo
    /// (sin robarle el foco). Se llama en cada vuelta del bucle.
    /// </summary>
    /// <summary>True mientras el overlay de Electron está activo: esta ventana se queda oculta y no dibuja nada.</summary>
    public bool Suppressed
    {
        get => _suppressed;
        set { _suppressed = value; Visibility = value ? Visibility.Hidden : Visibility.Visible; }
    }
    bool _suppressed;

    public void KeepOnTop()
    {
        if (_suppressed) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        // Las capturas de pantalla (también la nuestra para el OCR) no ven el overlay: así no tapa ni confunde la lectura de las cartas.
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
    }

    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
