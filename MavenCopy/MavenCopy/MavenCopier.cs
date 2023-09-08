using System.Text.Json;
using HtmlAgilityPack;
using MavenCopy.Data;
using Serilog;
using Serilog.Core;

namespace MavenCopy;

public class MavenCopier
{
    private const string MavenTreeCacheFileName = "maven_tree_info.json";
    private readonly Config _config;
    private readonly Logger _logger;
    private readonly Task?[] _taskList;
    private readonly string _failFilename = $"{DateTime.Now:yyyyMMddHHmmssfff}_failure.log";
    
    private readonly object _failLock = new();
    private readonly HttpClient _httpClient = new();
    private readonly HashSet<string> _failSets = new();
    private readonly Stack<(MavenTree, int)> _queue = new();
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true
    };

    public MavenCopier(Config config)
    {
        _config = config;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.0.0 Safari/537.36");
        
        var loggerConfiguration = new LoggerConfiguration();
        _logger = loggerConfiguration
            .WriteTo.Console()
            .WriteTo.File($"{_config.LogFolder}{Path.DirectorySeparatorChar}{DateTime.Now:yyyyMMdd}.log").CreateLogger();

        _taskList = new Task[config.ParallelCount];
    }

    private async Task DownloadTree(int id, MavenTree node, int failCount = 0)
    {
        Stream? response = null;
        var url = node.ToUri().ToString();
        
        try
        {
            var fileName = node.RelativeUri.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var filePath = Path.Combine(_config.BaseFolder, fileName);
            var fileInfo = new FileInfo(filePath);
            
            if (File.Exists(filePath) && fileInfo.Length > 0)
            {
                return;
            }

            var cachePathDir = Path.Combine(_config.CacheFolder, fileName.TrimStart(Path.DirectorySeparatorChar));
            var cachePath = Path.Combine(cachePathDir, MavenTreeCacheFileName);
            var cacheFile = new FileInfo(cachePath);
            
            if (node.RelativeUri.EndsWith("/"))
            {
                Directory.CreateDirectory(cachePathDir);
                if (File.Exists(cachePath) && cacheFile.Length > 0)
                {
                    var text = await File.ReadAllTextAsync(cachePath);
                    var mavenTree = JsonSerializer.Deserialize<MavenTree>(text);

                    if (mavenTree != null && mavenTree.CacheExpireDate != null && mavenTree.CacheExpireDate > DateTime.Now)
                    {
                        _logger.Information("Found cache: {URL}", node.ToUri());
                        DownloadSubTree(mavenTree);

                        return;
                    }
                }
            }

            if (!node.RelativeUri.EndsWith("/") && File.Exists(filePath))
            {
                _logger.Information("Found cache: {URL}", node.ToUri());
                
                return;
            }

            response = await _httpClient.GetStreamAsync(node.ToUri());
            
            if (node.RelativeUri.EndsWith('/'))
            {
                var htmlDocument = new HtmlDocument();
                htmlDocument.Load(response);

                var htmlNode = htmlDocument.DocumentNode;
                var linkNodes = htmlNode.SelectNodes("//a").Where(linkNode => linkNode.InnerText != "../");

                node.Items = linkNodes.Select(linkNode => linkNode.Attributes["href"].Value).ToArray();
                
                // memory free after read
                response.Close();
                
                _logger.Information("Download Index Cache (ThreadId: {TaskId}) {URL} to {Path}", id, node.ToUri(), filePath);
                
                node.CacheExpireDate = DateTime.Now.Add(TimeSpan.FromDays(_config.CacheExpireDate));
                await File.WriteAllTextAsync(cacheFile.FullName, JsonSerializer.Serialize(node, _options));
                QueueDownload(node, 0);
            }
            else
            {
                _logger.Information("Download File (ThreadId: {TaskId}) {URL} to {Path}", id, node.ToUri(), filePath);

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
                _logger.Warning(e, "DOWNLOAD FAILED :: DownloadUrl (URL: {URL}) {FailCount}", node.ToUri(), failCount);
                QueueDownload(node, ++failCount);
            }
            else
            {
                _logger.Error(e, "DOWNLOAD FAILED :: DownloadUrl (URL: {URL})", node.ToUri());
            }
        }
    }

    private void DownloadSubTree(MavenTree tree)
    {
        foreach (var treeItem in tree.Items)
        {
            var subTree = new MavenTree(tree.BaseUri, string.Concat(tree.RelativeUri.TrimEnd('/'), "/", treeItem));

            QueueDownload(subTree, 0);
        }
    }

    private void QueueDownload(MavenTree tree, int failCount)
    {
        _queue.Push((tree, failCount));
        _logger.Information("Queue ({QueueCount}) {URL}", _queue.Count, tree.ToUri());
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
                throw new ArgumentException("Invalid URL");
            }
        }
        catch
        {
            throw new ApplicationException("URL validation failed!");
        }

        var tree = new MavenTree(new Uri(_config.Url), "/");
        var downloadTask = DownloadTree(0, tree);
        _taskList[0] = downloadTask;

        while (!downloadTask.IsCompleted || _queue.Count > 0 || _taskList.Any(task => task != null))
        {
            var endIdx = Array.FindIndex(_taskList, task => task?.IsCompleted ?? true);
            
            if (endIdx >= 0)
            {
                if (_queue.TryPop(out var tuple))
                {
                    _taskList[endIdx] = DownloadTree(endIdx, tuple.Item1, tuple.Item2);
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
            _taskList[endIdx] = DownloadTree(endIdx, item.Item1, item.Item2);
        }
    }
}