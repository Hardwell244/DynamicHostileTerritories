using System;
using System.IO;
using Rage;

namespace DynamicHostileTerritories.Core
{
    /// <summary>
    /// Central logger. Every line goes to the RAGE Plugin Hook console/log AND to a
    /// dedicated file (Plugins\LSPDFR\DynamicHostileTerritories\DynamicHostileTerritories.log)
    /// so failures are visible even if you are not watching the console. Debug lines are
    /// only written when DebugLogging is enabled in the .ini. Logging never throws.
    /// </summary>
    public static class Logger
    {
        private static readonly object Sync = new object();
        private static bool _debugEnabled;
        private static string _filePath;

        public static void Initialize(bool debugEnabled)
        {
            _debugEnabled = debugEnabled;
            _filePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                @"Plugins\LSPDFR\DynamicHostileTerritories",
                "DynamicHostileTerritories.log");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath));
                File.WriteAllText(_filePath,
                    "=== Dynamic Hostile Territories log — session started " +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===" + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Game.LogTrivial("[DHT] Could not open log file, console only: " + ex.Message);
                _filePath = null;
            }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        public static void Error(string message, Exception ex) =>
            Write("ERROR", message + " | " + ex);

        public static void Debug(string message)
        {
            if (_debugEnabled)
                Write("DEBUG", message);
        }

        private static void Write(string level, string message)
        {
            Game.LogTrivial("[DHT] " + level + ": " + message);

            if (_filePath == null)
                return;

            try
            {
                string line = DateTime.Now.ToString("HH:mm:ss") + " [" + level + "] " + message + Environment.NewLine;
                lock (Sync)
                {
                    File.AppendAllText(_filePath, line);
                }
            }
            catch
            {
                // Swallow: a logging failure must never affect gameplay.
            }
        }
    }
}