# Key Validation Rules

## Overview

This document outlines the four critical validation rules that govern when carrier optimization processes can execute in the Altaworx SIM Card Cost Optimization system. These rules ensure optimal timing, respect business constraints, and provide administrative flexibility.

---

## 1. Runs during last 8 days of billing cycle

### What
Validates that optimization execution occurs only within the final 8 days of the billing cycle period

### Why
Ensures sufficient usage data is available for accurate cost optimization decisions, preventing premature optimization based on incomplete billing cycle data that could lead to suboptimal rate plan assignments

### How
Calculates days remaining until billing period end in the appropriate timezone and validates the current date falls within the 8-day execution window

### Algorithm
```
ALGORITHM: ValidateBillingCycle8DayWindow
INPUT: Current DateTime, Billing Period End Date, Billing TimeZone
OUTPUT: Validation Result (Allow/Deny Execution)

Step 1: Convert Current Time to Billing Timezone
       Get current UTC time using DateTime.UtcNow
       Convert UTC time to billing period's local timezone using TimeZoneInfo.ConvertTimeFromUtc
       Store as currentLocalTime for all subsequent calculations

Step 2: Calculate Days Until Billing Period End
       Calculate time difference: billingPeriod.BillingPeriodEnd.Subtract(currentLocalTime)
       Extract TotalDays from the TimeSpan difference
       Store as daysUntilBillingPeriodEnd

Step 3: Validate 8-Day Window
       If daysUntilBillingPeriodEnd < 8
       And currentLocalTime.Date <= billingPeriod.BillingPeriodEnd.Date
       Then mark as within valid execution window
       Else mark as outside execution window

Step 4: Log Validation Details
       Log current time, local time, and days remaining for audit purposes
       Include timezone information for troubleshooting

Step 5: Return Window Validation Result
       Return true if within 8-day window, false otherwise
```

### Code Location
**File**: `QueueCarrierPlanOptimization.cs` - Lines 139-150
```csharp
// is it one of the last 8 days of the billing period?
var currentTime = DateTime.UtcNow;
var currentLocalTime = TimeZoneInfo.ConvertTimeFromUtc(currentTime, billingPeriod.BillingTimeZone);
var daysUntilBillingPeriodEnd = billingPeriod.BillingPeriodEnd.Subtract(currentLocalTime).TotalDays;

if ((daysUntilBillingPeriodEnd < 8 && currentLocalTime.Date <= billingPeriod.BillingPeriodEnd.Date) ||
```

---

## 2. Honors service provider's optimization start hour

### What
Validates that optimization execution respects the configured optimization start hour for each service provider

### Why
Allows service providers to control when optimization processing occurs, enabling proper resource management and operational scheduling while respecting provider preferences and timezone considerations

### How
Compares the current local hour against the service provider's configured OptimizationStartHourLocalTime value and only allows execution at or after the specified hour

### Algorithm
```
ALGORITHM: ValidateOptimizationStartHour
INPUT: Current Local Time, Service Provider Start Hour Configuration
OUTPUT: Validation Result (Allow/Deny Execution)

Step 1: Verify Start Hour Configuration
       Check if serviceProvider.OptimizationStartHourLocalTime is not null
       If configuration is missing, skip hour validation
       If configuration exists, proceed to hour validation

Step 2: Extract Current Hour
       Get current hour from currentLocalTime using .Hour property (0-23 format)
       Ensure hour is in local timezone of billing period

Step 3: Compare Against Configured Start Hour
       If currentLocalTime.Hour >= serviceProvider.OptimizationStartHourLocalTime.Value
       Then mark as valid execution time
       Else mark as before allowed execution window

Step 4: Apply to Both Window Types
       Check start hour constraint for both 8-day window and final day scenarios
       Ensure start hour is respected in all execution paths

Step 5: Return Hour Validation Result
       Return true if current hour meets start hour requirement
       Return false if execution is before allowed window
```

### Code Location
**File**: `QueueCarrierPlanOptimization.cs` - Lines 150-157
```csharp
if ((daysUntilBillingPeriodEnd < 8 && currentLocalTime.Date <= billingPeriod.BillingPeriodEnd.Date) ||
(currentLocalTime.Date == billingPeriod.BillingPeriodEnd.Date && serviceProvider.OptimizationStartHourLocalTime != null))
{
    if (currentLocalTime.Hour >= serviceProvider.OptimizationStartHourLocalTime.Value)
    {
        return true; // Allow continuous runs on the last day from start hour
    }
}
```

---

## 3. Allows continuous runs on final day if optimization start hour has passed

### What
Validates that multiple optimization runs are permitted on the final day of billing cycle once the configured start hour has been reached

### Why
Maximizes optimization opportunities before billing cycle closes, ensuring the most accurate optimization with complete usage data while allowing multiple attempts for better cost savings on the critical final day

### How
Checks if current date equals billing period end date and allows continuous execution throughout the day after the optimization start hour has passed, with special logic to enable multiple runs

### Algorithm
```
ALGORITHM: ValidateFinalDayContinuousRuns
INPUT: Current Local Time, Billing Period End Date, Start Hour Configuration
OUTPUT: Validation Result (Allow/Deny Continuous Execution)

Step 1: Check Final Day Condition
       Compare currentLocalTime.Date with billingPeriod.BillingPeriodEnd.Date
       If dates are equal, mark as final billing day
       If dates are not equal, skip final day logic

Step 2: Verify Start Hour Configuration
       Check if serviceProvider.OptimizationStartHourLocalTime is not null
       If configuration is missing, skip continuous run logic
       If configuration exists, proceed to continuous run validation

Step 3: Validate Start Hour Has Passed
       If currentLocalTime.Hour >= serviceProvider.OptimizationStartHourLocalTime.Value
       Then enable continuous runs for remainder of day
       Else wait for start hour to be reached

Step 4: Enable Continuous Execution Logic
       Once start hour is reached on final day
       Allow multiple optimization runs without additional time restrictions
       Override normal single-run-per-day limitations

Step 5: Return Continuous Run Authorization
       Return true to allow execution throughout final day after start hour
       Enable special final day processing mode
```

### Code Location
**File**: `QueueCarrierPlanOptimization.cs` - Lines 151-159
```csharp
(currentLocalTime.Date == billingPeriod.BillingPeriodEnd.Date && serviceProvider.OptimizationStartHourLocalTime != null))
{
    if (currentLocalTime.Hour >= serviceProvider.OptimizationStartHourLocalTime.Value)
    {
        return true; // Allow continuous runs on the last day from start hour
    }
}
```

**Supporting Code Location**:
**File**: `QueueCarrierPlanOptimization.cs` - Lines 340-390 (IsOptimizationRunning method)
```csharp
// New logic: allow re-run if today is the last day and last optimization is completed
if (currentLocalTime.Date == billingPeriod.BillingPeriodEnd.Date)
{
    if (status != null && (int)status == (int)OptimizationStatus.Completed)
    {
        return false; //Allow a new run
    }
}
```

---

## 4. Supports execution override for manual runs

### What
Validates and processes execution override flags that allow manual optimization runs to bypass all standard timing and validation restrictions

### Why
Provides administrative control for emergency optimizations, testing scenarios, and business continuity situations while maintaining audit trails and proper authorization for critical operational needs and exceptional circumstances

### How
Examines the Lambda context for IsExecutionOverridden flag and allows complete bypass of all timing restrictions when the override is authorized, providing immediate execution capability

### Algorithm
```
ALGORITHM: ValidateExecutionOverride
INPUT: Lambda Context, Standard Validation Results
OUTPUT: Final Execution Decision (Allow/Deny)

Step 1: Check Override Flag Status
       Examine context.IsExecutionOverridden property
       If override flag is not set or is false
       Then proceed with standard validation results
       If override flag is true, proceed to override processing

Step 2: Log Override Authorization
       Log information about manual override being invoked
       Include timestamp and context for audit purposes
       Record override event for compliance tracking

Step 3: Bypass All Standard Validations
       If IsExecutionOverridden equals true
       Then ignore 8-day window restriction
       Bypass optimization start hour constraint
       Override final day timing requirements
       Skip all other timing-based validations

Step 4: Apply Override Immediately
       Return true immediately when override is detected
       Do not evaluate any other validation conditions
       Allow execution regardless of billing cycle timing

Step 5: Return Override Decision
       Return true (allow execution) when override is active
       Log successful override application for audit trail
       Continue with optimization processing bypassing all restrictions
```

### Code Location
**File**: `QueueCarrierPlanOptimization.cs` - Line 164
```csharp
// was the override parameter passed (forces execution)
return context.IsExecutionOverridden;
```

**Complete Method Context**:
**File**: `QueueCarrierPlanOptimization.cs` - Lines 135-165 (IsTimeToRun method)
```csharp
private bool IsTimeToRun(KeySysLambdaContext context, BillingPeriod billingPeriod, JasperProviderLite serviceProvider)
{
    LogInfo(context, "SUB", $"IsTimeToRun({billingPeriod.BillingPeriodEnd},{serviceProvider.ServiceProviderId},{serviceProvider.OptimizationStartHourLocalTime})");

    // Standard validation logic for 8-day window and start hour
    // ... [validation code] ...

    // was the override parameter passed (forces execution)
    return context.IsExecutionOverridden;
}
```

---

## Summary

These four validation rules work together to ensure:

1. **Data Quality**: Only execute when sufficient usage data is available (8-day rule)
2. **Operational Control**: Respect service provider scheduling preferences (start hour)
3. **Optimization Maximization**: Allow multiple attempts on the critical final day (continuous runs)
4. **Administrative Flexibility**: Provide emergency bypass capability (execution override)

The rules are implemented in the `IsTimeToRun` method and related supporting methods, providing a comprehensive validation framework that balances automation with operational control.