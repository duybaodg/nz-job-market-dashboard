using Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<CrawlWorker>();

var host = builder.Build();
host.Run();
