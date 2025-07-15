# AMOP 2.0 Trigger Process Flow - Flowchart

## Overview
This flowchart shows the detailed process of how triggers are sent from the Carrier Optimization System to AMOP 2.0 at various stages of the optimization process.

## Main Trigger Flow Process

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
                    │  │QueueCarrierPlan │                                        │
                    │  │  Optimization   │                                        │
                    │  │     Lambda      │                                        │
                    │  └─────────┬───────┘                                        │
                    │            │                                                │
                    │            ▼                                                │
                    └────────────┼────────────────────────────────────────────────┘
                                 │
                                 ▼
┌────────────────────────────────────────────────────────────────────────────────────┐
│                        AMOP 2.0 TRIGGER DECISION TREE                             │
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
    ┌─────────────────────────┐
    │   Process Completion    │
    │                         │
    │ • Cleanup instances     │
    │ • Generate reports      │
    │ • Send notifications    │
    └─────────┬───────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │   Cleanup Lambda        │
    │                         │
    │ • Mark instances        │
    │   CompleteWithSuccess   │
    │ • Generate reports      │
    │ • Queue email sends     │
    └─────────┬───────────────┘
              │
              ▼
         ┌────────────────────────────────────────────────────────────┐
         │              NO 100% TRIGGER FOUND                         │
         │         Cleanup Lambda does NOT send AMOP 2.0              │
         │         100% completion trigger in current code            │
         └────────────────────────────────────────────────────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │     End Process         │
    │                         │
    │ • Process ends at 50%   │
    │ • No final AMOP trigger │
    │ • Cleanup runs silently │
    └─────────────────────────┘
```

## AMOP 2.0 Trigger Internal Processing Flow

```
┌─────────────────────────────────────────────────────────────────────────────────────┐
│                     AMOP API TRIGGER INTERNAL FLOW                                 │
└─────────────────────────────────────────────────────────────────────────────────────┘

    ┌─────────────────────────┐
    │ Lambda Function Calls   │
    │ SendResponseToAMOP20()  │
    └─────────┬───────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │   Parameter Validation  │
    │                         │
    │ • Check session ID      │
    │ • Validate message type │
    │ • Verify context        │
    └─────────┬───────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │  Construct API Payload  │
    │                         │
    │ • Format JSON message   │
    │ • Add authentication    │
    │ • Set headers           │
    └─────────┬───────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │   HTTP Request Setup    │
    │                         │
    │ • Set endpoint URL      │
    │ • Configure timeout     │
    │ • Add retry policy      │
    └─────────┬───────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │   Send HTTP Request     │
    │   to AMOP 2.0 API       │
    └─────────┬───────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │   Check Response        │
    └─────────┬───────────────┘
              │
      ┌───────┴───────┐
      │               │
      ▼               ▼
┌─────────────┐ ┌─────────────┐
│  Success    │ │   Failure   │
│  (200 OK)   │ │ (Error Code)│
└─────────────┘ └─────────────┘
      │               │
      ▼               ▼
┌─────────────┐ ┌─────────────┐
│ Log Success │ │  Log Error  │
│ Continue    │ │ Retry Logic │
│ Process     │ │             │
└─────────────┘ └─────────────┘
                      │
                      ▼
            ┌─────────────────────┐
            │   Retry Attempt     │
            │                     │
            │ • Wait backoff      │
            │ • Retry HTTP call   │
            │ • Max 3 attempts    │
            └─────────┬───────────┘
                      │
                      ▼
            ┌─────────────────────┐
            │  Final Result       │
            │                     │
            │ Success or Log      │
            │ Critical Error      │
            └─────────────────────┘
```

## Error Handling & Alternative Trigger Flows

```
┌─────────────────────────────────────────────────────────────────────────────────────┐
│                         ERROR SCENARIO FLOWS                                       │
└─────────────────────────────────────────────────────────────────────────────────────┘

    ┌─────────────────────────┐
    │    Any Process Step     │
    └─────────┬───────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │   Exception Detected    │
    │                         │
    │ • Database error        │
    │ • API timeout           │
    │ • Validation failure    │
    │ • Resource limitation   │
    └─────────┬───────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │  Capture Error Details  │
    │                         │
    │ • Error message         │
    │ • Stack trace           │
    │ • Session context       │
    │ • Timestamp             │
    └─────────┬───────────────┘
              │
              ▼
    ┌─────────────────────────────────────────────────────────────────────┐
    │              ERROR TRIGGER TO AMOP 2.0                             │
    └─────────────────────────────────────────────────────────────────────┘
              │
              ▼
    ┌─────────────────────────────────────────────────────────────────────┐
    │  OptimizationAmopApiTrigger.SendResponseToAMOP20(                  │
    │      context: ILambdaContext,                                      │
    │      messageType: "ErrorMessage",                                  │
    │      sessionId: optimizationSessionId.ToString(),                  │
    │      sessionGuid: null,                                            │
    │      deviceCount: 0,                                               │
    │      errorMessage: detailedErrorMessage,                           │
    │      progressPercentage: 0,                                        │
    │      additionalInfo: stackTrace,                                   │
    │      additionalData: errorContext                                  │
    │  );                                                                │
    └─────────────────────────┬───────────────────────────────────────────┘
              │
              ▼
    ┌─────────────────────────┐
    │  Stop Optimization      │
    │                         │
    │ • Mark session failed   │
    │ • Clean up resources    │
    │ • Send email alert      │
    └─────────────────────────┘
```

## AMOP 2.0 Message Types & Payloads

### Progress Message Payload
```json
{
  "messageType": "Progress",
  "sessionId": "12345",
  "sessionGuid": "550e8400-e29b-41d4-a716-446655440000",
  "deviceCount": 1500,
  "errorMessage": null,
  "progressPercentage": 30,
  "additionalInfo": "",
  "timestamp": "2024-01-15T10:30:00Z",
  "additionalData": {
    "serviceProviderId": "SP001",
    "billingPeriod": "2024-01",
    "commGroupsProcessed": 5,
    "ratePlansValidated": 12
  }
}
```

### Error Message Payload
```json
{
  "messageType": "ErrorMessage",
  "sessionId": "12345",
  "sessionGuid": null,
  "deviceCount": 0,
  "errorMessage": "One or more Rate Plans have invalid Data per Overage Charge or Overage Rate",
  "progressPercentage": 0,
  "additionalInfo": "Stack trace and error details",
  "timestamp": "2024-01-15T10:30:00Z",
  "additionalData": {
    "errorCode": "INVALID_RATE_PLAN",
    "affectedRatePlans": ["RP001", "RP002"],
    "serviceProviderId": "SP001"
  }
}
```

## Trigger Timing & Frequency

| **Trigger Point** | **Progress %** | **Frequency** | **Purpose** |
|-------------------|----------------|---------------|-------------|
| Session Init      | 0%             | Once per run  | Start tracking |
| Device Sync       | 20%            | Once per sync | Sync progress |
| Comm Grouping     | 30%            | Once per group| Group validation |
| Rate Pool Gen     | 40%            | Once per pool | Pool creation |
| Optimization      | 50%            | Once per exec | Processing start |
| **NO CLEANUP**    | **NO 90%**     | **NOT FOUND** | **Missing trigger** |
| **NO COMPLETION** | **NO 100%**    | **NOT FOUND** | **Missing trigger** |
| **Error Triggers**| 0%             | As needed     | Immediate alert |

---

**Created by**: Madhu  
**Process**: AMOP 2.0 Trigger Flow  
**System**: Continuous Carrier Optimization  
**Status**: Production Active