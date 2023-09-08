using System.Text.Json;
using System.Text.Json.Serialization;
using HtmlAgilityPack;
using MavenCopy.Data;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MavenCopy;

public class MavenCopier
{
    private const string MavenTreeCacheFileName = "maven_tree_info.json";
    private readonly Config _config;
    private readonly Logger _logger;
    private readonly Task?[] _taskList;
    private readonly string _failFilename = $"{DateTime.Now:yyyyMMddHHmmssfff}_failure.log";

    private int _queueIdx = 0;
    private readonly object _failLock = new();
    private readonly object _queueLock = new();
    private readonly HttpClient _httpClient = new();
    private readonly HashSet<string> _failSets = new();
    private readonly Stack<(MavenTreeRequest Request, int QueueId, int FailCount)> _queue = new();
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public MavenCopier(Config config)
    {
        _config = config;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.0.0 Safari/537.36");
        
        var loggerConfiguration = new LoggerConfiguration();

        if (config.ShowConsole)
        {
            loggerConfiguration = loggerConfiguration.WriteTo.Console();
        }

        if (config.WriteFileLog)
        {
            loggerConfiguration = loggerConfiguration.WriteTo.File($"{_config.LogFolder}{Path.DirectorySeparatorChar}{DateTime.Now:yyyyMMdd}.log");
        }

        loggerConfiguration = config.LogLevel switch
        {
            LogEventLevel.Debug => loggerConfiguration.MinimumLevel.Debug(),
            LogEventLevel.Verbose => loggerConfiguration.MinimumLevel.Verbose(),
            LogEventLevel.Information => loggerConfiguration.MinimumLevel.Information(),
            LogEventLevel.Warning => loggerConfiguration.MinimumLevel.Warning(),
            LogEventLevel.Error => loggerConfiguration.MinimumLevel.Error(),
            LogEventLevel.Fatal => loggerConfiguration.MinimumLevel.Fatal(),
            _ => throw new ArgumentOutOfRangeException()
        };
        
        _logger = loggerConfiguration.CreateLogger();

        _taskList = new Task[config.ParallelCount];
    }

    private async Task DownloadTree(int queueId, int threadId, MavenTreeRequest treeRequest, int failCount = 0)
    {
        Stream? response = null;
        var url = treeRequest.ToUri().ToString();
        
        try
        {
            var fileName = treeRequest.RelativeUri.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var filePath = Path.Combine(_config.BaseFolder, fileName);
            var fileInfo = new FileInfo(filePath);
            
            if (File.Exists(filePath) && fileInfo.Length > 0)
            {
                return;
            }

            var cachePathDir = Path.Combine(_config.CacheFolder, fileName.TrimStart(Path.DirectorySeparatorChar));
            var cachePath = Path.Combine(cachePathDir, MavenTreeCacheFileName);
            var cacheFile = new FileInfo(cachePath);
            
            if (treeRequest.RelativeUri.EndsWith("/"))
            {
                Directory.CreateDirectory(cachePathDir);
                if (File.Exists(cachePath) && cacheFile.Length > 0)
                {
                    try
                    {
                        var text = await File.ReadAllTextAsync(cachePath);
                        var mavenTree = JsonSerializer.Deserialize<MavenTree>(text);

                        if (mavenTree != null && mavenTree.CacheExpireDate != null &&
                            mavenTree.CacheExpireDate > DateTime.Now)
                        {
                            _logger.Information("Found Cache (Type: Directory, Url: {Url})", treeRequest.ToUri());
                            DownloadSubTree(mavenTree.Items.Select(link => new MavenTreeRequest(treeRequest.BaseUri, string.Concat(treeRequest.RelativeUri.TrimEnd('/'), '/', link))));

                            return;
                        }
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }

            if (!treeRequest.RelativeUri.EndsWith("/") && File.Exists(filePath))
            {
                _logger.Information("Found Cache (Type: File, Url: {Url})", treeRequest.ToUri());
                
                return;
            }

            response = await _httpClient.GetStreamAsync(treeRequest.ToUri());
            
            if (treeRequest.RelativeUri.EndsWith('/'))
            {
                var htmlDocument = new HtmlDocument();
                htmlDocument.Load(response);

                var htmlNode = htmlDocument.DocumentNode;
                var linkNodes = htmlNode.SelectNodes("//a").Where(linkNode => linkNode.InnerText != "../");
                
                var links = linkNodes.Select(linkNode => linkNode.Attributes["href"].Value).ToArray();
                
                // memory free after read
                response.Close();
                
                _logger.Information("Download Index Cache (QueueId: {QueueId}, ThreadId: {TaskId}, Url: {Url}, Path: {Path})", queueId, threadId, treeRequest.ToUri(), filePath);

                var tree = new MavenTree(treeRequest.BaseUri, treeRequest.RelativeUri)
                {
                    Items = links,
                    CacheExpireDate = DateTime.Now.Add(TimeSpan.FromDays(_config.CacheExpireDate))
                };
                
                await File.WriteAllTextAsync(cacheFile.FullName, JsonSerializer.Serialize(tree, _options));
                
                DownloadSubTree(links.Select(link => new MavenTreeRequest(treeRequest.BaseUri, string.Concat(treeRequest.RelativeUri.TrimEnd('/'), '/', link))));
            }
            else
            {
                _logger.Information("Download File (QueueId: {QueueId}, ThreadId: {TaskId}, Url: {Url}, Path: {Path})", queueId, threadId, treeRequest.ToUri(), filePath);

                if (fileInfo.DirectoryName != null)
                {
                    Directory.CreateDirectory(fileInfo.DirectoryName);
                }
                
                await using var fileStream = new FileStream(filePath, FileMode.OpenOrCreate);
                await response.CopyToAsync(fileStream);
                response.Close();
            }
            
            lock (_failLock)
            {
                if (_failSets.Remove(url))
                {
                    File.WriteAllLines(Path.Combine(_config.LogFolder, _failFilename), _failSets);
                }
            }

        }
        catch (Exception e)
        {
            if (response != null)
            {
                try
                {
                    response.Close();
                }
                catch
                {
                    // Ignored
                }
            }

            lock (_failLock)
            {
                if (_failSets.Add(url))
                {
                    File.WriteAllLines(Path.Combine(_config.LogFolder, _failFilename), _failSets);
                }
            }

            
            if (failCount < _config.RetryCount)
            {
                _logger.Warning(e, "DOWNLOAD FAILED :: DownloadUrl Retry (Url: {Url}, Try: {FailCount})", treeRequest.ToUri(), failCount + 1);
                QueueDownload(treeRequest, ++failCount);
            }
            else
            {
                _logger.Error(e, "DOWNLOAD FAILED :: DownloadUrl (Url: {Url})", treeRequest.ToUri());
            }
        }
    }

    private void DownloadSubTree(IEnumerable<MavenTreeRequest> requests)
    {
        foreach (var request in requests)
        {
            var subTree = new MavenTreeRequest(request.BaseUri, string.Concat(request.RelativeUri));

            QueueDownload(subTree, 0);
        }
    }

    private void QueueDownload(MavenTreeRequest treeRequest, int failCount)
    {
        Monitor.Enter(_queueLock);
        var queueId = _queueIdx;
        _queue.Push((treeRequest, queueId, failCount));
        _queueIdx++;
        Monitor.Exit(_queueLock);
        
        _logger.Debug("Enqueue (QueueId: {QueueId}, Url: {Url})", queueId, treeRequest.ToUri());
    }

    public async Task Start()
    {
        try
        {
            var checkText = await _httpClient.GetStringAsync(_config.Url);

            var document = new HtmlDocument();
            document.LoadHtml(checkText);

            var htmlNode = document.DocumentNode;
            var linkNodes = htmlNode.SelectNodes("//a").Where(linkNode => linkNode.InnerText != "../");

            if (!linkNodes.Any())
            {
                throw new ArgumentException("Invalid Url");
            }
        }
        catch
        {
            throw new ApplicationException("Url validation failed!");
        }

        var treeRequest = new MavenTreeRequest(new Uri(_config.Url), "/");
        QueueDownload(treeRequest, 0);

        while (_queue.Count > 0 || _taskList.Any(task => task != null))
        {
            var endIdx = Array.FindIndex(_taskList, task => task?.IsCompleted ?? true);
            
            if (endIdx >= 0)
            {
                if (_queue.TryPop(out var tuple))
                {
                    _logger.Debug("Dequeue (QueueId: {QueueId}, Try: {Try}, Url: {Url})", tuple.QueueId, tuple.FailCount + 1, tuple.Request.ToUri());
                    _taskList[endIdx] = DownloadTree(tuple.QueueId, endIdx, tuple.Request, tuple.FailCount);
                }
                else
                {
                    await Task.Delay(100);
                }
                continue;
            }

            var tasks = _taskList.Where(task => task != null).Select(task => task!).ToList();
            var endTask = await Task.WhenAny(tasks);
            endIdx = Array.FindIndex(_taskList, task => endTask == task);
            var item = _queue.Pop();
            _logger.Debug("Dequeue (QueueId: {QueueId}, Try: {Try}, Url: {Url})", item.QueueId, item.FailCount + 1, item.Request.ToUri());
            _taskList[endIdx] = DownloadTree(item.QueueId, endIdx, item.Request, item.FailCount);
        }
    }
}