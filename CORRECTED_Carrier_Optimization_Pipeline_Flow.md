# CORRECTED Carrier Optimization Pipeline Flow

## Overview
The `AltaworxJasperGetDevicesCleanup` lambda operates as a **parallel monitoring service** that triggers optimization continuation **AFTER the 50% progress point**, not before communication grouping.

## Corrected Pipeline Flow Diagram

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
    │      messageType: "Progress",                                      │
    │      progressPercentage: 0                                         │
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
    │      messageType: "Progress",                                      │
    │      progressPercentage: 20                                        │
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
    │ • ✅ SENDS MESSAGE TO DeviceNotificationQueue                       │
    │                                                                     │
    └─────────────────────────┬───────────┬─────────────────────────────────┘
                              │           │
                              │           │ (Parallel Process)
                              │           ▼
                              │     ┌─────────────────────────────────────────────┐
                              │     │      🔧 DeviceNotificationQueue (SQS)        │
                              │     └─────────┬───────────────────────────────────┘
                              │               │
                              │               ▼
                              │     ┌─────────────────────────────────────────────┐
                              │     │      AltaworxJasperGetDevicesCleanup        │
                              │     │              (PARALLEL PROCESS)             │
                              │     │                                             │
                              │     │ • Monitors remaining device rows           │
                              │     │ • Implements retry logic                   │
                              │     │ • Waits for sync completion                │
                              │     │ • Syncs staging data to main tables        │
                              │     │ • Updates communication plans              │
                              │     │ • Generates reports & notifications        │
                              │     │ • ✅ SENDS AMOP 2.0 SYNC NOTIFICATION     │
                              │     └─────────────────────────────────────────────┘
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
    │      messageType: "Progress",                                      │
    │      progressPercentage: 30                                        │
    │  );                                                                │
    └─────────────────────────┬───────────────────────────────────────────┘
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
              │           └────────────────────────────────────────────────────────────┘
              │                            │
              │                            ▼
              │                  ┌─────────────────────┐
              │                  │   Stop Process      │
              │                  │   Mark as Failed    │
              │                  │   Exit Pipeline     │
              │                  └─────────────────────┘
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
    │      messageType: "Progress",                                      │
    │      progressPercentage: 40                                        │
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
    │      messageType: "Progress",                                      │
    │      progressPercentage: 50                                        │
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
              │               │
              │               │ ◄──────────────────────────────────────┐
              │               │                                        │
              │               │ (✅ CORRECTED: OPTIMIZATION TRIGGER)   │
              │               │                                        │
              │               │     ┌─────────────────────────────────────────────┐
              │               └────▶│     AltaworxJasperGetDevicesCleanup        │
              │                     │         (WHEN READY TO CONTINUE)           │
              │                     │                                             │
              │                     │ • ✅ TRIGGERS CARRIER OPTIMIZATION         │
              │                     │ • SendCarrierOptimizationMessageToQueue    │
              │                     └─────────────────────────────────────────────┘
              │
              ▼
         ┌────────────────────────────────────────────────────────────┐
         │              PROCESS ENDS AT 50% VISIBILITY                │
         │    ✅ CORRECTED: OPTIMIZATION CONTINUATION HAPPENS         │
         │         WHEN CLEANUP LAMBDA TRIGGERS NEXT PHASE           │
         └────────────────────────────────────────────────────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │     End Process         │
    │                         │
    │ • Optimization complete │
    │ • AMOP 2.0 aware via    │
    │   cleanup lambda        │
    └─────────────────────────┘
```

## Key Correction

### **Optimization Trigger Placement - AFTER 50%**

The `AltaworxJasperGetDevicesCleanup` lambda should trigger optimization continuation **AFTER the 50% progress point**, specifically:

1. **After Cost Calculation Completion** (AltaworxSimCardCostOptimizer)
2. **After Cleanup Processing** (AltaworxSimCardCostOptimizerCleanup)
3. **When System is Ready** for next optimization phase

### **Corrected Trigger Logic**

```csharp
// In AltaworxJasperGetDevicesCleanup - AFTER 50% completion
if ((sqsValues.IntegrationType == IntegrationType.Jasper ||
    sqsValues.IntegrationType == IntegrationType.POD19 ||
    sqsValues.IntegrationType == IntegrationType.TMobileJasper ||
    sqsValues.IntegrationType == IntegrationType.Rogers) &&
    sqsValues.ShouldQueueCarrierOptimization)
{
    // ✅ This triggers AFTER 50% progress point
    await SendCarrierOptimizationMessageToQueue(context, sqsValues.ServiceProviderId, sqsValues.OptimizationSessionId);
}
```

### **Flow Summary**

1. **0% - 50%**: Main optimization pipeline executes
2. **At 50%**: Cost calculation and cleanup lambdas run
3. **AFTER 50%**: `AltaworxJasperGetDevicesCleanup` monitors and triggers continuation
4. **Post-50%**: Next phase of optimization begins (if configured)

This corrected placement ensures that optimization continuation happens **after the current optimization cycle completes**, not during the initial communication grouping phase.