namespace PostPilot.Api;

/// <summary>
/// This class extends from APIGatewayHttpApiV2ProxyFunction which contains the method FunctionHandlerAsync which is the
/// actual Lambda function entry point. The Lambda handler field should be set to:
///
/// PostPilot.Api::PostPilot.Api.LambdaEntryPoint::FunctionHandlerAsync
/// </summary>
public class LambdaEntryPoint : Amazon.Lambda.AspNetCoreServer.APIGatewayHttpApiV2ProxyFunction
{
    /// <summary>
    /// The builder has configuration, logging and Amazon API Gateway already configured. The startup code
    /// will need to configure the services and middleware pipeline using the Init method.
    /// </summary>
    protected override void Init(IWebHostBuilder builder)
    {
        builder
            .UseStartup<Startup>();
    }

    /// <summary>
    /// Use this override to customize the services registered with the IHostBuilder.
    ///
    /// It is recommended not to call ConfigureWebHostDefaults to configure the IWebHostBuilder inside this method.
    /// Instead customize the IWebHostBuilder in the Init(IWebHostBuilder) overload.
    /// </summary>
    protected override void Init(IHostBuilder builder)
    {
    }
}
