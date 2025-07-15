# Updated AMOP 2.0 Trigger Flowchart - All Lambda Functions Mapped

## Complete Carrier Optimization Pipeline with Lambda Function Mapping

```
                    ┌─────────────────────────────────────────────────────────────┐
                    │            CARRIER OPTIMIZATION PIPELINE                     │
                    │                                                             │
                    │  ┌─────────────────┐                                        │
                    │  │ CloudWatch Cron │                                        │
                    │  │    Trigger      │                                        │
                    │  └─────────┬───────┘                                        │
                    │            │                                                │
                    │            ▼                                                │
                    │  ┌─────────────────┐                                        │
                    │  │QueueCarrierPlan │ ◄─── ONLY LAMBDA THAT SENDS           │
                    │  │  Optimization   │      AMOP 2.0 TRIGGERS                │
                    │  │     Lambda      │                                        │
                    │  └─────────┬───────┘                                        │
                    │            │                                                │
                    │            ▼                                                │
                    └────────────┼────────────────────────────────────────────────┘
                                 │
                                 ▼
┌────────────────────────────────────────────────────────────────────────────────────┐
│                        AMOP 2.0 TRIGGER DECISION TREE                             │
│                    (ALL TRIGGERS FROM QueueCarrierPlanOptimization)               │
└────────────────────────────────────────────────────────────────────────────────────┘
                                 │
                                 ▼
                    ┌─────────────────────────┐
                    │   Initialize Session    │
                    │                         │
                    │ • Check running sessions│
                    │ • Create session ID     │
                    │ • Generate session GUID │
                    └─────────┬───────────────┘
                              │
                              ▼
         ┌────────────────────────────────────────────────────────────┐
         │                TRIGGER POINT #1                            │
         │              Session Initialization                        │
         │        (QueueCarrierPlanOptimization Line 250)             │
         └────────────────────────────────────────────────────────────┘
                              │
                              ▼
    ┌─────────────────────────────────────────────────────────────────────┐
    │  OptimizationAmopApiTrigger.SendResponseToAMOP20(                  │
    │      context: ILambdaContext,                                      │
    │      messageType: "Progress",                                      │
    │      sessionId: optimizationSessionId.ToString(),                  │
    │      sessionGuid: optimizationSessionGuid,                         │
    │      deviceCount: 0,                                               │
    │      errorMessage: null,                                           │
    │      progressPercentage: 0,                                        │
    │      additionalInfo: "",                                           │
    │      additionalData: additionalData                                │
    │  );                                                                │
    └─────────────────────────┬───────────────────────────────────────────┘
                              │
                              ▼
                    ┌─────────────────────────┐
                    │   Queue Device Sync     │
                    │                         │
                    │ • Check sync strategy   │
                    │ • Queue SQS message     │
                    │ • Start device retrieval│
                    └─────────┬───────────────┘
                              │
                              ▼
         ┌────────────────────────────────────────────────────────────┐
         │                TRIGGER POINT #2                            │
         │               Device Sync Progress                         │
         │        (QueueCarrierPlanOptimization Line 277)             │
         └────────────────────────────────────────────────────────────┘
                              │
                              ▼
    ┌─────────────────────────────────────────────────────────────────────┐
    │  OptimizationAmopApiTrigger.SendResponseToAMOP20(                  │
    │      context: ILambdaContext,                                      │
    │      messageType: "Progress",                                      │
    │      sessionId: optimizationSessionId.ToString(),                  │
    │      sessionGuid: optimizationSessionGuid,                         │
    │      deviceCount: 0,                                               │
    │      errorMessage: null,                                           │
    │      progressPercentage: 20,                                       │
    │      additionalInfo: "",                                           │
    │      additionalData: additionalData                                │
    │  );                                                                │
    └─────────────────────────┬───────────────────────────────────────────┘
                              │
                              ▼
    ┌─────────────────────────────────────────────────────────────────────┐
    │           AltaworxJasperAWSGetDevicesQueue Lambda                   │
    │                    (DEVICE SYNC EXECUTION)                         │
    │                                                                     │
    │ • Retrieves device data from carrier APIs                          │
    │ • Updates staging tables                                            │
    │ • Processes device information                                      │
    │ • ❌ SENDS NO AMOP 2.0 TRIGGERS                                     │
    │                                                                     │
    └─────────────────────────┬───────────────────────────────────────────┘
                              │
                              ▼
                    ┌─────────────────────────┐
                    │  Communication Grouping │
                    │                         │
                    │ • Group devices by plan │
                    │ • Validate group sizes  │
                    │ • Check eligibility     │
                    └─────────┬───────────────┘
                              │
                              ▼
         ┌────────────────────────────────────────────────────────────┐
         │                TRIGGER POINT #3                            │
         │            Communication Grouping Complete                 │
         │        (QueueCarrierPlanOptimization Line 297)             │
         └────────────────────────────────────────────────────────────┘
                              │
                              ▼
    ┌─────────────────────────────────────────────────────────────────────┐
    │  OptimizationAmopApiTrigger.SendResponseToAMOP20(                  │
    │      context: ILambdaContext,                                      │
    │      messageType: "Progress",                                      │
    │      sessionId: optimizationSessionId.ToString(),                  │
    │      sessionGuid: optimizationSessionGuid,                         │
    │      deviceCount: deviceCount,                                     │
    │      errorMessage: null,                                           │
    │      progressPercentage: 30,                                       │
    │      additionalInfo: "",                                           │
    │      additionalData: additionalData                                │
    │  );                                                                │
    └─────────────────────────┬───────────────────────────────────────────┘
                              │
                              ▼
                    ┌─────────────────────────┐
                    │  Rate Plan Validation   │
                    │                         │
                    │ • Check rate plan data  │
                    │ • Validate overage rates│
                    │ • Check data charges    │
                    └─────────┬───────────────┘
                              │
                              ▼
                    ┌─────────────────────────┐
                    │    Validation Result    │
                    │         Check           │
                    └─────────┬───────────────┘
                              │
                ┌─────────────┴─────────────┐
                │                           │
                ▼                           ▼
    ┌─────────────────────┐      ┌─────────────────────┐
    │   Valid Rate Plans  │      │  Invalid Rate Plans │
    │                     │      │                     │
    │ Continue Process    │      │   Send Error        │
    └─────────┬───────────┘      │   Trigger           │
              │                  └─────────┬───────────┘
              │                            │
              │                            ▼
              │           ┌────────────────────────────────────────────────────────────┐
              │           │                ERROR TRIGGER                               │
              │           │             Rate Plan Validation Failed                   │
              │           │        (QueueCarrierPlanOptimization Line 499)            │
              │           └────────────────────────────────────────────────────────────┘
              │                            │
              │                            ▼
              │         ┌─────────────────────────────────────────────────────────────────────┐
              │         │  OptimizationAmopApiTrigger.SendResponseToAMOP20(                  │
              │         │      context: ILambdaContext,                                      │
              │         │      messageType: "ErrorMessage",                                  │
              │         │      sessionId: optimizationSessionId.ToString(),                  │
              │         │      sessionGuid: null,                                            │
              │         │      deviceCount: 0,                                               │
              │         │      errorMessage: "One or more Rate Plans have invalid           │
              │         │                    Data per Overage Charge or Overage Rate",      │
              │         │      progressPercentage: 0,                                        │
              │         │      additionalInfo: "",                                           │
              │         │      additionalData: additionalData                                │
              │         │  );                                                                │
              │         └─────────────────────────┬───────────────────────────────────────────┘
              │                                   │
              │                                   ▼
              │                         ┌─────────────────────┐
              │                         │   Stop Process      │
              │                         │   Mark as Failed    │
              │                         │   Exit Pipeline     │
              │                         └─────────────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │  Rate Pool Generation   │
    │                         │
    │ • Calculate permutations│
    │ • Create rate pools     │
    │ • Generate collections  │
    └─────────┬───────────────┘
              │
              ▼
         ┌────────────────────────────────────────────────────────────┐
         │                TRIGGER POINT #4                            │
         │              Rate Pool Generation Complete                 │
         │        (QueueCarrierPlanOptimization Line 353)             │
         └────────────────────────────────────────────────────────────┘
              │
              ▼
    ┌─────────────────────────────────────────────────────────────────────┐
    │  OptimizationAmopApiTrigger.SendResponseToAMOP20(                  │
    │      context: ILambdaContext,                                      │
    │      messageType: "Progress",                                      │
    │      sessionId: optimizationSessionId.ToString(),                  │
    │      sessionGuid: optimizationSessionGuid,                         │
    │      deviceCount: deviceCount,                                     │
    │      errorMessage: null,                                           │
    │      progressPercentage: 40,                                       │
    │      additionalInfo: "",                                           │
    │      additionalData: additionalData                                │
    │  );                                                                │
    └─────────────────────────┬───────────────────────────────────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │ Optimization Execution  │
    │                         │
    │ • Generate queues       │
    │ • Process cost calc     │
    │ • Execute algorithms    │
    └─────────┬───────────────┘
              │
              ▼
         ┌────────────────────────────────────────────────────────────┐
         │                TRIGGER POINT #5                            │
         │            Optimization Processing Initiated               │
         │        (QueueCarrierPlanOptimization Line 315)             │
         └────────────────────────────────────────────────────────────┘
              │
              ▼
    ┌─────────────────────────────────────────────────────────────────────┐
    │  OptimizationAmopApiTrigger.SendResponseToAMOP20(                  │
    │      context: ILambdaContext,                                      │
    │      messageType: "Progress",                                      │
    │      sessionId: optimizationSessionId.ToString(),                  │
    │      sessionGuid: optimizationSessionGuid,                         │
    │      deviceCount: 0,                                               │
    │      errorMessage: null,                                           │
    │      progressPercentage: 50,                                       │
    │      additionalInfo: "",                                           │
    │      additionalData: additionalData                                │
    │  );                                                                │
    └─────────────────────────┬───────────────────────────────────────────┘
              │
              ▼
    ┌─────────────────────────────────────────────────────────────────────┐
    │              AltaworxSimCardCostOptimizer Lambda                    │
    │                     (COST CALCULATION EXECUTION)                   │
    │                                                                     │
    │ • Processes optimization queues                                     │
    │ • Executes cost calculation algorithms                              │
    │ • Determines optimal rate plan assignments                          │
    │ • Calculates cost savings                                           │
    │ • ❌ SENDS NO AMOP 2.0 TRIGGERS                                     │
    │                                                                     │
    └─────────────────────────┬───────────────────────────────────────────┘
              │
              ▼
    ┌─────────────────────────────────────────────────────────────────────┐
    │           AltaworxSimCardCostOptimizerCleanup Lambda                │
    │                      (CLEANUP & RESULTS PROCESSING)                │
    │                                                                     │
    │ • Marks instances as CompleteWithSuccess                            │
    │ • Cleans up optimization results                                    │
    │ • Generates result files and reports                                │
    │ • Sends email notifications                                         │
    │ • Queues final cleanup steps                                        │
    │ • ❌ SENDS NO AMOP 2.0 TRIGGERS                                     │
    │ • ❌ NO 100% COMPLETION TRIGGER                                     │
    │                                                                     │
    └─────────────────────────┬───────────────────────────────────────────┘
              │
              ▼
         ┌────────────────────────────────────────────────────────────┐
         │              PROCESS ENDS AT 50% VISIBILITY                │
         │         NO FURTHER AMOP 2.0 TRIGGERS SENT                 │
         │         OPTIMIZATION COMPLETES "SILENTLY"                  │
         └────────────────────────────────────────────────────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │     End Process         │
    │                         │
    │ • Optimization complete │
    │ • AMOP 2.0 unaware      │
    │ • No completion trigger │
    └─────────────────────────┘
```

## Lambda Function Responsibility Matrix

| **Lambda Function** | **Primary Purpose** | **AMOP Triggers** | **Progress Visibility** |
|---------------------|--------------------|--------------------|------------------------|
| **QueueCarrierPlanOptimization** | Process orchestration | ✅ **ALL 5 triggers** | 0% → 50% |
| **AltaworxJasperAWSGetDevicesQueue** | Device data sync | ❌ **NONE** | Silent execution |
| **AltaworxSimCardCostOptimizer** | Cost calculations | ❌ **NONE** | Silent execution |
| **AltaworxSimCardCostOptimizerCleanup** | Results & cleanup | ❌ **NONE** | Silent execution |

## Detailed Lambda Execution Sequence

### 1. QueueCarrierPlanOptimization (Orchestrator)
```
CloudWatch Cron → QueueCarrierPlanOptimization
                      │
                      ├── 🎯 TRIGGER #1 (0%) → AMOP 2.0
                      ├── Queue device sync
                      ├── 🎯 TRIGGER #2 (20%) → AMOP 2.0
                      ├── Process communication groups
                      ├── 🎯 TRIGGER #3 (30%) → AMOP 2.0
                      ├── Validate rate plans
                      ├── 🎯 TRIGGER #4 (40%) → AMOP 2.0
                      ├── Start optimization
                      └── 🎯 TRIGGER #5 (50%) → AMOP 2.0
```

### 2. AltaworxJasperAWSGetDevicesQueue (Device Sync)
```
SQS Message → AltaworxJasperAWSGetDevicesQueue
                      │
                      ├── Call carrier APIs
                      ├── Retrieve device data
                      ├── Update staging tables
                      └── ❌ NO AMOP triggers sent
```

### 3. AltaworxSimCardCostOptimizer (Cost Calculator)
```
SQS Message → AltaworxSimCardCostOptimizer
                      │
                      ├── Process optimization queues
                      ├── Execute algorithms
                      ├── Calculate costs
                      └── ❌ NO AMOP triggers sent
```

### 4. AltaworxSimCardCostOptimizerCleanup (Results Processor)
```
SQS Message → AltaworxSimCardCostOptimizerCleanup
                      │
                      ├── Mark instances complete
                      ├── Generate reports
                      ├── Send emails
                      └── ❌ NO AMOP triggers sent
                         ❌ NO 100% completion trigger
```

## Key Issues Identified

### 1. **AMOP 2.0 Visibility Gap**
- Process stops at **50%** from AMOP 2.0 perspective
- **50% → 100%** completion is invisible to AMOP 2.0
- Cleanup and results processing happen "silently"

### 2. **Missing Completion Tracking**
- No trigger when optimization actually completes
- No notification of final results to AMOP 2.0
- No success/failure status after 50%

### 3. **Silent Lambda Execution**
- 3 out of 4 Lambdas operate without AMOP visibility
- Device sync, cost calculation, and cleanup are invisible
- Error handling in these Lambdas doesn't notify AMOP 2.0

## Recommended Solution

### Add Completion Trigger in Cleanup Lambda

```csharp
// In AltaworxSimCardCostOptimizerCleanup.cs after line 352
var endTime = StopOptimizationInstance(context, instanceId, OptimizationStatus.CompleteWithSuccess);

// ADD THIS COMPLETION TRIGGER:
OptimizationAmopApiTrigger.SendResponseToAMOP20(
    context, 
    "Progress", 
    instance.SessionId.ToString(), 
    instance.SessionGuid, 
    totalDevicesProcessed, 
    null, 
    100,    // 100% Complete
    "Optimization Complete - All instances processed successfully", 
    new {
        totalCostSavings = instance.TotalSavings,
        devicesOptimized = instance.DeviceCount,
        completionTime = DateTime.UtcNow,
        optimizationResults = "Success"
    }
);
```

This would provide complete AMOP 2.0 visibility: **0% → 20% → 30% → 40% → 50% → 100%**

---

**Summary**: Only **QueueCarrierPlanOptimization** sends AMOP 2.0 triggers. The other 3 Lambdas execute silently, creating a visibility gap from 50% to completion.