using MavenCopy.Extensions;

namespace MavenCopy.Data;

[Serializable]
public class MavenTreeRequest
{
    public MavenTreeRequest(Uri baseUri, string relativeUri)
    {
        BaseUri = baseUri;
        RelativeUri = relativeUri;
    }
    
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