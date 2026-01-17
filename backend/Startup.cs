using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Services;

namespace PostPilot.Api;

public class Startup
{
    public IConfiguration Configuration { get; }

    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        // Add controllers with JSON enum string conversion
        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        // Configure PostgreSQL with EF Core
        // In Lambda, connection string can come from environment variables or AWS Secrets Manager
        var connectionString = Configuration.GetConnectionString("DefaultConnection")
                               ?? Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");

        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException(
                "Database connection string not found. Set 'ConnectionStrings:DefaultConnection' in appsettings.json " +
                "or 'DB_CONNECTION_STRING' environment variable.");
        }

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        // Configure Meta OAuth settings (secrets from environment variables only)
        var metaSettings = new MetaOAuthSettings
        {
            AppId = Environment.GetEnvironmentVariable("META_APP_ID") ?? "",
            AppSecret = Environment.GetEnvironmentVariable("META_APP_SECRET") ?? "",
            RedirectUri = Configuration["Meta:RedirectUri"] ?? Environment.GetEnvironmentVariable("META_REDIRECT_URI") ?? "http://localhost:5173/oauth/meta/callback"
        };
        services.AddSingleton(metaSettings);

        // Register Meta OAuth service
        services.AddHttpClient<IMetaOAuthService, MetaOAuthService>();

        // Configure CORS for frontend
        services.AddCors(options =>
        {
            options.AddPolicy("AllowFrontend", policy =>
            {
                policy.SetIsOriginAllowed(origin => origin.StartsWith("http://localhost:") || origin.StartsWith("https://"))
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            });
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        // Configure the HTTP request pipeline.
        if (env.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Note: UseHttpsRedirection is typically not needed in Lambda as API Gateway handles HTTPS
        // But we'll include it for compatibility with local development
        if (!IsRunningInLambda())
        {
            app.UseHttpsRedirection();
        }

        app.UseCors("AllowFrontend");
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
    }

    private static bool IsRunningInLambda()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LAMBDA_TASK_ROOT"));
    }
}
