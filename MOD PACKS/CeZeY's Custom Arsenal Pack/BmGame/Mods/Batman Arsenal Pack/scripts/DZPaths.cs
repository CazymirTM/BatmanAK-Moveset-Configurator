using System.IO;
using BmSDK.Framework;

namespace DeadZoneAim;

public static class DZPaths
{
    static string? _dir;
    public static string Dir
    {
        get
        {
            if (_dir != null) return _dir;
            var d = Path.Combine(FileUtils.GetGamePath(), "Mods", "Batman Arsenal Pack") + Path.DirectorySeparatorChar;
            try { Directory.CreateDirectory(d); } catch { }
            try
            {
                var log = new FileInfo(d + "aim_log.txt");
                if (log.Exists && log.Length > 1024 * 1024) log.Delete();
            }
            catch { }
            return _dir = d;
        }
    }
}
