# Grouping Logic

## Overview

This document outlines the four critical grouping logic rules that govern device organization and rate plan management in the Altaworx SIM Card Cost Optimization system. These rules ensure efficient optimization processing, validate rate plan eligibility, and enforce business constraints.

---

## 1. Groups devices by RatePlanIds field from communication plans

### What
Groups devices into optimization communication groups based on the RatePlanIds field from communication plans, allowing devices with similar rate plan configurations to be optimized together

### Why
Enables parallel processing of optimization by grouping devices with similar characteristics, improves optimization efficiency by reducing the complexity of rate plan permutations, and ensures logical organization of devices for meaningful optimization comparisons

### How
Iterates through communication plans, filters out plans with empty RatePlanIds, and uses LINQ GroupBy operation on the RatePlanIds field to create distinct device groups for optimization processing

### Algorithm
```
ALGORITHM: GroupDevicesByRatePlanIds
INPUT: Communication Plans List, Rate Plans List
OUTPUT: Grouped Communication Plan Collections

Step 1: Filter Communication Plans
       Examine all communication plans in the collection
       Filter out plans where RatePlanIds field is null or whitespace
       Apply Where clause: Where(x => !string.IsNullOrWhiteSpace(x.RatePlanIds))

Step 2: Group by RatePlanIds Field
       Apply GroupBy operation on filtered communication plans
       Group key: x => x.RatePlanIds (comma-separated rate plan IDs)
       Create distinct groups for each unique RatePlanIds combination

Step 3: Create Communication Group for Each Grouping
       For each grouped collection of communication plans
       Call CreateCommPlanGroup to create optimization group
       Generate unique commPlanGroupId for group identification

Step 4: Add Communication Plans to Group
       For each communication plan in the group
       Call AddCommPlansToCommPlanGroup method
       Associate all plans with the same RatePlanIds to single group
       Prepare for rate plan validation and optimization processing

Step 5: Extract Rate Plans for Group
       Call RatePlansForGroup method with:
       - Complete rate plans list
       - Current communication plan group
       Parse RatePlanIds (comma-separated) to individual plan IDs
       Retrieve corresponding RatePlan objects for group processing
```

### Code Location
**File**: `QueueCarrierPlanOptimization.cs` - Lines 469-478
```csharp
// create comm plan groups
// each comm plan groups insert optimization sim cards
foreach (var commPlanGroup in commPlans.Where(x => !string.IsNullOrWhiteSpace(x.RatePlanIds)).GroupBy(x => x.RatePlanIds))
{
    // create new comm plan group
    long commPlanGroupId = CreateCommPlanGroup(context, instance.Id);
    commPlanGroupIds.Add(commPlanGroupId);

    // add comm plans to comm plan group
    AddCommPlansToCommPlanGroup(context, instance.Id, commPlanGroupId, commPlanGroup);

    // get rate plans for group
    var groupRatePlans = RatePlansForGroup(ratePlans, commPlanGroup);
```

**Supporting Code - Rate Plans Extraction**:
**File**: `QueueCarrierPlanOptimization.cs` - Lines 751-765
```csharp
private List<RatePlan> RatePlansForGroup(List<RatePlan> ratePlans, IGrouping<string, CommPlan> commPlanGroup)
{
    var ratePlanIds = commPlanGroup.Key;
    var ratePlanIdList = ratePlanIds.Split(',').Distinct().ToList();
    List<RatePlan> groupRatePlans = new List<RatePlan>();
    foreach (var planId in ratePlanIdList)
    {
        var ratePlan = ratePlans.FirstOrDefault(x => x.Id.ToString() == planId);
        if (ratePlan.Id.ToString() == planId)
        {
            groupRatePlans.Add(ratePlan);
        }
    }

    return groupRatePlans;
}
```

---

## 2. Validates rate plan eligibility (overage_rate > 0, data_per_overage_charge > 0)

### What
Validates that all rate plans in each optimization group have valid overage rates and data per overage charge values greater than zero to ensure accurate cost calculations

### Why
Prevents optimization failures by ensuring all rate plans have proper cost calculation parameters, avoids division by zero errors in optimization algorithms, and ensures accurate overage cost calculations for meaningful optimization results

### How
Examines each rate plan's DataPerOverageCharge and OverageRate properties using LINQ Any method to check for invalid values (less than or equal to zero) and stops optimization with error status if invalid plans are found

### Algorithm
```
ALGORITHM: ValidateRatePlanEligibility
INPUT: Group Rate Plans Collection
OUTPUT: Validation Result (Pass/Fail with Error Handling)

Step 1: Examine Rate Plan Overage Parameters
       For each rate plan in groupRatePlans collection
       Check DataPerOverageCharge property value
       Check OverageRate property value
       Apply validation logic: property <= 0 indicates invalid plan

Step 2: Apply LINQ Validation Query
       Use Any() method to check if any rate plan fails validation
       Condition: groupRatePlan.DataPerOverageCharge <= 0 OR groupRatePlan.OverageRate <= 0
       Return true if any rate plan has invalid overage parameters

Step 3: Handle Invalid Rate Plans
       If validation fails (Any() returns true)
       Log error: "One or more Rate Plans have invalid Data per Overage Charge or Overage Rate"
       Call StopOptimizationInstance with CompleteWithErrors status
       Set instance status to prevent further processing

Step 4: Send Error Notification to AMOP 2.0
       Call OptimizationAmopApiTrigger.SendResponseToAMOP20 with:
       - Message type: "ErrorMessage"
       - Session ID and other context information
       - Error message describing invalid rate plan issue
       - Progress percentage: 0 (indicates failure)

Step 5: Terminate Processing
       Return from optimization method early
       Prevent creation of optimization queues
       Ensure no further processing with invalid rate plans
```

### Code Location
**File**: `QueueCarrierPlanOptimization.cs` - Lines 494-502
```csharp
//check rate plans 
if (groupRatePlans.Any(groupRatePlan => groupRatePlan.DataPerOverageCharge <= 0 || groupRatePlan.OverageRate <= 0))
{
    LogInfo(context, "ERROR", "One or more Rate Plans have invalid Data per Overage Charge or Overage Rate");
    StopOptimizationInstance(context, instance.Id, OptimizationStatus.CompleteWithErrors);
    //triggger AMOP2.0 to send error message
    OptimizationAmopApiTrigger.SendResponseToAMOP20(context, "ErrorMessage", optimizationSessionId.ToString(), null, 0, "One or more Rate Plans have invalid Data per Overage Charge or Overage Rate", 0, "", additionalData);
    return;
}
```

**Alternative Validation for Mobility (CheckZeroValueRatePlans)**:
**File**: `QueueCarrierPlanOptimization.cs` - Lines 623-627
```csharp
// Check valid rate plans
if (CheckZeroValueRatePlans(context, instance.Id, groupRatePlans, shouldStopInstance: true))
{
    break;
}
```

---

## 3. Creates optimization communication groups for parallel processing

### What
Creates optimization communication groups that enable parallel processing of device optimization by organizing related communication plans into discrete processing units

### Why
Enables concurrent optimization processing across multiple groups, improves system performance and scalability by allowing parallel execution, and provides logical separation of optimization workloads for better resource utilization

### How
Calls CreateCommPlanGroup method to generate unique group identifiers, associates communication plans with groups through AddCommPlansToCommPlanGroup method, and maintains group ID collections for parallel queue processing

### Algorithm
```
ALGORITHM: CreateOptimizationCommunicationGroups
INPUT: Communication Plan Groups, Instance ID
OUTPUT: Communication Group IDs for Parallel Processing

Step 1: Initialize Group Management
       Create commPlanGroupIds list to track all created groups
       Initialize actualSimCount counter for device tracking
       Prepare for parallel processing coordination

Step 2: Create Individual Communication Group
       For each distinct communication plan group
       Call CreateCommPlanGroup(context, instance.Id)
       Generate unique commPlanGroupId for group identification
       Add generated ID to commPlanGroupIds collection

Step 3: Associate Communication Plans with Group
       Call AddCommPlansToCommPlanGroup method with:
       - Context and instance ID
       - Generated commPlanGroupId
       - Communication plan group collection
       Bulk insert communication plan associations to database

Step 4: Prepare Group for Optimization Processing
       Extract rate plans for the group
       Validate rate plan eligibility and count limits
       Create rate pool collections for optimization algorithms
       Assign devices to baseline rate plans

Step 5: Enable Parallel Queue Processing
       Add valid group IDs to commPlanGroupIds collection
       Remove invalid groups from processing list
       Call EnqueueOptimizationRunsAsync with group ID collection
       Enable parallel processing across multiple optimization groups
```

### Code Location
**File**: `QueueCarrierPlanOptimization.cs` - Lines 472-473
```csharp
// create new comm plan group
long commPlanGroupId = CreateCommPlanGroup(context, instance.Id);
commPlanGroupIds.Add(commPlanGroupId);
```

**File**: `QueueCarrierPlanOptimization.cs` - Lines 475-476
```csharp
// add comm plans to comm plan group
AddCommPlansToCommPlanGroup(context, instance.Id, commPlanGroupId, commPlanGroup);
```

**Supporting Code - Group Association**:
**File**: `QueueCarrierPlanOptimization.cs` - Lines 766-803
```csharp
private void AddCommPlansToCommPlanGroup(KeySysLambdaContext context, long instanceId, long commPlanGroupId, IGrouping<string, CommPlan> commPlanGroup)
{
    LogInfo(context, "SUB", "AddCommPlansToCommPlanGroup");

    DataTable table = new DataTable();
    table.Columns.Add("InstanceId", typeof(long));
    table.Columns.Add("CommGroupId", typeof(long));
    table.Columns.Add("CommPlanId", typeof(int));
    table.Columns.Add("CreatedBy");
    table.Columns.Add("CreatedDate", typeof(DateTime));

    foreach (CommPlan plan in commPlanGroup)
    {
        var dr = table.NewRow();

        dr[0] = instanceId;
        dr[1] = commPlanGroupId;
        dr[2] = plan.Id;
        dr[3] = "System";
        dr[4] = DateTime.UtcNow;

        table.Rows.Add(dr);
    }

    List<SqlBulkCopyColumnMapping> columnMappings = new List<SqlBulkCopyColumnMapping>()
    {
        new SqlBulkCopyColumnMapping("InstanceId", "InstanceId"),
        new SqlBulkCopyColumnMapping("CommGroupId", "CommGroupId"),
        new SqlBulkCopyColumnMapping("CommPlanId", "CommPlanId"),
        new SqlBulkCopyColumnMapping("CreatedBy", "CreatedBy"),
        new SqlBulkCopyColumnMapping("CreatedDate", "CreatedDate")
    };

    var logMessage = SqlHelper.SqlBulkCopy(context.ConnectionString, table, "OptimizationCommGroup_CommPlan", columnMappings);
    LogInfo(context, logMessage);
}
```

**Parallel Processing Enablement**:
**File**: `QueueCarrierPlanOptimization.cs` - Lines 561-564
```csharp
// queue comm plan groups rate plan permutations
await EnqueueOptimizationRunsAsync(context, instance.Id, commPlanGroupIds, OptimizationChargeType.RateChargeAndOverage, QueuesPerInstance);
```

---

## 4. Enforces 15 rate plan limit per group

### What
Enforces a maximum limit of 15 rate plans per optimization group to prevent performance degradation and ensure manageable optimization processing complexity

### Why
Prevents exponential growth in rate plan permutations that could cause performance issues, ensures optimization algorithms complete within reasonable time limits, and maintains system stability by controlling computational complexity

### How
Checks the count of rate plans in each group against the limit constant (15), sends alert emails when limit is exceeded, and continues processing with a warning rather than stopping optimization entirely

### Algorithm
```
ALGORITHM: EnforceRatePlanLimit
INPUT: Group Rate Plans Collection, Instance Information
OUTPUT: Limit Enforcement with Alerting

Step 1: Check Rate Plan Count
       Get count of rate plans in groupRatePlans collection
       Compare count against limit: groupRatePlans.Count > 15
       Determine if limit enforcement action is required

Step 2: Handle Limit Exceeded Scenario
       If count exceeds 15 rate plans
       Log error message with instance ID
       Format: "The rate plan count exceeds the limit of 15 for Instance: {instance.Id}"
       Record limit violation for audit purposes

Step 3: Send Alert Email Notification
       Call SendCarrierPlanLimitAlertEmail method with:
       - Context and instance information
       - Default limit value (OptimizationConstant.RatePlanLimit)
       Email recipients from optimization settings
       Include instance ID and limit details in email body

Step 4: Handle Empty Rate Plan Group
       If groupRatePlans.Count == 0
       Log warning: "The rate plan count is zero for this comm plan group"
       Skip optimization processing for empty group
       Continue with next communication group

Step 5: Continue Processing Despite Limit Violation
       System continues with optimization processing
       Rate plan limit is warning rather than hard stop
       Allows optimization to proceed with reduced efficiency
       Provides operational feedback without blocking business process
```

### Code Location
**File**: `QueueCarrierPlanOptimization.cs` - Lines 481-490
```csharp
//Rate plans are limited to 15. If the count is greater than 15 send an error email
if (groupRatePlans.Count > 15)
{
    LogInfo(context, "ERROR", $"The rate plan count exceeds the limit of 15 for Instance: {instance.Id}");
    SendCarrierPlanLimitAlertEmail(context, instance);
}
else if (groupRatePlans.Count == 0)
{
    LogInfo(context, "WARNING", $"The rate plan count is zero for this comm plan group");
}
```

**Supporting Code - Alert Email Method**:
**File**: `QueueCarrierPlanOptimization.cs` - Lines 881-916
```csharp
private void SendCarrierPlanLimitAlertEmail(KeySysLambdaContext context, OptimizationInstance instance, int ratePlanLimit = OptimizationConstant.RatePlanLimit)
{
    var credentials = context.GeneralProviderSettings.AwsSesCredentials;
    using (var client = new AmazonSimpleEmailServiceClient(credentials, RegionEndpoint.USEast1))
    {
        var message = new MimeMessage();

        message.From.Add(MailboxAddress.Parse(context.OptimizationSettings.FromEmailAddress));
        var recipientAddressList = context.OptimizationSettings.ToEmailAddresses.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        message.Subject = "Carrier Plan optimization Error: Rate Plans limit exceeded";

        foreach (var recipientAddress in recipientAddressList)
        {
            message.To.Add(MailboxAddress.Parse(recipientAddress));
        }

        message.Body = BuildAlertEmailBody(context, instance, ratePlanLimit).ToMessageBody();
        // ... email sending logic
    }
}
```

**Mobility Optimization Limit (Different Constant)**:
**File**: `QueueCarrierPlanOptimization.cs` - Lines 617-620
```csharp
// Rate plans of an optimization group are limited since we will generate permutation. If more than that, send an error email
if (groupRatePlans.Count > OptimizationConstant.MobilityCarrierRatePlanLimit)
{
    LogInfo(context, CommonConstants.WARNING, string.Format(LogCommonStrings.OPTIMIZATION_GROUP_RATE_PLAN_LIMIT_ERROR, OptimizationConstant.MobilityCarrierRatePlanLimit, optimizationGroup.Name, optimizationGroup.Id));
    SendCarrierPlanLimitAlertEmail(context, instance, OptimizationConstant.MobilityCarrierRatePlanLimit);
}
```

---

## Summary

These four grouping logic rules work together to ensure:

1. **Logical Organization**: Devices are grouped by rate plan characteristics for meaningful optimization (RatePlanIds grouping)
2. **Data Quality**: Only valid rate plans with proper overage parameters are used (eligibility validation)
3. **Performance Optimization**: Parallel processing enabled through communication group creation (parallel processing)
4. **System Stability**: Rate plan complexity controlled through enforced limits (15 rate plan limit)

The grouping logic is implemented primarily in the `RunOptimization` and `RunMobilityOptimization` methods within `QueueCarrierPlanOptimization.cs`, providing a comprehensive device organization framework that balances optimization effectiveness with system performance and stability.