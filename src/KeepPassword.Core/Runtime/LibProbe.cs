using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace KeepPassword.Core.Runtime;

/// <summary>
/// 发布后依赖放在 lib/，可执行文件留在外层。启动时把 lib 加入探测路径。
/// </summary>
public static class LibProbe
{
    private static string? _lib;
    private static int _attached;

    public static void Attach(params Assembly[] nativeAssemblies)
    {
        if (Interlocked.Exchange(ref _attached, 1) == 0)
        {
            _lib = Path.Combine(AppContext.BaseDirectory, "lib");
            if (!Directory.Exists(_lib))
            {
                return;
            }

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Split(Path.PathSeparator).Contains(_lib, StringComparer.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", _lib + Path.PathSeparator + path);
            }

            if (OperatingSystem.IsWindows())
            {
                TrySetDllDirectory(_lib);
            }

            AssemblyLoadContext.Default.Resolving += ResolveManaged;
        }

        if (_lib is null || !Directory.Exists(_lib))
        {
            return;
        }

        foreach (var assembly in nativeAssemblies.Append(typeof(LibProbe).Assembly).Distinct())
        {
            try
            {
                NativeLibrary.SetDllImportResolver(assembly, ResolveNative);
            }
            catch (ArgumentException)
            {
            }
        }
    }

    private static Assembly? ResolveManaged(AssemblyLoadContext context, AssemblyName name)
    {
        if (_lib is null || string.IsNullOrEmpty(name.Name))
        {
            return null;
        }

        var path = Path.Combine(_lib, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    private static IntPtr ResolveNative(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (_lib is null)
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in NativeCandidates(name))
        {
            var path = Path.Combine(_lib, candidate);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> NativeCandidates(string name)
    {
        yield return name;
        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
        {
            if (OperatingSystem.IsWindows())
            {
                yield return name + ".dll";
            }
            else if (OperatingSystem.IsMacOS())
            {
                yield return "lib" + name + ".dylib";
                yield return name + ".dylib";
            }
            else
            {
                yield return "lib" + name + ".so";
                yield return name + ".so";
            }
        }
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);

    private static void TrySetDllDirectory(string path)
    {
        try
        {
            SetDllDirectory(path);
        }
        catch (Exception)
        {
        }
    }
}
