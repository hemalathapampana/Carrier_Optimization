# Next Step Routing Analysis

## DeviceUsageByRatePlan

**What**: Routes completed device sync to optimization usage queue for rate plan analysis.  
**Why**: Enables cost optimization by analyzing device usage patterns against available rate plans.  
**How**: Sends SQS message with optimization session metadata to trigger usage-based optimization.

### Algorithm
```
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

**Code Location**: `AltaworxJasperAWSGetDevicesQueue.cs`
- Switch Case: Lines 208-209 (`case JasperDeviceSyncNextStep.DeviceUsageByRatePlan`)
- Method Call: Line 209 (`SendMessageToDeviceUsageByRatePlanQueue(context, OptimizationUsageQueueURL, sqsValues)`)
- Implementation: Lines 414-463 (complete method implementation)
- Message Attributes: Lines 432-449 (ServiceProviderId, RatePlanId, PageNumber, Initialize)

## DeviceUsageExport

**What**: Routes completed device sync to export queue for report generation and email processing.  
**Why**: Provides usage reporting capabilities for business intelligence and customer communication.  
**How**: Sends SQS message with processing initialization flags to trigger export workflow.

### Algorithm
```
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

**Code Location**: `AltaworxJasperAWSGetDevicesQueue.cs`
- Switch Case: Lines 211-212 (`case JasperDeviceSyncNextStep.DeviceUsageExport`)
- Method Call: Line 212 (`SendMessageToGetExportDeviceUsageQueueAsync(context, sqsValues, ExportDeviceUsageQueueURL)`)
- Implementation: Lines 465-511 (complete method implementation)
- Initialization: Line 467 (`var initializeProcessing = true`)
- Message Attributes: Lines 482-496 (InitializeProcessing, WaitCount, ServiceProviderId)

## UpdateDeviceRatePlan

**What**: Routes completed device sync to rate plan update queue for device plan modifications.  
**Why**: Applies optimized rate plan recommendations to actual device configurations.  
**How**: Sends SQS message with instance metadata to trigger rate plan update operations.

### Algorithm
```
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

**Code Location**: `AltaworxJasperAWSGetDevicesQueue.cs`
- Switch Case: Lines 214-215 (`case JasperDeviceSyncNextStep.UpdateDeviceRatePlan`)
- Method Call: Line 215 (`SendMessageToUpdateRatePlanQueueAsync(context, sqsValues, RatePlanUpdateQueueURL)`)
- Implementation: Lines 513-549 (complete method implementation)
- Message Attributes: Lines 529-540 (InstanceId, SyncedDevices)
- Body Assignment: Line 543 (`MessageBody = "NOT USED"`)