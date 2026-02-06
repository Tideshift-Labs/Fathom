using System.Net;

namespace ReSharperPlugin.CoRider.Handlers;

public interface IRequestHandler
{
    bool CanHandle(string path);
    void Handle(HttpListenerContext ctx);
}
