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