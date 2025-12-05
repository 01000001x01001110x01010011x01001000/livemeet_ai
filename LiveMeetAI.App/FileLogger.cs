using System;
using System.IO;

namespace LiveMeetAI.App
{
    static class FileLogger
    {
        private static readonly object _lock = new object();
        private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        private static readonly string LogFile = Path.Combine(LogDir, "livemeetai.log");

        static FileLogger()
        {
            try
            {
                if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
            }
            catch { }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);
        public static void Debug(string message) => Write("DEBUG", message);

        private static void Write(string level, string message)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                lock (_lock)
                {
                    File.AppendAllText(LogFile, line + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
