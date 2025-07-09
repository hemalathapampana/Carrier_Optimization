# Continuation Processing Analysis

## 1. Resumes from Redis Cache for Long-Running Optimizations

**What**: Tests and utilizes Redis cache connectivity to maintain optimization state and enable resumption of long-running processes.  
**Why**: Provides persistent storage for optimization progress and intermediate results to handle Lambda execution time limits.  
**How**: Tests Redis connection availability and sends configuration alerts when cache is unavailable but configured.

### Algorithm
```
STEP 1: Test Redis Connection
    On Lambda initialization, test Redis cache connectivity
    Set IsUsingRedisCache flag based on connection success
    Log Redis connection status for monitoring
    
STEP 2: Validate Redis Configuration
    CHECK if Redis connection string is configured
    CHECK if Redis connection test was successful
    IF configured but not connected:
        Log configuration issue
        Send alert email to administrators
        
STEP 3: Initialize Cache Usage
    IF Redis cache is available:
        Use Redis for storing optimization state
        Enable checkpoint functionality
        Allow resume from cache
    ELSE:
        Use database-only state management
        Rely on SQS message passing for continuation
        
STEP 4: Handle Cache Unavailability
    IF Redis configured but unreachable:
        Send configuration issue notification
        Include optimization session ID and instance ID
        Continue processing without cache
        Log degraded performance warning
```

### Code Location
```csharp
// Redis connection testing - Handler method in QueueCarrierPlanOptimization.cs
public async Task Handler(SQSEvent sqsEvent, ILambdaContext context)
{
    KeySysLambdaContext keysysContext = BaseFunctionHandler(context);
    
    // Test Redis connection and set cache availability flag
    IsUsingRedisCache = keysysContext.TestRedisConnection();
    InitializeRepositories(context, keysysContext);
}

// Redis configuration validation - RunOptimizationByPortalType method
private async Task RunOptimizationByPortalType(KeySysLambdaContext context, ServiceProviderRepository serviceProviderRepository, int billingPeriodId, int serviceProviderId, int tenantId, long optimizationSessionId, PortalTypes portalType, string additionalData)
{
    var instance = GetInstance(context, instanceId);

    // Check cache and send email if it is unreachable but configured with a valid connection string 
    if (context.IsRedisConnectionStringValid && !IsUsingRedisCache)
    {
        await LogAndSendConfigurationIssueEmailAsync(context, ErrorNotificationEmailReceiver, optimizationSessionId, instance.Id);
    }
}

// Cache availability tracking - Class field
private bool IsUsingRedisCache = false;
```

---

## 2. Continues Algorithm Execution from Checkpoint

**What**: Resumes optimization processing from specific checkpoints using SQS message attributes and database state tracking.  
**Why**: Enables long-running optimizations to continue across multiple Lambda invocations without losing progress.  
**How**: Uses message attributes to track processing state and resume from specific optimization phases.

### Algorithm
```
STEP 1: Check Processing State
    Read message attributes for continuation markers
    Look for OptimizationSessionId and HasSynced flags
    Determine current phase of optimization process
    
STEP 2: Validate Existing Session
    IF OptimizationSessionId exists in message:
        Retrieve existing optimization session from database
        Get optimization session GUID for tracking
        Continue from current checkpoint
    ELSE:
        Start new optimization session
        Create new session tracking data
        
STEP 3: Check Sync Status Checkpoint
    IF HasSynced = false:
        Resume from device sync phase
        Truncate staging tables for clean restart
        Enqueue device list synchronization
        Exit and wait for sync completion
        
STEP 4: Check Device Count Checkpoint
    IF optimization session exists:
        Get instance ID from session
        Retrieve optimization device count
        Update progress tracking (30% complete)
        Continue to optimization execution
        
STEP 5: Resume Optimization Processing
    Load existing optimization instance data
    Continue algorithm from last completed phase
    Update progress indicators for monitoring
    Process remaining optimization queues
```

### Code Location
```csharp
// Checkpoint detection - ProcessEventRecord method
private async Task ProcessEventRecord(KeySysLambdaContext context, ServiceProviderRepository serviceProviderRepository, SQSEvent.SQSMessage message)
{
    // Check for existing optimization session (checkpoint marker)
    if (!message.MessageAttributes.ContainsKey("OptimizationSessionId"))
    {
        // Start new optimization session
        var isOptRunning = IsOptimizationRunning(context, tenantId);
        if (!isOptRunning)
        {
            var billingPeriod = GetBillingPeriod(context, billingPeriodId.Value);
            optimizationSessionId = await StartOptimizationSession(context, tenantId, billingPeriod);
        }
    }
    else
    {
        // Resume from existing session (checkpoint)
        optimizationSessionId = long.Parse(message.MessageAttributes["OptimizationSessionId"].StringValue);
        optimizationSessionGuid = optimizationAmopApiTrigger.GetOptimizationSessionGuidBySessionId(context, optimizationSessionId);
    }

    // Check sync status checkpoint
    if (!message.MessageAttributes.ContainsKey("HasSynced") || !bool.TryParse(message.MessageAttributes["HasSynced"].StringValue, out var hasSynced))
    {
        hasSynced = false;
    }
    
    if (!hasSynced)
    {
        // Resume from sync checkpoint
        if (isAutoCarrierOptimization)
        {
            OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, 0, null, 20, "", additionalData);
        }
        logger.LogInfo("INFO", "Have not synced devices and usage already for this optimization run...enqueuing");
        TruncateStagingTables(logger, context.GeneralProviderSettings.JasperDbConnectionString, serviceProviderId);
        await EnqueueGetDeviceListAsync(_deviceSyncQueueUrl, serviceProviderId, 1, lastSyncDate, awsCredentials, logger, optimizationSessionId);
        return;
    }
    
    // Resume device count checkpoint
    if (isAutoCarrierOptimization)
    {
        int instanceId = 0;
        instanceId = optimizationAmopApiTrigger.GetInstancebySessionId(context, optimizationSessionId.ToString());
        if (instanceId != 0)
        {
            deviceCount = optimizationAmopApiTrigger.GetOptimizationDeviceCount(context, instanceId, "M2M");
        }
        OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, deviceCount, null, 30, "", additionalData);
    }
}

// Rate plan sequence continuation checkpoint
private async Task ProcessRatePlanSequences(KeySysLambdaContext context, SQSEvent.SQSMessage message)
{
    // Resume from rate plan sequence processing checkpoint
    if (message.MessageAttributes.ContainsKey(SQSMessageKeyConstant.RATE_PLAN_SEQUENCES))
    {
        await ProcessRatePlanSequences(context, message);
        return;
    }
}
```

---

## 3. Handles Lambda Timeout Scenarios

**What**: Manages Lambda execution time limits by breaking work into manageable chunks and using SQS for continuation.  
**Why**: Prevents optimization failures due to Lambda timeout constraints while maintaining processing progress.  
**How**: Divides optimization work into batches, sends continuation messages to SQS, and tracks progress through multiple Lambda invocations.

### Algorithm
```
STEP 1: Break Work into Manageable Batches
    Divide rate plan sequences into smaller chunks
    Use batch size constants to control chunk size
    Ensure each batch fits within Lambda timeout limits
    
STEP 2: Send Continuation Messages
    FOR each batch of rate plan sequences:
        Create SQS message with batch data
        Include continuation markers (comm group ID, sequences)
        Add message attributes for state tracking
        Send to carrier optimization queue
        
STEP 3: Track Progress Across Invocations
    Update progress indicators at each phase
    Send progress updates to AMOP API (0%, 20%, 30%, 40%, 50%)
    Log completion of each processing phase
    Maintain state in database for recovery
    
STEP 4: Handle Timeout Gracefully
    IF Lambda approaches timeout:
        Save current progress to database
        Send remaining work to SQS for next invocation
        Exit gracefully with progress maintained
        
STEP 5: Resume in Next Invocation
    New Lambda receives continuation message
    Load state from message attributes
    Continue processing from last checkpoint
    Update progress indicators
    
STEP 6: Coordinate Multiple Workers
    Send work to multiple SQS queues for parallel processing
    Each worker handles subset of optimization queues
    Coordinate completion through database status updates
    Monitor overall optimization progress
```

### Code Location
```csharp
// Batch processing for timeout handling - SendMessageToCreateQueueRatePlans method
private async Task SendMessageToCreateQueueRatePlans(KeySysLambdaContext context, List<RatePlanSequence> ratePoolSequences, long commGroupId)
{
    LogInfo(context, CommonConstants.SUB, $"{nameof(ratePoolSequences.Count)}: {ratePoolSequences.Count}");
    
    // Break into batches to handle timeout scenarios
    var ratePoolBatches = ratePoolSequences.Chunk(OptimizationConstant.RATE_PLAN_SEQUENCES_BATCH_SIZE);
    foreach (var sequences in ratePoolBatches)
    {
        var attributes = new Dictionary<string, string>()
        {
            {SQSMessageKeyConstant.RATE_PLAN_SEQUENCES, JsonSerializer.Serialize(sequences)},
            {SQSMessageKeyConstant.COMM_GROUP_ID, commGroupId.ToString()},
        };
        await sqsService.SendSQSMessage(ParameterizedLog(context), AwsCredentials(context.Base64Service, context.GeneralProviderSettings.AWSAccesKeyID, context.GeneralProviderSettings.AWSSecretAccessKey), _carrierOptimizationQueueUrl, attributes);
    }
}

// Progress tracking across timeouts - Multiple progress updates
private async Task ProcessEventRecord(KeySysLambdaContext context, ServiceProviderRepository serviceProviderRepository, SQSEvent.SQSMessage message)
{
    // Progress tracking at 0%
    if (isAutoCarrierOptimization)
    {
        OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, 0, null, 0, "", additionalData);
    }
    
    // Progress tracking at 20% (sync phase)
    if (isAutoCarrierOptimization)
    {
        OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, 0, null, 20, "", additionalData);
    }
    
    // Progress tracking at 30% (device count phase)
    OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, deviceCount, null, 30, "", additionalData);
    
    // Progress tracking at 50% (optimization completion)
    if (isAutoCarrierOptimization)
    {
        OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, 0, null, 50, "", additionalData);
    }
}

// Worker coordination - ProcessRatePlanSequences method
private async Task ProcessRatePlanSequences(KeySysLambdaContext context, SQSEvent.SQSMessage message)
{
    var sequences = JsonSerializer.Deserialize<RatePlanSequence[]>(message.MessageAttributes[SQSMessageKeyConstant.RATE_PLAN_SEQUENCES].StringValue);
    var isCommGroupIdParsed = int.TryParse(message.MessageAttributes[SQSMessageKeyConstant.COMM_GROUP_ID].StringValue, out var commGroupId);
    
    if (isCommGroupIdParsed && sequences != null && sequences.Length > 0)
    {
        // Process batch within timeout limits
        CreateQueueRatePlans(context, dtQueueRatePlan);
        
        // Send to worker processes with queue distribution
        await SendRunOptimizerMessage(context, sequences, QueuesPerInstance);
    }
}

// Timeout-aware queue creation - BulkCreateQueue method
public List<long> BulkCreateQueue(KeySysLambdaContext context, long instanceId, long commPlanGroupId, int serviceProviderId, bool usesProration, int sequenceCount, bool isBillInAdvance = false)
{
    // Bulk operations to maximize efficiency within timeout
    var dataTable = BuildOptimizationQueueTable();
    for (int i = 0; i < sequenceCount; i++)
    {
        var dataRow = AddOptimizationQueueRow(dataTable, instanceId, commPlanGroupId, serviceProviderId, usesProration, isBillInAdvance);
        dataTable.Rows.Add(dataRow);
    }
    
    // Use bulk copy for performance within timeout constraints
    var logMessage = SqlHelper.SqlBulkCopy(context.ConnectionString, dataTable, DatabaseTableNames.OPITMIZATION_QUEUE, SQLBulkCopyHelper.AutoMapColumns(dataTable));
    LogInfo(context, CommonConstants.INFO, $"{sequenceCount} Queues Created");
    
    return queueIds;
}
```