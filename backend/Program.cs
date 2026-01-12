using PostPilot.Api;

// This is the entry point for local development (dotnet run)
// For AWS Lambda deployment, LambdaEntryPoint.cs is used instead

var builder = WebApplication.CreateBuilder(args);

// Use the shared Startup class for consistency with Lambda
var startup = new Startup(builder.Configuration);
startup.ConfigureServices(builder.Services);

var app = builder.Build();

// Configure the middleware pipeline
startup.Configure(app, app.Environment);

app.Run();
