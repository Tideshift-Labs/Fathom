using System.Net;

namespace ReSharperPlugin.Fathom.Handlers;

public interface IRequestHandler
{
    bool CanHandle(string path);
    void Handle(HttpListenerContext ctx);
}
