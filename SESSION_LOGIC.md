# Session Logic

## Overview

This document outlines the four critical session logic rules that govern optimization session management in the Altaworx SIM Card Cost Optimization system. These rules ensure proper session lifecycle management, prevent conflicts, and provide integration capabilities.

---

## 1. Prevents multiple concurrent optimizations per tenant

### What
Validates that no other optimization session is currently running for the same tenant to prevent concurrent processing conflicts and resource contention

### Why
Prevents data corruption, resource conflicts, and inconsistent results that could occur from multiple simultaneous optimization processes while ensuring data integrity and proper resource allocation per tenant

### How
Queries the database for active optimization sessions using vwOptimizationSessionRunning view and blocks new optimization attempts when existing sessions are found in non-error states

### Algorithm
```
ALGORITHM: PreventConcurrentOptimizations
INPUT: Tenant ID, Current Session Context
OUTPUT: Validation Result (Allow/Block New Session)

Step 1: Query Active Optimization Sessions
       Execute SQL query against vwOptimizationSessionRunning view
       Join with vwOptimizationSession to get most recent session for tenant
       Filter for sessions where OptimizationQueueStatusId != CompleteWithErrors
       AND OptimizationInstanceStatusId != CompleteWithErrors

Step 2: Check for Running Sessions
       If query returns no results (optimizationIdRunning < 0)
       Then return false (no running sessions, allow new optimization)
       If query returns active session ID, proceed to step 3

Step 3: Evaluate Session Status
       Store the found optimizationIdRunning value
       Log the detection of existing running optimization
       Prepare to block new session creation

Step 4: Handle Concurrent Session Detection
       Log warning message about existing optimization
       Send alert email to configured recipients
       Include details about the running session and attempted new session

Step 5: Return Concurrent Session Blocking Result
       Return true (optimization is running, block new session)
       Ensure proper cleanup and logging of blocked attempt
```

### Code Location
**File**: `QueueCarrierPlanOptimization.cs` - Lines 340-365
```csharp
private bool IsOptimizationRunning(KeySysLambdaContext context, int tenantId)
{
    LogInfo(context, "SUB", $"({tenantId})");
    var optimizationIdRunning = -1;

    var queryText = @"SELECT OptimizationSessionId FROM vwOptimizationSessionRunning sr
                        JOIN (SELECT TOP 1 * FROM vwOptimizationSession
                            WHERE TenantId = @tenantId
                            AND IsActive = 1
                            AND IsDeleted = 0
                            ORDER BY CreatedDate DESC) optf ON sr.OptimizationSessionId = optf.id
                        WHERE SR.OptimizationQueueStatusId != @optimizationStatusError OR OptimizationInstanceStatusId != @optimizationStatusError";
    
    using (var conn = new SqlConnection(context.ConnectionString))
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandType = CommandType.Text;
            cmd.CommandText = queryText;
            cmd.Parameters.AddWithValue("@optimizationStatusError", (int)OptimizationStatus.CompleteWithErrors);
            cmd.Parameters.AddWithValue("@tenantId", tenantId);
            cmd.CommandTimeout = SQLConstant.TimeoutSeconds;
            conn.Open();

            var result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
            {
                int.TryParse(result.ToString(), out optimizationIdRunning);
            }
        }
    }
```

---

## 2. Allows re-runs on final day if previous session completed

### What
Validates that multiple optimization runs are permitted on the final day of billing cycle when the previous optimization session has completed successfully

### Why
Maximizes optimization opportunities on the critical final billing day by allowing additional optimization attempts once previous sessions complete, ensuring the best possible cost savings before billing cycle ends

### How
Checks if current date equals billing period end date and verifies the last optimization instance status is "Completed" before allowing a new optimization session to start

### Algorithm
```
ALGORITHM: AllowFinalDayReRuns
INPUT: Current Date, Billing Period End Date, Previous Session Status
OUTPUT: Validation Result (Allow/Block Re-run)

Step 1: Check Final Day Condition
       Convert current UTC time to billing period timezone
       Compare currentLocalTime.Date with billingPeriod.BillingPeriodEnd.Date
       If dates are equal, mark as final billing day
       If dates are not equal, skip final day re-run logic

Step 2: Query Previous Optimization Status
       Execute SQL query against OptimizationInstance table
       Get TOP 1 OptimizationInstanceStatusId for the running session
       Order by CreatedDate DESC to get most recent instance
       Extract status value for comparison

Step 3: Validate Previous Session Completion
       Compare retrieved status with OptimizationStatus.Completed enum value
       If status equals Completed (previous optimization finished successfully)
       Then mark as eligible for re-run
       Else maintain blocking status

Step 4: Log Final Day Re-run Decision
       Log information about allowing re-run for completed session
       Include session ID and completion status for audit trail
       Record final day special processing allowance

Step 5: Return Final Day Re-run Authorization
       Return false (allow new run) if previous session completed on final day
       Continue with standard blocking if not completed or not final day
```

### Code Location
**File**: `QueueCarrierPlanOptimization.cs` - Lines 366-390
```csharp
// New logic: allow re-run if today is the last day and last optimization is completed
if (optimizationIdRunning >= 0)
{
    var currentLocalTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, billingPeriod.BillingTimeZone);

    if (currentLocalTime.Date == billingPeriod.BillingPeriodEnd.Date)
    {
        var statusQuery = @"SELECT TOP 1 OptimizationInstanceStatusId 
                            FROM OptimizationInstance 
                            WHERE OptimizationSessionId = @optimizationIdRunning 
                            ORDER BY CreatedDate DESC";

        using (var conn = new SqlConnection(context.ConnectionString))
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandType = CommandType.Text;
                cmd.CommandText = statusQuery;
                cmd.Parameters.AddWithValue("@optimizationIdRunning", optimizationIdRunning);
                conn.Open();

                var status = cmd.ExecuteScalar();
                if (status != null && (int)status == (int)OptimizationStatus.Completed)
                {
                    LogInfo(context, "INFO", $"Last optimization completed. Allowing re-run for session: {optimizationIdRunning}");
                    return false; //Allow a new run
                }
            }
        }
    }
}

return optimizationIdRunning >= 0;
```

---

## 3. Creates optimization session with unique GUID

### What
Creates a new optimization session with a unique identifier (GUID) to track and manage the entire optimization process lifecycle

### Why
Provides unique identification for each optimization run enabling proper tracking, audit trails, and integration with external systems while ensuring data integrity and preventing session conflicts

### How
Generates a new optimization session ID through database procedures and retrieves the corresponding GUID identifier for external system integration and tracking

### Algorithm
```
ALGORITHM: CreateOptimizationSessionWithGUID
INPUT: Tenant ID, Billing Period Information
OUTPUT: Session ID and Unique GUID

Step 1: Check for Existing Session in Message
       Examine SQS message attributes for "OptimizationSessionId"
       If OptimizationSessionId exists in message attributes
       Then parse existing session ID from message
       Retrieve corresponding GUID using GetOptimizationSessionGuidBySessionId

Step 2: Create New Optimization Session
       If no existing session ID in message
       Check if optimization is already running for tenant
       If not running, call StartOptimizationSession method
       Pass tenant ID and billing period information

Step 3: Generate Session Metadata
       Create billing period details object with:
       - BillPeriodId, SiteId, ServiceProviderId
       - OptimizationType, OptimizationFrom
       - BillingPeriodStartDate, BillingPeriodEndDate
       - DeviceCount, TenantId
       Serialize metadata as JSON for tracking

Step 4: Retrieve Session GUID
       Call optimizationAmopApiTrigger.GetOptimizationSessionGuidBySessionId
       Pass the session ID to get corresponding GUID
       Store GUID for external system integration

Step 5: Return Session Information
       Return both session ID and GUID for tracking
       Log session creation for audit purposes
       Prepare for AMOP 2.0 integration calls
```

### Code Location
**File**: `QueueCarrierPlanOptimization.cs` - Lines 205-235
```csharp
long optimizationSessionId;
string additionalData = null;
if (!message.MessageAttributes.ContainsKey("OptimizationSessionId"))
{
    var isOptRunning = IsOptimizationRunning(context, tenantId);
    if (!isOptRunning)
    {
        var billingPeriod = GetBillingPeriod(context, billingPeriodId.Value);
        optimizationSessionId = await StartOptimizationSession(context, tenantId, billingPeriod);
        var billPeriodDetails = optimizationAmopApiTrigger.GetBillingPeriodById(context, billingPeriodId.Value);
        var additionalDataObject = new
        {
            data = new
            {
                BillPeriodId = billingPeriodId.Value,
                SiteId = 0,
                ServiceProviderId = serviceProviderId,
                OptimizationType = 0,
                OptimizationFrom = "group",
                BillingPeriodStartDate = billPeriodDetails.BillingCycleStartDate,
                BillingPeriodEndDate = billPeriodDetails.BillingCycleEndDate,
                DeviceCount = 0,
                TenantId = tenantId,
            }
        };
        additionalData = Newtonsoft.Json.JsonConvert.SerializeObject(additionalDataObject);
        optimizationSessionGuid = optimizationAmopApiTrigger.GetOptimizationSessionGuidBySessionId(context, optimizationSessionId);
```

**Alternative Flow - Existing Session**:
**File**: `QueueCarrierPlanOptimization.cs` - Lines 250-255
```csharp
else
{
    optimizationSessionId = long.Parse(message.MessageAttributes["OptimizationSessionId"].StringValue);
    optimizationSessionGuid = optimizationAmopApiTrigger.GetOptimizationSessionGuidBySessionId(context, optimizationSessionId);
}
```

---

## 4. Tracks progress for AMOP 2.0 integration

### What
Tracks and reports optimization progress at various stages to the AMOP 2.0 system using percentage-based progress indicators and status messages

### Why
Provides real-time visibility into optimization progress for external monitoring systems and user interfaces, enabling better user experience and operational oversight of long-running processes

### How
Sends progress updates to AMOP 2.0 at key milestones using OptimizationAmopApiTrigger with percentage completion values and additional metadata

### Algorithm
```
ALGORITHM: TrackAMOP20Progress
INPUT: Session ID, Session GUID, Progress Percentage, Device Count, Additional Data
OUTPUT: Progress Update to AMOP 2.0

Step 1: Initialize Progress Tracking
       Check if isAutoCarrierOptimization flag is enabled
       If flag is false, skip AMOP 2.0 progress tracking
       If flag is true, proceed with progress reporting

Step 2: Send Initial Progress (0%)
       Call OptimizationAmopApiTrigger.SendResponseToAMOP20
       Parameters: "Progress", sessionId, sessionGuid, 0, null, 0, "", additionalData
       Indicates optimization session initialization

Step 3: Report Device Sync Progress (20%)
       After device synchronization completion
       Send progress update with 20% completion
       Include device count if available from optimization instance

Step 4: Send Data Preparation Progress (30%)
       After rate plan validation and communication grouping
       Report 30% completion to indicate preparation phase done
       Include device count and processing statistics

Step 5: Report Optimization Progress (40%)
       After optimization algorithm execution begins
       Send 40% completion with updated device count
       Track algorithm processing milestones

Step 6: Send Completion Progress (50%)
       After queue processing and optimization completion
       Report 50% completion indicating core optimization done
       Prepare for cleanup and result compilation phases

Step 7: Handle Error Reporting
       If errors occur during optimization
       Call SendResponseToAMOP20 with "ErrorMessage" type
       Include error details and context information
       Set appropriate error codes and descriptions
```

### Code Location
**File**: `QueueCarrierPlanOptimization.cs` - Multiple locations for progress tracking:

**Initial Progress (0%)**:
Lines 240-244
```csharp
if (isAutoCarrierOptimization)
{
    OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, 0, null, 0, "", additionalData);
}
```

**Device Sync Progress (20%)**:
Lines 260-264
```csharp
if (isAutoCarrierOptimization)
{
    OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, 0, null, 20, "", additionalData);
}
```

**Data Preparation Progress (30%)**:
Lines 275-280
```csharp
if (isAutoCarrierOptimization)
{
    deviceCount = optimizationAmopApiTrigger.GetOptimizationDeviceCount(context, instanceId, "M2M");
    OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, deviceCount, null, 30, "", additionalData);
}
```

**Optimization Progress (40%)**:
Lines 320-324
```csharp
if (isAutoCarrierOptimization)
{
    OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, deviceCount, null, 40, "", additionalData);
}
```

**Final Progress (50%)**:
Lines 325-329
```csharp
if (isAutoCarrierOptimization)
{
    OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "Progress", optimizationSessionId.ToString(), optimizationSessionGuid, 0, null, 50, "", additionalData);
}
```

**Error Reporting**:
Lines 408-410
```csharp
OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, "One or more Rate Plans have invalid Data per Overage Charge or Overage Rate", 0, "", additionalData);
```

---

## Summary

These four session logic rules work together to ensure:

1. **Resource Protection**: Prevent concurrent optimizations that could cause conflicts (concurrent session prevention)
2. **Final Day Optimization**: Maximize optimization opportunities on critical billing day (final day re-runs)
3. **Unique Tracking**: Provide proper identification and audit trails (unique GUID creation)
4. **External Integration**: Enable real-time monitoring and user experience (AMOP 2.0 progress tracking)

The session logic is implemented across multiple methods in the `QueueCarrierPlanOptimization.cs` file, providing a comprehensive session management framework that balances operational safety with optimization effectiveness.