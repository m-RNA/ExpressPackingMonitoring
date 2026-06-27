#nullable disable
using System;
using System.IO;

namespace ExpressPackingMonitoring
{
    internal static class AppPaths
    {
        public static string FindFFmpeg()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string toolsPath = Path.Combine(baseDir, "tools", "ffmpeg.exe");
            if (File.Exists(toolsPath)) return toolsPath;

            string legacyPath = Path.Combine(baseDir, "ffmpeg.exe");
            if (File.Exists(legacyPath)) return legacyPath;

            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                string projectPath = Path.Combine(dir.FullName, "ffmpeg.exe");
                if (File.Exists(projectPath)) return projectPath;
            }

            return null;
        }
    }
}
