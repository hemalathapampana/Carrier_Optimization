# Result Processing Analysis

## Overview
This document analyzes the result processing workflow executed by the **AltaworxSimCardCostOptimizer Lambda** function for comparing strategy results, selecting optimal assignments, validating cost savings, recording optimization details, and updating queue status.

---

## 1. Compare Strategy Results

### Definition
**What**: Compares results from all four assignment strategies to identify the optimal device-to-rate-plan assignments.  
**Why**: Ensures the best possible cost optimization by evaluating multiple assignment approaches before selecting final recommendations.  
**How**: Executes all assignment strategies through RatePoolAssigner and retains the best performing result in Best_Result property.

### Algorithm
```
STEP 1: Execute All Assignment Strategies in AltaworxSimCardCostOptimizer Lambda
    Create RatePoolAssigner with optimization configuration
    Call assigner.AssignSimCards() with all strategy combinations
    Execute Strategy 1: No Grouping + Largest to Smallest
    Execute Strategy 2: No Grouping + Smallest to Largest
    Execute Strategy 3: Group by Communication Plan + Largest to Smallest  
    Execute Strategy 4: Group by Communication Plan + Smallest to Largest
    
STEP 2: Compare Strategy Performance
    Calculate total cost for each assignment strategy result
    Evaluate cost savings compared to current assignments
    Consider device coverage and assignment completeness
    Assess optimization effectiveness across strategies
    
STEP 3: Apply Strategy Selection Logic
    Compare cost outcomes across all executed strategies
    Factor in business rules and constraints
    Consider assignment feasibility and implementation complexity
    Account for communication plan consistency requirements
    
STEP 4: Identify Best Assignment Strategy
    Select strategy with optimal cost-to-benefit ratio
    Ensure selected strategy meets all business constraints
    Validate assignment completeness and device coverage
    Store winning strategy result in assigner.Best_Result
    
STEP 5: Prepare Result for Processing
    Extract optimal assignment details from best strategy
    Calculate final cost savings and optimization metrics
    Prepare assignment data for database recording
    Set success status based on result availability
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Strategy execution and comparison in ProcessQueues method
private async Task ProcessQueues(KeySysLambdaContext context, List<long> queueIds, string messageId, bool skipLowerCostCheck, OptimizationChargeType chargeType)
{
    // each run will have 4 sequential calculation with strategy based on a pair of attributes SimCardGrouping and RemainingAssignmentOrder
    // No Grouping + Largest To Smallest
    // No Grouping + Smallest To Largest
    // Group By Communication Plan + Largest To Smallest
    // Group By Communication Plan + Smallest To Largest
    // => stop at the first calculation if there is cache => continue with the next calculation on new lambda instance
    
    var assigner = new RatePoolAssigner(string.Empty, ratePoolCollection, simCards, context.logger, SanityCheckTimeLimit, context.LambdaContext, IsUsingRedisCache,
        instance.PortalType,
        shouldFilterByRatePlanType,
        shouldPoolUsageBetweenRatePlans);
    
    // Execute all strategies and compare results internally
    assigner.AssignSimCards(GetSimCardGroupingByPortalType(instance.PortalType, instance.IsCustomerOptimization),
                                context.OptimizationSettings.BillingTimeZone,
                                false,
                                false,
                                ratePoolSequences);

    // Result comparison and processing
    await WrapUpCurrentInstance(context, queueIds, skipLowerCostCheck, chargeType, amopCustomerId, accountNumber, commPlanGroupId, assigner);
}

// Strategy selection based on portal type
private static List<SimCardGrouping> GetSimCardGroupingByPortalType(PortalTypes portalType, bool isCustomerOptimization)
{
    if (portalType == PortalTypes.Mobility || isCustomerOptimization)
    {
        return new List<SimCardGrouping> { SimCardGrouping.NoGrouping };
    }
    else
    {
        return new List<SimCardGrouping> {
                SimCardGrouping.NoGrouping,
                SimCardGrouping.GroupByCommunicationPlan };
    }
}
```

---

## 2. Select Best Assignment

### Definition
**What**: Selects the optimal device assignment strategy result and extracts assignment details for implementation.  
**Why**: Provides the single best optimization recommendation from multiple strategy evaluations for business implementation.  
**How**: Accesses the Best_Result property from RatePoolAssigner and validates the assignment solution quality.

### Algorithm
```
STEP 1: Extract Best Result in AltaworxSimCardCostOptimizer Lambda
    Access assigner.Best_Result property after strategy execution
    Check if Best_Result contains valid assignment solution
    Verify that optimal strategy was successfully identified
    
STEP 2: Validate Assignment Quality
    Confirm Best_Result is not null (indicates successful optimization)
    Verify assignment covers all devices in optimization scope
    Check that selected rate plans are valid and available
    Ensure assignment meets business constraints
    
STEP 3: Determine Success Status
    Set isSuccess = true IF assigner.Best_Result != null
    Set isSuccess = false IF no optimal assignment found
    Log success or failure status for monitoring
    
STEP 4: Extract Assignment Details
    IF successful assignment found:
        Extract QueueId from result for tracking
        Get assigned rate plan details
        Calculate cost savings and optimization metrics
        Prepare assignment data for database storage
    ELSE:
        Log optimization failure reasons
        Prepare error handling and notification
        
STEP 5: Prepare for Implementation
    Format assignment details for database recording
    Calculate final cost impact and savings
    Prepare success/failure status for queue updates
    Set up data for optimization detail recording
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Best assignment selection in WrapUpCurrentInstance method
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

        // Select best assignment and determine success status
        var isSuccess = assigner.Best_Result != null;
        if (isSuccess)
        {
            // Extract best assignment details
            var result = assigner.Best_Result;
            // Continue with result processing...
        }
    }
}
```

---

## 3. Validate Cost Savings

### Definition
**What**: Validates that the selected assignment provides legitimate cost savings compared to current rate plan assignments.  
**Why**: Ensures optimization recommendations only proceed when they deliver actual financial benefits to customers.  
**How**: Uses skipLowerCostCheck flag to control cost validation logic and ensure savings meet minimum thresholds.

### Algorithm
```
STEP 1: Initialize Cost Validation in AltaworxSimCardCostOptimizer Lambda
    Read skipLowerCostCheck flag from SQS message attributes
    Set default skipLowerCostCheck = false if not specified
    Load current assignment costs for comparison baseline
    
STEP 2: Extract Cost Validation Flag
    Parse SkipLowerCostCheck from message attributes
    IF flag parsing fails:
        Set skipLowerCostCheck = false (default validation enabled)
        Proceed with standard cost validation rules
    Log cost validation configuration for tracking
    
STEP 3: Apply Cost Savings Validation Rules
    IF skipLowerCostCheck = false:
        Compare optimized assignment costs vs current costs
        Verify cost reduction meets minimum savings threshold
        Validate savings across all cost components (base, overage, fees, taxes)
        Ensure savings are sustainable and realistic
    ELSE:
        Skip cost validation checks (for testing/special cases)
        Proceed with assignment regardless of cost impact
        
STEP 4: Calculate Cost Impact Metrics
    Determine total cost savings from optimization
    Calculate percentage cost reduction achieved
    Assess impact on individual cost components
    Validate savings against expected optimization targets
    
STEP 5: Determine Validation Result
    IF cost savings validation passes:
        Proceed with assignment implementation
        Record validated savings metrics
    ELSE:
        Reject optimization result
        Log cost validation failure reasons
        Report insufficient savings to monitoring
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Cost validation flag extraction in ProcessEventRecord method
private async Task ProcessEventRecord(KeySysLambdaContext context, SQSEvent.SQSMessage message)
{
    LogInfo(context, "SUB", "ProcessEventRecord");
    LogInfo(context, "INFO", $"Message attributes: {string.Join(Environment.NewLine, message.MessageAttributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value.StringValue))}");
    
    // Extract cost validation configuration
    if (!message.MessageAttributes.ContainsKey("SkipLowerCostCheck") ||
        !bool.TryParse(message.MessageAttributes["SkipLowerCostCheck"].StringValue, out var skipLowerCostCheck))
    {
        skipLowerCostCheck = false;
    }

    // Pass cost validation flag through processing pipeline
    if (message.MessageAttributes.ContainsKey("IsChainingProcess")
        && bool.TryParse(message.MessageAttributes["IsChainingProcess"].StringValue, out var isChainingOptimization)
        && isChainingOptimization)
    {
        await ProcessQueuesContinue(context, queueIds, messageId, skipLowerCostCheck, chargeType);
    }
    else
    {
        await ProcessQueues(context, queueIds, messageId, skipLowerCostCheck, chargeType);
    }
}

// Cost validation applied in result recording
// skipLowerCostCheck flag passed to RecordResults method for validation logic
RecordResults(context, result.QueueId, amopCustomerId.Value, commPlanGroupId, result, skipLowerCostCheck);
```

---

## 4. Record Optimization Details

### Definition
**What**: Records comprehensive optimization details including assignment decisions, cost calculations, and performance metrics to database.  
**Why**: Provides audit trail, enables result analysis, and supports business reporting and optimization tracking.  
**How**: Calls RecordResults method with optimization result data, customer information, and validation settings.

### Algorithm
```
STEP 1: Prepare Optimization Details in AltaworxSimCardCostOptimizer Lambda
    Extract result details from assigner.Best_Result
    Get queue ID for optimization tracking
    Collect customer identification (AMOP customer ID or account number)
    Gather communication plan group information
    
STEP 2: Determine Customer Identification Method
    IF amopCustomerId has value:
        Use AMOP customer ID for result recording
        Call RecordResults with AMOP customer identification
    ELSE:
        Use account number for result recording
        Call RecordResults with account number identification
        
STEP 3: Record Assignment Details
    Save device-to-rate-plan assignment decisions
    Record cost calculations and savings metrics
    Store optimization strategy used (grouping and ordering)
    Save optimization execution metadata
    
STEP 4: Record Cost Analysis Details
    Store base cost calculations and proration details
    Record overage cost calculations and usage analysis
    Save regulatory fee and tax calculations
    Document total cost impact and savings achieved
    
STEP 5: Record Performance Metrics
    Save optimization execution time and performance data
    Record strategy comparison results and selection rationale
    Store device coverage and assignment completeness metrics
    Document any constraints or limitations encountered
    
STEP 6: Create Audit Trail
    Record optimization session tracking information
    Save result validation status and cost savings verification
    Store timestamp and execution context information
    Create searchable optimization history records
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Optimization detail recording in WrapUpCurrentInstance method
private async Task WrapUpCurrentInstance(KeySysLambdaContext context, List<long> queueIds, bool skipLowerCostCheck, OptimizationChargeType chargeType, int? amopCustomerId, string accountNumber, long commPlanGroupId, RatePoolAssigner assigner)
{
    // Select best assignment and determine success status
    var isSuccess = assigner.Best_Result != null;
    if (isSuccess)
    {
        // record results
        var result = assigner.Best_Result;
        
        // Record optimization details with customer identification
        if (amopCustomerId.HasValue)
        {
            RecordResults(context, result.QueueId, amopCustomerId.Value, commPlanGroupId, result, skipLowerCostCheck);
        }
        else
        {
            RecordResults(context, result.QueueId, accountNumber, commPlanGroupId, result, skipLowerCostCheck);
        }
    }

    // Update queue status for all processed queues
    foreach (long queueId in queueIds)
    {
        // stop queue
        StopQueue(context, queueId, isSuccess);
    }
}

// Result data includes:
// - result.QueueId: Queue identifier for tracking
// - amopCustomerId/accountNumber: Customer identification
// - commPlanGroupId: Communication plan group reference
// - result: Complete optimization result with assignments and costs
// - skipLowerCostCheck: Cost validation configuration
```

---

## 5. Update Queue Status

### Definition
**What**: Updates optimization queue status to reflect completion state and processing outcome for workflow coordination.  
**Why**: Enables system coordination, prevents duplicate processing, and tracks optimization progress across distributed components.  
**How**: Calls StopQueue method for each processed queue with success/failure status to update queue state.

### Algorithm
```
STEP 1: Determine Final Queue Status in AltaworxSimCardCostOptimizer Lambda
    Use isSuccess flag from best result validation
    Set success = true IF assigner.Best_Result != null
    Set success = false IF no optimal assignment found
    Prepare to update all processed queue IDs
    
STEP 2: Validate Queue Processing Completion
    Ensure all assignment strategies have completed execution
    Verify result recording has finished successfully
    Confirm optimization details have been saved
    Check for any pending continuation processing
    
STEP 3: Update Individual Queue Status
    FOR each queueId in processed queue list:
        Call StopQueue with queueId and success status
        Update queue status in database
        Set completion timestamp
        Record final processing outcome
        
STEP 4: Set Appropriate Completion Status
    IF success = true:
        Set queue status to OptimizationStatus.CompleteWithSuccess
        Record successful optimization completion
        Enable queue for result reporting
    ELSE:
        Set queue status to OptimizationStatus.CompleteWithErrors
        Record optimization failure details
        Mark queue for error analysis
        
STEP 5: Prevent Duplicate Processing
    Update queue status to finished state
    Add queue to QUEUE_FINISHED_STATUSES list:
        OptimizationStatus.CleaningUp
        OptimizationStatus.CompleteWithSuccess  
        OptimizationStatus.CompleteWithErrors
    Block future processing attempts on completed queues
    
STEP 6: Enable Workflow Coordination
    Signal completion to downstream processes
    Enable cleanup and reporting workflows
    Update optimization instance tracking
    Prepare for result aggregation and reporting
```

### Code Implementation
**Lambda**: AltaworxSimCardCostOptimizer

```csharp
// Queue status update in WrapUpCurrentInstance method
private async Task WrapUpCurrentInstance(KeySysLambdaContext context, List<long> queueIds, bool skipLowerCostCheck, OptimizationChargeType chargeType, int? amopCustomerId, string accountNumber, long commPlanGroupId, RatePoolAssigner assigner)
{
    // Determine final processing status
    var isSuccess = assigner.Best_Result != null;
    
    // Record optimization results if successful
    if (isSuccess)
    {
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

    // Update status for all processed queues
    foreach (long queueId in queueIds)
    {
        // stop queue with final success/failure status
        StopQueue(context, queueId, isSuccess);
    }
}

// Queue finished statuses that prevent duplicate processing
private static readonly List<OptimizationStatus> QUEUE_FINISHED_STATUSES = new List<OptimizationStatus>(){
    OptimizationStatus.CleaningUp,
    OptimizationStatus.CompleteWithSuccess,
    OptimizationStatus.CompleteWithErrors
};

// Duplicate processing prevention check
if (QUEUE_FINISHED_STATUSES.Contains(queue.RunStatusId))
{
    LogInfo(context, "WARNING", $"Duplicated queue processing request for queue with id {queueId}. Continue to process next queue.");
    continue;
}
```

---

## Result Processing Integration

### Complete Workflow Coordination
The **AltaworxSimCardCostOptimizer Lambda** coordinates result processing through:

1. **Strategy Execution** - Runs all 4 assignment strategies and compares outcomes
2. **Best Result Selection** - Identifies optimal assignment from strategy comparison  
3. **Cost Validation** - Applies skipLowerCostCheck logic to verify savings
4. **Detail Recording** - Saves comprehensive optimization results to database
5. **Status Updates** - Marks queues as complete to prevent duplicate processing

### Error Handling and Recovery
The system handles processing failures by:
- Checking assigner.IsCompleted for partial processing scenarios
- Using Redis cache for continuation across Lambda timeouts
- Setting appropriate error statuses for failed optimizations
- Preventing duplicate processing through status validation