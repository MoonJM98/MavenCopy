namespace MavenCopy.Data;

[Serializable]
public class Config
{
    public string Url { get; set; } = "";
    public string BaseFolder { get; set; } = "." + Path.DirectorySeparatorChar + "library";
    public string CacheFolder { get; set; } = "."+ Path.DirectorySeparatorChar + "cache";
    public string LogFolder { get; set; } = "."+ Path.DirectorySeparatorChar + "log";
    public int RetryCount { get; set; } = 10;
    public int ParallelCount { get; set; } = 10;
    public long CacheExpireDate { get; set; } = 30;
}