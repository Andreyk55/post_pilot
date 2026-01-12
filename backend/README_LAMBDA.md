# AWS Lambda Architecture for Post Pilot API

This backend has been configured to run as a **single AWS Lambda function** that handles all API routes for the UI, while maintaining full support for **local development and testing**.

## Architecture Overview

### How It Works

1. **Single Lambda for All Routes**: One Lambda function serves all API endpoints (`/api/posts`, `/api/posts/{id}`, etc.)
2. **Internal Routing**: AWS API Gateway forwards requests to the Lambda, which uses ASP.NET Core's internal routing to direct requests to the appropriate controller
3. **Dual Entry Points**:
   - `Program.cs` - Used for local development (`dotnet run`)
   - `LambdaEntryPoint.cs` - Used when deployed to AWS Lambda
4. **Shared Configuration**: Both entry points use the same `Startup.cs` class for consistent behavior

### Project Structure

```
backend/
├── Controllers/
│   └── PostsController.cs          # All API route handlers
├── Data/
│   └── AppDbContext.cs             # EF Core database context
├── Entities/
│   └── Post.cs                     # Database entities
├── Enums/
│   ├── Platform.cs
│   └── PostStatus.cs
├── LambdaEntryPoint.cs             # AWS Lambda entry point
├── Program.cs                      # Local development entry point
├── Startup.cs                      # Shared service/middleware configuration
├── aws-lambda-tools-defaults.json  # Lambda deployment configuration
├── appsettings.json
├── appsettings.Development.json    # Local development settings
└── appsettings.Production.json     # Production/Lambda settings
```

## Local Development

### Prerequisites

- .NET 10 SDK
- PostgreSQL (localhost:5432)

### Running Locally

```bash
cd backend
dotnet run
```

The API will start on `http://localhost:5122` with Swagger UI at `http://localhost:5122/swagger`

### Configuration

Database connection string is read from `appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=postpilot;Username=postgres;Password=postgres"
  }
}
```

## AWS Lambda Deployment

### Prerequisites

1. Install AWS Lambda Tools:
   ```bash
   dotnet tool install -g Amazon.Lambda.Tools
   ```

2. Configure AWS credentials:
   ```bash
   aws configure
   ```

### Deployment Configuration

Lambda settings are defined in [aws-lambda-tools-defaults.json](aws-lambda-tools-defaults.json):

- **Handler**: `PostPilot.Api::PostPilot.Api.LambdaEntryPoint::FunctionHandlerAsync`
- **Runtime**: `dotnet10` (.NET 10)
- **Memory**: 512 MB
- **Timeout**: 30 seconds
- **Architecture**: ARM64 (cost-optimized)

### Deploy to Lambda

```bash
cd backend
dotnet lambda deploy-function PostPilotApi
```

You'll be prompted to:
1. Select AWS region
2. Choose/create IAM role (needs AWSLambdaBasicExecutionRole + RDS access)
3. Confirm deployment

### Environment Configuration

#### Database Connection

In Lambda, the connection string can be provided via:

1. **Environment Variable** (recommended):
   ```bash
   aws lambda update-function-configuration \
     --function-name PostPilotApi \
     --environment "Variables={DB_CONNECTION_STRING='Host=your-rds.region.rds.amazonaws.com;Port=5432;Database=postpilot;Username=postgres;Password=yourpassword'}"
   ```

2. **AWS Secrets Manager** (more secure):
   - Store connection string in Secrets Manager
   - Grant Lambda IAM role `secretsmanager:GetSecretValue` permission
   - Update `Startup.cs` to retrieve from Secrets Manager

Example Secrets Manager integration:

```csharp
// Add to Startup.cs ConfigureServices method
if (IsRunningInLambda())
{
    var client = new Amazon.SecretsManager.AmazonSecretsManagerClient();
    var request = new Amazon.SecretsManager.Model.GetSecretValueRequest
    {
        SecretId = "postpilot/db/connection"
    };
    var response = await client.GetSecretValueAsync(request);
    connectionString = response.SecretString;
}
```

## API Gateway Integration

### Create HTTP API (Recommended)

```bash
# Create HTTP API
aws apigatewayv2 create-api \
  --name PostPilotApi \
  --protocol-type HTTP \
  --target arn:aws:lambda:REGION:ACCOUNT_ID:function:PostPilotApi

# Get API Gateway endpoint
aws apigatewayv2 get-apis
```

The API Gateway will proxy **all requests** to the Lambda function, which handles routing internally.

### Route Configuration

No individual route configuration needed! API Gateway uses a catch-all `/{proxy+}` route:

- `GET /api/posts` → Lambda → PostsController.GetPosts()
- `POST /api/posts` → Lambda → PostsController.CreatePost()
- `PUT /api/posts/{id}` → Lambda → PostsController.UpdatePost()
- `DELETE /api/posts/{id}` → Lambda → PostsController.DeletePost()

## Key Files Explained

### [LambdaEntryPoint.cs](LambdaEntryPoint.cs)

The AWS Lambda entry point. Extends `APIGatewayHttpApiV2ProxyFunction` which:
- Receives API Gateway HTTP API events
- Converts them to ASP.NET Core HttpContext
- Invokes the ASP.NET Core pipeline
- Returns API Gateway-compatible responses

### [Startup.cs](Startup.cs)

Shared configuration used by both local and Lambda execution:
- Service registration (controllers, database, CORS)
- Middleware pipeline (routing, CORS, endpoints)
- Environment detection (Lambda vs local)

Key features:
- Reads connection string from `appsettings.json` or `DB_CONNECTION_STRING` env var
- Disables HTTPS redirection in Lambda (API Gateway handles it)
- Configures CORS for frontend access

### [Program.cs](Program.cs)

Simplified local development entry point that delegates to `Startup.cs` for consistency.

## Database Setup

### Local (Development)

1. Start PostgreSQL:
   ```bash
   docker run -d --name postgres -p 5432:5432 -e POSTGRES_PASSWORD=postgres postgres:17
   ```

2. Run migrations:
   ```bash
   cd backend
   dotnet ef database update
   ```

### AWS (Production)

1. **Create RDS PostgreSQL instance**:
   - Engine: PostgreSQL 17
   - Instance class: db.t4g.micro (or larger)
   - VPC: Same as Lambda or configure VPC peering

2. **Configure Lambda VPC** (if RDS is in VPC):
   - Add Lambda to same VPC as RDS
   - Attach security group allowing Lambda → RDS (port 5432)

3. **Run migrations**:
   Option A: From local machine (if RDS is public):
   ```bash
   export ConnectionStrings__DefaultConnection="Host=..."
   dotnet ef database update
   ```

   Option B: Create a separate "migrator" Lambda that runs migrations

## Testing the Lambda Locally

Use AWS Lambda Test Tool:

```bash
# Install test tool
dotnet tool install -g Amazon.Lambda.TestTool-10.0

# Run from backend directory
dotnet lambda-test-tool-10.0
```

This launches a local server that simulates API Gateway + Lambda.

## Monitoring and Logs

### CloudWatch Logs

Lambda automatically logs to CloudWatch:

```bash
# View recent logs
aws logs tail /aws/lambda/PostPilotApi --follow
```

### X-Ray Tracing

Enable in [aws-lambda-tools-defaults.json](aws-lambda-tools-defaults.json):

```json
{
  "tracing-mode": "Active"
}
```

## Performance Considerations

### Cold Starts

- **Current**: ~2-3 seconds for .NET Lambda cold start
- **Mitigation**:
  - Use ARM64 architecture (faster, cheaper)
  - Consider Provisioned Concurrency for critical APIs
  - Keep Lambda warm with EventBridge scheduled pings

### Database Connections

- Lambda creates new AppDbContext per request
- EF Core connection pooling helps reuse connections
- Consider **RDS Proxy** to prevent connection exhaustion

### Memory/Timeout

- Default: 512 MB, 30s timeout (sufficient for simple CRUD)
- Increase if handling large payloads or complex queries

## Security Best Practices

1. **IAM Role**: Grant minimum permissions:
   - `AWSLambdaVPCAccessExecutionRole` (if in VPC)
   - `secretsmanager:GetSecretValue` (if using Secrets Manager)
   - RDS security group access

2. **Secrets**: NEVER hardcode in `appsettings.json` or environment variables. Use Secrets Manager.

3. **API Gateway**: Enable throttling, API keys, or Cognito authorizer for production.

4. **CORS**: Update `Startup.cs` to whitelist only your frontend domain in production.

## Cost Optimization

- **Architecture**: ARM64 is 20% cheaper than x86_64
- **Memory**: 512 MB is sufficient for this API (~$0.0000083/second)
- **Free Tier**: 1M requests/month free, 400,000 GB-seconds compute
- **Estimated cost**: ~$1-5/month for moderate traffic (<100k requests)

## Troubleshooting

### "Connection string not found" error

Check that `DB_CONNECTION_STRING` environment variable is set in Lambda configuration.

### Lambda timeout

If requests take >30s, increase timeout in [aws-lambda-tools-defaults.json](aws-lambda-tools-defaults.json) (max 15 minutes).

### VPC Lambda cannot access internet

Lambda in VPC needs NAT Gateway for outbound internet access (e.g., external API calls).

### Migrations fail

Run migrations from a location with network access to RDS (local with public RDS, or from EC2/bastion in same VPC).

## Next Steps

1. **Add Authentication**: Integrate JWT or AWS Cognito
2. **API Gateway Authorizer**: Protect endpoints
3. **Custom Domain**: Use Route 53 + CloudFront
4. **CI/CD**: Automate deployment with GitHub Actions or AWS CodePipeline
5. **Monitoring**: Set up CloudWatch dashboards and alarms

## Resources

- [AWS Lambda .NET Documentation](https://docs.aws.amazon.com/lambda/latest/dg/lambda-csharp.html)
- [Amazon.Lambda.AspNetCoreServer](https://github.com/aws/aws-lambda-dotnet/tree/master/Libraries/src/Amazon.Lambda.AspNetCoreServer)
- [AWS Lambda Tools for .NET CLI](https://github.com/aws/aws-extensions-for-dotnet-cli)
