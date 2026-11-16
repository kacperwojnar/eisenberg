namespace Eisenberg.Data;

public static class EnvFile
{
    public static void Load(string path)
    {
        if (!File.Exists(path))
            return;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var split = line.IndexOf('=');
            if (split <= 0)
                continue;
            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim();
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
