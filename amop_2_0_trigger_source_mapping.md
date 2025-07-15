# AMOP 2.0 Trigger Source Mapping - Where Each Trigger Originates

## Executive Summary

**Yes, ALL AMOP 2.0 triggers currently start inside the `QueueCarrierPlanOptimization` Lambda function.** No other Lambda functions in the system send AMOP 2.0 triggers. The cleanup and device sync lambdas do NOT send any AMOP triggers.

## Detailed Trigger Source Mapping

### Lambda Function Trigger Responsibility

| **Lambda Function** | **Sends AMOP Triggers?** | **Trigger Count** | **Purpose** |
|---------------------|---------------------------|-------------------|-------------|
| **QueueCarrierPlanOptimization** | ✅ **YES** | **5 Progress + Error** | **Main orchestrator** |
| **AltaworxJasperAWSGetDevicesQueue** | ❌ **NO** | **0** | Device sync only |
| **AltaworxSimCardCostOptimizer** | ❌ **NO** | **0** | Cost calculation only |
| **AltaworxSimCardCostOptimizerCleanup** | ❌ **NO** | **0** | Cleanup only |

## Complete AMOP 2.0 Trigger Flow with Source Location

```
┌────────────────────────────────────────────────────────────────────────────────┐
│                    QueueCarrierPlanOptimization Lambda                         │
│                         (SINGLE TRIGGER SOURCE)                               │
└────────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
              ┌─────────────────────────────────────────┐
              │         AMOP 2.0 TRIGGER SEQUENCE       │
              │        (ALL FROM SAME LAMBDA)           │
              └─────────────────────────────────────────┘
                                    │
                    ┌───────────────┼───────────────┐
                    │               │               │
                    ▼               ▼               ▼
        ┌─────────────────┐ ┌─────────────────┐ ┌─────────────────┐
        │   Progress      │ │   Progress      │ │   Error         │
        │   Triggers      │ │   Updates       │ │   Triggers      │
        │   (0%-50%)      │ │   (Real-time)   │ │   (As needed)   │
        └─────────────────┘ └─────────────────┘ └─────────────────┘
```

## Trigger #1: Session Initialization (0%)
**Source Lambda:** `QueueCarrierPlanOptimization`  
**File:** `QueueCarrierPlanOptimization.cs`  
**Line:** 250  
**Method:** `Handler()` → Session creation logic

```csharp
// QueueCarrierPlanOptimization.cs - Line 250
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "Progress", 
    optimizationSessionId.ToString(), 
    optimizationSessionGuid, 
    0, 
    null, 
    0,          // 0% Progress
    "", 
    additionalData
);
```

**Trigger Condition:**
```csharp
// After creating new optimization session
optimizationSessionId = await StartOptimizationSession(context, tenantId, billingPeriod);
optimizationSessionGuid = optimizationAmopApiTrigger.GetOptimizationSessionGuidBySessionId(context, optimizationSessionId);
// → TRIGGER SENT HERE
```

## Trigger #2: Device Sync Progress (20%)
**Source Lambda:** `QueueCarrierPlanOptimization`  
**File:** `QueueCarrierPlanOptimization.cs`  
**Line:** 277  
**Method:** `Handler()` → After queuing device sync

```csharp
// QueueCarrierPlanOptimization.cs - Line 277
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "Progress", 
    optimizationSessionId.ToString(), 
    optimizationSessionGuid, 
    0, 
    null, 
    20,         // 20% Progress
    "", 
    additionalData
);
```

**Trigger Condition:**
```csharp
// After enqueueing device sync to SQS
await EnqueueGetDeviceListAsync(_deviceSyncQueueUrl, serviceProviderId, 1, lastSyncDate, awsCredentials, logger, optimizationSessionId);
// → TRIGGER SENT HERE
```

## Trigger #3: Communication Grouping (30%)
**Source Lambda:** `QueueCarrierPlanOptimization`  
**File:** `QueueCarrierPlanOptimization.cs`  
**Line:** 297  
**Method:** `Handler()` → After device count validation

```csharp
// QueueCarrierPlanOptimization.cs - Line 297
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "Progress", 
    optimizationSessionId.ToString(), 
    optimizationSessionGuid, 
    deviceCount, 
    null, 
    30,         // 30% Progress
    "", 
    additionalData
);
```

**Trigger Condition:**
```csharp
// After getting device count and validation
instanceId = optimizationAmopApiTrigger.GetInstancebySessionId(context, optimizationSessionId.ToString());
var deviceCount = GetOptimizationDeviceCount(context, instanceId);
// → TRIGGER SENT HERE
```

## Trigger #4: Rate Pool Generation (40%)
**Source Lambda:** `QueueCarrierPlanOptimization`  
**File:** `QueueCarrierPlanOptimization.cs`  
**Line:** 353  
**Method:** `RunOptimization()` → After creating communication groups

```csharp
// QueueCarrierPlanOptimization.cs - Line 353
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "Progress", 
    optimizationSessionId.ToString(), 
    optimizationSessionGuid, 
    deviceCount, 
    null, 
    40,         // 40% Progress
    "", 
    additionalData
);
```

**Trigger Condition:**
```csharp
// After processing communication groups and creating rate pools
foreach (var commGroup in commGroups)
{
    // Process rate pools...
}
// → TRIGGER SENT HERE
```

## Trigger #5: Optimization Processing Start (50%)
**Source Lambda:** `QueueCarrierPlanOptimization`  
**File:** `QueueCarrierPlanOptimization.cs`  
**Line:** 315  
**Method:** `Handler()` → After starting optimization processing

```csharp
// QueueCarrierPlanOptimization.cs - Line 315
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "Progress", 
    optimizationSessionId.ToString(), 
    optimizationSessionGuid, 
    0, 
    null, 
    50,         // 50% Progress
    "", 
    additionalData
);
```

**Trigger Condition:**
```csharp
// After starting optimization by portal type
await RunOptimizationByPortalType(context, serviceProviderRepository, billingPeriodId.Value, serviceProviderId, tenantId, optimizationSessionId, portalType, additionalData);
// → TRIGGER SENT HERE
```

## Error Triggers: Rate Plan Validation Failures
**Source Lambda:** `QueueCarrierPlanOptimization`  
**Files & Lines:**
- Line 499: Invalid rate plan charges
- Line 578: No communication groups  
- Line 702: Insufficient groups for optimization

### Error Trigger Example:
```csharp
// QueueCarrierPlanOptimization.cs - Line 499
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "ErrorMessage", 
    optimizationSessionId.ToString(), 
    null, 
    0, 
    "One or more Rate Plans have invalid Data per Overage Charge or Overage Rate", 
    0,          // 0% Progress (Error)
    "", 
    additionalData
);
```

## Lambda Functions That DO NOT Send AMOP Triggers

### 1. AltaworxJasperAWSGetDevicesQueue Lambda
**Purpose:** Device synchronization with carrier APIs  
**AMOP Triggers:** ❌ **NONE**  
**Why:** This lambda only retrieves device data and updates staging tables. It sends **NO** AMOP triggers.

### 2. AltaworxSimCardCostOptimizer Lambda  
**Purpose:** Cost calculation and optimization algorithms  
**AMOP Triggers:** ❌ **NONE**  
**Why:** This lambda processes optimization queues but sends **NO** AMOP triggers.

### 3. AltaworxSimCardCostOptimizerCleanup Lambda
**Purpose:** Results cleanup and report generation  
**AMOP Triggers:** ❌ **NONE**  
**Why:** This lambda marks instances complete and generates reports but sends **NO** AMOP triggers.

## Process Flow with Lambda Responsibility

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              TRIGGER FLOW                                      │
└─────────────────────────────────────────────────────────────────────────────────┘

CloudWatch Cron
       │
       ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│             QueueCarrierPlanOptimization Lambda                                 │
│                    (SINGLE AMOP TRIGGER SOURCE)                                │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                 │
│ 🎯 TRIGGER #1: Session Init (0%) ──────────────────► AMOP 2.0                  │
│        ↓                                                                        │
│ 🎯 TRIGGER #2: Device Sync (20%) ──────────────────► AMOP 2.0                  │
│        ↓                                                                        │
│        │ ┌─────────────────────────────────────────────────────────────────────┤
│        │ │ AltaworxJasperAWSGetDevicesQueue Lambda                             │
│        │ │ • Retrieves device data                                             │
│        │ │ • NO AMOP triggers sent                                             │
│        │ └─────────────────────────────────────────────────────────────────────┤
│        ↓                                                                        │
│ 🎯 TRIGGER #3: Comm Grouping (30%) ────────────────► AMOP 2.0                  │
│        ↓                                                                        │
│ 🎯 TRIGGER #4: Rate Pools (40%) ───────────────────► AMOP 2.0                  │
│        ↓                                                                        │
│ 🎯 TRIGGER #5: Processing Start (50%) ─────────────► AMOP 2.0                  │
│        ↓                                                                        │
│        │ ┌─────────────────────────────────────────────────────────────────────┤
│        │ │ AltaworxSimCardCostOptimizer Lambda                                 │
│        │ │ • Processes optimization queues                                     │
│        │ │ • NO AMOP triggers sent                                             │
│        │ └─────────────────────────────────────────────────────────────────────┤
│        ↓                                                                        │
│        │ ┌─────────────────────────────────────────────────────────────────────┤
│        │ │ AltaworxSimCardCostOptimizerCleanup Lambda                          │
│        │ │ • Marks instances complete                                          │
│        │ │ • Generates reports                                                 │
│        │ │ • NO AMOP triggers sent                                             │
│        │ │ • ❌ NO 90% or 100% triggers                                        │
│        │ └─────────────────────────────────────────────────────────────────────┤
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
```

## Key Findings

### 1. **Single Point of AMOP Integration**
- **ALL** AMOP 2.0 triggers originate from `QueueCarrierPlanOptimization` Lambda
- **NO** other Lambda functions send AMOP triggers
- This creates a centralized trigger management point

### 2. **Missing Completion Triggers**
- Process stops at **50%** from AMOP 2.0 perspective
- Cleanup Lambda does **NOT** send completion triggers
- **90%** and **100%** progress updates are missing

### 3. **Error Handling Centralized**
- All error triggers also come from `QueueCarrierPlanOptimization`
- Other Lambdas may fail but don't notify AMOP 2.0 directly

## Recommendation

**Add completion triggers in the Cleanup Lambda:**

```csharp
// In AltaworxSimCardCostOptimizerCleanup.cs after line 352
var endTime = StopOptimizationInstance(context, instanceId, OptimizationStatus.CompleteWithSuccess);

// ADD THESE TRIGGERS:
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "Progress", 
    instance.SessionId.ToString(), 
    sessionGuid, 
    totalDevices, 
    null, 
    100,    // 100% Complete
    "Optimization Complete", 
    completionData
);
```

---

**Summary**: **QueueCarrierPlanOptimization is the ONLY Lambda that sends AMOP 2.0 triggers** - all 5 progress triggers and all error triggers originate from this single function.