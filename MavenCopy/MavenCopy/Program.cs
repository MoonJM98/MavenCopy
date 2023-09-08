// See https://aka.ms/new-console-template for more information


using System.Text.Json;
using MavenCopy;
using MavenCopy.Data;

Config config;
if (File.Exists("config.json"))
{
    config = JsonSerializer.Deserialize<Config>(File.ReadAllText("config.json")) ?? new Config();
}
else
{
    config = new Config
    {
        BaseFolder = Path.Combine(Directory.GetCurrentDirectory(), "library"),
        CacheFolder = Path.Combine(Directory.GetCurrentDirectory(), "cache"),
        LogFolder = Path.Combine(Directory.GetCurrentDirectory(), "log"),
        Url = "https://repo.maven.apache.org/maven2/",
    };
    File.WriteAllText("config.json", JsonSerializer.Serialize(config, new JsonSerializerOptions()
    {
        WriteIndented = true
    }));
}

var mavenCopier = new MavenCopier(config);
Task.Run(mavenCopier.Start).Wait();