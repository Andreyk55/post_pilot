# AWS Lambda Architecture for Post Pilot

This backend uses a **multi-Lambda architecture** for post scheduling and publishing:

1. **API Lambda** - Handles all web API routes
2. **Dispatcher Lambda** - Polls for due posts, sends to SQS (triggered by EventBridge every minute)
3. **Publisher Lambda** - Publishes posts to social media (triggered by SQS)

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         POST SCHEDULING PIPELINE                         │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│   EventBridge Rule (every 1 min)                                        │
│            │                                                             │
│            ▼                                                             │
│   ┌─────────────────────┐                                               │
│   │  Dispatcher Lambda  │  Queries DB for due posts                     │
│   │                     │  Atomically claims them (Pending → Publishing)│
│   └─────────┬───────────┘  Sends to SQS                                 │
│             │                                                            │
│             ▼                                                            │
│   ┌─────────────────────┐                                               │
│   │  SQS Queue (FIFO)   │  Decouples dispatcher from publisher          │
│   └─────────┬───────────┘  Handles retries automatically                │
│             │                                                            │
│             ▼                                                            │
│   ┌─────────────────────┐                                               │
│   │  Publisher Lambda   │  Publishes to Meta/Facebook                   │
│   │                     │  Updates DB (Published/Failed/RetryPending)   │
│   └─────────┬───────────┘                                               │
│             │                                                            │
│             ▼ (after 3 failures)                                        │
│   ┌─────────────────────┐                                               │
│   │  Dead Letter Queue  │  Failed messages for manual inspection        │
│   └─────────────────────┘                                               │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│                              API LAYER                                   │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│   API Gateway (HTTP API)                                                │
│            │                                                             │
│            ▼                                                             │
│   ┌─────────────────────┐                                               │
│   │    API Lambda       │  Handles all /api/* routes                    │
│   │  (ASP.NET Core)     │  Creates posts, manages schedules             │
│   └─────────────────────┘                                               │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### How It Works

1. **API Lambda**: Serves all API endpoints (`/api/posts`, `/api/pages`, etc.) via ASP.NET Core
2. **Dispatcher Lambda**: Runs every minute, finds posts due for publishing, sends them to SQS
3. **Publisher Lambda**: Receives messages from SQS, publishes to Facebook via Meta Graph API
4. **Dual Entry Points**:
   - `Program.cs` - Used for local development (`dotnet run`)
   - `LambdaEntryPoint.cs` - Used for API Lambda in AWS
   - `Lambdas/DispatcherFunction.cs` - Dispatcher Lambda handler
   - `Lambdas/PublisherFunction.cs` - Publisher Lambda handler
5. **Shared Configuration**: `Startup.cs` for API, `Lambdas/LambdaStartup.cs` for Dispatcher/Publisher

### Project Structure

```
backend/
├── Controllers/
│   └── PostsController.cs              # API route handlers
├── Data/
│   └── AppDbContext.cs                 # EF Core database context
├── Entities/
│   └── Post.cs                         # Database entities
├── Enums/
│   ├── Platform.cs
│   └── PostStatus.cs
├── Lambdas/                            # Lambda function handlers
│   ├── Models/
│   │   └── PublishPostMessage.cs       # SQS message DTO
│   ├── DispatcherFunction.cs           # Dispatcher Lambda (EventBridge → SQS)
│   ├── PublisherFunction.cs            # Publisher Lambda (SQS → Meta API)
│   └── LambdaStartup.cs                # Shared DI for Lambdas
├── Services/
│   ├── Publishing/
│   │   ├── FacebookPagePublisher.cs    # Meta Graph API publisher
│   │   └── IPostPublisher.cs           # Publisher interface
│   └── Scheduling/
│       ├── EventBridgePostScheduler.cs # Production scheduler
│       └── LocalSchedulerBackgroundService.cs  # Local dev polling
├── LambdaEntryPoint.cs                 # API Lambda entry point
├── Program.cs                          # Local development entry point
├── Startup.cs                          # API service configuration
├── template.yaml                       # AWS SAM deployment template
├── samconfig.toml                      # SAM deployment config
└── appsettings.*.json                  # Environment configs
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

## AWS Lambda Deployment (SAM)

The entire infrastructure is defined in [template.yaml](template.yaml) and deployed using AWS SAM.

### Prerequisites

1. Install AWS SAM CLI:
   ```bash
   # macOS
   brew install aws-sam-cli

   # Windows
   choco install aws-sam-cli

   # pip
   pip install aws-sam-cli
   ```

2. Configure AWS credentials:
   ```bash
   aws configure
   ```

### What Gets Deployed

| Resource | Type | Purpose |
|----------|------|---------|
| `postpilot-api-{env}` | Lambda | API endpoints |
| `postpilot-dispatcher-{env}` | Lambda | Polls DB for due posts |
| `postpilot-publisher-{env}` | Lambda | Publishes to Meta |
| `postpilot-publish-queue-{env}.fifo` | SQS | Post publish queue |
| `postpilot-publish-dlq-{env}` | SQS | Dead letter queue |
| `postpilot-dispatcher-schedule-{env}` | EventBridge Rule | Triggers dispatcher every 1 min |
| `postpilot-schedules-{env}` | EventBridge Schedule Group | For per-post schedules |
| `postpilot-scheduler-role-{env}` | IAM Role | EventBridge → Lambda invocation |

### Build

```bash
cd backend
sam build
```

### Deploy (First Time)

```bash
sam deploy --guided
```

You'll be prompted for:
- **Stack name**: `postpilot-dev` (or staging/prod)
- **DbConnectionString**: PostgreSQL connection string for RDS
- **MetaAppId**: Facebook App ID
- **MetaAppSecret**: Facebook App Secret
- **VpcSubnetIds**: Comma-separated subnet IDs (if RDS is in VPC)
- **VpcSecurityGroupIds**: Comma-separated security group IDs

### Deploy (Subsequent)

```bash
# Deploy to dev
sam deploy --config-env dev

# Deploy to staging
sam deploy --config-env staging

# Deploy to prod
sam deploy --config-env prod
```

### Lambda Handlers

| Lambda | Handler |
|--------|---------|
| API | `PostPilot.Api::PostPilot.Api.LambdaEntryPoint::FunctionHandlerAsync` |
| Dispatcher | `PostPilot.Api::PostPilot.Api.Lambdas.DispatcherFunction::FunctionHandler` |
| Publisher | `PostPilot.Api::PostPilot.Api.Lambdas.PublisherFunction::FunctionHandler` |

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

## Testing Locally

### API (Local Development Server)

```bash
cd backend
dotnet run
```

The API runs on `http://localhost:5122` with:
- Swagger UI at `/swagger`
- Local background scheduler polling every 30 seconds

### SAM Local Testing

```bash
# Test Dispatcher Lambda
sam local invoke DispatcherFunction --env-vars env.json

# Test Publisher Lambda with sample SQS event
sam local invoke PublisherFunction --event events/sqs-event.json --env-vars env.json

# Start local API Gateway
sam local start-api --env-vars env.json
```

Sample `env.json`:
```json
{
  "DispatcherFunction": {
    "DB_CONNECTION_STRING": "Host=localhost;Port=5432;Database=postpilot;Username=postgres;Password=postgres",
    "SQS_QUEUE_URL": "http://localhost:4566/000000000000/postpilot-publish-queue-dev.fifo",
    "META_APP_ID": "your-app-id",
    "META_APP_SECRET": "your-app-secret"
  }
}
```

### AWS Lambda Test Tool

```bash
# Install test tool
dotnet tool install -g Amazon.Lambda.TestTool-10.0

# Run from backend directory
dotnet lambda-test-tool-10.0
```

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

## Post Scheduling Flow

### Production (AWS)

1. User creates/schedules post via API → saved to DB with `Status=Pending`
2. EventBridge triggers Dispatcher Lambda every minute
3. Dispatcher queries for due posts (`ScheduledAt <= now` OR `NextRetryAt <= now`)
4. Dispatcher atomically claims posts (`Status → Publishing`) and sends to SQS
5. Publisher Lambda receives SQS message, calls Meta Graph API
6. On success: `Status → Published`, stores `ExternalPostId`
7. On transient error: `Status → RetryPending`, sets `NextRetryAt` (exponential backoff)
8. On permanent error: `Status → Failed`, stores error message
9. After 3 SQS retries, message moves to DLQ for manual inspection

### Local Development

1. User creates/schedules post via API → saved to DB with `Status=Pending`
2. `LocalSchedulerBackgroundService` polls every 30 seconds
3. Same publishing logic via `FacebookPagePublisher`

## Next Steps

1. **Add Authentication**: Integrate JWT or AWS Cognito
2. **API Gateway Authorizer**: Protect endpoints
3. **Custom Domain**: Use Route 53 + CloudFront
4. **CI/CD**: Automate deployment with GitHub Actions or AWS CodePipeline
5. **Monitoring**: Set up CloudWatch dashboards and alarms
6. **Secrets Manager**: Move DB credentials and Meta secrets to AWS Secrets Manager

## Resources

- [AWS Lambda .NET Documentation](https://docs.aws.amazon.com/lambda/latest/dg/lambda-csharp.html)
- [Amazon.Lambda.AspNetCoreServer](https://github.com/aws/aws-lambda-dotnet/tree/master/Libraries/src/Amazon.Lambda.AspNetCoreServer)
- [AWS Lambda Tools for .NET CLI](https://github.com/aws/aws-extensions-for-dotnet-cli)
