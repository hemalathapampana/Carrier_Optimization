# AltaworxSimCardCostOptimizerCleanup: Compilation Process Analysis

## Executive Summary

The AltaworxSimCardCostOptimizerCleanup Lambda function is responsible for processing optimization results from completed optimization runs, selecting winning assignments for each communication group, compiling cost savings statistics, generating comprehensive Excel reports with device assignments, and cleaning up non-winning optimization results. This document provides a detailed analysis of the compilation process flow.

## Compilation Process Overview

The compilation process occurs in the `CleanupInstance` method and follows this sequence:

1. **Instance Validation & Preparation**
2. **Communication Group Processing**
3. **Winner Selection**
4. **Result Compilation by Portal Type**
5. **Statistics Generation**
6. **Excel Report Creation**
7. **Database Cleanup**
8. **Email Distribution**

## Detailed Process Flow

### 1. Instance Validation & Preparation (CleanupInstance)

The process begins with validation and setup:

```csharp
// Lines 299-356: Instance validation and initialization
private void CleanupInstance(KeySysLambdaContext context, long instanceId, 
    bool isCustomerOptimization, bool isLastInstance, int serviceProviderId)
{
    // Get and validate optimization instance
    var instance = GetInstance(context, instanceId);
    
    // Check for valid status - skip if already finished
    if (INSTANCE_FINISHED_STATUSES.Contains((OptimizationStatus)instance.RunStatusId))
    {
        LogInfo(context, "WARNING", $"Duplicated instance cleanup request for instance with id {instanceId}.");
        return;
    }
    
    // Get billing period and communication groups
    var carrierBillingPeriod = new BillingPeriod(...);
    var commGroups = GetCommGroups(context, instanceId);
    var integrationTypes = integrationTypeRepository.GetIntegrationTypes();
    
    var queueIds = new List<long>();
    
    // Process each communication group...
}
```

### 2. Communication Group Processing & Winner Selection

For each communication group, the system identifies the winning optimization queue:

```csharp
// Lines 336-352: Process each communication group
foreach (var commGroup in commGroups)
{
    // Select the winning queue (lowest total cost)
    var winningQueueId = GetWinningQueueId(context, commGroup.Id);
    
    // End all incomplete queues for this comm group
    EndQueuesForCommGroup(context, commGroup.Id);
    
    // Clean up all non-winning results
    CleanupDeviceResultsForCommGroup(context, commGroup.Id, winningQueueId);
    
    // Track winning queue for result compilation
    queueIds.Add(winningQueueId);
}
```

#### Winner Selection Algorithm (GetWinningQueueId)

```csharp
// Lines 2070-2093: Winner selection based on lowest total cost
protected long GetWinningQueueId(KeySysLambdaContext context, long commGroupId)
{
    using (var conn = new SqlConnection(context.ConnectionString))
    {
        using (var cmd = new SqlCommand("SELECT TOP 1 Id FROM OptimizationQueue " +
            "WHERE CommPlanGroupId = @commGroupId AND TotalCost IS NOT NULL " +
            "AND RunEndTime IS NOT NULL ORDER BY TotalCost ASC", conn))
        {
            // Returns the queue with the lowest total cost for the communication group
        }
    }
}
```

### 3. Result Compilation by Portal Type

The system compiles results differently based on the portal type (M2M, Mobility, or CrossProvider):

```csharp
// Lines 424-444: Portal-specific result writing
private OptimizationInstanceResultFile WriteResultByPortalType(
    KeySysLambdaContext context, bool isCustomerOptimization, 
    OptimizationInstance instance, BillingPeriod billingPeriod, 
    List<long> queueIds, bool usesProration)
{
    if (instance.PortalType == PortalTypes.Mobility)
    {
        return WriteMobilityResultsByOptimizationType(context, instance, queueIds, 
            billingPeriod, usesProration, isCustomerOptimization);
    }
    else if (instance.PortalType == PortalTypes.M2M)
    {
        return WriteM2MResults(context, instance, queueIds, 
            billingPeriod, usesProration, isCustomerOptimization);
    }
    else if (instance.PortalType == PortalTypes.CrossProvider)
    {
        return WriteCrossProviderCustomerResults(context, instance, queueIds, usesProration);
    }
}
```

### 4. M2M Results Compilation

For M2M portal type, the compilation process includes:

```csharp
// Lines 747-804: M2M result compilation
protected OptimizationInstanceResultFile WriteM2MResults(KeySysLambdaContext context, 
    OptimizationInstance instance, List<long> queueIds, BillingPeriod billingPeriod, 
    bool usesProration, bool isCustomerOptimization)
{
    M2MOptimizationResult result = new M2MOptimizationResult();
    M2MOptimizationResult crossCustomerResult = new M2MOptimizationResult();

    // Get rate pools for compilation
    var crossOptimizationResultRatePools = GetResultRatePools(context, instance, 
        billingPeriod, usesProration, queueIds, isCustomerOptimization);
    
    // Create customer-specific rate pools
    var optimizationResultRatePools = GenerateCustomerSpecificRatePools(crossOptimizationResultRatePools);
    
    // Add unassigned devices to a special rate pool
    AddUnassignedRatePool(context, instance, billingPeriod, usesProration, 
        crossOptimizationResultRatePools, optimizationResultRatePools);

    foreach (var queueId in queueIds)
    {
        // Get device results for each winning queue
        var deviceResults = GetM2MResults(context, new List<long>() { queueId }, billingPeriod);
        
        // Build optimization result
        result = BuildM2MOptimizationResult(deviceResults, optimizationResultRatePools, result);
        
        // Handle shared pool results for cross-customer optimization
        var sharedPoolDeviceResults = GetM2MSharedPoolResults(context, 
            new List<long>() { queueId }, billingPeriod);
        sharedPoolDeviceResults.AddRange(deviceResults);
        crossCustomerResult = BuildM2MOptimizationResult(sharedPoolDeviceResults, 
            crossOptimizationResultRatePools, crossCustomerResult, true);
    }

    // Generate statistics and assignment files...
}
```

### 5. Statistics Generation

The system generates comprehensive statistics using the `RatePoolStatisticsWriter`:

```csharp
// Lines 622, 780, 2307: Statistics generation across different portal types
var statFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(
    SimCardGrouping.GroupByCommunicationPlan, result);

// For shared pool scenarios
sharedPoolStatFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(
    SimCardGrouping.GroupByCommunicationPlan, crossCustomerResult);
```

### 6. Excel Report Generation

The final Excel reports are generated using the `RatePoolAssignmentWriter`:

```csharp
// Lines 632, 790, 2317: Excel file generation
var assignmentXlsxBytes = RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(
    statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes);
```

This method combines:
- **Statistics bytes**: Cost savings and optimization metrics
- **Assignment bytes**: Device-to-rate-plan assignments (text format)
- **Shared pool statistics**: Cross-customer optimization data (if applicable)
- **Shared pool assignments**: Cross-customer device assignments (if applicable)

### 7. Mobility Results Compilation

For Mobility portal type, there are two compilation paths:

#### Standard Mobility Customer Results

```csharp
// Lines 592-646: Standard mobility customer optimization
protected OptimizationInstanceResultFile WriteMobilityResults(KeySysLambdaContext context, 
    OptimizationInstance instance, List<long> queueIds, BillingPeriod billingPeriod, 
    bool usesProration, bool isCustomerOptimization)
{
    var result = new MobilityOptimizationResult();
    var crossCustomerResult = new MobilityOptimizationResult();
    
    // Get bill-in-advance queue (special handling for mobility)
    var billInAdvanceQueue = GetBillInAdvanceQueueFromInstance(context, instance.Id);
    
    // Process similar to M2M but with mobility-specific logic
    // Generate rate pools, compile results, create reports
}
```

#### Mobility Carrier Results

```csharp
// Lines 647-717: Carrier-level mobility optimization
protected OptimizationInstanceResultFile WriteMobilityCarrierResults(KeySysLambdaContext context, 
    OptimizationInstance instance, List<long> queueIds, BillingPeriod billingPeriod, bool usesProration)
{
    // Get carrier-specific rate plans and optimization groups
    var ratePlans = carrierRatePlanRepository.GetValidRatePlans(ParameterizedLog(context), 
        instance.ServiceProviderId.GetValueOrDefault());
    var optimizationGroups = carrierRatePlanRepository.GetValidOptimizationGroupsWithRatePlanIds(
        ParameterizedLog(context), instance.ServiceProviderId.GetValueOrDefault());

    // Group device results by optimization groups
    var deviceResultsByOptimizationGroups = deviceResults
        .Where(x => x.RatePlanTypeId != null && x.OptimizationGroupId != null)
        .GroupBy(x => x.OptimizationGroupId)
        .ToDictionary(x => x.Key, x => x.ToList());

    // Map devices to optimization groups and generate assignments
    foreach (var optimizationGroup in optimizationGroups)
    {
        // Calculate original costs and optimized assignments
        // Generate device assignments and summaries
    }

    // Create specialized Excel report for carrier optimization
    var assignmentXlsxBytes = RatePoolAssignmentWriter.WriteOptimizationResultSheet(
        deviceAssignments, summariesByRatePlans);
}
```

### 8. Cross-Provider Results Compilation

For cross-provider scenarios, the system handles multiple service providers:

```csharp
// Lines 2276-2331: Cross-provider customer optimization
protected OptimizationInstanceResultFile WriteCrossProviderCustomerResults(
    KeySysLambdaContext context, OptimizationInstance instance, List<long> queueIds, bool usesProration)
{
    var totalDeviceCount = 0;
    var customerBillingPeriod = crossProviderOptimizationRepository.GetBillingPeriod(
        ParameterizedLog(context), instance.AMOPCustomerId.GetValueOrDefault(), 
        instance.CustomerBillingPeriodId.GetValueOrDefault(), 
        context.OptimizationSettings.BillingTimeZone);
    
    // Reuse M2M optimization result model for cross-provider scenarios
    var result = new M2MOptimizationResult();
    var crossCustomerResult = new M2MOptimizationResult();

    // Get cross-provider rate pools
    var crossOptimizationResultRatePools = GetResultRatePools(context, instance, 
        customerBillingPeriod, usesProration, queueIds, isCustomerOptimization);

    foreach (var queueId in queueIds)
    {
        // Get cross-provider results
        var deviceResults = crossProviderOptimizationRepository.GetCrossProviderResults(
            ParameterizedLog(context), new List<long>() { queueId }, customerBillingPeriod);
        totalDeviceCount += deviceResults.Count;
        
        // Build optimization results
        result = BuildM2MOptimizationResult(deviceResults, optimizationResultRatePools, result);
        
        // Handle shared pool results across providers
        var sharedPoolDeviceResults = crossProviderOptimizationRepository
            .GetCrossProviderSharedPoolResults(ParameterizedLog(context), 
            new List<long>() { queueId }, customerBillingPeriod);
    }

    // Generate final Excel report
    var assignmentXlsxBytes = RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(
        statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes);
}
```

### 9. Database Cleanup Process

The cleanup process removes non-winning optimization results:

```csharp
// Lines 2114-2135: Cleanup non-winning device results
private void CleanupDeviceResultsForCommGroup(KeySysLambdaContext context, 
    long commGroupId, long queueId)
{
    using (var conn = new SqlConnection(context.ConnectionString))
    {
        using (var cmd = new SqlCommand("usp_Optimization_DeviceResultAndQueueRatePlan_Cleanup", conn))
        {
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.Parameters.AddWithValue("@commGroupId", commGroupId);
            cmd.Parameters.AddWithValue("@winningQueueId", queueId);
            cmd.CommandTimeout = 900; // 15-minute timeout for large cleanups
            
            cmd.ExecuteNonQuery();
        }
    }
}
```

This stored procedure:
- Deletes device results from non-winning queues
- Removes queue-specific rate plan mappings
- Preserves only the winning optimization results

### 10. Result Distribution

After compilation, results are distributed based on optimization type:

#### Single Service Provider Distribution

```csharp
// Lines 376-423: Single provider result processing
private void ProcessResultForSingleServiceProvider(KeySysLambdaContext context, 
    bool isCustomerOptimization, bool isLastInstance, int serviceProviderId, 
    OptimizationInstance instance, IList<IntegrationTypeModel> integrationTypes, 
    OptimizationInstanceResultFile fileResult)
{
    var integrationType = (IntegrationType)instance.IntegrationId.GetValueOrDefault();
    var syncResults = GetSummaryValues(context, integrationType, 
        instance.ServiceProviderId.GetValueOrDefault());

    if (isCustomerOptimization)
    {
        OptimizationCustomerSendResults(context, instance, syncResults, isLastInstance, serviceProviderId);
    }
    else
    {
        SendResults(context, instance, fileResult.AssignmentXlsxBytes, billingTimeZone,
            syncResults, integrationType, integrationTypes);
    }

    // Queue automatic rate plan updates if configured
    if (CanAutoUpdateRatePlans && HasTimeForUpdates)
    {
        QueueRatePlanUpdates(context, instance.Id, instance.TenantId);
        SendGoForRatePlanUpdatesEmail(context, instance, billingTimeZone);
    }
}
```

#### Cross-Provider Distribution

```csharp
// Lines 2346-2361: Cross-provider result processing
private void ProcessResultForCrossProvider(KeySysLambdaContext context, 
    bool isCustomerOptimization, bool isLastInstance, OptimizationInstance instance, 
    OptimizationInstanceResultFile fileResult)
{
    if (isCustomerOptimization)
    {
        var customer = GetRevCustomerById(context, instance.RevCustomerId.Value);
        crossProviderOptimizationRepository.UpdateProcessingCustomerOptimizationInstance(
            ParameterizedLog(context), instance.SessionId.GetValueOrDefault(), 
            instance.Id, null, fileResult.TotalDeviceCount, false, 
            instance.CustomerType, customer.RevCustomerId, instance.AMOPCustomerId);
        
        if (isLastInstance)
        {
            // Send message to cleanup and send optimization email
            QueueLastStepOptCustomerCleanup(context, instance.Id, 
                instance.SessionId.Value, true, 0, _optCustomerCleanUpDelaySeconds);
        }
    }
}
```

## Key Components and Their Roles

### Rate Pool Management

**Rate Pools** represent collections of devices assigned to specific rate plans:

- **Result Rate Pools**: Final optimized assignments
- **Cross-Customer Rate Pools**: Shared pools across multiple customers
- **Unassigned Rate Pools**: Devices that couldn't be optimized

### Statistics Compilation

The `RatePoolStatisticsWriter` generates:

- **Cost Savings Analysis**: Before vs. after optimization costs
- **Device Distribution**: How devices are allocated across rate plans
- **Usage Patterns**: Data consumption and overage analysis
- **Optimization Metrics**: Success rates and improvement percentages

### Excel Report Structure

The generated Excel reports contain multiple tabs:

1. **Summary Tab**: High-level optimization statistics
2. **Device Assignments Tab**: Detailed device-to-rate-plan mappings
3. **Shared Pool Tab** (if applicable): Cross-customer optimization results
4. **Cost Analysis Tab**: Before/after cost comparisons

## Error Handling and Retry Logic

### Cleanup Retry Mechanism

```csharp
// Lines 2220-2260: Cleanup retry logic
private void RequeueCleanup(KeySysLambdaContext context, long instanceId, 
    int retryCount, int optimizationQueueLength, bool isCustomerOptimization)
{
    retryCount += 1;
    int delaySeconds = DelaySecondsFromQueueLength(optimizationQueueLength);
    
    // Requeue with exponential backoff based on queue length
    var request = new SendMessageRequest
    {
        DelaySeconds = delaySeconds, // Up to 15 minutes max in SQS
        MessageAttributes = new Dictionary<string, MessageAttributeValue>
        {
            { "InstanceId", new MessageAttributeValue { StringValue = instanceId.ToString() }},
            { "RetryCount", new MessageAttributeValue { StringValue = retryCount.ToString() }},
            { "IsCustomerOptimization", new MessageAttributeValue { StringValue = isCustomerOptimization.ToString() }}
        },
        QueueUrl = context.CleanupDestinationQueueUrl
    };
}
```

### Queue Length-Based Delays

```csharp
// Lines 2261-2275: Dynamic delay calculation
private int DelaySecondsFromQueueLength(int optimizationQueueLength)
{
    var delaySeconds = 600; // Default 10-minute delay
    
    if (optimizationQueueLength > 50)
    {
        delaySeconds = 900; // 15-minute delay for heavy load
    }
    
    return delaySeconds;
}
```

## Performance Considerations

### Database Timeouts

- **Cleanup Operations**: 15-minute timeout for large data cleanup
- **Result Queries**: Optimized queries with appropriate indexing
- **Statistics Generation**: Batch processing for large datasets

### Memory Management

- **Streaming Data**: Large result sets processed in batches
- **Excel Generation**: Efficient byte array handling
- **Rate Pool Collections**: Optimized data structures for device assignments

### Concurrency Handling

- **Queue-Based Processing**: SQS ensures ordered processing
- **Database Locking**: `WITH (HOLDLOCK)` for critical updates
- **Retry Logic**: Handles temporary failures and heavy load scenarios

## Monitoring and Logging

The compilation process includes comprehensive logging:

- **Process Milestones**: Key stages logged with execution times
- **Error Conditions**: Detailed error messages with context
- **Performance Metrics**: Queue lengths, processing times, device counts
- **Business Metrics**: Cost savings, optimization success rates

## Conclusion

The AltaworxSimCardCostOptimizerCleanup compilation process is a sophisticated system that:

1. **Selects optimal results** from multiple optimization runs
2. **Compiles comprehensive statistics** on cost savings and device optimization
3. **Generates detailed Excel reports** for business users
4. **Cleans up intermediate data** to maintain database performance
5. **Distributes results** through multiple channels (email, database, queues)

The system handles multiple portal types (M2M, Mobility, CrossProvider) and optimization scenarios (single customer, cross-customer, carrier-level) while maintaining data integrity and providing robust error handling and retry mechanisms.

## Algorithmic Breakdown: What, Why, and How

### 1. Winner Selection for Communication Groups

#### WHAT:
Identifies the optimal optimization queue for each communication group based on lowest total cost.

#### WHY:
- Multiple optimization algorithms run simultaneously for the same devices
- Need to select the best performing optimization result
- Ensures cost-effective rate plan assignments for customers

#### HOW - Algorithm:
```
Algorithm: SelectWinningQueue
Input: communicationGroupId
Output: winningQueueId

1. FOR each communicationGroup in optimizationInstance:
   a. Query all completed optimization queues for the group
   b. Filter queues with valid TotalCost and RunEndTime
   c. Sort by TotalCost in ascending order
   d. SELECT TOP 1 queue with lowest cost
   e. Return queueId as winner

2. Store winningQueueIds for result compilation
```

#### Code Location:
```csharp
// Lines 336-352: CleanupInstance method - Communication group processing
foreach (var commGroup in commGroups)
{
    var winningQueueId = GetWinningQueueId(context, commGroup.Id);
    // ... process winning queue
    queueIds.Add(winningQueueId);
}

// Lines 2070-2093: Winner selection algorithm
protected long GetWinningQueueId(KeySysLambdaContext context, long commGroupId)
{
    using (var cmd = new SqlCommand("SELECT TOP 1 Id FROM OptimizationQueue " +
        "WHERE CommPlanGroupId = @commGroupId AND TotalCost IS NOT NULL " +
        "AND RunEndTime IS NOT NULL ORDER BY TotalCost ASC", conn))
    {
        // Returns queue with lowest total cost
    }
}
```

### 2. Cost Savings and Optimization Statistics Compilation

#### WHAT:
Calculates comprehensive optimization metrics including cost savings, device distributions, and usage patterns.

#### WHY:
- Provides business value quantification of optimization efforts
- Enables tracking of optimization effectiveness over time
- Supports decision-making for future optimization strategies

#### HOW - Algorithm:
```
Algorithm: CompileOptimizationStatistics
Input: winningQueueIds, billingPeriod, portalType
Output: optimizationStatistics

1. Initialize result containers:
   a. Create M2MOptimizationResult OR MobilityOptimizationResult
   b. Create crossCustomerResult for shared pool analysis

2. FOR each portalType (M2M, Mobility, CrossProvider):
   a. Get rate plans for the billing period
   b. Create rate pool mappings from optimization results
   c. Generate customer-specific and cross-customer rate pools

3. FOR each winningQueueId:
   a. Retrieve device optimization results from database
   b. Calculate original costs (before optimization)
   c. Calculate optimized costs (after optimization)
   d. Assign devices to appropriate rate pools
   e. Aggregate cost savings per rate pool

4. Generate statistics:
   a. Total cost savings = originalCost - optimizedCost
   b. Device distribution per rate plan
   c. Usage patterns and overage analysis
   d. Optimization success percentage

5. Create shared pool analysis (if applicable):
   a. Include cross-customer optimization opportunities
   b. Calculate additional savings from shared pools

6. Serialize statistics to byte arrays for Excel generation
```

#### Code Location:
```csharp
// Lines 747-804: M2M statistics compilation
protected OptimizationInstanceResultFile WriteM2MResults(KeySysLambdaContext context, 
    OptimizationInstance instance, List<long> queueIds, BillingPeriod billingPeriod, 
    bool usesProration, bool isCustomerOptimization)
{
    // Get rate pools for compilation
    var crossOptimizationResultRatePools = GetResultRatePools(context, instance, 
        billingPeriod, usesProration, queueIds, isCustomerOptimization);
    
    foreach (var queueId in queueIds)
    {
        var deviceResults = GetM2MResults(context, new List<long>() { queueId }, billingPeriod);
        result = BuildM2MOptimizationResult(deviceResults, optimizationResultRatePools, result);
    }
}

// Lines 622, 780, 2307: Statistics generation
var statFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(
    SimCardGrouping.GroupByCommunicationPlan, result);
```

### 3. Excel Report Generation with Device Assignments

#### WHAT:
Creates comprehensive Excel workbooks containing optimization statistics, device assignments, and cost analysis.

#### WHY:
- Provides user-friendly visualization of optimization results
- Enables detailed analysis of device-to-rate-plan assignments
- Supports business reporting and audit requirements

#### HOW - Algorithm:
```
Algorithm: GenerateExcelReports
Input: optimizationStatistics, deviceAssignments, sharedPoolData
Output: excelWorkbookBytes

1. Prepare data components:
   a. Statistics bytes = RatePoolStatisticsWriter.WriteRatePoolStatistics()
   b. Assignment bytes = RatePoolAssignmentWriter.WriteRatePoolAssignments()
   c. SharedPool statistics (if cross-customer optimization exists)
   d. SharedPool assignments (if cross-customer optimization exists)

2. Create Excel workbook structure:
   Sheet 1: "Summary Statistics"
   - Total cost savings
   - Device count by rate plan
   - Optimization success metrics
   
   Sheet 2: "Device Assignments"
   - ICCID to Rate Plan mappings
   - Original vs. Optimized rate plans
   - Cost savings per device
   
   Sheet 3: "Shared Pool Analysis" (conditional)
   - Cross-customer optimization opportunities
   - Additional savings potential

3. Populate Excel sheets:
   a. Convert byte arrays to structured data
   b. Apply formatting and styling
   c. Add charts and visualizations
   d. Include metadata (billing period, execution time)

4. Generate final Excel file:
   a. Combine all sheets into single workbook
   b. Compress and optimize file size
   c. Return as byte array for storage/transmission
```

#### Code Location:
```csharp
// Lines 632, 790, 2317: Excel generation across portal types
var assignmentXlsxBytes = RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(
    statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes);

// Lines 647-717: Mobility carrier-specific Excel generation
var assignmentXlsxBytes = RatePoolAssignmentWriter.WriteOptimizationResultSheet(
    deviceAssignments, summariesByRatePlans);

// Lines 592-646: Standard mobility Excel generation
var assignmentXlsxBytes = RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(
    statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes);
```

### 4. Non-Winning Optimization Results Cleanup

#### WHAT:
Removes optimization results from non-winning queues to maintain database performance and storage efficiency.

#### WHY:
- Prevents database bloat from multiple optimization attempts
- Maintains only the best optimization results for each communication group
- Improves query performance for future operations

#### HOW - Algorithm:
```
Algorithm: CleanupNonWinningResults
Input: communicationGroupId, winningQueueId
Output: cleanupStatus

1. FOR each communicationGroup:
   a. Get winningQueueId from winner selection algorithm
   b. Identify all other queues for the same communication group
   c. Mark non-winning queues for cleanup

2. Execute cleanup operations:
   a. Call stored procedure: usp_Optimization_DeviceResultAndQueueRatePlan_Cleanup
   b. Parameters: @commGroupId, @winningQueueId
   c. Timeout: 900 seconds (15 minutes) for large datasets

3. Cleanup operations performed by stored procedure:
   a. DELETE device results WHERE queueId != winningQueueId
   b. DELETE rate plan mappings WHERE queueId != winningQueueId
   c. UPDATE queue status to 'CompleteWithErrors' for non-winners
   d. PRESERVE only winning queue results

4. End non-winning queues:
   a. UPDATE OptimizationQueue SET RunEndTime = GETUTCDATE()
   b. SET RunStatusId = CompleteWithErrors
   c. SET TotalCost = NULL (invalidate non-winning costs)

5. Verify cleanup completion:
   a. Check for any remaining non-winning results
   b. Log cleanup statistics
   c. Handle any cleanup errors or timeouts
```

#### Code Location:
```csharp
// Lines 341-347: Cleanup orchestration in CleanupInstance
foreach (var commGroup in commGroups)
{
    var winningQueueId = GetWinningQueueId(context, commGroup.Id);
    EndQueuesForCommGroup(context, commGroup.Id);
    CleanupDeviceResultsForCommGroup(context, commGroup.Id, winningQueueId);
    queueIds.Add(winningQueueId);
}

// Lines 2094-2113: End non-winning queues
private void EndQueuesForCommGroup(KeySysLambdaContext context, long commGroupId)
{
    using (var cmd = new SqlCommand("UPDATE OptimizationQueue WITH (HOLDLOCK) " +
        "SET RunEndTime = GETUTCDATE(), RunStatusId = @runStatusId, TotalCost = NULL " +
        "WHERE CommPlanGroupId = @commGroupId AND RunEndTime IS NULL", conn))
    {
        cmd.Parameters.AddWithValue("@runStatusId", (int)OptimizationStatus.CompleteWithErrors);
        cmd.ExecuteNonQuery();
    }
}

// Lines 2114-2135: Cleanup device results
private void CleanupDeviceResultsForCommGroup(KeySysLambdaContext context, 
    long commGroupId, long queueId)
{
    using (var cmd = new SqlCommand("usp_Optimization_DeviceResultAndQueueRatePlan_Cleanup", conn))
    {
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.Parameters.AddWithValue("@commGroupId", commGroupId);
        cmd.Parameters.AddWithValue("@winningQueueId", queueId);
        cmd.CommandTimeout = 900; // 15-minute timeout
        cmd.ExecuteNonQuery();
    }
}
```

## Complete Compilation Process Algorithm

```
Algorithm: OptimizationCompilationProcess
Input: instanceId, isCustomerOptimization, isLastInstance, serviceProviderId
Output: compiledOptimizationResults

1. VALIDATE and PREPARE:
   a. Get optimization instance by instanceId
   b. Check if instance status allows processing
   c. Get billing period and communication groups
   d. Initialize result containers

2. PROCESS COMMUNICATION GROUPS:
   FOR each communicationGroup in instance:
       a. Execute SelectWinningQueue algorithm
       b. Store winningQueueId for compilation
       c. Execute CleanupNonWinningResults algorithm

3. COMPILE OPTIMIZATION RESULTS:
   a. Determine portal type (M2M, Mobility, CrossProvider)
   b. Execute CompileOptimizationStatistics algorithm
   c. Build rate pool collections
   d. Calculate cost savings and device distributions

4. GENERATE REPORTS:
   a. Execute GenerateExcelReports algorithm
   b. Create statistics and assignment byte arrays
   c. Combine into final Excel workbook

5. FINALIZE PROCESSING:
   a. Save result file to database
   b. Update instance status to CompleteWithSuccess
   c. Distribute results via email/queues
   d. Queue rate plan updates (if applicable)

6. ERROR HANDLING:
   IF any step fails:
       a. Log error details
       b. Execute RequeueCleanup algorithm
       c. Apply exponential backoff delay
       d. Retry up to maximum attempts
```

## Portal-Specific Algorithm Variations

### M2M Portal Algorithm:
```
1. Get M2M rate plans for billing period
2. Create customer-specific and cross-customer rate pools
3. Process winning queue results
4. Build M2M optimization result objects
5. Generate shared pool analysis for cross-customer opportunities
6. Create Excel with M2M-specific formatting
```

### Mobility Portal Algorithm:
```
1. Check optimization type (customer vs. carrier)
2. IF customer optimization:
   - Get bill-in-advance queue
   - Process similar to M2M with mobility-specific logic
3. IF carrier optimization:
   - Get optimization groups and rate plans
   - Group devices by optimization groups
   - Calculate original vs. optimized assignments
   - Generate carrier-specific Excel report
```

### CrossProvider Portal Algorithm:
```
1. Get cross-provider billing period
2. Retrieve rate pools from multiple service providers
3. Process results from multiple winning queues
4. Handle shared pool results across providers
5. Update cross-provider customer processing status
6. Queue final email cleanup if last instance
```

## Key Data Structures and Flow

### Rate Pool Structure:
```
ResultRatePool {
    RatePlan ratePlan
    Dictionary<string, SimCard> simCards
    decimal totalCost
    decimal totalSavings
    int deviceCount
    bool isSharedPool
}
```

### Optimization Result Structure:
```
OptimizationResult {
    RatePoolCollection combinedRatePools
    decimal totalOriginalCost
    decimal totalOptimizedCost
    decimal totalSavings
    int totalDeviceCount
    long queueId
}
```

This algorithmic breakdown provides a clear understanding of each compilation process step with specific code locations and implementation details.

## M2M and Mobility Report Generation: Algorithmic Breakdown

### M2M Reports

#### 1. Device Assignment Spreadsheets

##### WHAT:
Generates detailed Excel spreadsheets containing device-to-rate-plan assignments with ICCID mappings, usage data, and cost information.

##### WHY:
- Provides detailed visibility into which devices are assigned to which rate plans
- Enables tracking of individual device optimization decisions
- Supports audit and compliance requirements for M2M deployments
- Allows customers to understand per-device cost savings

##### HOW - Algorithm:
```
Algorithm: GenerateM2MDeviceAssignmentSpreadsheet
Input: winningQueueIds, billingPeriod, optimizationInstance
Output: deviceAssignmentExcelBytes

1. RETRIEVE M2M DEVICE RESULTS:
   a. FOR each winningQueueId:
      - Query OptimizationDeviceResult table
      - JOIN with Device, JasperCommunicationPlan, RatePlan tables
      - Extract: DeviceId, ICCID, MSISDN, UsageMB, RatePlanCode, ChargeAmt
      - Calculate billing period activation status

2. BUILD RATE POOL COLLECTIONS:
   a. Get customer-specific rate pools
   b. Create cross-customer rate pools for shared optimization
   c. Add unassigned rate pool for devices that couldn't be optimized

3. ASSIGN DEVICES TO RATE POOLS:
   a. FOR each deviceResult:
      - Find matching rate pool by RatePlanId
      - Add device to appropriate rate pool
      - Calculate cost savings (original vs optimized)

4. GENERATE ASSIGNMENT DATA:
   a. Create assignment text file with device mappings
   b. Include: ICCID, Original Rate Plan, New Rate Plan, Cost Savings
   c. Format for Excel consumption

5. CREATE EXCEL WORKBOOK:
   a. Convert assignment data to Excel format
   b. Add formatting and headers
   c. Include multiple sheets if shared pools exist
```

##### Code Location:
```csharp
// Lines 747-804: Main M2M results compilation
protected OptimizationInstanceResultFile WriteM2MResults(KeySysLambdaContext context, 
    OptimizationInstance instance, List<long> queueIds, BillingPeriod billingPeriod, 
    bool usesProration, bool isCustomerOptimization)

// Lines 834-898: Device results retrieval
private List<SimCardResult> GetM2MResults(KeySysLambdaContext context, 
    List<long> queueIds, BillingPeriod billingPeriod)
{
    using (var cmd = new SqlCommand(@"
        SELECT device.[Id] AS DeviceId, [UsageMB], device.[ICCID], device.[MSISDN],
               ISNULL(commPlan.[AliasName], device.[CommunicationPlan]) AS CommunicationPlan, 
               ISNULL(carrierPlan.[RatePlanCode], customerPlan.[RatePlanCode]) AS RatePlanCode, 
               ISNULL(deviceResult.[AssignedCustomerRatePlanId], deviceResult.[AssignedCarrierRatePlanId]) AS RatePlanId
        FROM OptimizationDeviceResult deviceResult 
        INNER JOIN Device device ON deviceResult.[AmopDeviceId] = device.[Id]", conn))
}

// Lines 782-785: Assignment file generation
var assignmentFileBytes = RatePoolAssignmentWriter.WriteRatePoolAssignments(result);
var assignmentXlsxBytes = RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(
    statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes);
```

#### 2. Cost Savings Summaries

##### WHAT:
Compiles comprehensive cost savings analysis showing before/after optimization costs, savings percentages, and financial impact.

##### WHY:
- Quantifies the business value of optimization efforts
- Provides ROI justification for optimization investments
- Enables tracking of optimization performance over time
- Supports financial reporting and budgeting decisions

##### HOW - Algorithm:
```
Algorithm: GenerateM2MCostSavingsSummary
Input: optimizationResults, billingPeriod
Output: costSavingsStatistics

1. CALCULATE ORIGINAL COSTS:
   a. FOR each device in optimization results:
      - Get original rate plan assignment
      - Calculate base rate + usage overage charges
      - Include SMS charges if applicable
      - Sum total original cost per device

2. CALCULATE OPTIMIZED COSTS:
   a. FOR each device in optimization results:
      - Get optimized rate plan assignment
      - Calculate new base rate + usage overage charges
      - Include SMS charges with new rate plan
      - Sum total optimized cost per device

3. COMPUTE SAVINGS METRICS:
   a. Total savings = sum(originalCost - optimizedCost)
   b. Savings percentage = (totalSavings / totalOriginalCost) * 100
   c. Average savings per device = totalSavings / deviceCount
   d. Cost reduction by rate plan category

4. GENERATE SUMMARY STATISTICS:
   a. Group savings by rate pool
   b. Calculate utilization improvements
   c. Identify optimization success rates
   d. Create cost distribution analysis

5. FORMAT FOR REPORTING:
   a. Create statistics byte array
   b. Include charts and visualizations
   c. Add executive summary metrics
```

##### Code Location:
```csharp
// Lines 777-780: Cost savings statistics generation
var statFileBytes = RatePoolStatisticsWriter.WriteRatePoolStatistics(
    SimCardGrouping.GroupByCommunicationPlan, result);

// Lines 1377-1390: M2M optimization result building
private M2MOptimizationResult BuildM2MOptimizationResult(List<SimCardResult> deviceResults, 
    List<ResultRatePool> ratePools, M2MOptimizationResult result, bool shouldSkipAutoChangeRatePlan = false)
{
    result.QueueId = deviceResults.FirstOrDefault()?.QueueId ?? 0;
    AddSimCardsToResultRatePools(deviceResults, ratePools, shouldSkipAutoChangeRatePlan);
    result.CombinedRatePools = new RatePoolCollection(ratePools);
    return result;
}

// Lines 1073-1128: SimCard result parsing with cost data
private SimCardResult SimCardResultFromReader(SqlDataReader rdr, BillingPeriod billingPeriod)
{
    // Extracts: ChargeAmt, BaseRateAmount, RateChargeAmount, OverageChargeAmount
    // Calculates billing period activation and cost allocation
}
```

#### 3. Rate Plan Utilization Statistics

##### WHAT:
Analyzes how devices are distributed across rate plans and identifies utilization patterns and optimization opportunities.

##### WHY:
- Identifies underutilized or overutilized rate plans
- Supports rate plan portfolio optimization decisions
- Enables capacity planning for future device deployments
- Helps identify opportunities for shared pool optimization

##### HOW - Algorithm:
```
Algorithm: GenerateM2MRatePlanUtilizationStatistics
Input: optimizationResults, ratePlans, billingPeriod
Output: utilizationStatistics

1. ANALYZE DEVICE DISTRIBUTION:
   a. GROUP devices by rate plan
   b. COUNT devices per rate plan
   c. CALCULATE utilization percentage per rate plan
   d. IDENTIFY over/under-utilized plans

2. CALCULATE USAGE PATTERNS:
   a. FOR each rate plan:
      - Average data usage per device
      - Peak usage periods
      - Overage frequency and amounts
      - Device activation patterns

3. ASSESS OPTIMIZATION EFFECTIVENESS:
   a. Before/after rate plan assignments
   b. Device migration patterns
   c. Consolidation opportunities identified
   d. Shared pool utilization rates

4. GENERATE UTILIZATION METRICS:
   a. Rate plan efficiency scores
   b. Capacity utilization percentages
   c. Cost per MB by rate plan
   d. Optimization success rates by plan

5. CREATE REPORTING DATA:
   a. Utilization charts and graphs
   b. Trend analysis over time
   c. Recommendations for plan adjustments
```

##### Code Location:
```csharp
// Lines 1150-1174: Rate pool creation and utilization tracking
private List<ResultRatePool> GetResultRatePools(KeySysLambdaContext context, 
    OptimizationInstance instance, BillingPeriod billingPeriod, bool usesProration, 
    List<long> queueIds, bool isCustomerOptimization)

// Lines 1201-1221: Rate pool generation from rate plans
private static List<ResultRatePool> GenerateResultRatePoolFromRatePlans(
    BillingPeriod billingPeriod, bool usesProration, List<RatePlan> ratePlans, 
    List<RatePlanPoolMapping> planPoolMappings, bool isSharedRatePool, OptimizationInstance instance)

// Lines 1391-1433: Device assignment to rate pools
private static void AddSimCardsToResultRatePools(List<SimCardResult> deviceResults, 
    List<ResultRatePool> ratePools, bool shouldSkipAutoChangeRatePlan = false)
{
    foreach (var deviceResult in deviceResults)
    {
        var targetRatePool = ratePools.FirstOrDefault(r => r.RatePlan.Id == deviceResult.RatePlanId);
        if (targetRatePool != null && (!shouldSkipAutoChangeRatePlan || !targetRatePool.RatePlan.AutoChangeRatePlan))
        {
            targetRatePool.AddSimCard(deviceResult);
        }
    }
}
```

#### 4. Optimization Group Details

##### WHAT:
Provides detailed analysis of optimization groups, showing how devices are organized and optimized within logical groupings.

##### WHY:
- Enables understanding of optimization logic and grouping strategies
- Supports troubleshooting of optimization decisions
- Provides transparency into the optimization algorithm behavior
- Helps identify optimization group effectiveness

##### HOW - Algorithm:
```
Algorithm: GenerateM2MOptimizationGroupDetails
Input: optimizationResults, communicationGroups
Output: optimizationGroupDetails

1. ORGANIZE BY COMMUNICATION GROUPS:
   a. GROUP devices by communication plan
   b. IDENTIFY optimization groups within each comm plan
   c. MAP devices to their assigned optimization groups

2. ANALYZE GROUP PERFORMANCE:
   a. FOR each optimization group:
      - Count devices in group
      - Calculate group cost savings
      - Identify rate plan assignments within group
      - Assess group optimization success rate

3. GENERATE GROUP METRICS:
   a. Group size distribution
   b. Cost savings by group
   c. Rate plan diversity within groups
   d. Cross-group optimization opportunities

4. CREATE DETAILED REPORTING:
   a. Group assignment tables
   b. Performance metrics by group
   c. Optimization decision explanations
   d. Group efficiency analysis
```

##### Code Location:
```csharp
// Lines 805-820: Unassigned rate pool management
private static void AddUnassignedRatePool(KeySysLambdaContext context, 
    OptimizationInstance instance, BillingPeriod billingPeriod, bool usesProration, 
    List<ResultRatePool> crossOptimizationResultRatePools, 
    List<ResultRatePool> optimizationResultRatePools = null)

// Lines 821-833: Customer-specific rate pool generation
private static List<ResultRatePool> GenerateCustomerSpecificRatePools(
    List<ResultRatePool> crossOptimizationResultRatePools)

// Lines 940-1004: Shared pool results for cross-group optimization
private List<SimCardResult> GetM2MSharedPoolResults(KeySysLambdaContext context, 
    List<long> queueIds, BillingPeriod billingPeriod)
```

### Mobility Reports

#### 1. Optimization Group Summaries

##### WHAT:
Generates summary reports for mobility optimization groups showing carrier-level optimization results and group performance metrics.

##### WHY:
- Provides carrier-specific optimization insights
- Enables comparison between different optimization groups
- Supports carrier relationship management decisions
- Identifies optimization opportunities at the group level

##### HOW - Algorithm:
```
Algorithm: GenerateMobilityOptimizationGroupSummaries
Input: optimizationInstance, queueIds, billingPeriod
Output: optimizationGroupSummaries

1. RETRIEVE CARRIER DATA:
   a. Get valid rate plans from carrier repository
   b. Get optimization groups with rate plan mappings
   c. Filter by service provider and billing period

2. PROCESS DEVICE RESULTS:
   a. Get mobility device results for winning queues
   b. Group devices by OptimizationGroupId
   c. Validate rate plan and group assignments

3. GENERATE GROUP SUMMARIES:
   a. FOR each optimization group:
      - Calculate total devices in group
      - Compute cost savings for group
      - Analyze rate plan distribution
      - Generate group performance metrics

4. CREATE SUMMARY MODELS:
   a. Map result pools to summary models
   b. Include optimization group metadata
   c. Calculate group-level statistics
   d. Format for Excel reporting
```

##### Code Location:
```csharp
// Lines 647-717: Mobility carrier results processing
protected OptimizationInstanceResultFile WriteMobilityCarrierResults(KeySysLambdaContext context, 
    OptimizationInstance instance, List<long> queueIds, BillingPeriod billingPeriod, bool usesProration)
{
    var ratePlans = carrierRatePlanRepository.GetValidRatePlans(ParameterizedLog(context), 
        instance.ServiceProviderId.GetValueOrDefault());
    var optimizationGroups = carrierRatePlanRepository.GetValidOptimizationGroupsWithRatePlanIds(
        ParameterizedLog(context), instance.ServiceProviderId.GetValueOrDefault());
}

// Lines 718-727: Summary generation from result pools
private List<MobilityCarrierSummaryReportModel> MapToSummariesFromResult(
    List<ResultRatePool> optimizationGroupResultPools, OptimizationGroup optimizationGroup)
{
    var summaries = new List<MobilityCarrierSummaryReportModel>();
    foreach (var resultPool in optimizationGroupResultPools)
    {
        summaries.Add(MobilityCarrierSummaryReportModel.FromResultPool(resultPool, optimizationGroup));
    }
    return summaries;
}
```

#### 2. Device Assignment by Group

##### WHAT:
Creates detailed device assignment reports organized by optimization groups, showing how devices are assigned to rate plans within each group.

##### WHY:
- Provides granular visibility into group-level optimization decisions
- Enables validation of optimization group logic
- Supports troubleshooting of device assignment issues
- Facilitates optimization group performance analysis

##### HOW - Algorithm:
```
Algorithm: GenerateMobilityDeviceAssignmentsByGroup
Input: optimizationGroups, deviceResults, billingPeriod
Output: deviceAssignmentsByGroup

1. ORGANIZE DEVICES BY GROUPS:
   a. Group device results by OptimizationGroupId
   b. Map rate plans to each optimization group
   c. Create result rate pools for each group

2. CALCULATE ORIGINAL ASSIGNMENTS:
   a. Create original rate pool collection
   b. Assign devices to original rate pools
   c. Calculate original costs per device

3. PROCESS OPTIMIZED ASSIGNMENTS:
   a. FOR each optimization group:
      - Assign devices to optimized rate pools
      - Calculate optimized costs per device
      - Determine cost savings per device

4. GENERATE ASSIGNMENT MODELS:
   a. Create device assignment export models
   b. Include original and optimized rate plans
   c. Add cost savings and optimization group info
   d. Format for Excel consumption

5. COMBINE GROUP ASSIGNMENTS:
   a. Aggregate assignments across all groups
   b. Maintain group-level organization
   c. Include group metadata and performance metrics
```

##### Code Location:
```csharp
// Lines 661-698: Device assignment processing by optimization group
var deviceResultsByOptimizationGroups = deviceResults
    .Where(x => x.RatePlanTypeId != null && x.OptimizationGroupId != null)
    .GroupBy(x => x.OptimizationGroupId)
    .ToDictionary(x => x.Key, x => x.ToList());

foreach (var optimizationGroup in optimizationGroups)
{
    // Process each group's device assignments
    var groupRatePlans = MapRatePlansToOptimizationGroup(ratePlans, optimizationGroup);
    // Create result pools and assign devices
}

// Lines 728-746: Device assignment mapping
private List<MobilityCarrierAssignmentExportModel> MapToMobilityDeviceAssignmentsFromResult(
    RatePoolCollection originalAssignmentCollection, List<ResultRatePool> optimizationGroupResultPools, 
    BillingPeriod billingPeriod, OptimizationGroup optimizationGroup)
{
    var deviceAssignments = new List<MobilityCarrierAssignmentExportModel>();
    foreach (var resultPool in optimizationGroupResultPools)
    {
        foreach (var sim in resultPool.SimCards)
        {
            var originalRatePool = originalAssignmentCollection.RatePools
                .FirstOrDefault(x => x.SimCards.TryGetValue(sim.Key, out var _));
            var deviceAssignment = MobilityCarrierAssignmentExportModel.FromSimCardResult(
                sim.Value, originalRatePool?.RatePlan, resultPool.RatePlan, 
                billingPeriod.BillingPeriodStart, optimizationGroup.Name);
            deviceAssignments.Add(deviceAssignment);
        }
    }
    return deviceAssignments;
}
```

#### 3. Cost Analysis by Carrier

##### WHAT:
Provides comprehensive cost analysis broken down by carrier, showing optimization impact and financial benefits per carrier relationship.

##### WHY:
- Enables carrier-specific ROI analysis
- Supports carrier contract negotiations
- Identifies cost optimization opportunities by carrier
- Facilitates carrier performance comparisons

##### HOW - Algorithm:
```
Algorithm: GenerateMobilityCostAnalysisByCarrier
Input: optimizationGroups, deviceResults, ratePlans
Output: costAnalysisByCarrier

1. ORGANIZE DATA BY CARRIER:
   a. Group optimization groups by carrier
   b. Group rate plans by carrier
   c. Group device results by carrier

2. CALCULATE CARRIER COSTS:
   a. FOR each carrier:
      - Sum original costs across all devices
      - Sum optimized costs across all devices
      - Calculate total carrier cost savings
      - Compute carrier-specific utilization metrics

3. ANALYZE RATE PLAN PERFORMANCE:
   a. FOR each carrier's rate plans:
      - Device count per rate plan
      - Average cost per device per plan
      - Optimization success rate per plan
      - Usage efficiency per plan

4. GENERATE CARRIER COMPARISONS:
   a. Cost per MB by carrier
   b. Optimization effectiveness by carrier
   c. Device distribution by carrier
   d. Savings percentage by carrier

5. CREATE COST ANALYSIS REPORTS:
   a. Carrier summary tables
   b. Cost breakdown charts
   c. Performance comparison matrices
   d. Trend analysis over time
```

##### Code Location:
```csharp
// Lines 673-696: Original vs optimized cost calculation
var originalRatePools = RatePoolFactory.CreateRatePools(ratePlans, billingPeriod, 
    usesProration, OptimizationChargeType.RateChargeAndOverage);
var originalAssignmentCollection = RatePoolCollectionFactory.CreateRatePoolCollection(
    originalRatePools, shouldPoolByOptimizationGroup: true);

foreach (SimCardResult deviceResult in groupDeviceResults)
{
    // Add device to original assignment collection
    foreach (var ratePool in originalAssignmentCollection.RatePools)
    {
        if (ratePool.RatePlan.Id == deviceResult.StartingRatePlanId)
        {
            ratePool.AddSimCard(deviceResult.ToSimCard());
            break;
        }
    }
    // Add device to optimized result collection
    foreach (var ratePool in optimizationGroupResultPools)
    {
        if (ratePool.RatePlan.Id == deviceResult.RatePlanId)
        {
            ratePool.AddSimCard(deviceResult);
            break;
        }
    }
}

// Lines 715-716: Final Excel generation with cost analysis
var assignmentXlsxBytes = RatePoolAssignmentWriter.WriteOptimizationResultSheet(
    deviceAssignments, summariesByRatePlans);
```

## Shared Components Between M2M and Mobility

### Device Result Processing

Both M2M and Mobility reports share common device result processing patterns:

#### Code Location:
```csharp
// Lines 1073-1128: Common SimCard result parsing
private SimCardResult SimCardResultFromReader(SqlDataReader rdr, BillingPeriod billingPeriod)
{
    // Extracts: ICCID, MSISDN, UsageMB, ChargeAmt, RatePlanCode
    // Calculates: billing period activation, cost allocation
    // Handles: SMS usage, base rates, overage charges
}

// Lines 899-939: Mobility device results retrieval
private List<SimCardResult> GetMobilityResults(KeySysLambdaContext context, 
    List<long> queueIds, BillingPeriod billingPeriod)

// Lines 1005-1046: Mobility shared pool results
private List<SimCardResult> GetMobilitySharedPoolResults(KeySysLambdaContext context, 
    List<long> queueIds, BillingPeriod billingPeriod)
```

### Excel Report Generation

Both portal types use similar Excel generation patterns:

#### Code Location:
```csharp
// M2M Excel Generation - Lines 789-790:
var assignmentXlsxBytes = RatePoolAssignmentWriter.GenerateExcelFileFromByteArrays(
    statFileBytes, assignmentFileBytes, sharedPoolStatFileBytes, sharedPoolAssignmentFileBytes);

// Mobility Excel Generation - Lines 715-716:
var assignmentXlsxBytes = RatePoolAssignmentWriter.WriteOptimizationResultSheet(
    deviceAssignments, summariesByRatePlans);
```

This comprehensive algorithmic breakdown provides clear understanding of how M2M and Mobility reports are generated, with specific focus on the business value and technical implementation of each report type.