using MavenCopy.Extensions;

namespace MavenCopy.Data;

[Serializable]
public class MavenTreeRequest
{
    private static int _queueIdx;
    
    public MavenTreeRequest(Uri baseUri, string relativeUri, int priority)
    {
        QueueId = Interlocked.Increment(ref _queueIdx);
        Priority = priority;
        BaseUri = baseUri;
        RelativeUri = relativeUri;
    }
    
    public int QueueId { get; }
    
    public int Priority { get; }
    
    public int FailCount { get; set; }
    
    public Uri BaseUri { get; set; }
    
    public string RelativeUri { get; set; }
    
    public Uri ToUri()
    {
        return BaseUri.Append(RelativeUri);
    }

    public override string ToString()
    {
        return ToUri().ToString();
    }
}