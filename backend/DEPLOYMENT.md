# Quick Deployment Guide

## Prerequisites

```bash
# Install AWS Lambda Tools for .NET
dotnet tool install -g Amazon.Lambda.Tools

# Configure AWS credentials
aws configure
```

## Local Development

```bash
cd backend
dotnet run
# API: http://localhost:5122
# Swagger: http://localhost:5122/swagger
```

## Deploy to AWS Lambda

### 1. First-time deployment

```bash
cd backend
dotnet lambda deploy-function PostPilotApi
```

Follow prompts to:
- Select AWS region (e.g., `us-east-1`)
- Create or select IAM role
  - Recommended: Create new role with these policies:
    - `AWSLambdaBasicExecutionRole`
    - `AWSLambdaVPCAccessExecutionRole` (if using VPC)
- Confirm deployment

### 2. Set Database Connection String

```bash
# Replace with your actual RDS connection string
aws lambda update-function-configuration \
  --function-name PostPilotApi \
  --environment "Variables={DB_CONNECTION_STRING='Host=your-rds.amazonaws.com;Port=5432;Database=postpilot;Username=postgres;Password=yourpassword'}"
```

### 3. Create API Gateway HTTP API

```bash
# Create HTTP API with Lambda integration
aws apigatewayv2 create-api \
  --name PostPilotApi \
  --protocol-type HTTP \
  --target arn:aws:lambda:REGION:ACCOUNT_ID:function:PostPilotApi

# Grant API Gateway permission to invoke Lambda
aws lambda add-permission \
  --function-name PostPilotApi \
  --statement-id apigateway-invoke \
  --action lambda:InvokeFunction \
  --principal apigatewayv2.amazonaws.com
```

### 4. Get API Gateway Endpoint

```bash
aws apigatewayv2 get-apis --query "Items[?Name=='PostPilotApi'].ApiEndpoint" --output text
```

Your API will be available at: `https://{api-id}.execute-api.{region}.amazonaws.com`

## Update Existing Deployment

```bash
cd backend
dotnet lambda deploy-function PostPilotApi
```

## Test Lambda Function

```bash
# Invoke directly
aws lambda invoke \
  --function-name PostPilotApi \
  --payload '{"version":"2.0","routeKey":"GET /api/posts","rawPath":"/api/posts","requestContext":{"http":{"method":"GET","path":"/api/posts"}}}' \
  response.json

cat response.json
```

## View Logs

```bash
# Real-time logs
aws logs tail /aws/lambda/PostPilotApi --follow

# Recent logs
aws logs tail /aws/lambda/PostPilotApi --since 1h
```

## Delete Function

```bash
aws lambda delete-function --function-name PostPilotApi
```

## Complete Infrastructure Setup (AWS CLI)

```bash
#!/bin/bash
# Complete setup script

REGION="us-east-1"
ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
FUNCTION_NAME="PostPilotApi"
DB_CONNECTION="Host=your-rds.amazonaws.com;Port=5432;Database=postpilot;Username=postgres;Password=yourpassword"

# 1. Deploy Lambda
cd backend
dotnet lambda deploy-function $FUNCTION_NAME --region $REGION

# 2. Set environment variables
aws lambda update-function-configuration \
  --function-name $FUNCTION_NAME \
  --environment "Variables={DB_CONNECTION_STRING='$DB_CONNECTION'}" \
  --region $REGION

# 3. Create API Gateway
API_ID=$(aws apigatewayv2 create-api \
  --name $FUNCTION_NAME \
  --protocol-type HTTP \
  --target arn:aws:lambda:$REGION:$ACCOUNT_ID:function:$FUNCTION_NAME \
  --region $REGION \
  --query ApiId --output text)

# 4. Grant API Gateway permission
aws lambda add-permission \
  --function-name $FUNCTION_NAME \
  --statement-id apigateway-$API_ID \
  --action lambda:InvokeFunction \
  --principal apigatewayv2.amazonaws.com \
  --source-arn "arn:aws:execute-api:$REGION:$ACCOUNT_ID:$API_ID/*" \
  --region $REGION

# 5. Get endpoint
ENDPOINT=$(aws apigatewayv2 get-api --api-id $API_ID --region $REGION --query ApiEndpoint --output text)
echo "API Endpoint: $ENDPOINT"
echo "Test with: curl $ENDPOINT/api/posts"
```

## Infrastructure as Code (Terraform Example)

```hcl
# main.tf
resource "aws_lambda_function" "api" {
  filename         = "PostPilotApi.zip"
  function_name    = "PostPilotApi"
  role            = aws_iam_role.lambda.arn
  handler         = "PostPilot.Api::PostPilot.Api.LambdaEntryPoint::FunctionHandlerAsync"
  runtime         = "dotnet10"
  timeout         = 30
  memory_size     = 512
  architectures   = ["arm64"]

  environment {
    variables = {
      DB_CONNECTION_STRING = var.db_connection_string
    }
  }
}

resource "aws_apigatewayv2_api" "api" {
  name          = "PostPilotApi"
  protocol_type = "HTTP"
  target        = aws_lambda_function.api.arn
}

resource "aws_lambda_permission" "api_gateway" {
  statement_id  = "AllowAPIGatewayInvoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.api.function_name
  principal     = "apigatewayv2.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.api.execution_arn}/*"
}

output "api_endpoint" {
  value = aws_apigatewayv2_api.api.api_endpoint
}
```

## Useful AWS CLI Commands

```bash
# List functions
aws lambda list-functions --query "Functions[?contains(FunctionName, 'PostPilot')]"

# Get function configuration
aws lambda get-function-configuration --function-name PostPilotApi

# Update timeout
aws lambda update-function-configuration \
  --function-name PostPilotApi \
  --timeout 60

# Update memory
aws lambda update-function-configuration \
  --function-name PostPilotApi \
  --memory-size 1024

# Add VPC configuration
aws lambda update-function-configuration \
  --function-name PostPilotApi \
  --vpc-config SubnetIds=subnet-xxx,subnet-yyy,SecurityGroupIds=sg-xxx

# View CloudWatch metrics
aws cloudwatch get-metric-statistics \
  --namespace AWS/Lambda \
  --metric-name Duration \
  --dimensions Name=FunctionName,Value=PostPilotApi \
  --start-time 2026-01-12T00:00:00Z \
  --end-time 2026-01-12T23:59:59Z \
  --period 3600 \
  --statistics Average,Maximum
```

## Testing Endpoints

```bash
# Set your API endpoint
API_ENDPOINT="https://abc123.execute-api.us-east-1.amazonaws.com"

# Get all posts
curl $API_ENDPOINT/api/posts

# Get specific post
curl $API_ENDPOINT/api/posts/{id}

# Create post
curl -X POST $API_ENDPOINT/api/posts \
  -H "Content-Type: application/json" \
  -d '{
    "content": "Hello from Lambda!",
    "platform": "Twitter",
    "scheduledAt": "2026-01-15T10:00:00Z"
  }'

# Update post
curl -X PUT $API_ENDPOINT/api/posts/{id} \
  -H "Content-Type: application/json" \
  -d '{
    "content": "Updated content",
    "platform": "Facebook",
    "scheduledAt": "2026-01-16T10:00:00Z"
  }'

# Delete post
curl -X DELETE $API_ENDPOINT/api/posts/{id}
```

## Rollback

```bash
# List versions
aws lambda list-versions-by-function --function-name PostPilotApi

# Update to previous version
aws lambda update-function-configuration \
  --function-name PostPilotApi \
  --revision-id {previous-revision-id}
```
