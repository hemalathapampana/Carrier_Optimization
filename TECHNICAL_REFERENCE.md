# Technical Reference Guide

## Core Classes and Methods

### AltaworxSimCardCostOptimizer Class

#### Key Methods

##### `ProcessQueues(context, queueIds, messageId, skipLowerCostCheck, chargeType)`
**Purpose**: Processes multiple optimization queues concurrently
**Parameters**:
- `context`: KeySysLambdaContext - Lambda execution context
- `queueIds`: List<long> - Queue IDs to process
- `messageId`: string - SQS message identifier
- `skipLowerCostCheck`: bool - Skip cost validation checks
- `chargeType`: OptimizationChargeType - Type of charges to consider

**Flow**:
1. Validates queue existence and status
2. Loads SIM cards and rate plans
3. Creates rate pool collections
4. Executes optimization algorithm
5. Records results or continues processing

##### `ProcessQueuesContinue(context, queueIds, messageId, skipLowerCostCheck, chargeType)`
**Purpose**: Continues optimization processing from cached state
**Key Features**:
- Retrieves partial optimization state from Redis
- Continues processing from last checkpoint
- Handles Lambda timeout scenarios

### QueueCarrierPlanOptimization Class

#### Key Methods

##### `IsTimeToRun(context, billingPeriod, serviceProvider)`
**Purpose**: Determines if optimization should execute based on timing rules
**Logic**:
```csharp
if ((daysUntilBillingPeriodEnd < 8 && currentLocalTime.Date <= billingPeriod.BillingPeriodEnd.Date) ||
    (currentLocalTime.Date == billingPeriod.BillingPeriodEnd.Date && serviceProvider.OptimizationStartHourLocalTime != null))
{
    if (currentLocalTime.Hour >= serviceProvider.OptimizationStartHourLocalTime.Value)
    {
        return true; // Allow continuous runs on the last day from start hour
    }
}
```

##### `RunOptimization(context, tenantId, serviceProviderId, billingPeriodId, optimizationSessionId, billingPeriod, instance, additionalData, integrationAuthenticationId)`
**Purpose**: Executes M2M optimization workflow
**Key Steps**:
1. Validates rate plans and communication plans
2. Creates communication plan groups
3. Generates rate plan permutations
4. Queues optimization tasks
5. Enqueues cleanup process

### AltaworxJasperAWSGetDevicesQueue Class

#### Key Methods

##### `GetJasperDevices(context, sqsValues, jasperAuth, lastSyncDate, pageNumber)`
**Purpose**: Retrieves device data from Jasper API
**API Integration**:
```csharp
client.BaseAddress = new Uri($"{jasperAuth.ProductionApiUrl.TrimEnd('/')}/{JasperDevicesGetPath.TrimStart('/')}?modifiedSince={lastSyncDate:s}Z&pageNumber={pageNumber}");
client.DefaultRequestHeaders.Add("Authorization", "Basic " + encoded);
```

##### `ProcessDeviceList(context, sqsValues, jasperAuth)`
**Purpose**: Processes paginated device data retrieval
**Features**:
- Handles pagination with `MaxPagesToProcess` limit
- Implements retry policies for HTTP failures
- Manages bulk SQL operations

### AltaworxSimCardCostOptimizerCleanup Class

#### Key Methods

##### `CleanupInstance(context, instanceId, isCustomerOptimization, isLastInstance, serviceProviderId)`
**Purpose**: Finalizes optimization instances and generates results
**Process**:
1. Validates instance status
2. Identifies winning optimization queues
3. Cleans up non-winning results
4. Generates result files
5. Sends email notifications

##### `WriteResultByPortalType(context, isCustomerOptimization, instance, billingPeriod, queueIds, usesProration)`
**Purpose**: Generates optimization results based on portal type
**Portal-Specific Logic**:
- **Mobility**: `WriteMobilityResults()`
- **M2M**: `WriteM2MResults()`
- **CrossProvider**: `WriteCrossProviderCustomerResults()`

## Rate Plan Optimization Algorithm

### Rate Pool Assignment Strategy

The system uses a sophisticated assignment algorithm that considers multiple factors:

#### 1. **Rate Pool Creation**
```csharp
var avgUsage = simCards.Count > 0 ? simCards.Sum(x => x.CycleDataUsageMB) / simCards.Count : 0;
var calculatedPlans = RatePoolCalculator.CalculateMaxAvgUsage(queueRatePlans, avgUsage);
var ratePools = RatePoolFactory.CreateRatePools(calculatedPlans, billingPeriod, queue.UsesProration, chargeType);
```

#### 2. **SIM Card Grouping Strategies**
- **NoGrouping**: Individual optimization for each SIM card
- **GroupByCommunicationPlan**: Optimizes SIM cards with same communication plan together

#### 3. **Assignment Ordering**
- **Largest to Smallest**: Assigns highest usage devices first
- **Smallest to Largest**: Assigns lowest usage devices first

### Cost Calculation Framework

#### Charge Types
1. **RateChargeAndOverage**: Monthly rate + overage charges
2. **RateChargeOnly**: Monthly rate charges only
3. **OverageOnly**: Overage charges only

#### Proration Logic
For partial billing periods:
```csharp
if (usesProration)
{
    var prorationFactor = (billingPeriod.BillingPeriodEnd - billingPeriod.BillingPeriodStart).TotalDays / 30.0;
    adjustedRateCharge = rateCharge * prorationFactor;
}
```

## Database Operations

### Bulk Operations

#### Optimization Queue Creation
```sql
-- Bulk insert optimization queues
INSERT INTO OptimizationQueue (InstanceId, CommPlanGroupId, ServiceProviderId, UsesProration, IsBillInAdvance, CreatedBy, CreatedDate)
SELECT @InstanceId, @CommPlanGroupId, @ServiceProviderId, @UsesProration, @IsBillInAdvance, 'System', GETUTCDATE()
```

#### Result Cleanup
```sql
-- Cleanup non-winning results
EXEC usp_Optimization_DeviceResultAndQueueRatePlan_Cleanup @commGroupId, @winningQueueId
```

### Key Stored Procedures

#### `usp_Update_Jasper_Device`
Updates device information from Jasper staging tables
**Parameters**:
- `@isLastPage`: Indicates final page of device sync
- `@BillingCycleEndDay`: Billing cycle end day
- `@BillingCycleEndHour`: Billing cycle end hour
- `@ServiceProviderId`: Service provider identifier

#### `usp_Optimization_DeviceResultAndQueueRatePlan_Cleanup`
Cleans up optimization results for non-winning queues
**Parameters**:
- `@commGroupId`: Communication group identifier
- `@winningQueueId`: Winning queue identifier

#### `usp_Optimization_PreviousRatePlanUpdateSummary`
Retrieves historical rate plan update performance data
**Parameters**:
- `@InstanceId`: Optimization instance identifier

## Caching Strategy

### Redis Cache Usage

#### SIM Card Caching
```csharp
simCards = RedisCacheHelper.GetSimCardsFromCache(context, instance.Id, commPlans, commPlanGroupId,
    () => GetSimCardsByPortalType(context, instance, queue.ServiceProviderId, billingPeriod, instance.PortalType, commPlanGroupId, commPlans, optimizationGroups));
```

#### Partial Optimization State
```csharp
// Save partial state
var remainingQueueIds = RedisCacheHelper.RecordPartialAssignerToCache(context, assigner);

// Retrieve and continue
var assigner = RedisCacheHelper.GetPartialAssignerFromCache(context, queueIds, context.OptimizationSettings.BillingTimeZone);
```

### Cache Key Structure
- **SIM Cards**: `simcards:{instanceId}:{commPlanGroupId}`
- **Partial Assigner**: `partial_assigner:{queueIds_hash}`
- **Rate Plans**: `rateplans:{serviceProviderId}:{billingPeriodId}`

## Error Handling Patterns

### Retry Policies

#### SQL Transient Retry
```csharp
private static ISyncPolicy GetSqlTransientPolicy(IKeysysLogger logger, GetDeviceQueueSqsValues sqsValues, string errorContext = "")
{
    return Policy
        .Handle<SqlException>()
        .Or<TimeoutException>()
        .WaitAndRetry(SQL_TRANSIENT_RETRY_MAX_COUNT, 
            retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
            onRetry: (outcome, timespan, retryCount, context) =>
            {
                logger.LogInfo("RETRY", $"SQL retry attempt {retryCount} after {timespan} seconds. Context: {errorContext}");
            });
}
```

#### HTTP Retry Policy
```csharp
private static AsyncPolicyWrap<bool> GetHttpTransientPolicy(IKeysysLogger logger, GetDeviceQueueSqsValues sqsValues, string errorContext = "")
{
    var retryPolicy = Policy
        .Handle<HttpRequestException>()
        .Or<TaskCanceledException>()
        .WaitAndRetryAsync(HTTP_TRANSIENT_RETRY_MAX_COUNT,
            retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
    
    var fallbackPolicy = Policy<bool>
        .Handle<Exception>()
        .FallbackAsync(async cancellationToken =>
        {
            sqsValues.Errors.Add($"HTTP operation failed after {HTTP_TRANSIENT_RETRY_MAX_COUNT} retries. Context: {errorContext}");
            return await Task.FromResult(true);
        });
    
    return fallbackPolicy.WrapAsync(retryPolicy);
}
```

### Error Notification System

#### Email Alert Structure
```csharp
var subject = "[Error] Altaworx SIM Card Cost Optimization";
var body = new BodyBuilder()
{
    HtmlBody = $"<div>Error occurred in optimization process:</div><br/><div>{errorMessage}</div>",
    TextBody = $"Error occurred in optimization process: {errorMessage}"
};
```

#### AMOP 2.0 Integration
```csharp
OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, errorMessage, 0, "", additionalData);
```

## Performance Optimization

### Lambda Configuration

#### Memory and Timeout Settings
- **Core Optimizer**: 1024MB memory, 15 minutes timeout
- **Device Sync**: 512MB memory, 5 minutes timeout
- **Cleanup**: 256MB memory, 15 minutes timeout
- **Queue Creator**: 256MB memory, 5 minutes timeout

#### Concurrency Settings
- **Reserved Concurrency**: 50 for core optimizer
- **Burst Concurrency**: 100 for device sync
- **Sequential Processing**: Cleanup functions

### Database Connection Management

#### Connection String Configuration
```csharp
private void InitializeRepositories(ILambdaContext context, KeySysLambdaContext keysysContext)
{
    serviceProviderRepository = new ServiceProviderRepository(keysysContext.ConnectionString);
    carrierRatePlanRepository = new CarrierRatePlanRepository(keysysContext.ConnectionString);
    optimizationMobilityDeviceRepository = new OptimizationMobilityDeviceRepository(keysysContext.ConnectionString);
    crossProviderOptimizationRepository = new CrossProviderOptimizationRepository(keysysContext.ConnectionString);
}
```

#### Connection Pooling
- **Max Pool Size**: 100 connections
- **Connection Timeout**: 30 seconds
- **Command Timeout**: 900 seconds for long-running operations

### SQS Message Processing

#### Message Batching
- **Batch Size**: 1 message per Lambda invocation
- **Visibility Timeout**: 15 minutes
- **Max Receive Count**: 3 attempts

#### Message Attributes
```csharp
var messageAttributes = new Dictionary<string, MessageAttributeValue>
{
    { "QueueIds", new MessageAttributeValue { DataType = "String", StringValue = string.Join(",", queueIds) } },
    { "ChargeType", new MessageAttributeValue { DataType = "String", StringValue = ((int)chargeType).ToString() } },
    { "SkipLowerCostCheck", new MessageAttributeValue { DataType = "String", StringValue = skipLowerCostCheck.ToString() } }
};
```

## Monitoring and Logging

### CloudWatch Metrics

#### Custom Metrics
- **OptimizationDuration**: Time taken for optimization
- **DevicesProcessed**: Number of devices optimized
- **CostSavings**: Total cost savings achieved
- **ErrorRate**: Percentage of failed optimizations

#### Log Levels
- **INFO**: General operation information
- **WARNING**: Non-critical issues
- **ERROR**: Critical errors requiring attention
- **DEBUG**: Detailed debugging information

### Alerting Thresholds

#### Performance Alerts
- **High Error Rate**: > 5% over 15 minutes
- **Long Processing Time**: > 10 minutes for single optimization
- **Queue Depth**: > 100 messages in any queue

#### Business Alerts
- **Low Cost Savings**: < 5% savings for optimization run
- **High Device Count Variance**: > 10% difference between expected and actual
- **Rate Plan Limit Exceeded**: > 15 rate plans in optimization group

## Security Considerations

### IAM Policies

#### Lambda Execution Role
```json
{
    "Version": "2012-10-17",
    "Statement": [
        {
            "Effect": "Allow",
            "Action": [
                "sqs:ReceiveMessage",
                "sqs:DeleteMessage",
                "sqs:SendMessage",
                "sqs:GetQueueAttributes"
            ],
            "Resource": "arn:aws:sqs:*:*:altaworx-optimization-*"
        },
        {
            "Effect": "Allow",
            "Action": [
                "ses:SendEmail",
                "ses:SendRawEmail"
            ],
            "Resource": "*"
        }
    ]
}
```

#### Database Access
- **Least Privilege**: Only necessary stored procedures accessible
- **Network Security**: VPC-only database access
- **Encryption**: TLS 1.2 for all database connections

### Data Protection

#### Sensitive Data Handling
- **API Keys**: Stored in AWS Secrets Manager
- **Database Credentials**: Encrypted environment variables
- **Customer Data**: Encrypted at rest and in transit

#### Audit Logging
- **All Operations**: Comprehensive logging of all system operations
- **Data Access**: Detailed logging of data access patterns
- **Configuration Changes**: Audit trail for all configuration modifications

## Deployment Procedures

### CI/CD Pipeline

#### Build Process
1. **Code Compilation**: C# project compilation
2. **Unit Testing**: Automated test execution
3. **Package Creation**: Lambda deployment package
4. **Security Scanning**: Code security analysis

#### Deployment Stages
1. **Development**: Individual developer testing
2. **Integration**: System integration testing
3. **Staging**: Production-like environment testing
4. **Production**: Live system deployment

### Configuration Management

#### Environment Variables
- **Development**: Local development settings
- **Staging**: Pre-production configuration
- **Production**: Live system configuration

#### Feature Flags
- **New Algorithm**: Toggle for new optimization algorithms
- **Cache Enabled**: Enable/disable Redis caching
- **Auto Rate Plan Updates**: Enable/disable automatic rate plan changes

## Troubleshooting Guide

### Common Issues

#### Cache Connection Failures
**Symptoms**: "Redis cache is configured but not reachable" errors
**Solution**:
1. Check Redis cluster status
2. Verify network connectivity
3. Review security group configurations
4. Restart Lambda functions if necessary

#### API Timeout Issues
**Symptoms**: HTTP timeout exceptions in device sync
**Solution**:
1. Check external API status
2. Review retry policy configuration
3. Increase timeout values if appropriate
4. Implement circuit breaker pattern

#### Database Deadlocks
**Symptoms**: SQL deadlock exceptions during optimization
**Solution**:
1. Review concurrent processing limits
2. Optimize database queries
3. Implement proper transaction isolation
4. Add retry logic for deadlock scenarios

### Diagnostic Tools

#### Log Analysis Queries
```sql
-- Check optimization performance
SELECT 
    InstanceId,
    RunStartTime,
    RunEndTime,
    DATEDIFF(MINUTE, RunStartTime, RunEndTime) as DurationMinutes,
    DeviceCount
FROM OptimizationInstance
WHERE RunStartTime >= DATEADD(DAY, -7, GETUTCDATE())
ORDER BY RunStartTime DESC;
```

#### SQS Queue Monitoring
```bash
# Check queue depth
aws sqs get-queue-attributes --queue-url <queue-url> --attribute-names ApproximateNumberOfMessages

# View messages in queue
aws sqs receive-message --queue-url <queue-url> --max-number-of-messages 10
```

This technical reference provides detailed implementation information for developers and system administrators working with the Altaworx SIM Card Cost Optimization system.