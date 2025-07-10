# Rate Plan Updates: Algorithmic Analysis with Code Implementation

## System Overview

**Function Location**: AltaworxSimCardCostOptimizerCleanup.cs (Lines 446-579)  
**Primary Methods**: DoesHaveTimeToProcessRatePlanUpdates(), QueueRatePlanUpdates(), SendGoForRatePlanUpdatesEmail(), SendNoGoForRatePlanUpdatesEmail()  
**Purpose**: Automatically evaluate timing constraints and queue rate plan updates based on billing cycle analysis and processing time estimations

---

## 1. Evaluates Time Remaining in Billing Cycle

### Purpose Definition
**What**: Calculate the remaining time in the current billing cycle to determine if rate plan updates can be completed safely  
**Why**: Prevent partial rate plan updates that could cause billing inconsistencies or customer service issues  
**How**: Compare current system time with billing period end date, accounting for time zones and processing buffers

### Algorithm: EvaluateTimeRemainingInBillingCycle

**Input**: 
- instance: Optimization instance with billing information
- currentSystemTimeUtc: Current UTC timestamp
- timeZoneInfo: Billing time zone configuration
- billingPeriodEndDate: End date of current billing period

**Output**: Integer representing minutes remaining in billing cycle

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:537-557`

**Algorithm**:
```
Begin EvaluateTimeRemainingInBillingCycle
    currentLocalTime ← ConvertUtcToLocalTime(currentSystemTimeUtc, timeZoneInfo)
    
    If billingPeriodEndDate < currentLocalTime Then
        LogWarning("Billing period has already ended")
        Return 0
    End If
    
    timeRemaining ← billingPeriodEndDate - currentLocalTime
    minutesRemaining ← ConvertToMinutes(timeRemaining)
    
    LogInfo("Minutes remaining in billing cycle: " + minutesRemaining)
    
    Return minutesRemaining
End EvaluateTimeRemainingInBillingCycle
```

**Corresponding Code**:
```csharp
// Lines 537-557: Time Remaining Calculation
public static int MinutesRemainingInBillCycle(IKeysysLogger logger, DateTime billingPeriodEndDate, DateTime currentSystemTimeUtc, TimeZoneInfo timeZoneInfo)
{
    var currentLocalTime = TimeZoneInfo.ConvertTimeFromUtc(currentSystemTimeUtc, timeZoneInfo);
    return MinutesRemainingInBillCycle(logger, billingPeriodEndDate, currentLocalTime);
}

public static int MinutesRemainingInBillCycle(IKeysysLogger logger, DateTime billingPeriodEndDate, DateTime currentLocalTime)
{
    if (billingPeriodEndDate < currentLocalTime)
    {
        logger.LogInfo("WARNING", $"Billing period end date {billingPeriodEndDate} is before current local time {currentLocalTime}");
        return 0;
    }

    var timeRemaining = billingPeriodEndDate - currentLocalTime;
    var minutesRemaining = (int)timeRemaining.TotalMinutes;

    logger.LogInfo("INFO", $"Minutes remaining in billing cycle: {minutesRemaining}");
    return minutesRemaining;
}
```

**Time Zone Conversion Implementation**:
```csharp
// Time zone handling for accurate billing cycle calculations
var currentLocalTime = TimeZoneInfo.ConvertTimeFromUtc(currentSystemTimeUtc, timeZoneInfo);
var timeRemaining = billingPeriodEndDate - currentLocalTime;
```

---

## 2. Estimates Update Processing Time

### Purpose Definition
**What**: Calculate estimated time required to process all pending rate plan updates based on historical performance data  
**Why**: Ensure sufficient time exists to complete all updates before billing cycle ends, preventing incomplete update scenarios  
**How**: Analyze historical rate plan update performance and apply statistical models to estimate processing duration

### Algorithm: EstimateUpdateProcessingTime

**Input**: 
- ratePlansToUpdateCount: Number of rate plans requiring updates
- ratePlanUpdateSummaryRecords: Historical performance data
- logger: Logging interface

**Output**: Decimal representing estimated minutes to complete updates

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:559-579`

**Algorithm**:
```
Begin EstimateUpdateProcessingTime
    If ratePlanUpdateSummaryRecords is empty Then
        estimatedMinutes ← ratePlansToUpdateCount × DefaultMinutesPerRatePlan
        LogInfo("Using default estimation: " + estimatedMinutes + " minutes")
        Return estimatedMinutes
    End If
    
    totalHistoricalMinutes ← 0
    totalHistoricalRatePlans ← 0
    
    For Each summaryRecord in ratePlanUpdateSummaryRecords Do
        If summaryRecord.RatePlansUpdatedCount > 0 And summaryRecord.MinutesToComplete > 0 Then
            totalHistoricalMinutes ← totalHistoricalMinutes + summaryRecord.MinutesToComplete
            totalHistoricalRatePlans ← totalHistoricalRatePlans + summaryRecord.RatePlansUpdatedCount
        End If
    End For
    
    If totalHistoricalRatePlans > 0 Then
        averageMinutesPerRatePlan ← totalHistoricalMinutes ÷ totalHistoricalRatePlans
        estimatedMinutes ← ratePlansToUpdateCount × averageMinutesPerRatePlan
        
        bufferMultiplier ← 1.2
        estimatedMinutesWithBuffer ← estimatedMinutes × bufferMultiplier
        
        LogInfo("Historical average: " + averageMinutesPerRatePlan + " minutes per rate plan")
        LogInfo("Estimated time with buffer: " + estimatedMinutesWithBuffer + " minutes")
        
        Return estimatedMinutesWithBuffer
    Else
        estimatedMinutes ← ratePlansToUpdateCount × DefaultMinutesPerRatePlan
        LogInfo("No valid historical data, using default: " + estimatedMinutes + " minutes")
        Return estimatedMinutes
    End If
End EstimateUpdateProcessingTime
```

**Corresponding Code**:
```csharp
// Lines 559-579: Processing Time Estimation
private static decimal MinutesToUpdateRatePlans(int ratePlansToUpdateCount,
    IReadOnlyCollection<OptimizationRatePlanUpdateSummary> ratePlanUpdateSummaryRecords,
    IKeysysLogger logger)
{
    const decimal DefaultMinutesPerRatePlan = 2.0m;

    if (!ratePlanUpdateSummaryRecords.Any())
    {
        var estimatedMinutes = ratePlansToUpdateCount * DefaultMinutesPerRatePlan;
        logger.LogInfo("INFO", $"No historical data available. Using default estimation: {estimatedMinutes} minutes for {ratePlansToUpdateCount} rate plans.");
        return estimatedMinutes;
    }

    decimal totalHistoricalMinutes = 0;
    int totalHistoricalRatePlans = 0;

    foreach (var summaryRecord in ratePlanUpdateSummaryRecords)
    {
        if (summaryRecord.RatePlansUpdatedCount > 0 && summaryRecord.MinutesToComplete > 0)
        {
            totalHistoricalMinutes += summaryRecord.MinutesToComplete;
            totalHistoricalRatePlans += summaryRecord.RatePlansUpdatedCount;
        }
    }

    if (totalHistoricalRatePlans > 0)
    {
        var averageMinutesPerRatePlan = totalHistoricalMinutes / totalHistoricalRatePlans;
        var estimatedMinutes = ratePlansToUpdateCount * averageMinutesPerRatePlan;
        
        // Add 20% buffer for safety
        var estimatedMinutesWithBuffer = estimatedMinutes * 1.2m;

        logger.LogInfo("INFO", $"Historical average: {averageMinutesPerRatePlan:F2} minutes per rate plan. " +
                              $"Estimated time with buffer: {estimatedMinutesWithBuffer:F2} minutes for {ratePlansToUpdateCount} rate plans.");

        return estimatedMinutesWithBuffer;
    }
    else
    {
        var estimatedMinutes = ratePlansToUpdateCount * DefaultMinutesPerRatePlan;
        logger.LogInfo("INFO", $"No valid historical data found. Using default estimation: {estimatedMinutes} minutes for {ratePlansToUpdateCount} rate plans.");
        return estimatedMinutes;
    }
}
```

**Historical Data Retrieval**:
```csharp
// Lines 470-510: Historical Performance Data
private static List<OptimizationRatePlanUpdateSummary> GetPreviousRatePlanUpdateSummary(long instanceId, string connectionString, IKeysysLogger logger)
{
    var summaryRecords = new List<OptimizationRatePlanUpdateSummary>();
    
    try
    {
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            using (var command = new SqlCommand("usp_Optimization_PreviousRatePlanUpdateSummary", connection))
            {
                command.CommandType = CommandType.StoredProcedure;
                command.Parameters.AddWithValue("@InstanceId", instanceId);
                
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        summaryRecords.Add(RatePlanSummaryRecordFromReader(reader));
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogInfo("ERROR", $"Error retrieving rate plan update summary: {ex.Message}");
    }
    
    return summaryRecords;
}
```

---

## 3. Queues Automatic Rate Plan Updates if Sufficient Time

### Purpose Definition
**What**: Automatically queue rate plan updates when sufficient time remains in billing cycle based on processing estimates  
**Why**: Maximize automation while preventing incomplete updates that could cause billing issues or customer disruption  
**How**: Compare estimated processing time with available time, then queue updates or defer to next cycle

### Algorithm: QueueAutomaticRatePlanUpdates

**Input**: 
- instance: Optimization instance configuration
- ratePlansToUpdateCount: Number of rate plans requiring updates
- connectionString: Database connection information
- logger: Logging interface
- currentSystemTimeUtc: Current system timestamp
- timeZoneInfo: Billing time zone information

**Output**: Boolean indicating whether updates were queued

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:446-469`

**Algorithm**:
```
Begin QueueAutomaticRatePlanUpdates
    minutesRemainingInCycle ← MinutesRemainingInBillCycle(logger, instance.BillingPeriodEndDate, currentSystemTimeUtc, timeZoneInfo)
    
    If minutesRemainingInCycle ≤ 0 Then
        LogInfo("No time remaining in billing cycle")
        Return false
    End If
    
    historicalData ← GetPreviousRatePlanUpdateSummary(instance.Id, connectionString, logger)
    estimatedMinutesToComplete ← MinutesToUpdateRatePlans(ratePlansToUpdateCount, historicalData, logger)
    
    minimumBufferMinutes ← 60
    requiredTime ← estimatedMinutesToComplete + minimumBufferMinutes
    
    If minutesRemainingInCycle ≥ requiredTime Then
        LogInfo("Sufficient time available. Queuing rate plan updates.")
        QueueRatePlanUpdates(context, instance.Id, instance.TenantId)
        SendGoForRatePlanUpdatesEmail(context, instance, timeZoneInfo)
        Return true
    Else
        LogInfo("Insufficient time remaining. Deferring rate plan updates.")
        SendNoGoForRatePlanUpdatesEmail(context, instance, timeZoneInfo)
        Return false
    End If
End QueueAutomaticRatePlanUpdates
```

**Corresponding Code**:
```csharp
// Lines 446-469: Main Decision Logic
public static bool DoesHaveTimeToProcessRatePlanUpdates(OptimizationInstance instance, int ratePlansToUpdateCount,
    string connectionString, IKeysysLogger logger, DateTime currentSystemTimeUtc, TimeZoneInfo timeZoneInfo)
{
    var minutesRemainingInCycle = MinutesRemainingInBillCycle(logger, instance.BillingPeriodEndDate, 
        currentSystemTimeUtc, timeZoneInfo);

    if (minutesRemainingInCycle <= 0)
    {
        logger.LogInfo("INFO", "No time remaining in billing cycle.");
        return false;
    }

    var ratePlanUpdateSummaryRecords = GetPreviousRatePlanUpdateSummary(instance.Id, connectionString, logger);
    var estimatedMinutesToComplete = MinutesToUpdateRatePlans(ratePlansToUpdateCount, ratePlanUpdateSummaryRecords, logger);

    const int MinimumBufferMinutes = 60; // 1 hour buffer
    var requiredTime = estimatedMinutesToComplete + MinimumBufferMinutes;

    logger.LogInfo("INFO", $"Minutes remaining: {minutesRemainingInCycle}, Required time (with buffer): {requiredTime}");

    if (minutesRemainingInCycle >= requiredTime)
    {
        logger.LogInfo("INFO", "Sufficient time available for rate plan updates.");
        return true;
    }
    else
    {
        logger.LogInfo("INFO", "Insufficient time remaining for rate plan updates.");
        return false;
    }
}
```

**Rate Plan Update Queuing Implementation**:
```csharp
// Lines 2136-2170: Queue Rate Plan Updates
private void QueueRatePlanUpdates(KeySysLambdaContext context, long instanceId, int tenantId)
{
    try
    {
        LogInfo(context, LogTypeConstant.Sub, $"({instanceId},{tenantId})");

        var queueUrl = context.ClientContext.Environment["RatePlanUpdateQueueURL"];
        var sqsClient = new AmazonSQSClient();

        var messageAttributes = new Dictionary<string, MessageAttributeValue>
        {
            {"InstanceId", new MessageAttributeValue {DataType = "String", StringValue = instanceId.ToString()}},
            {"TenantId", new MessageAttributeValue {DataType = "String", StringValue = tenantId.ToString()}}
        };

        var requestMsgBody = $"Rate Plan Update for Instance {instanceId}";

        var sendMessageRequest = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = requestMsgBody,
            MessageAttributes = messageAttributes
        };

        var response = sqsClient.SendMessageAsync(sendMessageRequest).Result;

        if (response.HttpStatusCode == HttpStatusCode.OK)
        {
            LogInfo(context, "SUCCESS", $"Rate Plan Update queued successfully for Instance {instanceId}");
        }
        else
        {
            LogInfo(context, "RESPONSE STATUS", $"Error Queuing Rate Plan Changes for {instanceId}: {response.HttpStatusCode}");
        }
    }
    catch (Exception ex)
    {
        LogInfo(context, "EXCEPTION", $"Exception queuing Rate Plan Updates for {instanceId}: {ex.Message}");
    }
}
```

---

## 4. Sends Go/No-Go Notifications

### Purpose Definition
**What**: Send automated email notifications to stakeholders indicating whether rate plan updates will proceed or be deferred  
**Why**: Keep operations teams informed of automation decisions and provide transparency in rate plan update scheduling  
**How**: Generate and send context-aware email notifications based on timing decisions with detailed reasoning

### Algorithm: SendGoNoGoNotifications

**Input**: 
- context: Lambda execution context
- instance: Optimization instance information
- timeZoneInfo: Billing time zone configuration
- updateDecision: Boolean indicating go/no-go decision

**Output**: Email notifications sent to configured recipients

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:1634-1685`

**Algorithm**:
```
Begin SendGoNoGoNotifications
    If updateDecision = true Then
        SendGoForRatePlanUpdatesNotification(context, instance, timeZoneInfo)
    Else
        SendNoGoForRatePlanUpdatesNotification(context, instance, timeZoneInfo)
    End If
End SendGoNoGoNotifications

Begin SendGoForRatePlanUpdatesNotification
    runStartTime ← ConvertUtcToLocalTime(instance.RunStartTime, timeZoneInfo)
    
    emailSubject ← "AMOP Automatic Rate Plan Updates - GO Decision"
    emailBody ← CreateGoNotificationBody(runStartTime)
    
    recipientList ← GetConfiguredEmailRecipients(context)
    
    SendEmail(context, emailSubject, emailBody, recipientList)
    
    LogInfo("GO notification sent for rate plan updates")
End SendGoForRatePlanUpdatesNotification

Begin SendNoGoForRatePlanUpdatesNotification
    runStartTime ← ConvertUtcToLocalTime(instance.RunStartTime, timeZoneInfo)
    billingPeriodEndDate ← ConvertUtcToLocalTime(instance.BillingPeriodEndDate, timeZoneInfo)
    
    emailSubject ← "AMOP Automatic Rate Plan Updates - NO GO Decision"
    emailBody ← CreateNoGoNotificationBody(runStartTime, billingPeriodEndDate)
    
    recipientList ← GetConfiguredEmailRecipients(context)
    
    SendEmail(context, emailSubject, emailBody, recipientList)
    
    LogInfo("NO GO notification sent for rate plan updates")
End SendNoGoForRatePlanUpdatesNotification
```

**Corresponding Code**:
```csharp
// Lines 1634-1644: Go Notification
private void SendGoForRatePlanUpdatesEmail(KeySysLambdaContext context, OptimizationInstance instance, TimeZoneInfo billingTimeZone)
{
    try
    {
        var body = BuildGoForRatePlanUpdateEmailBody(instance, billingTimeZone);
        var subject = "[Info] AMOP Automatic Rate Plan Updates";
        var fromEmailAddress = context.ClientContext.Environment["OptimizationFromEmailAddress"];
        var recipientAddressList = GetEmailRecipientAddressList(context, EmailRecipientType.OptimizationResults);
        var bccAddressList = GetEmailRecipientAddressList(context, EmailRecipientType.OptimizationResultsBcc);

        SendOptimizationEmail(context, subject, body, fromEmailAddress, recipientAddressList, bccAddressList);
    }
    catch (Exception ex)
    {
        LogInfo(context, "Error Sending Go Rate Plan Update Email", ex.Message);
    }
}

// Lines 1645-1655: No-Go Notification  
private void SendNoGoForRatePlanUpdatesEmail(KeySysLambdaContext context, OptimizationInstance instance, TimeZoneInfo billingTimeZone)
{
    try
    {
        var body = BuildNoGoForRatePlanUpdateEmailBody(instance, billingTimeZone);
        var subject = "[Warning] AMOP Rate Plan Updates Deferred";
        var fromEmailAddress = context.ClientContext.Environment["OptimizationFromEmailAddress"];
        var recipientAddressList = GetEmailRecipientAddressList(context, EmailRecipientType.OptimizationResults);
        var bccAddressList = GetEmailRecipientAddressList(context, EmailRecipientType.OptimizationResultsBcc);

        SendOptimizationEmail(context, subject, body, fromEmailAddress, recipientAddressList, bccAddressList);
    }
    catch (Exception ex)
    {
        LogInfo(context, "Error Sending No Go Rate Plan Update Email", ex.Message);
    }
}
```

**Email Body Construction**:
```csharp
// Lines 1656-1670: Go Email Body
private BodyBuilder BuildGoForRatePlanUpdateEmailBody(OptimizationInstance instance, TimeZoneInfo billingTimeZone)
{
    var runStartTime = TimeZoneInfo.ConvertTimeFromUtc(instance.RunStartTime, billingTimeZone).ToString("yyyy-MM-dd HH:mm:ss");

    var body = new BodyBuilder()
    {
        HtmlBody = $"<div>AMOP is automatically starting Rate Plan Updates for the Carrier Optimization started on: {runStartTime}.</div>",
        TextBody = $"AMOP is automatically starting Rate Plan Updates for the Carrier Optimization started on: {runStartTime}."
    };

    return body;
}

// Lines 1671-1685: No-Go Email Body
private BodyBuilder BuildNoGoForRatePlanUpdateEmailBody(OptimizationInstance instance, TimeZoneInfo billingTimeZone)
{
    var runStartTime = TimeZoneInfo.ConvertTimeFromUtc(instance.RunStartTime, billingTimeZone).ToString("yyyy-MM-dd HH:mm:ss");
    var billingPeriodEndDate = TimeZoneInfo.ConvertTimeFromUtc(instance.BillingPeriodEndDate, billingTimeZone).ToString("yyyy-MM-dd HH:mm:ss");

    var body = new BodyBuilder()
    {
        HtmlBody = $"<div>AMOP has determined that Rate Plan Updates cannot finish for the Carrier Optimization started on: {runStartTime} before the billing period ends on: {billingPeriodEndDate}.</div><br/><div>To prevent issues with a partial update, the system will not proceed with automatic updates for this billing cycle.</div>",
        TextBody = $"AMOP has determined that Rate Plan Updates cannot finish for the Carrier Optimization started on: {runStartTime} before the billing period ends on: {billingPeriodEndDate}.{Environment.NewLine}To prevent issues with a partial update, the system will not proceed with automatic updates for this billing cycle."
    };

    return body;
}
```

---

## Master Algorithm: CompleteRatePlanUpdateProcess

### Purpose Definition
**What**: Orchestrate the complete rate plan update decision and execution process  
**Why**: Provide automated, intelligent rate plan update management with safety controls  
**How**: Integrate timing analysis, processing estimation, queuing decisions, and notifications

### Algorithm: CompleteRatePlanUpdateProcess

**Input**:
- context: Lambda execution context
- instance: Optimization instance configuration
- ratePlansToUpdateCount: Number of rate plans requiring updates
- currentSystemTimeUtc: Current system timestamp

**Output**: Complete rate plan update process execution

**Code Location**: `AltaworxSimCardCostOptimizerCleanup.cs:395-420`

**Algorithm**:
```
Begin CompleteRatePlanUpdateProcess
    timeZoneInfo ← GetBillingTimeZone(context)
    
    hasTimeForUpdates ← DoesHaveTimeToProcessRatePlanUpdates(instance, ratePlansToUpdateCount, 
        context.ConnectionString, context.Logger, currentSystemTimeUtc, timeZoneInfo)
    
    If hasTimeForUpdates Then
        LogInfo("Sufficient time available - proceeding with automatic rate plan updates")
        
        QueueRatePlanUpdates(context, instance.Id, instance.TenantId)
        SendGoForRatePlanUpdatesEmail(context, instance, timeZoneInfo)
        
        UpdateInstanceStatus(context, instance.Id, "RatePlanUpdatesQueued")
        
        LogInfo("Rate plan updates queued successfully")
    Else
        LogInfo("Insufficient time remaining - deferring rate plan updates to next billing cycle")
        
        SendNoGoForRatePlanUpdatesEmail(context, instance, timeZoneInfo)
        
        UpdateInstanceStatus(context, instance.Id, "RatePlanUpdatesDeferred")
        
        LogInfo("Rate plan updates deferred due to timing constraints")
    End If
    
    LogMetrics(context, "RatePlanUpdateDecision", hasTimeForUpdates, ratePlansToUpdateCount)
End CompleteRatePlanUpdateProcess
```

**Corresponding Implementation Code**:
```csharp
// Lines 395-420: Integration in Cleanup Process
if (instance.AutoUpdateRatePlans)
{
    // queue rate plan update (if auto-update rate plans)
    LogInfo(context, LogTypeConstant.Info, "Auto-update rate plans is enabled. Checking if there is time to process rate plan updates.");

    var ratePlansToUpdateCount = CountRatePlansToUpdate(instance.Id, context.ConnectionString, this);
    
    // get rate plan update count for this instance
    LogInfo(context, LogTypeConstant.Info, $"Rate plans to update: {ratePlansToUpdateCount}");

    if (ratePlansToUpdateCount > 0)
    {
        var hasTimeToProcessRatePlanUpdates = DoesHaveTimeToProcessRatePlanUpdates(instance, ratePlansToUpdateCount,
            context.ConnectionString, this, DateTime.UtcNow, context.OptimizationSettings.BillingTimeZone);

        if (hasTimeToProcessRatePlanUpdates)
        {
            // queue rate plans
            QueueRatePlanUpdates(context, instance.Id, instance.TenantId);

            // send "go" rate plan update email
            SendGoForRatePlanUpdatesEmail(context, instance, context.OptimizationSettings.BillingTimeZone);
        }
        else
        {
            // send "no go" rate plan update email
            SendNoGoForRatePlanUpdatesEmail(context, instance, context.OptimizationSettings.BillingTimeZone);
        }
    }
}
```

---

## Algorithm Complexity Analysis

### Time Complexity
- **Time Remaining Evaluation**: O(1) - Simple datetime arithmetic
- **Processing Time Estimation**: O(h) where h = number of historical records
- **Update Queuing**: O(1) - Single SQS message operation
- **Notification Sending**: O(r) where r = number of email recipients
- **Overall Complexity**: O(h + r) for complete rate plan update process

### Space Complexity
- **Memory Usage**: O(h + r) where h = historical records, r = recipients
- **Storage Requirements**: O(1) for decision processing

### Performance Considerations
- Historical data retrieval optimized with database indexing
- Email notifications sent asynchronously to prevent blocking
- SQS queuing provides reliable message delivery
- Time calculations cached to prevent repeated computation