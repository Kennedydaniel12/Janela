using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;

namespace Janela;

static class AppIcons
{
    public const string Host = "icons.janela.local";
    public static string Folder => Path.Combine(Program.DataPath, "app-icons");

    // Both capture entry points run on the dashboard's UI thread.
    static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int> LastAttempt = new(StringComparer.OrdinalIgnoreCase);

    public static string? Find(string app)
    {
        if (string.IsNullOrEmpty(app)) return null;
        if (Known.TryGetValue(app, out var url)) return url;
        var name = FileName(app);
        if (!File.Exists(Path.Combine(Folder, name))) return null;
        return Known[app] = $"https://{Host}/{name}";
    }

    public static void Capture(Process process, string app)
    {
        if (Known.ContainsKey(app) ||
            (LastAttempt.TryGetValue(app, out var pid) && pid == process.Id)) return;

        LastAttempt[app] = process.Id;
        if (Find(app) != null) return;
        try
        {
            var executable = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(executable)) return;
            using var icon = Icon.ExtractAssociatedIcon(executable);
            if (icon == null) return;
            using var bitmap = icon.ToBitmap();
            Directory.CreateDirectory(Folder);
            var name = FileName(app);
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            File.WriteAllBytes(Path.Combine(Folder, name), png.ToArray());
            Known[app] = $"https://{Host}/{name}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.Runtime.InteropServices.ExternalException or
            IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Protected and short-lived processes may not expose their executable.
        }
    }

    static string FileName(string app) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(app.ToLowerInvariant()))) + ".png";
}
