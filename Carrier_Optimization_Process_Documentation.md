# Carrier Optimization Process - Detailed Logic Documentation

## Overview
The Carrier Optimization process is a complex system that automatically optimizes SIM card rate plans to minimize costs while maintaining service quality. The process involves four main AWS Lambda functions working together to achieve end-to-end optimization.

## Lambda Functions Overview

1. **QueueCarrierPlanOptimization** - Initiates and queues the optimization process
2. **AltaworxJasperAWSGetDevicesQueue** - Syncs device data from Jasper API
3. **AltaworxSimCardCostOptimizer** - Performs the actual optimization calculations
4. **AltaworxSimCardCostOptimizerCleanup** - Processes results and sends notifications

## Detailed Process Flow

### 1. Process Initiation (QueueCarrierPlanOptimization)

#### 1.1 Trigger Conditions
- **Scheduled Execution**: Runs automatically for active Jasper service providers
- **Manual Execution**: Triggered via SQS message with specific parameters
- **Time-based Logic**: Executes during the last 8 days of the billing period OR on the last day from the configured start hour

#### 1.2 Pre-execution Checks
```csharp
// Check if optimization is already running
private bool IsOptimizationRunning(KeySysLambdaContext context, int tenantId)
{
    // Query vwOptimizationSessionRunning to check for active sessions
    // Allow re-run only if last day and previous optimization completed
}
```

#### 1.3 Session Management
- Creates new optimization session if none exists
- Records session metadata including:
  - Service Provider ID
  - Tenant ID
  - Billing Period details
  - Device count expectations
  - Session GUID for tracking

#### 1.4 Device Synchronization Check
- Verifies if devices have been synced for current optimization run
- If not synced:
  - Truncates staging tables
  - Triggers device sync via **AltaworxJasperAWSGetDevicesQueue**
  - Sets last sync date to 1 month + 1 day ago

### 2. Device Data Synchronization (AltaworxJasperAWSGetDevicesQueue)

#### 2.1 Device Retrieval Process
```csharp
// Paginates through Jasper API to get all devices
private async Task<bool> GetJasperDevices(KeySysLambdaContext context, ...)
{
    // Calls Jasper API with authentication
    // Processes devices page by page (up to MaxPagesToProcess)
    // Filters duplicates by ICCID
    // Returns true when last page reached
}
```

#### 2.2 Data Processing
- **Staging**: Inserts device data into `JasperDeviceStaging` table
- **Validation**: Checks for data integrity and completeness
- **Updates**: Calls `usp_Update_Jasper_Device` stored procedure
- **Error Handling**: Implements retry policies for transient failures

#### 2.3 Next Step Routing
Based on `NextStep` parameter:
- **DeviceUsageByRatePlan**: Triggers usage sync for optimization
- **DeviceUsageExport**: Triggers export for manual processing
- **UpdateDeviceRatePlan**: Triggers rate plan updates

### 3. Optimization Setup (Back to QueueCarrierPlanOptimization)

#### 3.1 Communication Plan Grouping
- Groups devices by communication plans with same rate plan IDs
- Creates `OptimizationCommGroup` records
- Links communication plans to groups

#### 3.2 Rate Plan Validation
```csharp
// Validates rate plans for optimization
if (groupRatePlans.Any(groupRatePlan => 
    groupRatePlan.DataPerOverageCharge <= 0 || 
    groupRatePlan.OverageRate <= 0))
{
    // Stop optimization with error
    // Send error notification to AMOP 2.0
}
```

#### 3.3 Rate Pool Generation
- Calculates maximum average usage across devices
- Creates rate pools with proration settings
- Generates rate pool collections for optimization

#### 3.4 Baseline Device Assignment
- Assigns each device to initial rate plan based on usage patterns
- Records baseline costs for comparison
- Filters devices with insufficient data

#### 3.5 Queue Creation and Permutation
For M2M optimization:
```csharp
// Generate all possible rate plan sequences
var ratePoolSequences = RatePoolAssigner.GenerateRatePoolSequences(ratePoolCollection.RatePools);
```

For Mobility optimization:
```csharp
// Generate sequences by rate plan types
var ratePoolSequences = RatePoolAssigner.GenerateRatePoolSequencesByRatePlanTypes(ratePoolCollection.RatePools);
```

### 4. Optimization Execution (AltaworxSimCardCostOptimizer)

#### 4.1 Queue Processing
- Processes optimization queues in batches
- Validates queue status to prevent duplicate processing
- Supports chaining for large optimizations using Redis cache

#### 4.2 Device Assignment Algorithm
```csharp
// Core optimization logic
var assigner = new RatePoolAssigner(
    ratePoolCollection, 
    simCards, 
    logger, 
    sanityCheckTimeLimit, 
    lambdaContext, 
    isUsingRedisCache
);

// Runs multiple assignment strategies:
// 1. No Grouping + Largest To Smallest
// 2. No Grouping + Smallest To Largest  
// 3. Group By Communication Plan + Largest To Smallest
// 4. Group By Communication Plan + Smallest To Largest
```

#### 4.3 Portal Type Specific Processing
- **M2M**: Uses communication plans for grouping
- **Mobility**: Uses optimization groups with rate plan type filtering
- **Cross-Provider**: Special handling for multi-provider scenarios

#### 4.4 Optimization Results
- Finds best cost assignment across all permutations
- Records winning queue ID and assignment details
- Handles shared pooling scenarios for cross-customer optimization

### 5. Result Processing and Cleanup (AltaworxSimCardCostOptimizerCleanup)

#### 5.1 Queue Monitoring
- Monitors SQS queue length to ensure all optimization tasks complete
- Implements retry logic with exponential backoff
- Maximum 10 retry attempts before timeout

#### 5.2 Instance Cleanup Process
```csharp
private void CleanupInstance(KeySysLambdaContext context, long instanceId, ...)
{
    // Get winning queue for each communication group
    var winningQueueId = GetWinningQueueId(context, commGroup.Id);
    
    // End all non-winning queues
    EndQueuesForCommGroup(context, commGroup.Id);
    
    // Clean up non-winning results
    CleanupDeviceResultsForCommGroup(context, commGroup.Id, winningQueueId);
}
```

#### 5.3 Result File Generation
Portal-specific result processing:
- **M2M**: Generates Excel with assignment details and statistics
- **Mobility**: Creates carrier optimization reports with device assignments
- **Cross-Provider**: Handles multi-provider result aggregation

#### 5.4 Reporting Features
- **Statistics Tab**: Rate plan utilization, cost savings, device counts
- **Assignment Tab**: Individual device assignments and cost breakdowns
- **Shared Pool Tab**: Cross-customer pooling results (if applicable)

#### 5.5 Email Notifications
- **Success**: Sends optimization results via email attachment
- **Error**: Sends error notifications with diagnostic information
- **Rate Plan Updates**: Notifies about automatic rate plan changes

### 6. Rate Plan Update Process (Optional)

#### 6.1 Time-based Decision Logic
```csharp
public static bool DoesHaveTimeToProcessRatePlanUpdates(
    OptimizationInstance instance, 
    int ratePlansToUpdateCount,
    DateTime currentSystemTimeUtc, 
    TimeZoneInfo timeZoneInfo)
{
    // Calculate minutes remaining in billing cycle
    decimal minutesRemaining = MinutesRemainingInBillCycle(...);
    
    // Estimate minutes needed for updates
    var minutesToUpdate = MinutesToUpdateRatePlans(ratePlansToUpdateCount, ...);
    
    // Require 10-minute buffer
    return minutesRemaining > 0 && minutesRemaining - minutesToUpdate >= 10;
}
```

#### 6.2 Update Execution
- Queues rate plan updates for devices with beneficial changes
- Processes updates in batches of 250 devices
- Tracks update performance metrics for future estimations

## Key Features and Safeguards

### 1. Error Handling
- **Transient Failures**: Retry policies with exponential backoff
- **Data Validation**: Comprehensive validation at each step
- **Notification System**: Email alerts for critical failures

### 2. Performance Optimization
- **Redis Caching**: Caches device data and partial results
- **Pagination**: Processes large datasets in manageable chunks
- **Parallel Processing**: Multiple lambda instances for scalability

### 3. Data Integrity
- **Staging Tables**: Temporary storage for data validation
- **Transaction Management**: Ensures data consistency
- **Duplicate Prevention**: Handles SQS "at-least-once" delivery

### 4. Monitoring and Logging
- **Progress Tracking**: Real-time updates to AMOP 2.0 system
- **Comprehensive Logging**: Detailed logs for troubleshooting
- **Performance Metrics**: Execution time and resource usage tracking

## Configuration Parameters

### Environment Variables
- `CarrierOptimizationQueueURL`: SQS queue for optimization messages
- `DeviceSyncQueueURL`: SQS queue for device synchronization
- `QueuesPerInstance`: Number of queues per optimizer instance
- `SanityCheckTimeLimit`: Maximum execution time per optimization
- `MaxPagesToProcess`: Maximum pages to process from Jasper API

### Database Configuration
- **Connection Strings**: Central DB and Jasper-specific connections
- **Timeout Settings**: SQL command timeouts for long-running operations
- **Retry Policies**: Database resilience configuration

## Error Scenarios and Recovery

### 1. Device Sync Failures
- **Network Issues**: Retry with exponential backoff
- **API Limits**: Respect rate limiting and queue delays
- **Data Corruption**: Validate and clean staging data

### 2. Optimization Failures
- **Memory Limits**: Use Redis for state management
- **Timeout Issues**: Implement chaining for large optimizations
- **Calculation Errors**: Validate results before saving

### 3. Result Processing Failures
- **File Generation**: Retry with error logging
- **Email Delivery**: Multiple recipient support with error handling
- **Database Updates**: Transaction rollback on failures

## Performance Considerations

### 1. Scalability
- **Horizontal Scaling**: Multiple lambda instances
- **Queue Management**: Distribute load across instances
- **Memory Management**: Efficient object lifecycle management

### 2. Cost Optimization
- **Lambda Timeouts**: Balance between completion and cost
- **Resource Allocation**: Right-size memory and CPU
- **Caching Strategy**: Reduce redundant calculations

### 3. Monitoring
- **CloudWatch Metrics**: Track execution times and errors
- **SQS Monitoring**: Queue depth and processing rates
- **Cost Tracking**: Monitor optimization savings vs. processing costs

## Conclusion

The Carrier Optimization process is a sophisticated system that automatically optimizes SIM card rate plans through a multi-stage pipeline. It combines device synchronization, mathematical optimization, and result processing to deliver cost savings while maintaining service quality. The system is designed for reliability, scalability, and comprehensive error handling to ensure successful optimization even in complex scenarios.