# Key Validation Rules - Algorithmic Analysis

## Overview

This document provides a comprehensive algorithmic explanation of the key validation rules implemented in the Altaworx SIM Card Cost Optimization system. These rules control when optimization processes can execute, ensuring they run at optimal times while respecting business constraints.

## Code Location

**Primary Implementation**: `QueueCarrierPlanOptimization.cs` - Lines 135-165  
**Method**: `IsTimeToRun(KeySysLambdaContext context, BillingPeriod billingPeriod, JasperProviderLite serviceProvider)`

## 1. WHAT - Key Validation Rules Definition

### Rule 1: Runs during last 8 days of billing cycle
- **Constraint**: Optimization only executes within the final 8 days of a billing period
- **Purpose**: Ensures sufficient usage data is available for accurate optimization

### Rule 2: Honors service provider's optimization start hour  
- **Constraint**: Respects the configured optimization start hour for each service provider
- **Purpose**: Allows providers to control when optimization processing occurs

### Rule 3: Allows continuous runs on final day if optimization start hour has passed
- **Constraint**: On the last day of billing, allows multiple optimization runs after start hour
- **Purpose**: Maximizes optimization opportunities before billing cycle ends

### Rule 4: Supports execution override for manual runs
- **Constraint**: Manual executions can bypass timing restrictions
- **Purpose**: Enables administrator intervention and testing capabilities

## 2. WHY - Business Justification

### 2.1 Last 8 Days Restriction
```
Business Logic:
- Earlier in billing cycle → Insufficient usage data → Poor optimization decisions
- Last 8 days → Near-complete usage patterns → Accurate cost projections
- Avoids premature optimization based on incomplete data
```

### 2.2 Optimization Start Hour
```
Business Logic:
- Different time zones → Different optimal processing windows
- Service provider preferences → Operational scheduling control
- Resource management → Distributed processing loads
```

### 2.3 Final Day Continuous Runs
```
Business Logic:
- Last opportunity for optimization before billing
- Usage patterns complete → Maximum accuracy
- Multiple runs → Better optimization coverage
```

### 2.4 Execution Override
```
Business Logic:
- Emergency optimizations → Business continuity
- Testing and validation → Quality assurance
- Administrative control → Operational flexibility
```

## 3. HOW - Algorithmic Implementation

### 3.1 Complete Algorithm Flow

```csharp
public bool IsTimeToRun(KeySysLambdaContext context, 
                       BillingPeriod billingPeriod, 
                       JasperProviderLite serviceProvider)
{
    // STEP 1: Calculate current time in billing period timezone
    var currentTime = DateTime.UtcNow;
    var currentLocalTime = TimeZoneInfo.ConvertTimeFromUtc(currentTime, billingPeriod.BillingTimeZone);
    var daysUntilBillingPeriodEnd = billingPeriod.BillingPeriodEnd.Subtract(currentLocalTime).TotalDays;

    // STEP 2: Primary validation - Check 8-day window OR final day
    bool isWithin8Days = (daysUntilBillingPeriodEnd < 8 && 
                         currentLocalTime.Date <= billingPeriod.BillingPeriodEnd.Date);
    
    bool isFinalDay = (currentLocalTime.Date == billingPeriod.BillingPeriodEnd.Date && 
                      serviceProvider.OptimizationStartHourLocalTime != null);

    // STEP 3: If within valid time window, check start hour constraint
    if (isWithin8Days || isFinalDay)
    {
        if (currentLocalTime.Hour >= serviceProvider.OptimizationStartHourLocalTime.Value)
        {
            return true; // Allow continuous runs on the last day from start hour
        }
    }

    // STEP 4: Check execution override (manual runs)
    return context.IsExecutionOverridden;
}
```

### 3.2 Detailed Validation Logic

#### Step 1: Time Zone Conversion
```
Algorithm:
INPUT: UTC timestamp, Billing period timezone
PROCESS: 
  1. Convert UTC → Local billing timezone
  2. Calculate days remaining until billing period end
  3. Preserve timezone context for all comparisons
OUTPUT: Local time in billing timezone
```

#### Step 2: 8-Day Window Validation
```
Algorithm:
INPUT: Current local time, Billing period end date
PROCESS:
  1. Calculate: daysUntilEnd = (billingPeriodEnd - currentLocalTime).TotalDays
  2. Check: daysUntilEnd < 8
  3. Verify: currentDate <= billingPeriodEndDate (safety check)
OUTPUT: Boolean indicating if within 8-day window
```

#### Step 3: Final Day Special Logic
```
Algorithm:
INPUT: Current local time, Billing period end date
PROCESS:
  1. Check: currentDate == billingPeriodEndDate
  2. Verify: optimizationStartHour is configured
  3. Enable: Continuous execution after start hour
OUTPUT: Boolean indicating final day status
```

#### Step 4: Start Hour Validation
```
Algorithm:
INPUT: Current local hour, Service provider start hour
PROCESS:
  1. Compare: currentHour >= configuredStartHour
  2. Allow: All subsequent hours in the day
  3. Block: Hours before configured start time
OUTPUT: Boolean indicating start hour compliance
```

#### Step 5: Override Check
```
Algorithm:
INPUT: Lambda context execution flag
PROCESS:
  1. Check: context.IsExecutionOverridden flag
  2. Bypass: All timing restrictions if true
  3. Enable: Manual administrative control
OUTPUT: Boolean indicating override status
```

### 3.3 Enhanced Final Day Logic

The system implements special handling for the final day of billing:

```csharp
// Enhanced logic from lines 150-159
if (currentLocalTime.Date == billingPeriod.BillingPeriodEnd.Date && 
    serviceProvider.OptimizationStartHourLocalTime != null)
{
    // On final day, allow continuous execution after start hour
    if (currentLocalTime.Hour >= serviceProvider.OptimizationStartHourLocalTime.Value)
    {
        return true; // Continuous runs enabled
    }
}
```

**Final Day Algorithm**:
```
IF (today == final_billing_day) AND (start_hour_configured):
    IF (current_hour >= start_hour):
        RETURN true  // Allow execution
    ELSE:
        RETURN false // Wait for start hour
ENDIF
```

## 4. Implementation Details

### 4.1 Supporting Infrastructure

#### Optimization Session Check (Lines 340-390)
```csharp
private bool IsOptimizationRunning(KeySysLambdaContext context, int tenantId)
{
    // Enhanced logic: Allow re-run if today is the last day and last optimization is completed
    if (optimizationIdRunning >= 0)
    {
        var currentLocalTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, billingPeriod.BillingTimeZone);

        if (currentLocalTime.Date == billingPeriod.BillingPeriodEnd.Date)
        {
            // Check if last optimization completed
            var status = GetLastOptimizationStatus(optimizationIdRunning);
            if (status == OptimizationStatus.Completed)
            {
                return false; // Allow new run on final day
            }
        }
    }
    
    return optimizationIdRunning >= 0;
}
```

#### Configuration Parameters
- **BillingPeriod.BillingTimeZone**: Timezone for billing period calculations
- **ServiceProvider.OptimizationStartHourLocalTime**: Configured start hour (0-23)
- **Context.IsExecutionOverridden**: Manual override flag

### 4.2 Error Handling and Logging

```csharp
LogInfo(context, "INFO", $"Days until billing period end: {daysUntilBillingPeriodEnd}, " +
                        $"currentTime: {currentTime}, currentLocalTime: {currentLocalTime}");
```

The system provides comprehensive logging for debugging and audit purposes.

## 5. Edge Cases and Special Scenarios

### 5.1 Timezone Boundary Conditions
- **Cross-midnight scenarios**: Properly handled by local time conversion
- **DST transitions**: Managed by .NET TimeZoneInfo class
- **International deployments**: Each billing period has its own timezone

### 5.2 Final Day Processing
- **Multiple optimizations**: Allowed after start hour on final day
- **Completion checking**: Verifies previous optimization completion before allowing new runs
- **Resource management**: Prevents concurrent optimization conflicts

### 5.3 Override Scenarios
- **Emergency optimizations**: Full bypass of timing restrictions
- **Testing environments**: Manual control for validation
- **Administrative intervention**: Business continuity support

## 6. Performance Implications

### 6.1 Timing Calculations
- **Lightweight operations**: Simple datetime arithmetic
- **Cached timezone info**: Efficient timezone conversions
- **Database query optimization**: Minimal queries for validation

### 6.2 Execution Flow Impact
- **Early exit conditions**: Fast rejection of invalid timing
- **Logging overhead**: Minimal impact on performance
- **Override path**: Immediate execution when enabled

## 7. Monitoring and Observability

### 7.1 Key Metrics
- **Validation success rate**: Percentage of validations passing
- **Override usage frequency**: Manual intervention tracking
- **Final day execution count**: Multiple runs on billing end date

### 7.2 Alerting Thresholds
- **Unexpected validation failures**: Business rule violations
- **High override usage**: Potential process issues
- **Missing optimization windows**: Billing cycle completion without execution

## 8. Configuration Management

### 8.1 Service Provider Settings
```sql
-- Service provider optimization configuration
SELECT ServiceProviderId, OptimizationStartHourLocalTime, BillingTimeZone
FROM ServiceProviders 
WHERE IsActive = 1 AND OptimizationEnabled = 1
```

### 8.2 Billing Period Configuration
```sql
-- Billing period setup
SELECT BillingPeriodId, BillingPeriodStart, BillingPeriodEnd, BillingTimeZone
FROM BillingPeriods 
WHERE IsActive = 1
```

## 9. Testing Strategies

### 9.1 Unit Test Scenarios
```csharp
[Test]
public void IsTimeToRun_Within8Days_BeforeStartHour_ReturnsFalse()
{
    // Arrange: 5 days before billing end, before start hour
    // Act: Call IsTimeToRun
    // Assert: Returns false
}

[Test]
public void IsTimeToRun_FinalDay_AfterStartHour_ReturnsTrue()
{
    // Arrange: Final billing day, after start hour
    // Act: Call IsTimeToRun
    // Assert: Returns true (continuous runs allowed)
}

[Test]
public void IsTimeToRun_Override_BypassesAllRestrictions()
{
    // Arrange: Set execution override flag
    // Act: Call IsTimeToRun outside normal windows
    // Assert: Returns true (override respected)
}
```

### 9.2 Integration Test Scenarios
- **End-to-end billing cycle**: Complete billing period simulation
- **Multi-timezone testing**: Different service provider timezones
- **Override workflow**: Manual intervention testing

## 10. Maintenance and Evolution

### 10.1 Future Enhancements
- **Dynamic window sizing**: Configurable 8-day window
- **Provider-specific rules**: Custom validation per provider
- **Machine learning integration**: Predictive optimization timing

### 10.2 Backward Compatibility
- **Legacy system support**: Gradual migration strategies
- **Configuration versioning**: Rollback capabilities
- **Database schema evolution**: Non-breaking changes

This algorithmic analysis provides a complete understanding of the validation rules that govern when carrier optimization processes can execute, ensuring optimal timing while maintaining business rule compliance.