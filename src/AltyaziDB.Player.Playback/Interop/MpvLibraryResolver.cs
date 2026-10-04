using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AltyaziDB.Player.Playback.Interop;

internal static class MpvLibraryResolver
{
    private const string ImportName = "altyazidb-libmpv";
    private const string PackageVersion = "0.41.0";
    private static readonly string[] LibraryNames = { "libmpv-2.dll", "mpv-2.dll", "libmpv.dll" };
    private static int _registered;

    public static string NativeImportName => ImportName;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(
            typeof(MpvLibraryResolver).Assembly,
            ResolveLibrary);
    }

    public static string GetDiagnosticMessage()
    {
        var roots = GetSearchRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = roots.Where(Directory.Exists).ToArray();
        var discovered = existing
            .SelectMany(root => LibraryNames.Select(name => Path.Combine(root, name)))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return $"Uygulama klasörü: {AppContext.BaseDirectory}\n" +
               $"Aranan libmpv adları: {string.Join(", ", LibraryNames)}\n" +
               $"Var olan arama klasörleri: {(existing.Length == 0 ? "yok" : string.Join("; ", existing))}\n" +
               $"Bulunan adaylar: {(discovered.Length == 0 ? "yok" : string.Join("; ", discovered))}";
    }

    private static nint ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, ImportName, StringComparison.Ordinal))
        {
            return nint.Zero;
        }

        foreach (var candidate in GetCandidateFiles())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (NativeLibrary.TryLoad(candidate, out var candidateHandle))
            {
                return candidateHandle;
            }
        }

        foreach (var fileName in LibraryNames)
        {
            if (NativeLibrary.TryLoad(fileName, assembly, searchPath, out var fallbackHandle))
            {
                return fallbackHandle;
            }
        }

        return nint.Zero;
    }

    private static IEnumerable<string> GetCandidateFiles()
    {
        var configuredPath = Environment.GetEnvironmentVariable("ALTYAZIDB_MPV_DLL");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return configuredPath;
        }

        foreach (var root in GetSearchRoots())
        {
            foreach (var fileName in LibraryNames)
            {
                yield return Path.Combine(root, fileName);
            }
        }

        var packageRoot = GetNuGetPackageRoot();
        if (!Directory.Exists(packageRoot))
        {
            yield break;
        }

        foreach (var fileName in LibraryNames)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(packageRoot, fileName, SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return baseDirectory;
        yield return Path.Combine(baseDirectory, "runtimes", "win-x64", "native");
        yield return Path.Combine(baseDirectory, "native");
        yield return GetNuGetPackageRoot();
        yield return Path.Combine(GetNuGetPackageRoot(), "runtimes", "win-x64", "native");
    }

    private static string GetNuGetPackageRoot()
    {
        var explicitPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(explicitPackages))
        {
            return Path.Combine(explicitPackages, "endpne.libmpv.windows", PackageVersion);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".nuget", "packages", "endpne.libmpv.windows", PackageVersion);
    }
}
