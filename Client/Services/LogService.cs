using System;
using System.IO;
using System.Text;

namespace intuneMigratorClient.Services;

public static class LogService
{
    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), "intuneMigratorClient.log");
    private static readonly object LockObj = new();
    private const long MaxLogSize = 4 * 1024 * 1024; // 4 MB

    public static void Info(string message) => WriteLog(message, 1);
    public static void Warning(string message) => WriteLog(message, 2);
    public static void Error(string message) => WriteLog(message, 3);

    private static void WriteLog(string message, int type)
    {
        try
        {
            var now = DateTime.Now;
            var tz = TimeZoneInfo.Local.GetUtcOffset(now);
            // CMTrace format expects offset in minutes (e.g., +60)
            var offset = $"{(tz.TotalMinutes >= 0 ? "+" : "-")}{Math.Abs((int)tz.TotalMinutes)}";

            var logEntry = $"<![LOG[{message}]LOG]!><time=\"{now:HH:mm:ss.fff}{offset}\" date=\"{now:MM-dd-yyyy}\" component=\"intuneMigratorClient\" context=\"\" type=\"{type}\" thread=\"{Environment.CurrentManagedThreadId}\" file=\"\">";

            lock (LockObj)
            {
                var fileInfo = new FileInfo(LogFile);
                if (fileInfo.Exists && fileInfo.Length > MaxLogSize)
                {
                    var backupFile = LogFile + ".log_";
                    if (File.Exists(backupFile))
                    {
                        File.Delete(backupFile);
                    }
                    File.Move(LogFile, backupFile);
                }
                File.AppendAllText(LogFile, logEntry + Environment.NewLine);
            }
        }
        catch
        {
            // Fail silently to avoid crashing the application on logging errors
        }
    }
}