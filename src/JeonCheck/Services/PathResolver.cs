using System.IO;

namespace JeonCheck.Services;

public static class PathResolver
{
    private static string? _rootDirectory;

    public static string RootDirectory
    {
        get
        {
            if (_rootDirectory != null)
                return _rootDirectory;

            string baseDir = AppContext.BaseDirectory;
            DirectoryInfo? dir = new DirectoryInfo(baseDir);

            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "data")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "tools")))
                {
                    _rootDirectory = dir.FullName;
                    return _rootDirectory;
                }
                dir = dir.Parent;
            }

            _rootDirectory = Directory.GetCurrentDirectory();
            return _rootDirectory;
        }
    }

    public static string CppcheckExePath => Path.Combine(RootDirectory, "tools", "cppcheck", "cppcheck.exe");
    public static string ClangTidyExePath => Path.Combine(RootDirectory, "tools", "clang-tidy", "clang-tidy.exe");
    public static string ClangTidyIncludeDirectory => Path.Combine(RootDirectory, "tools", "clang-tidy", "include");
    public static string DataDirectory => Path.Combine(RootDirectory, "data");
    public static string TargetsDirectory => Path.Combine(DataDirectory, "targets");
    public static string TargetsJsonPath => Path.Combine(TargetsDirectory, "targets.json");
    public static string RulesKoJsonPath => Path.Combine(DataDirectory, "rules_ko.json");
    public static string SamplesDirectory => Path.Combine(RootDirectory, "samples");

    public static string GetPlatformXmlPath(string xmlFileName)
    {
        return Path.Combine(TargetsDirectory, xmlFileName);
    }
}
