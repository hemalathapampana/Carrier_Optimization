# Monitoring Logic Analysis

## Overview
This document analyzes the monitoring logic implemented by the **AltaworxSimCardCostOptimizerCleanup Lambda** function for polling optimization queues, tracking completion status, implementing retry mechanisms, and monitoring queue depths for optimization workflow coordination.

---

## 1. Polls All Optimization Queues for Completion

### Definition
**What**: Continuously monitors optimization queue completion status by polling SQS queue attributes for message counts.  
**Why**: Ensures cleanup processes only execute when all optimization work is complete to prevent premature result processing.  
**How**: Uses GetOptimizationQueueLength method to poll SQS queue attributes and check for remaining optimization messages.

### Algorithm
```
STEP 1: Initialize Queue Polling in AltaworxSimCardCostOptimizerCleanup Lambda
    Access WatchQueueURL from environment configuration
    Create AWS SQS client with configured credentials
    Prepare to monitor optimization queue completion status
    
STEP 2: Poll SQS Queue Attributes
    Call GetQueueAttributesRequest with WatchQueueURL
    Request ApproximateNumberOfMessages attribute
    Request ApproximateNumberOfMessagesDelayed attribute  
    Request ApproximateNumberOfMessagesNotVisible attribute
    
STEP 3: Calculate Total Queue Depth
    Sum ApproximateNumberOfMessages (visible messages)
    Add ApproximateNumberOfMessagesDelayed (delayed messages)
    Add ApproximateNumberOfMessagesNotVisible (in-flight messages)
    Calculate total queue length for completion monitoring
    
STEP 4: Determine Completion Status
    IF total queue length = 0:
        All optimization queues are complete
        Proceed with cleanup instance processing
    ELSE:
        Optimization work still in progress
        Continue monitoring with retry mechanism
        
STEP 5: Handle Polling Errors
    IF SQS polling fails (TaskStatus.Faulted or Canceled):
        Log error status for debugging
        Return int.MaxValue to indicate unknown queue state
        Trigger retry mechanism for continued monitoring
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizerCleanup

```csharp
// Queue completion polling in GetOptimizationQueueLength method
private int GetOptimizationQueueLength(KeySysLambdaContext context)
{
    var awsCredentials = context.GeneralProviderSettings.AwsCredentials;
    using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
    {
        var request = new GetQueueAttributesRequest(_watchQueueUrl, new List<string> { "ApproximateNumberOfMessages", "ApproximateNumberOfMessagesDelayed", "ApproximateNumberOfMessagesNotVisible" });
        var response = client.GetQueueAttributesAsync(request);
        response.Wait();
        if (response.Status == TaskStatus.Faulted || response.Status == TaskStatus.Canceled)
        {
            LogInfo(context, "RESPONSE STATUS", $"Error Getting Queue Length: {response.Status}");
            return int.MaxValue;
        }

        var queueLength = response.Result.ApproximateNumberOfMessages + response.Result.ApproximateNumberOfMessagesDelayed + response.Result.ApproximateNumberOfMessagesNotVisible;
        return queueLength;
    }
}

// Queue completion check in ProcessEventRecord method
var optimizationQueueLength = GetOptimizationQueueLength(context);

if (optimizationQueueLength == 0)
{
    try
    {
        CleanupInstance(context, instanceId, isCustomerOptimization, isLastInstance, serviceProviderId);
    }
    catch (Exception ex)
    {
        LogInfo(context, "WARN", $"Error occurred on cleanup, requeuing: {ex.Message}");
        RequeueCleanup(context, instanceId, retryCount, optimizationQueueLength, isCustomerOptimization);
    }
}

// Environment configuration for queue monitoring
private string _watchQueueUrl = Environment.GetEnvironmentVariable("WatchQueueURL");
```

---

## 2. Uses Queue-Based Delay Strategy (600s → 900s Based on Queue Depth)

### Definition
**What**: Implements delay strategy based on optimization queue depth rather than exponential backoff, with longer delays for higher queue volumes.  
**Why**: Allows busy optimization periods to process without overwhelming the system while providing appropriate wait times for completion monitoring.  
**How**: Uses DelaySecondsFromQueueLength method to set delays based on current optimization queue volume.

### Algorithm
```
STEP 1: Initialize Delay Calculation in AltaworxSimCardCostOptimizerCleanup Lambda
    Receive current optimization queue length parameter
    Set default delay = 600 seconds (10 minutes)
    Prepare to calculate appropriate delay based on queue volume
    
STEP 2: Assess Queue Volume Impact
    CHECK current optimization queue length
    IF optimization queue length > 50 messages:
        Many optimization tasks still processing
        Set longer delay for high-volume scenarios
    ELSE:
        Normal optimization processing volume
        Use standard delay timing
        
STEP 3: Apply Queue-Based Delay Strategy
    IF queue length > 50:
        Set delay = 900 seconds (15 minutes maximum SQS delay)
        Allow extra time for high-volume processing
    ELSE:
        Set delay = 600 seconds (10 minutes default)
        Standard monitoring interval for normal processing
        
STEP 4: Return Calculated Delay
    Return appropriate delay seconds for SQS message scheduling
    Ensure delay fits within SQS maximum delay constraints (15 minutes)
    Enable appropriate monitoring frequency based on queue load
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizerCleanup

```csharp
// Queue-based delay strategy in DelaySecondsFromQueueLength method
private int DelaySecondsFromQueueLength(int optimizationQueueLength)
{
    // default delay per check
    var delaySeconds = 600;

    // if there are unstarted items in the queue, wait for those to at least start
    if (optimizationQueueLength > 50)
    {
        // can't delay more than 15 minutes in SQS
        delaySeconds = 900;
    }

    return delaySeconds;
}

// Delay application in RequeueCleanup method
private void RequeueCleanup(KeySysLambdaContext context, long instanceId, int retryCount, int optimizationQueueLength, bool isCustomerOptimization)
{
    LogInfo(context, "SUB", $"RequeueCleanup({instanceId},{retryCount},{optimizationQueueLength})");

    var awsCredentials = context.GeneralProviderSettings.AwsCredentials;
    using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
    {
        retryCount += 1;
        int delaySeconds = DelaySecondsFromQueueLength(optimizationQueueLength);
        var requestMsgBody = $"Requeue Cleanup for Instance {instanceId}, Retry #{retryCount}";
        // SQS message setup with calculated delay...
    }
}
```

---

## 3. Retries Up to 10 Times Before Timeout

### Definition
**What**: Implements retry mechanism with maximum 10 attempts before declaring optimization cleanup timeout.  
**Why**: Provides sufficient retry attempts for long-running optimizations while preventing infinite retry loops.  
**How**: Tracks retry count in SQS message attributes and enforces 10-retry limit before timeout declaration.

### Algorithm
```
STEP 1: Initialize Retry Tracking in AltaworxSimCardCostOptimizerCleanup Lambda
    Extract RetryCount from SQS message attributes
    Set retryCount = 0 if not specified in message
    Parse retry count for current cleanup attempt tracking
    
STEP 2: Validate Current Retry Count
    Parse RetryCount from message attributes using int.Parse()
    IF RetryCount attribute missing:
        Set retryCount = 0 for first attempt
        Initialize retry tracking for cleanup monitoring
    Log current retry count for monitoring and debugging
    
STEP 3: Evaluate Retry Limit Enforcement
    CHECK if retryCount < 10 (maximum retry threshold)
    IF under retry limit:
        Continue with cleanup monitoring process
        Allow additional retry attempts for completion monitoring
    ELSE:
        Retry limit exceeded - declare timeout
        
STEP 4: Execute Retry or Timeout Logic
    IF retryCount < 10 AND optimization queue not complete:
        Increment retry count by 1
        Calculate delay based on queue length
        Enqueue cleanup continuation message with new retry count
    ELSE IF retryCount >= 10:
        Log timeout exception message
        Stop retry attempts and declare optimization cleanup timeout
        
STEP 5: Handle Timeout Declaration
    Log "Optimization Cleanup Timed Out. Too many retry attempts."
    Stop further retry attempts for current optimization instance
    Allow system to proceed with error handling protocols
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizerCleanup

```csharp
// Retry tracking in ProcessEventRecord method
private void ProcessEventRecord(KeySysLambdaContext context, SQSEvent.SQSMessage message)
{
    LogInfo(context, "SUB", "ProcessEventRecord");
    if (message.MessageAttributes.ContainsKey("InstanceId"))
    {
        var instanceIdString = message.MessageAttributes["InstanceId"].StringValue;
        var instanceId = long.Parse(instanceIdString);

        var retryCount = 0;
        if (message.MessageAttributes.ContainsKey("RetryCount"))
        {
            var retryCountString = message.MessageAttributes["RetryCount"].StringValue;
            retryCount = int.Parse(retryCountString);
        }

        LogInfo(context, "SUB", $"InstanceId: {instanceId}, RetryCount: {retryCount}");
        var optimizationQueueLength = GetOptimizationQueueLength(context);

        if (optimizationQueueLength == 0)
        {
            // Queue complete - proceed with cleanup
            CleanupInstance(context, instanceId, isCustomerOptimization, isLastInstance, serviceProviderId);
        }
        else if (retryCount < 10)
        {
            // Retry limit not reached - continue monitoring
            RequeueCleanup(context, instanceId, retryCount, optimizationQueueLength, isCustomerOptimization);
        }
        else
        {
            // Retry limit exceeded - declare timeout
            LogInfo(context, "EXCEPTION", $"Optimization Cleanup Timed Out. Too many retry attempts.");
        }
    }
}

// Retry count increment in RequeueCleanup method
private void RequeueCleanup(KeySysLambdaContext context, long instanceId, int retryCount, int optimizationQueueLength, bool isCustomerOptimization)
{
    retryCount += 1;
    int delaySeconds = DelaySecondsFromQueueLength(optimizationQueueLength);
    
    var request = new SendMessageRequest
    {
        DelaySeconds = delaySeconds,
        MessageAttributes = new Dictionary<string, MessageAttributeValue>
        {
            {
                "RetryCount", new MessageAttributeValue
                { DataType = "String", StringValue = retryCount.ToString()}
            }
        }
    };
}
```

---

## 4. Tracks Queue Depths and Processing Status

### Definition
**What**: Monitors optimization queue depths and processing status through SQS message attributes and queue metrics for workflow coordination.  
**Why**: Enables intelligent cleanup timing and resource management based on actual optimization workload and completion status.  
**How**: Combines SQS queue depth monitoring with instance status tracking and message attribute processing for comprehensive status awareness.

### Algorithm
```
STEP 1: Initialize Status Tracking in AltaworxSimCardCostOptimizerCleanup Lambda
    Extract InstanceId from SQS message attributes
    Extract IsCustomerOptimization flag for processing type
    Extract IsLastInstance flag for workflow coordination
    Extract ServiceProviderId for multi-provider scenarios
    
STEP 2: Track Queue Depth Metrics
    Call GetOptimizationQueueLength() for current queue status
    Monitor ApproximateNumberOfMessages for active work
    Track ApproximateNumberOfMessagesDelayed for scheduled work
    Monitor ApproximateNumberOfMessagesNotVisible for in-flight work
    
STEP 3: Assess Processing Status
    Check optimization instance status using GetInstance()
    Validate instance against INSTANCE_FINISHED_STATUSES:
        OptimizationStatus.CleaningUp
        OptimizationStatus.CompleteWithSuccess  
        OptimizationStatus.CompleteWithErrors
    Prevent duplicate processing of completed instances
    
STEP 4: Coordinate Workflow Status
    IF optimizationQueueLength = 0:
        All optimization work complete
        Proceed with cleanup and result processing
    ELSE:
        Optimization work still in progress
        Continue monitoring with appropriate delay
        
STEP 5: Track Processing Context
    Monitor IsCustomerOptimization for customer-specific processing
    Track IsLastInstance for final processing coordination
    Log processing status for monitoring and debugging
    Update retry count for attempt tracking
    
STEP 6: Handle Status-Based Decision Making
    Use queue depth for delay calculation (600s vs 900s)
    Apply retry limits based on processing attempts
    Coordinate cleanup timing with optimization completion
    Enable result processing when all work is complete
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizerCleanup

```csharp
// Status tracking in ProcessEventRecord method
private void ProcessEventRecord(KeySysLambdaContext context, SQSEvent.SQSMessage message)
{
    // Extract processing status attributes
    bool isCustomerOptimization = false;
    if (message.MessageAttributes.ContainsKey("IsCustomerOptimization"))
    {
        isCustomerOptimization = Convert.ToBoolean(message.MessageAttributes["IsCustomerOptimization"].StringValue);
    }

    bool isLastInstance = false;
    if (message.MessageAttributes.ContainsKey("IsLastInstance"))
    {
        isLastInstance = Convert.ToBoolean(message.MessageAttributes["IsLastInstance"].StringValue);
    }

    int serviceProviderId = 0;
    if (message.MessageAttributes.ContainsKey("ServiceProviderId"))
    {
        serviceProviderId = int.Parse(message.MessageAttributes["ServiceProviderId"].StringValue);
    }

    // Track queue depth and processing status
    var optimizationQueueLength = GetOptimizationQueueLength(context);
    LogInfo(context, "SUB", $"InstanceId: {instanceId}, RetryCount: {retryCount}");
}

// Instance status validation in CleanupInstance method
private void CleanupInstance(KeySysLambdaContext context, long instanceId, bool isCustomerOptimization, bool isLastInstance, int serviceProviderId)
{
    // get instance
    var instance = GetInstance(context, instanceId);

    //check if instance is found & has valid status for process
    if (instance.Id <= 0)
    {
        LogInfo(context, "EXCEPTION", $"Could not find instance with id {instanceId}.");
        return;
    }

    if (INSTANCE_FINISHED_STATUSES.Contains((OptimizationStatus)instance.RunStatusId))
    {
        LogInfo(context, "WARNING", $"Duplicated instance cleanup request for instance with id {instanceId}.");
        return;
    }
}

// Instance finished statuses for duplicate prevention
private static readonly List<OptimizationStatus> INSTANCE_FINISHED_STATUSES = new List<OptimizationStatus>(){
    OptimizationStatus.CleaningUp,
    OptimizationStatus.CompleteWithSuccess,
    OptimizationStatus.CompleteWithErrors
};
```

---

## Monitoring Integration

### Complete Workflow Coordination
The **AltaworxSimCardCostOptimizerCleanup Lambda** coordinates monitoring through:

1. **Queue Polling** - Continuous SQS queue attribute monitoring for completion detection
2. **Delay Strategy** - Queue-depth-based delays (600s default, 900s for high volume)
3. **Retry Management** - Up to 10 retry attempts before timeout declaration
4. **Status Tracking** - Instance status validation and processing context monitoring

### Error Handling and Recovery
The system handles monitoring failures by:
- Returning int.MaxValue for SQS polling errors to trigger retry
- Using try-catch blocks around cleanup operations with requeue on failure
- Preventing duplicate processing through instance status validation
- Implementing timeout limits to prevent infinite retry loops