using System.Net;

namespace ReSharperPlugin.RiderActionExplorer.Handlers;

public interface IRequestHandler
{
    bool CanHandle(string path);
    void Handle(HttpListenerContext ctx);
}
