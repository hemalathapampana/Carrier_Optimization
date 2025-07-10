# Continuous Optimization Process - Step-by-Step Analysis

## Overview
This document provides a detailed breakdown of how continuous optimization works in the AltaworxSimCardCostOptimizer Lambda, explaining each step with What, Why, How, Algorithm, and Code locations.

---

## STEP 1: Lambda Handler Initialization

### **WHAT**: Initialize Lambda execution context and test Redis connectivity
### **WHY**: Establish foundation for potential continuation processing and validate cache availability
### **HOW**: Test Redis connection during Lambda startup and set flags for cache usage

### **ALGORITHM**:
```
START Lambda Handler
├── Initialize KeySysLambdaContext
├── Set SanityCheckTimeLimit (default: 180 seconds)
├── Test Redis Connection
│   ├── IF Redis configured AND reachable → IsUsingRedisCache = true
│   └── IF Redis configured BUT unreachable → IsUsingRedisCache = false, Log warning
├── Initialize repositories
└── Process SQS Event
```

### **CODE LOCATION**: Lines 40-65 in AltaworxSimCardCostOptimizer.cs
```csharp
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
```

---

## STEP 2: Message Processing Decision

### **WHAT**: Analyze SQS message to determine processing mode (Standard vs Continuation)
### **WHY**: Route to appropriate processing method based on whether this is initial or continuation processing
### **HOW**: Check message attributes for "IsChainingProcess" flag

### **ALGORITHM**:
```
Process SQS Message
├── Parse message attributes
├── Extract QueueIds, SkipLowerCostCheck, ChargeType
├── CHECK: Does message contain "IsChainingProcess" = true?
│   ├── YES → Continuation Processing Path
│   │   ├── Validate Redis connection available
│   │   ├── IF no Redis → Log error and STOP
│   │   └── IF Redis available → Call ProcessQueuesContinue()
│   └── NO → Standard Processing Path
│       └── Call ProcessQueues()
```

### **CODE LOCATION**: Lines 85-125 in AltaworxSimCardCostOptimizer.cs
```csharp
private async Task ProcessEventRecord(KeySysLambdaContext context, SQSEvent.SQSMessage message)
{
    LogInfo(context, "SUB", "ProcessEventRecord");
    
    // Parse message attributes
    if (message.MessageAttributes.ContainsKey("QueueIds"))
    {
        var messageId = message.MessageId;
        var queueIdStrings = message.MessageAttributes["QueueIds"].StringValue.Split(',').ToList();
        var queueIds = queueIdStrings.Select(long.Parse).ToList();
        
        // DECISION POINT: Check for continuation processing
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
}
```

---

## STEP 3A: Standard Processing Path (Initial Run)

### **WHAT**: Execute full optimization from beginning with timeout monitoring
### **WHY**: Perform complete optimization algorithm while being aware of Lambda execution time limits
### **HOW**: Load data, create optimizer, monitor remaining time, execute optimization

### **ALGORITHM**:
```
Standard Processing Path
├── Load queue and instance data
├── Initialize billing period and account info
├── Load SimCards data
│   ├── IF Redis available → Try cache first
│   └── IF cache miss → Load from database
├── Create RatePoolCollection
├── Monitor remaining Lambda execution time
├── Create RatePoolAssigner with timeout awareness
├── Execute AssignSimCards() optimization
└── Call WrapUpCurrentInstance() for completion/continuation logic
```

### **CODE LOCATION**: Lines 130-270 in AltaworxSimCardCostOptimizer.cs
```csharp
private async Task ProcessQueues(KeySysLambdaContext context, List<long> queueIds, string messageId, bool skipLowerCostCheck, OptimizationChargeType chargeType)
{
    LogInfo(context, "SUB", $"ProcessQueues(,,{messageId},{skipLowerCostCheck},{chargeType})");

    // Load initial data and configuration
    foreach (long queueId in queueIds)
    {
        var queue = GetQueue(context, queueId);
        // ... data loading logic ...
        
        if (isFirstId)
        {
            // Load SimCards with Redis caching
            if (IsUsingRedisCache)
            {
                simCards = RedisCacheHelper.GetSimCardsFromCache(context, instance.Id, commPlans, commPlanGroupId,
                    () => GetSimCardsByPortalType(context, instance, queue.ServiceProviderId, billingPeriod, instance.PortalType, commPlanGroupId, commPlans, optimizationGroups));
            }
            else
            {
                simCards = GetSimCardsByPortalType(context, instance, queue.ServiceProviderId, billingPeriod, instance.PortalType, commPlanGroupId, commPlans, optimizationGroups);
            }
        }
    }

    if (!isFirstId)
    {
        // TIMEOUT MONITORING
        var remainingSeconds = (int)Math.Floor(context.LambdaContext.RemainingTime.TotalSeconds);
        LogInfo(context, "INFO", $"Remaining run time: {remainingSeconds} seconds.");

        // Create timeout-aware optimizer
        var assigner = new RatePoolAssigner(string.Empty, ratePoolCollection, simCards, context.logger, SanityCheckTimeLimit, context.LambdaContext, IsUsingRedisCache,
            instance.PortalType, shouldFilterByRatePlanType, shouldPoolUsageBetweenRatePlans);
        
        // Execute optimization
        assigner.AssignSimCards(GetSimCardGroupingByPortalType(instance.PortalType, instance.IsCustomerOptimization),
            context.OptimizationSettings.BillingTimeZone, false, false, ratePoolSequences);

        await WrapUpCurrentInstance(context, queueIds, skipLowerCostCheck, chargeType, amopCustomerId, accountNumber, commPlanGroupId, assigner);
    }
}
```

---

## STEP 3B: Continuation Processing Path (Resume)

### **WHAT**: Resume optimization from saved Redis checkpoint
### **WHY**: Continue long-running optimization that exceeded Lambda timeout in previous execution
### **HOW**: Load saved RatePoolAssigner state from Redis and resume from checkpoint

### **ALGORITHM**:
```
Continuation Processing Path
├── Validate queue status (not already completed)
├── Load saved RatePoolAssigner from Redis cache
├── CHECK: Is cached state available?
│   ├── NO → Consider optimization complete, RETURN
│   └── YES → Continue processing
├── Set Lambda context and logger on resumed assigner
├── Call AssignSimCardsContinue() to resume optimization
└── Call WrapUpCurrentInstance() for completion/continuation logic
```

### **CODE LOCATION**: Lines 290-365 in AltaworxSimCardCostOptimizer.cs
```csharp
private async Task ProcessQueuesContinue(KeySysLambdaContext context, List<long> queueIds, string messageId, bool skipLowerCostCheck, OptimizationChargeType chargeType)
{
    LogInfo(context, "SUB", $"(,,{messageId},{skipLowerCostCheck},{chargeType})");

    if (queueIds.Count() <= 0)
    {
        LogInfo(context, "ERROR", $"No Queue Ids included. Stopping process.");
        return;
    }

    // Get reference queue for validation
    var referenceQueueId = queueIds.First();
    var queue = GetQueue(context, referenceQueueId);

    // Validate queue status
    if (QUEUE_FINISHED_STATUSES.Contains(queue.RunStatusId))
    {
        LogInfo(context, "WARNING", $"Duplicated queue processing request for queue with id {referenceQueueId}.");
        return;
    }

    // LOAD SAVED STATE FROM REDIS
    var assigner = RedisCacheHelper.GetPartialAssignerFromCache(context, queueIds, context.OptimizationSettings.BillingTimeZone);

    // if cache not found => consider done
    if (assigner == null)
    {
        return;
    }
    else
    {
        // RESUME PROCESSING
        assigner.SetLambdaContext(context.LambdaContext);
        assigner.SetLambdaLogger(context.logger);
        // call assignSimCardsContinue to continue the processing
        assigner.AssignSimCardsContinue(context.OptimizationSettings.BillingTimeZone, false);
    }
    
    await WrapUpCurrentInstance(context, queueIds, skipLowerCostCheck, chargeType, amopCustomerId, accountNumber, commPlanGroupId, assigner);
}
```

---

## STEP 4: Completion and Continuation Decision Logic

### **WHAT**: Determine if optimization is complete or needs continuation
### **WHY**: Handle Lambda timeout scenarios by saving progress and chaining to next execution
### **HOW**: Check completion status and either save results or save state for continuation

### **ALGORITHM**:
```
WrapUpCurrentInstance Decision Tree
├── CHECK: Is optimization completed?
│   ├── NO → Incomplete Processing
│   │   ├── CHECK: Is Redis cache available?
│   │   │   ├── YES → Save State for Continuation
│   │   │   │   ├── Save partial assigner to Redis cache
│   │   │   │   ├── Get remaining queue IDs
│   │   │   │   └── Enqueue continuation message with "IsChainingProcess" = true
│   │   │   └── NO → Log error (cannot continue without cache)
│   └── YES → Complete Processing
│       ├── Clear Redis cache (cleanup)
│       ├── Save optimization results to database
│       └── Mark queues as complete
```

### **CODE LOCATION**: Lines 365-400 in AltaworxSimCardCostOptimizer.cs
```csharp
private async Task WrapUpCurrentInstance(KeySysLambdaContext context, List<long> queueIds, bool skipLowerCostCheck, OptimizationChargeType chargeType, int? amopCustomerId, string accountNumber, long commPlanGroupId, RatePoolAssigner assigner)
{
    LogInfo(context, "SUB", $"(,{string.Join(',', queueIds)},)");
    
    // DECISION: Complete or Continue?
    if (!assigner.IsCompleted && context.IsRedisConnectionStringValid && IsUsingRedisCache)
    {
        // INCOMPLETE → SAVE STATE AND CONTINUE
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
        // COMPLETED → SAVE RESULTS AND CLEANUP
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
```

---

## STEP 5: Redis Cache Operations (Supporting Infrastructure)

### **WHAT**: Manage optimization state persistence in Redis for continuation
### **WHY**: Enable long-running optimizations to survive Lambda timeout constraints
### **HOW**: Use Redis cache helper methods to save/load/clear partial optimization state

### **ALGORITHM**:
```
Redis Cache Operations
├── RecordPartialAssignerToCache()
│   ├── Serialize current RatePoolAssigner state
│   ├── Store in Redis with queue-based key
│   └── Return remaining queue IDs for continuation
├── GetPartialAssignerFromCache()
│   ├── Retrieve serialized state from Redis
│   ├── Deserialize to RatePoolAssigner object
│   └── Return null if cache miss
└── ClearPartialAssignerFromCache()
    ├── Remove cached state from Redis
    └── Cleanup completion
```

### **CODE LOCATION**: Referenced but implementation in RedisCacheHelper class
```csharp
// Cache operations used in the flow:
var assigner = RedisCacheHelper.GetPartialAssignerFromCache(context, queueIds, context.OptimizationSettings.BillingTimeZone);
var remainingQueueIds = RedisCacheHelper.RecordPartialAssignerToCache(context, assigner);
RedisCacheHelper.ClearPartialAssignerFromCache(context, queueIds);
```

---

## STEP 6: SQS Message Chaining (Continuation Trigger)

### **WHAT**: Send continuation message to SQS queue to trigger next Lambda execution
### **WHY**: Chain Lambda executions to handle optimizations longer than single Lambda timeout
### **HOW**: Enqueue new SQS message with "IsChainingProcess" flag set to true

### **ALGORITHM**:
```
SQS Message Chaining
├── Create continuation message
├── Set message attributes:
│   ├── QueueIds = remaining queue IDs
│   ├── IsChainingProcess = true
│   ├── SkipLowerCostCheck = original value
│   └── ChargeType = original value
├── Send message to optimization queue
└── New Lambda will pick up message and enter Continuation Processing Path
```

### **CODE LOCATION**: Line 376 in AltaworxSimCardCostOptimizer.cs
```csharp
await EnqueueOptimizationContinueProcessAsync(context, remainingQueueIds, chargeType, skipLowerCostCheck);
```

---

## Complete Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                    CONTINUOUS OPTIMIZATION FLOW                              │
└─────────────────────────────────────────────────────────────────────────────┘

Lambda Start
     │
     ▼
┌─────────────────┐
│ STEP 1: Init   │──→ Test Redis Connection
│ Handler        │    Set IsUsingRedisCache flag
└─────────────────┘
     │
     ▼
┌─────────────────┐
│ STEP 2: Message│──→ Check "IsChainingProcess" attribute
│ Processing     │
└─────────────────┘
     │
     ▼
Is IsChainingProcess = true?
     │
     ├─NO──→┌──────────────────┐
     │      │ STEP 3A: Standard│──→ Load data, create optimizer
     │      │ Processing       │    Monitor timeout, execute optimization
     │      └──────────────────┘
     │           │
     └─YES──→┌──────────────────┐
             │ STEP 3B: Continue│──→ Load from Redis cache
             │ Processing       │    Resume optimization
             └──────────────────┘
                  │
                  ▼
             ┌─────────────────┐
             │ STEP 4: Wrap Up │──→ Is optimization complete?
             │ Decision        │
             └─────────────────┘
                  │
                  ▼
     Is Optimization Complete?
                  │
        ┌─NO──────┴──────YES─┐
        ▼                   ▼
┌──────────────────┐  ┌─────────────────┐
│ STEP 5: Save     │  │ Save Results    │
│ State to Redis   │  │ Mark Complete   │
└──────────────────┘  │ Clear Cache     │
        │             └─────────────────┘
        ▼                      │
┌──────────────────┐           │
│ STEP 6: Enqueue  │           │
│ Continue Message │           │
└──────────────────┘           │
        │                      │
        ▼                      │
   New Lambda ←─────────────────┘
   (Continuation)
```

---

## Key Features of Continuous Optimization

### **1. Timeout Awareness**
- **Code Location**: Line 246-247
- Monitors `context.LambdaContext.RemainingTime` 
- Uses `SanityCheckTimeLimit` for algorithm control

### **2. State Persistence** 
- **Code Location**: Lines 372, 346, 381
- Saves/loads `RatePoolAssigner` state to/from Redis
- Maintains optimization progress across executions

### **3. Message Chaining**
- **Code Location**: Line 376  
- Creates new SQS message with `IsChainingProcess=true`
- Triggers continuation in new Lambda instance

### **4. Graceful Degradation**
- **Code Location**: Lines 54-59
- Continues without cache if Redis unavailable
- Logs warnings but doesn't fail completely

### **5. Duplicate Protection**
- **Code Location**: Lines 35-40 (QUEUE_FINISHED_STATUSES)
- Prevents duplicate processing of completed queues
- Handles SQS "at-least-once" delivery guarantees

This continuous optimization mechanism ensures that large-scale optimization jobs can complete successfully despite Lambda's 15-minute execution time limit, while maintaining state consistency and providing robust error handling.