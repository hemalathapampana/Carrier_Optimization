# Continuation Processing Analysis

## Overview
This document analyzes the continuation processing capabilities in the **AltaworxSimCardCostOptimizer Lambda** function, covering Redis cache resumption, checkpoint-based execution, and timeout scenario handling for long-running optimization processes.

---

## 1. Resumes from Redis Cache for Long-Running Optimizations

### Definition
**What**: Tests and utilizes Redis cache connectivity to maintain optimization state and enable resumption of long-running processes.  
**Why**: Provides persistent storage for optimization progress and intermediate results to handle Lambda execution time limits.  
**How**: Tests Redis connection availability and sends configuration alerts when cache is unavailable but configured.

### Algorithm
```
STEP 1: Initialize Redis Connection Test
    On AltaworxSimCardCostOptimizer Lambda startup
    Test Redis cache connectivity using TestRedisConnection()
    Set IsUsingRedisCache flag based on connection success
    Log Redis connection status for monitoring
    
STEP 2: Validate Redis Configuration
    CHECK if Redis connection string is configured in environment
    CHECK if Redis connection test was successful
    IF configured but not connected:
        Log configuration issue warning for debugging
        Continue processing without Redis cache
        
STEP 3: Initialize Cache Usage Strategy
    IF Redis cache is available and connected:
        Use Redis for storing partial assigner state
        Enable checkpoint functionality for long optimizations
        Allow resume from cache for interrupted processing
    ELSE:
        Fall back to standard processing mode
        Complete optimization in single Lambda execution
        
STEP 4: Handle Cache Unavailability Scenarios
    IF Redis configured but unreachable during optimization:
        Log configuration issue for monitoring
        Continue processing without cache (degraded mode)
        Complete optimization without continuation capability
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Main Lambda handler - Redis connection testing in AltaworxSimCardCostOptimizer
public async Task Handler(SQSEvent sqsEvent, ILambdaContext context)
{
    KeySysLambdaContext keysysContext = null;
    try
    {
        keysysContext = BaseFunctionHandler(context);
        if (SanityCheckTimeLimit == 0)
        {
            SanityCheckTimeLimit = DEFAULT_SANITY_CHECK_TIME_LIMIT;
        }

        //if a redis cache connection string is set but not reachable => considered as an error & continue without cache
        IsUsingRedisCache = keysysContext.TestRedisConnection();
        if (keysysContext.IsRedisConnectionStringValid && !IsUsingRedisCache)
        {
            //only log and no email since there are many instances of this lambda during the calculation or else the email receiver will be spam with error notices.
            var errorMessage = "Redis cache is configured but not reachable. Proceeding without cache.";
            LogInfo(keysysContext, "EXCEPTION", errorMessage);
        }

        InitializeRepositories(context, keysysContext);
        await ProcessEvent(keysysContext, sqsEvent);
    }
    catch (Exception ex)
    {
        LogInfo(keysysContext, "EXCEPTION", ex.Message + " " + ex.StackTrace);
    }
    base.CleanUp(keysysContext);
}

// Cache availability tracking field
private bool IsUsingRedisCache = false;
```

---

## 2. Continues Algorithm Execution from Checkpoint

### Definition
**What**: Resumes optimization processing from specific checkpoints using SQS message attributes and database state tracking.  
**Why**: Enables long-running optimizations to continue across multiple Lambda invocations without losing progress.  
**How**: Uses message attributes to track processing state and resume from specific optimization phases.

### Algorithm
```
STEP 1: Check Processing Mode in AltaworxSimCardCostOptimizer Lambda
    Read incoming SQS message attributes for processing type
    Look for IsChainingProcess flag in message attributes
    Check for QueueIds in message for optimization targets
    Determine if standard or continuation processing required
    
STEP 2: Validate Continuation Requirements
    IF IsChainingProcess = true in message attributes:
        CHECK if Redis connection string is configured
        IF no Redis connection available:
            Log exception and stop processing
            Return without processing queues
        Route to ProcessQueuesContinue method
    ELSE:
        Route to standard ProcessQueues method
        Execute full optimization from beginning
        
STEP 3: Load Continuation State from Redis Cache
    Extract QueueIds from message attributes
    Use RedisCacheHelper.GetPartialAssignerFromCache()
    Load saved RatePoolAssigner state from Redis
    IF cache not found:
        Consider optimization complete and return
        
STEP 4: Resume Algorithm Execution
    Set Lambda context and logger on loaded assigner
    Call assigner.AssignSimCardsContinue() method
    Continue optimization from saved checkpoint position
    Process remaining optimization calculations
    
STEP 5: Handle Completion or Save Progress
    Check if assigner.IsCompleted after processing
    IF not completed:
        Save current state back to Redis cache
        Enqueue continuation message for next invocation
    ELSE:
        Clear cache and save final results to database
        Complete optimization processing
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer
```csharp
// Continuation processing detection in AltaworxSimCardCostOptimizer
private async Task ProcessEventRecord(KeySysLambdaContext context, SQSEvent.SQSMessage message)
{
    LogInfo(context, "SUB", "ProcessEventRecord");
    LogInfo(context, "INFO", $"Message attributes: {string.Join(Environment.NewLine, message.MessageAttributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value.StringValue))}");
    
    if (message.MessageAttributes.ContainsKey("QueueIds"))
    {
        var messageId = message.MessageId;
        var queueIdStrings = message.MessageAttributes["QueueIds"].StringValue.Split(',').ToList();
        var queueIds = queueIdStrings.Select(long.Parse).ToList();
        
        // Check for continuation processing flag
        if (message.MessageAttributes.ContainsKey("IsChainingProcess")
            && bool.TryParse(message.MessageAttributes["IsChainingProcess"].StringValue, out var isChainingOptimization)
            && isChainingOptimization)
        {
            if (!context.IsRedisConnectionStringValid)
            {
                LogInfo(context, "EXCEPTION", $"No cache connection string is setup. Stopping process.");
                return;
            }
            await ProcessQueuesContinue(context, queueIds, messageId, skipLowerCostCheck, chargeType);
        }
        else
        {
            await ProcessQueues(context, queueIds, messageId, skipLowerCostCheck, chargeType);
        }
    }
    else
    {
        LogInfo(context, "EXCEPTION", $"No Queue Ids provided in message");
    }
}

// Continuation processing method
private async Task ProcessQueuesContinue(KeySysLambdaContext context, List<long> queueIds, string messageId, bool skipLowerCostCheck, OptimizationChargeType chargeType)
{
    LogInfo(context, "SUB", $"(,,{messageId},{skipLowerCostCheck},{chargeType})");

    if (queueIds.Count() <= 0)
    {
        LogInfo(context, "ERROR", $"No Queue Ids included. Stopping process.");
        return;
    }

    //reference a queue to get resources since they will share the same info
    var referenceQueueId = queueIds.First();
    var queue = GetQueue(context, referenceQueueId);

    // read assigner from cache
    var assigner = RedisCacheHelper.GetPartialAssignerFromCache(context, queueIds, context.OptimizationSettings.BillingTimeZone);

    // if cache not found => consider done
    // if cache is found but complete => save the result
    if (assigner == null)
    {
        return;
    }
    else
    {
        assigner.SetLambdaContext(context.LambdaContext);
        assigner.SetLambdaLogger(context.logger);
        // call assignSimCardsContinue to continue the processing
        assigner.AssignSimCardsContinue(context.OptimizationSettings.BillingTimeZone, false);
    }
    await WrapUpCurrentInstance(context, queueIds, skipLowerCostCheck, chargeType, amopCustomerId, accountNumber, commPlanGroupId, assigner);
}
```

---

## 3. Handles Lambda Timeout Scenarios

### Definition
**What**: Manages Lambda execution time limits by breaking work into manageable chunks and using SQS for continuation.  
**Why**: Prevents optimization failures due to Lambda timeout constraints while maintaining processing progress.  
**How**: Divides optimization work into batches, sends continuation messages to SQS, and tracks progress through multiple Lambda invocations.

### Algorithm
```
STEP 1: Monitor Execution Time in AltaworxSimCardCostOptimizer Lambda
    Check remaining Lambda execution time during optimization
    Use context.LambdaContext.RemainingTime for time tracking
    Log remaining seconds for monitoring purposes
    Use SanityCheckTimeLimit for optimization algorithm control
    
STEP 2: Initialize Optimization with Timeout Awareness
    Create RatePoolAssigner with timeout-aware configuration
    Set IsUsingRedisCache flag for continuation capability
    Configure assigner with Lambda context for time monitoring
    Enable cache-based checkpointing if Redis available
    
STEP 3: Execute Optimization with Timeout Handling
    Call assigner.AssignSimCards() for optimization processing
    Algorithm monitors Lambda execution time internally
    IF optimization completes within timeout:
        Save results and mark queues as complete
    ELSE prepare for continuation processing
    
STEP 4: Handle Incomplete Optimization
    Check assigner.IsCompleted status after processing
    IF not completed AND Redis cache available:
        Save partial assigner state to Redis cache
        Use RedisCacheHelper.RecordPartialAssignerToCache()
        Get remaining queue IDs for continuation
        Enqueue continuation message with IsChainingProcess flag
        
STEP 5: Resume in Next Lambda Invocation
    New AltaworxSimCardCostOptimizer Lambda receives IsChainingProcess message
    Load saved assigner state using RedisCacheHelper.GetPartialAssignerFromCache()
    Set Lambda context and logger on resumed assigner
    Call assigner.AssignSimCardsContinue() to resume processing
    
STEP 6: Complete or Continue Cycle
    After continuation processing check assigner.IsCompleted again
    IF completed:
        Clear cache and save final results
        Mark optimization queues as complete
    ELSE:
        Save state again and enqueue next continuation
        Repeat cycle until optimization completes
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer
```csharp
// Timeout monitoring in ProcessQueues method in AltaworxSimCardCostOptimizer
private async Task ProcessQueues(KeySysLambdaContext context, List<long> queueIds, string messageId, bool skipLowerCostCheck, OptimizationChargeType chargeType)
{
    LogInfo(context, "SUB", $"ProcessQueues(,,{messageId},{skipLowerCostCheck},{chargeType})");

    // Monitor remaining execution time
    var remainingSeconds = (int)Math.Floor(context.LambdaContext.RemainingTime.TotalSeconds);
    LogInfo(context, "INFO", $"Remaining run time: {remainingSeconds} seconds.");

    // Create timeout-aware optimization assigner
    var assigner = new RatePoolAssigner(string.Empty, ratePoolCollection, simCards, context.logger, SanityCheckTimeLimit, context.LambdaContext, IsUsingRedisCache,
        instance.PortalType,
        shouldFilterByRatePlanType,
        shouldPoolUsageBetweenRatePlans);
    
    // Execute optimization with timeout monitoring
    assigner.AssignSimCards(GetSimCardGroupingByPortalType(instance.PortalType, instance.IsCustomerOptimization),
                                context.OptimizationSettings.BillingTimeZone,
                                false,
                                false,
                                ratePoolSequences);

    await WrapUpCurrentInstance(context, queueIds, skipLowerCostCheck, chargeType, amopCustomerId, accountNumber, commPlanGroupId, assigner);
}

// Timeout handling and continuation in WrapUpCurrentInstance method
private async Task WrapUpCurrentInstance(KeySysLambdaContext context, List<long> queueIds, bool skipLowerCostCheck, OptimizationChargeType chargeType, int? amopCustomerId, string accountNumber, long commPlanGroupId, RatePoolAssigner assigner)
{
    LogInfo(context, "SUB", $"(,{string.Join(',', queueIds)},)");
    // if complete => save
    // else record to cache & send sqs message
    if (!assigner.IsCompleted && context.IsRedisConnectionStringValid && IsUsingRedisCache)
    {
        //save to cache the assigner
        var remainingQueueIds = RedisCacheHelper.RecordPartialAssignerToCache(context, assigner);
        if (remainingQueueIds != null && remainingQueueIds.Count > 0)
        {
            //requeue to continue
            await EnqueueOptimizationContinueProcessAsync(context, remainingQueueIds, chargeType, skipLowerCostCheck);
        }
    }
    else
    {
        if (context.IsRedisConnectionStringValid && IsUsingRedisCache)
        {
            RedisCacheHelper.ClearPartialAssignerFromCache(context, queueIds);
        }

        var isSuccess = assigner.Best_Result != null;
        if (isSuccess)
        {
            // record results
            var result = assigner.Best_Result;
            if (amopCustomerId.HasValue)
            {
                RecordResults(context, result.QueueId, amopCustomerId.Value, commPlanGroupId, result, skipLowerCostCheck);
            }
            else
            {
                RecordResults(context, result.QueueId, accountNumber, commPlanGroupId, result, skipLowerCostCheck);
            }
        }

        foreach (long queueId in queueIds)
        {
            // stop queue
            StopQueue(context, queueId, isSuccess);
        }
    }
}

// Redis cache helper usage for continuation
// RedisCacheHelper.RecordPartialAssignerToCache(context, assigner) - saves state
// RedisCacheHelper.GetPartialAssignerFromCache(context, queueIds, billingTimeZone) - loads state  
// RedisCacheHelper.ClearPartialAssignerFromCache(context, queueIds) - cleans up
```