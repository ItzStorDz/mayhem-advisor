using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MayhemAdvisor.Overlay;

/// <summary>El icono del programa (icono.ico junto al .exe; se crea con herramientas/crear-icono.js).</summary>
static class AppIcon
{
    static string File => Path.Combine(AppContext.BaseDirectory, "icono.ico");

    /// <summary>Para la bandeja: el tamaño pequeño que usa Windows con la escala actual de la pantalla.</summary>
    public static System.Drawing.Icon ForTray()
    {
        try { return new System.Drawing.Icon(File, System.Windows.Forms.SystemInformation.SmallIconSize); }
        catch (Exception ex) { Log.Write($"No pude cargar el icono: {ex.Message}"); return System.Drawing.SystemIcons.Application; }
    }

    /// <summary>Para las ventanas de WPF (barra de tareas y Alt+Tab).</summary>
    public static ImageSource? ForWindow()
    {
        try { return BitmapFrame.Create(new Uri(File), BitmapCreateOptions.None, BitmapCacheOption.OnLoad); }
        catch (Exception ex) { Log.Write($"No pude cargar el icono: {ex.Message}"); return null; }
    }
}
