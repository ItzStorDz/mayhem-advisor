using Microsoft.Win32;

namespace MayhemAdvisor.Overlay;

/// <summary>
/// "Iniciar con Windows": una entrada en HKCU\...\Run (solo para tu usuario, sin permisos de administrador).
/// El instalador usa la misma entrada, así que la casilla del instalador y la del menú de la bandeja son la misma.
/// </summary>
static class WindowsStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "MayhemAdvisor";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
                else key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch (Exception ex) { Log.Write($"No pude cambiar el inicio con Windows: {ex.Message}"); }
        }
    }
}
