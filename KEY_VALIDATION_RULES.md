# Key Validation Rules for Carrier Optimization System

## Overview

The carrier optimization system implements four critical validation rules that control when and how optimization processes execute. These rules ensure optimal timing, respect service provider preferences, and provide operational flexibility.

## Rule 1: Runs During Last 8 Days of Billing Cycle

### What
**What**: The system only executes optimization runs during the final 8 days of a billing cycle period.

**Who**: This rule applies to all service providers with active billing periods in the system.

**How**: The system calculates the difference between the current local time and billing period end date, allowing execution only when `daysUntilBillingPeriodEnd < 8`.

### Why
- **Cost Optimization Window**: The last 8 days provide sufficient time to implement rate plan changes before the billing cycle closes
- **Data Accuracy**: Usage patterns are more complete and accurate near the end of billing cycles
- **Change Implementation**: Allows time for rate plan updates to be processed and applied
- **Risk Mitigation**: Reduces the risk of making optimization decisions on incomplete usage data

### Algorithm
```csharp
// Calculate days remaining in billing cycle
var currentTime = DateTime.UtcNow;
var currentLocalTime = TimeZoneInfo.ConvertTimeFromUtc(currentTime, billingPeriod.BillingTimeZone);
var daysUntilBillingPeriodEnd = billingPeriod.BillingPeriodEnd.Subtract(currentLocalTime).TotalDays;

// Check if within 8-day window
if (daysUntilBillingPeriodEnd < 8 && currentLocalTime.Date <= billingPeriod.BillingPeriodEnd.Date)
{
    // Allow optimization execution
    return true;
}
```

### Code Location
- **File**: `QueueCarrierPlanOptimization.cs`
- **Method**: `IsTimeToRun(KeySysLambdaContext context, BillingPeriod billingPeriod, JasperProviderLite serviceProvider)`
- **Lines**: 136-158 (main logic)
- **Line 143**: Time zone conversion logic
- **Line 144**: Days calculation
- **Line 149**: 8-day window check

---

## Rule 2: Honors Service Provider's Optimization Start Hour

### What
**What**: The system respects each service provider's configured `OptimizationStartHourLocalTime` setting to determine when optimization can begin.

**Who**: Individual service providers can configure their preferred optimization start time in local time zone.

**How**: The system checks if the current local hour is greater than or equal to the service provider's configured start hour.

### Why
- **Business Hours Alignment**: Allows service providers to align optimization runs with their business operations
- **Resource Management**: Enables load balancing across different time periods
- **Operational Control**: Gives service providers control over when system changes occur
- **Time Zone Respect**: Honors local business hours regardless of system UTC time

### Algorithm
```csharp
// Check if current hour meets optimization start hour requirement
if (serviceProvider.OptimizationStartHourLocalTime != null)
{
    if (currentLocalTime.Hour >= serviceProvider.OptimizationStartHourLocalTime.Value)
    {
        return true; // Optimization can proceed
    }
}
```

### Code Location
- **File**: `QueueCarrierPlanOptimization.cs`
- **Method**: `IsTimeToRun(KeySysLambdaContext context, BillingPeriod billingPeriod, JasperProviderLite serviceProvider)`
- **Lines**: 149-154
- **Line 151**: Hour comparison logic
- **Data Source**: `usp_Jasper_Get_Active_ServiceProviders` stored procedure
- **Configuration**: `OptimizationStartHourLocalTime` field in service provider settings

---

## Rule 3: Allows Continuous Runs on Final Day

### What
**What**: On the final day of the billing period, the system allows continuous optimization runs once the optimization start hour has passed, overriding normal single-run restrictions.

**Who**: This applies to all service providers on their final billing day, enabling multiple optimization attempts.

**How**: The system implements special logic for the final day that bypasses the normal "optimization already running" check if the previous optimization completed successfully.

### Why
- **Critical Window**: The final day is the last opportunity to optimize before billing cycle closes
- **Success Maximization**: Multiple attempts increase the likelihood of successful optimization
- **Flexibility**: Allows recovery from failed attempts or improved optimization with updated data
- **Business Continuity**: Ensures optimization happens even if earlier attempts encountered issues

### Algorithm
```csharp
// Special handling for final day
if (currentLocalTime.Date == billingPeriod.BillingPeriodEnd.Date && 
    serviceProvider.OptimizationStartHourLocalTime != null)
{
    if (currentLocalTime.Hour >= serviceProvider.OptimizationStartHourLocalTime.Value)
    {
        return true; // Allow continuous runs on final day
    }
}

// In IsOptimizationRunning method - allow re-run if last optimization completed
if (optimizationIdRunning >= 0)
{
    var currentLocalTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, billingPeriod.BillingTimeZone);
    
    if (currentLocalTime.Date == billingPeriod.BillingPeriodEnd.Date)
    {
        // Check if last optimization completed successfully
        var status = GetLastOptimizationStatus(optimizationIdRunning);
        if (status == OptimizationStatus.Completed)
        {
            return false; // Allow a new run
        }
    }
}
```

### Code Location
- **File**: `QueueCarrierPlanOptimization.cs`
- **Primary Method**: `IsTimeToRun` (lines 150-154)
- **Supporting Method**: `IsOptimizationRunning` (lines 360-425)
- **Final Day Check**: Line 150 (date comparison)
- **Continuous Run Logic**: Lines 396-417 in `IsOptimizationRunning`
- **Status Check**: Lines 405-414 (optimization completion verification)

---

## Rule 4: Supports Execution Override for Manual Runs

### What
**What**: The system provides an execution override mechanism (`IsExecutionOverridden`) that bypasses all timing restrictions for manual optimization runs.

**Who**: System administrators and operators can trigger manual optimization runs outside normal scheduling windows.

**How**: When the execution override flag is set in the Lambda context, the system returns `true` regardless of other timing conditions.

### Why
- **Emergency Operations**: Allows immediate optimization during critical situations
- **Testing and Debugging**: Enables testing optimization logic outside normal windows
- **Manual Intervention**: Provides operator control for special circumstances
- **Operational Flexibility**: Supports business requirements that don't fit standard schedules

### Algorithm
```csharp
// Final check - override all restrictions if manual execution requested
return context.IsExecutionOverridden;
```

### Code Location
- **File**: `QueueCarrierPlanOptimization.cs`
- **Method**: `IsTimeToRun(KeySysLambdaContext context, BillingPeriod billingPeriod, JasperProviderLite serviceProvider)`
- **Line**: 158 (final return statement)
- **Context Property**: `context.IsExecutionOverridden`
- **Usage**: Always evaluated as the final condition after all other rules

---

## Complete Validation Logic Flow

### Combined Algorithm
```csharp
private bool IsTimeToRun(KeySysLambdaContext context, BillingPeriod billingPeriod, JasperProviderLite serviceProvider)
{
    // Calculate time-based conditions
    var currentTime = DateTime.UtcNow;
    var currentLocalTime = TimeZoneInfo.ConvertTimeFromUtc(currentTime, billingPeriod.BillingTimeZone);
    var daysUntilBillingPeriodEnd = billingPeriod.BillingPeriodEnd.Subtract(currentLocalTime).TotalDays;

    // Rule 1 & 3: Last 8 days OR final day with start hour
    if ((daysUntilBillingPeriodEnd < 8 && currentLocalTime.Date <= billingPeriod.BillingPeriodEnd.Date) ||
        (currentLocalTime.Date == billingPeriod.BillingPeriodEnd.Date && serviceProvider.OptimizationStartHourLocalTime != null))
    {
        // Rule 2: Honor optimization start hour
        if (currentLocalTime.Hour >= serviceProvider.OptimizationStartHourLocalTime.Value)
        {
            return true; // Allow continuous runs on the last day from start hour
        }
    }

    // Rule 4: Execution override bypasses all restrictions
    return context.IsExecutionOverridden;
}
```

### Dependencies
- **Service Provider Data**: Retrieved via `GetJasperServiceProviders()` method
- **Billing Period**: Retrieved via `GetBillingPeriodsForServiceProviders()` method
- **Time Zone Handling**: Uses `TimeZoneInfo.ConvertTimeFromUtc()` for local time calculations
- **Override Context**: Provided through Lambda execution context

### Integration Points
- **Scheduled Execution**: Called from `QueueJasperServiceProviders()` method
- **Manual Execution**: Triggered via SQS message processing
- **Optimization Sessions**: Integrates with session management and progress tracking
- **AMOP 2.0 API**: Reports progress and status to external monitoring systems

---

## Configuration and Monitoring

### Service Provider Configuration
```sql
-- Stored procedure: usp_Jasper_Get_Active_ServiceProviders
-- Returns: ServiceProviderId, TenantId, OptimizationStartHourLocalTime
```

### Key Environment Variables
- `CarrierOptimizationQueueURL`: SQS queue for optimization messages
- `DeviceSyncQueueURL`: Queue for device synchronization
- `ErrorNotificationEmailReceiver`: Email address for error notifications

### Logging Points
- **Rule Evaluation**: Logs timing calculations and rule decisions
- **Override Usage**: Logs when execution override is applied  
- **Time Zone Conversions**: Logs local time calculations
- **Validation Results**: Logs final allow/deny decisions

This comprehensive validation system ensures that carrier optimization runs at optimal times while providing the flexibility needed for operational requirements and emergency situations.