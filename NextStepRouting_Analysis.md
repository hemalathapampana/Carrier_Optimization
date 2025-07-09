# Next Step Routing Analysis

## Overview
This document analyzes the routing logic for device synchronization completion, covering three workflow paths based on the `JasperDeviceSyncNextStep` enumeration.

---

## 1. DeviceUsageByRatePlan Routing

### Definition
**What**: Routes completed device sync to optimization usage queue for rate plan analysis.  
**Why**: Enables cost optimization by analyzing device usage patterns against available rate plans.  
**How**: Sends SQS message with optimization session metadata to trigger usage-based optimization.

### Algorithm
```mathematical
For optimization routing O with device sync completion:
Let optimization_queue = OptimizationUsageQueueURL
Let message_attributes = {
    ServiceProviderId: sqsValues.ServiceProviderId,
    RatePlanId: 0,
    PageNumber: 1,
    Initialize: false,
    OptimizationSessionId: sqsValues.OptimizationSessionId
}

If optimization_queue ≠ ∅:
    Send_SQS_Message(optimization_queue, message_attributes)
    Message_body ← "Get Optimization Usage for Service Provider {ServiceProviderId}"
```

### Code Locations
**File**: `AltaworxJasperAWSGetDevicesQueue.cs`

| Component | Line(s) | Code Reference |
|-----------|---------|----------------|
| **Main Switch Case** | 208-210 | `case JasperDeviceSyncNextStep.DeviceUsageByRatePlan:` |
| **Method Invocation** | 209 | `await SendMessageToDeviceUsageByRatePlanQueue(context, OptimizationUsageQueueURL, sqsValues);` |
| **Method Declaration** | 414 | `private async Task SendMessageToDeviceUsageByRatePlanQueue(...)` |
| **Queue URL Variable** | 48 | `private string OptimizationUsageQueueURL = Environment.GetEnvironmentVariable("OptimizationUsageQueueURL");` |
| **Message Body** | 427 | `var requestMsgBody = $"Get Optimization Usage for Service Provider {sqsValues.ServiceProviderId}";` |
| **Message Attributes** | 432-449 | ServiceProviderId, RatePlanId, PageNumber, Initialize |
| **OptimizationSessionId** | 452-454 | Conditional addition of OptimizationSessionId attribute |
| **SQS Send** | 456 | `var response = await client.SendMessageAsync(request);` |

---

## 2. DeviceUsageExport Routing

### Definition
**What**: Routes completed device sync to export queue for report generation and email processing.  
**Why**: Provides usage reporting capabilities for business intelligence and customer communication.  
**How**: Sends SQS message with processing initialization flags to trigger export workflow.

### Algorithm
```mathematical
For export routing E with device sync completion:
Let export_queue = ExportDeviceUsageQueueURL
Let initialize_processing = true
Let message_attributes = {
    InitializeProcessing: true,
    WaitCount: 0,
    ServiceProviderId: sqsValues.ServiceProviderId
}

If export_queue ≠ ∅:
    Send_SQS_Message(export_queue, message_attributes)
    Message_body ← "Requesting email to process"
```

### Code Locations
**File**: `AltaworxJasperAWSGetDevicesQueue.cs`

| Component | Line(s) | Code Reference |
|-----------|---------|----------------|
| **Main Switch Case** | 211-213 | `case JasperDeviceSyncNextStep.DeviceUsageExport:` |
| **Method Invocation** | 212 | `await SendMessageToGetExportDeviceUsageQueueAsync(context, sqsValues, ExportDeviceUsageQueueURL);` |
| **Method Declaration** | 465 | `private async Task SendMessageToGetExportDeviceUsageQueueAsync(...)` |
| **Queue URL Variable** | 47 | `private string ExportDeviceUsageQueueURL = Environment.GetEnvironmentVariable("ExportDeviceUsageQueueURL");` |
| **Initialization Flag** | 467 | `var initializeProcessing = true;` |
| **Message Body** | 476 | `var requestMsgBody = $"Requesting email to process";` |
| **Message Attributes** | 482-496 | InitializeProcessing, WaitCount, ServiceProviderId |
| **SQS Send** | 504 | `var response = await client.SendMessageAsync(request);` |

---

## 3. UpdateDeviceRatePlan Routing

### Definition
**What**: Routes completed device sync to rate plan update queue for device plan modifications.  
**Why**: Applies optimized rate plan recommendations to actual device configurations.  
**How**: Sends SQS message with instance metadata to trigger rate plan update operations.

### Algorithm
```mathematical
For rate plan update R with device sync completion:
Let update_queue = RatePlanUpdateQueueURL
Let message_attributes = {
    InstanceId: sqsValues.OptimizationInstanceId,
    SyncedDevices: true
}

If update_queue ≠ ∅:
    Send_SQS_Message(update_queue, message_attributes)
    Message_body ← "NOT USED"
```

### Code Locations
**File**: `AltaworxJasperAWSGetDevicesQueue.cs`

| Component | Line(s) | Code Reference |
|-----------|---------|----------------|
| **Main Switch Case** | 214-216 | `case JasperDeviceSyncNextStep.UpdateDeviceRatePlan:` |
| **Method Invocation** | 215 | `await SendMessageToUpdateRatePlanQueueAsync(context, sqsValues, RatePlanUpdateQueueURL);` |
| **Method Declaration** | 513 | `private async Task SendMessageToUpdateRatePlanQueueAsync(...)` |
| **Queue URL Variable** | 49 | `private string RatePlanUpdateQueueURL = Environment.GetEnvironmentVariable("RatePlanUpdateQueueURL");` |
| **Message Body** | 543 | `MessageBody = "NOT USED",` |
| **Message Attributes** | 529-540 | InstanceId, SyncedDevices |
| **SQS Send** | 545 | `var response = await client.SendMessageAsync(request);` |

---

## Control Flow Summary

### ProcessNextStep Method
**Location**: Lines 205-219

```csharp
private async Task ProcessNextStep(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues)
{
    switch (sqsValues.NextStep)
    {
        case JasperDeviceSyncNextStep.DeviceUsageByRatePlan:     // Line 208
        case JasperDeviceSyncNextStep.DeviceUsageExport:        // Line 211  
        case JasperDeviceSyncNextStep.UpdateDeviceRatePlan:     // Line 214
        default:                                                // Line 217
    }
}
```

### Queue URL Environment Variables
| Queue Type | Variable | Line | Environment Key |
|------------|----------|------|-----------------|
| **Optimization** | OptimizationUsageQueueURL | 48 | "OptimizationUsageQueueURL" |
| **Export** | ExportDeviceUsageQueueURL | 47 | "ExportDeviceUsageQueueURL" |
| **Rate Plan Update** | RatePlanUpdateQueueURL | 49 | "RatePlanUpdateQueueURL" |