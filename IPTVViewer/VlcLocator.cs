using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace IPTVViewer
{
    /// <summary>
    /// Движок VLC для единого exe: если рядом с программой нет папки
    /// libvlc\<x64|x86>, архив распаковывается из ресурсов в LocalAppData.
    /// Повторные запуски берут уже распакованные файлы.
    /// </summary>
    public static class VlcLocator
    {
        private static readonly object Sync = new object();

        public static string Ensure(string applicationDataDir)
        {
            var arch = Environment.Is64BitProcess ? "x64" : "x86";

            // 1) Портативный вариант: папка libvlc рядом с exe.
            var portable = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "libvlc", arch);
            if (IsComplete(portable))
                return portable;

            // 2) Распаковка из ресурсов (единый exe).
            if (string.IsNullOrWhiteSpace(applicationDataDir))
                throw new InvalidOperationException("Не задан каталог данных приложения");

            lock (Sync)
            {
                var target = Path.Combine(applicationDataDir, "vlc", arch);
                if (IsComplete(target))
                    return target;

                CleanupTempDirs(applicationDataDir);
                Extract(arch, target);
                return target;
            }
        }

        private static bool IsComplete(string dir)
        {
            return !string.IsNullOrWhiteSpace(dir) &&
                   File.Exists(Path.Combine(dir, "libvlc.dll")) &&
                   File.Exists(Path.Combine(dir, "libvlccore.dll"));
        }

        private static void Extract(string arch, string target)
        {
            var resourceName = "IPTVViewer.vlc-" + arch + ".zip";
            var assembly = Assembly.GetExecutingAssembly();
            using (var zipStream = assembly.GetManifestResourceStream(resourceName))
            {
                if (zipStream == null || zipStream.Length == 0)
                    throw new InvalidOperationException(
                        "В exe не встроен движок VLC (" + resourceName + ").");

                var tempDir = target + ".tmp-" +
                    Guid.NewGuid().ToString("N").Substring(0, 8);
                Directory.CreateDirectory(tempDir);

                try
                {
                    using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            var destination = Path.Combine(tempDir, entry.FullName);

                            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                                entry.FullName.EndsWith("\\", StringComparison.Ordinal) ||
                                string.IsNullOrEmpty(entry.Name))
                            {
                                Directory.CreateDirectory(destination);
                                continue;
                            }

                            var entryDir = Path.GetDirectoryName(destination);
                            if (!string.IsNullOrEmpty(entryDir))
                                Directory.CreateDirectory(entryDir);

                            using (var entryStream = entry.Open())
                            using (var file = File.Create(destination))
                                entryStream.CopyTo(file, 1 << 20);
                        }
                    }

                    if (Directory.Exists(target))
                        Directory.Delete(target, true);
                    Directory.Move(tempDir, target);
                }
                catch
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                    throw;
                }
            }
        }

        private static void CleanupTempDirs(string applicationDataDir)
        {
            var parent = Path.Combine(applicationDataDir, "vlc");
            if (!Directory.Exists(parent)) return;
            foreach (var dir in Directory.GetDirectories(parent, "*.tmp-*"))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
