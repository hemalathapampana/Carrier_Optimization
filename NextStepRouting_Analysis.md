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

### Code Implementation
```csharp
// Main routing switch case
case JasperDeviceSyncNextStep.DeviceUsageByRatePlan:
    await SendMessageToDeviceUsageByRatePlanQueue(context, OptimizationUsageQueueURL, sqsValues);
    break;

// Queue URL configuration
private string OptimizationUsageQueueURL = Environment.GetEnvironmentVariable("OptimizationUsageQueueURL");

// Method implementation
private async Task SendMessageToDeviceUsageByRatePlanQueue(KeySysLambdaContext context, string optimizationUsageQueueURL, GetDeviceQueueSqsValues sqsValues, int currentRatePlanId = 0, int currentPageNumber = 1)
{
    var awsCredentials = AwsCredentials(context);
    using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
    {
        var requestMsgBody = $"Get Optimization Usage for Service Provider {sqsValues.ServiceProviderId}";
        var request = new SendMessageRequest
        {
            DelaySeconds = DefaultDelaySeconds,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                {
                    "ServiceProviderId", new MessageAttributeValue
                        { DataType = "String", StringValue = sqsValues.ServiceProviderId.ToString()}
                },
                {
                    "RatePlanId", new MessageAttributeValue
                        { DataType = "String", StringValue = currentRatePlanId.ToString()}
                },
                {
                    "PageNumber", new MessageAttributeValue
                        { DataType = "String", StringValue = currentPageNumber.ToString()}
                },
                {
                    "Initialize", new MessageAttributeValue
                        { DataType = "String", StringValue = false.ToString()}
                }
            },
            MessageBody = requestMsgBody,
            QueueUrl = optimizationUsageQueueURL
        };

        if (sqsValues.OptimizationSessionId != null && sqsValues.OptimizationSessionId.Value > 0)
        {
            request.MessageAttributes.Add("OptimizationSessionId", new MessageAttributeValue { DataType = "String", StringValue = sqsValues.OptimizationSessionId.Value.ToString() });
        }

        var response = await client.SendMessageAsync(request);
    }
}
```

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

### Code Implementation
```csharp
// Main routing switch case
case JasperDeviceSyncNextStep.DeviceUsageExport:
    await SendMessageToGetExportDeviceUsageQueueAsync(context, sqsValues, ExportDeviceUsageQueueURL);
    break;

// Queue URL configuration
private string ExportDeviceUsageQueueURL = Environment.GetEnvironmentVariable("ExportDeviceUsageQueueURL");

// Method implementation
private async Task SendMessageToGetExportDeviceUsageQueueAsync(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues, string exportDeviceUsageQueueURL)
{
    var initializeProcessing = true;

    var awsCredentials = AwsCredentials(context);
    using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
    {
        var requestMsgBody = $"Requesting email to process";
        var request = new SendMessageRequest
        {
            DelaySeconds = DefaultDelaySeconds,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                {
                    "InitializeProcessing", new MessageAttributeValue
                    {
                        DataType = "String", StringValue = initializeProcessing.ToString()
                    }
                },
                {
                    "WaitCount", new MessageAttributeValue
                    {
                        DataType = "String", StringValue = 0.ToString()
                    }
                },
                {
                    "ServiceProviderId", new MessageAttributeValue
                    {
                        DataType = "String", StringValue = sqsValues.ServiceProviderId.ToString()
                    }
                }
            },
            MessageBody = requestMsgBody,
            QueueUrl = exportDeviceUsageQueueURL
        };

        var response = await client.SendMessageAsync(request);
    }
}
```

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

### Code Implementation
```csharp
// Main routing switch case
case JasperDeviceSyncNextStep.UpdateDeviceRatePlan:
    await SendMessageToUpdateRatePlanQueueAsync(context, sqsValues, RatePlanUpdateQueueURL);
    break;

// Queue URL configuration
private string RatePlanUpdateQueueURL = Environment.GetEnvironmentVariable("RatePlanUpdateQueueURL");

// Method implementation
private async Task SendMessageToUpdateRatePlanQueueAsync(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues, string queueUrl)
{
    var awsCredentials = AwsCredentials(context);
    using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
    {
        var request = new SendMessageRequest
        {
            DelaySeconds = DefaultDelaySeconds,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                {
                    "InstanceId", new MessageAttributeValue
                    {
                        DataType = "Number", StringValue = sqsValues.OptimizationInstanceId.ToString()
                    }
                },
                {
                    "SyncedDevices", new MessageAttributeValue
                    {
                        DataType = "String", StringValue = true.ToString()
                    }
                }
            },
            MessageBody = "NOT USED",
            QueueUrl = queueUrl
        };

        var response = await client.SendMessageAsync(request);
    }
}
```

---

## Control Flow Summary

### Main ProcessNextStep Method
```csharp
private async Task ProcessNextStep(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues)
{
    switch (sqsValues.NextStep)
    {
        case JasperDeviceSyncNextStep.DeviceUsageByRatePlan:
            await SendMessageToDeviceUsageByRatePlanQueue(context, OptimizationUsageQueueURL, sqsValues);
            break;
        case JasperDeviceSyncNextStep.DeviceUsageExport:
            await SendMessageToGetExportDeviceUsageQueueAsync(context, sqsValues, ExportDeviceUsageQueueURL);
            break;
        case JasperDeviceSyncNextStep.UpdateDeviceRatePlan:
            await SendMessageToUpdateRatePlanQueueAsync(context, sqsValues, RatePlanUpdateQueueURL);
            break;
        default:
            LogInfo(context, "EXCEPTION", $"Unknown usage sync type: {sqsValues.NextStep}");
            break;
    }
}
```

### Environment Configuration
```csharp
private string OptimizationUsageQueueURL = Environment.GetEnvironmentVariable("OptimizationUsageQueueURL");
private string ExportDeviceUsageQueueURL = Environment.GetEnvironmentVariable("ExportDeviceUsageQueueURL");
private string RatePlanUpdateQueueURL = Environment.GetEnvironmentVariable("RatePlanUpdateQueueURL");
```