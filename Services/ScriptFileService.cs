using System.IO;

namespace SqlAutoRollback.Services;

public sealed class ScriptFileService : IScriptFileService
{
    public string Read(string path)
    {
        return File.ReadAllText(path);
    }

    public void Save(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, contents);
    }

    public string SaveNew(string contents)
    {
        Directory.CreateDirectory(AppPaths.Scripts);
        var path = AllocateNewPath();
        File.WriteAllText(path, contents);
        return path;
    }

    private static string AllocateNewPath()
    {
        var stamp = DateTime.Now.ToString("dd.MM.yyyy HH.mm.ss");
        var path = Path.Combine(AppPaths.Scripts, stamp + ".sql");
        if (!File.Exists(path))
        {
            return path;
        }

        var index = 1;
        while (true)
        {
            var candidate = Path.Combine(AppPaths.Scripts, $"{stamp} ({index}).sql");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            index++;
        }
    }
}
