using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNet;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Owin;

var services = new ServiceCollection();
services.AddLogging();
services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = "AspNetOwinSample", Version = "1.0.0" })
    .WithHttpTransport()
    .WithTools([McpServerTool.Create((string text) => $"Echo: {text}", new() { Name = "echo" })]);

var provider = services.BuildServiceProvider();

Microsoft.Owin.Hosting.WebApp.Start("http://localhost:5058", app => app.UseMcp(provider));
Console.WriteLine("Listening on http://localhost:5058/");
Console.ReadLine();
