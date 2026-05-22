using Ryn.Core;

var app = RynApplication.CreateBuilder()
    .ConfigureOptions(opts => opts.Url = new Uri("https://example.com"))
    .Build();

await app.RunAsync();
