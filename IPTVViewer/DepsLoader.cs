using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace IPTVViewer
{
    /// <summary>
    /// Единый exe: управляемые сборки (LibVLCSharp и зависимости) встроены
    /// в ресурсы и загружаются из памяти, когда рядом с exe их нет.
    /// Байтовая загрузка сохраняет версию сборки — проверка соответствия
    /// LibVLC / LibVLCSharp проходит корректно.
    /// </summary>
    public static class DepsLoader
    {
        private static readonly object Sync = new object();

        private static readonly Dictionary<string, Assembly> _byFullName =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Регистрация обработчика загрузки. Вызывается первым в Main.</summary>
        public static void Register()
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                lock (Sync)
                {
                    Assembly cached;
                    if (_byFullName.TryGetValue(args.Name, out cached))
                        return cached;

                    var simpleName = new AssemblyName(args.Name).Name;
                    var bytes = ReadResource(simpleName);
                    if (bytes == null)
                        return null;

                    var assembly = Assembly.Load(bytes);
                    _byFullName[args.Name] = assembly;
                    return assembly;
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] ReadResource(string simpleName)
        {
            Dictionary<string, byte[]> map;
            lock (Sync)
            {
                map = _mapNoLock;
                if (map == null)
                {
                    map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                    var assembly = typeof(DepsLoader).Assembly;
                    var names = new[]
                    {
                        "LibVLCSharp",
                        "LibVLCSharp.WinForms",
                        "System.Buffers",
                        "System.Memory",
                        "System.Numerics.Vectors",
                        "System.Runtime.CompilerServices.Unsafe"
                    };

                    foreach (var name in names)
                    {
                        var resourceName = "IPTVViewer.deps." + name + ".dll";
                        using (var stream = assembly.GetManifestResourceStream(resourceName))
                        {
                            if (stream == null || stream.Length == 0)
                                continue;

                            using (var memory = new MemoryStream())
                            {
                                stream.CopyTo(memory, 1 << 20);
                                map[name] = memory.ToArray();
                            }
                        }
                    }

                    _mapNoLock = map;
                }
            }

            byte[] bytes;
            return map.TryGetValue(simpleName, out bytes) ? bytes : null;
        }

        private static Dictionary<string, byte[]> _mapNoLock;
    }
}
