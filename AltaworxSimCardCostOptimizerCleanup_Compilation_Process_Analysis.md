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