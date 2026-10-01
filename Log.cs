using System;
using System.IO;

namespace ClaudeUsageTray
{
    /// <summary>
    /// Tiny append-only log at %APPDATA%\ClaudeUsageTray\log.txt, rotated to log.old.txt at 256 KB.
    /// Never throws: logging must not take the tray icon down. Never write tokens here.
    /// </summary>
    public static class Log
    {
        private const long MaxBytes = 256 * 1024;
        private static readonly object Gate = new();

        public static string FilePath => Path.Combine(AppSettings.Directory, "log.txt");

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppSettings.Directory);

                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        var old = Path.Combine(AppSettings.Directory, "log.old.txt");
                        File.Copy(FilePath, old, overwrite: true);
                        File.Delete(FilePath);
                    }

                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
