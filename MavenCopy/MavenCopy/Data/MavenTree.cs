using MavenCopy.Extensions;

namespace MavenCopy.Data;

[Serializable]
public class MavenTree
{
    public MavenTree(Uri baseUri, string relativeUri)
    {
        BaseUri = baseUri;
        RelativeUri = relativeUri;
    }
    
    public DateTime? CacheExpireDate { get; set; }
    
    public Uri BaseUri { get; set; }
    
    public string RelativeUri { get; set; }
    
    public string[] Items { get; set; } = Array.Empty<string>();

    public Uri ToUri()
    {
        return BaseUri.Append(RelativeUri);
    }
}