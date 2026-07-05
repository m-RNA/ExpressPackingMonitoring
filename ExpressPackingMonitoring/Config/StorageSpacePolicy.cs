using System;
using System.IO;

namespace ExpressPackingMonitoring.Config
{
    public static class StorageSpacePolicy
    {
        public const long BytesPerGiB = 1024L * 1024L * 1024L;

        public static long CalculateMinimumReserveBytes(DriveInfo drive)
        {
            bool isSystemDrive = IsSystemDrive(drive.RootDirectory.FullName);
            long minimumBytes = (isSystemDrive ? 30L : 20L) * BytesPerGiB;
            long percentBytes = (long)Math.Ceiling(drive.TotalSize * (isSystemDrive ? 0.10 : 0.05) / (double)BytesPerGiB) * BytesPerGiB;
            return Math.Max(minimumBytes, percentBytes);
        }

        public static long GetEffectiveReserveBytes(StorageLocation location, DriveInfo drive)
        {
            long minimumReserveBytes = CalculateMinimumReserveBytes(drive);
            long configuredReserveBytes = location.ReserveGB > 0
                ? (long)Math.Ceiling(location.ReserveGB) * BytesPerGiB
                : 0;
            return Math.Max(minimumReserveBytes, configuredReserveBytes);
        }

        public static double GetEffectiveReserveGB(StorageLocation location)
        {
            double minimumReserveGB = GetMinimumReserveGB(location.Path);
            return Math.Ceiling(Math.Max(minimumReserveGB, location.ReserveGB));
        }

        public static double NormalizeReserveGB(string path, double reserveGB)
        {
            double minimumReserveGB = GetMinimumReserveGB(path);
            if (double.IsNaN(reserveGB) || double.IsInfinity(reserveGB))
                return minimumReserveGB;
            return Math.Ceiling(Math.Max(minimumReserveGB, reserveGB));
        }

        public static double GetMinimumReserveGB(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return 20.0;

                string normalizedPath = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
                string? root = Path.GetPathRoot(Path.GetFullPath(normalizedPath));
                if (string.IsNullOrEmpty(root)) return 20.0;

                var drive = new DriveInfo(root);
                if (!drive.IsReady) return 20.0;
                return Math.Ceiling(CalculateMinimumReserveBytes(drive) / (double)BytesPerGiB);
            }
            catch
            {
                return 20.0;
            }
        }

        private static bool IsSystemDrive(string driveRoot)
        {
            string systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "";
            return string.Equals(
                Path.GetFullPath(driveRoot).TrimEnd(Path.DirectorySeparatorChar),
                systemRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
